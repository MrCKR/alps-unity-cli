#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.connect-elements — 在选定边或面之间插入连接边。
    /// </summary>
    public class ConnectElementsTool : IUnityCliTool<ConnectElementsTool.Request, ConnectElementsTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> EdgeIndices { get; set; }            // 可选
            public List<EdgeRef> Edges { get; set; }               // 可选
            public List<long> FaceIndices { get; set; }            // 可选
        }

        public sealed class EdgeRef
        {
            public long A { get; set; }
            public long B { get; set; }
        }

        public sealed class Result
        {
            public long ConnectedCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.connect-elements",
            Summary = "Insert a connecting edge loop across selected edges or faces of a ProBuilder mesh.",
            Description = "Connect the sole triangular face of a ProBuilder mesh, or exactly two distinct boundary edges of that face. The mesh must use serialized format 2, bind its serialized compiled Mesh to MeshFilter.sharedMesh, and have exactly three raw and shared vertices with unique positions, an empty shared-texture map, one non-null Renderer material on source submesh 0, and source Vertex channels whose 5.2.4 midpoint/centroid arithmetic stays finite. UV0 must be complete for all raw vertices or absent for the whole mesh; partial UV0 profiles are rejected. Other selections are rejected before the scene effect. Specify edgeIndices, vertex-pair edges [{a,b}], or one faceIndices value.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "连接唯一三角面", Input = new Request { Target = "MyTriangle", FaceIndices = new List<long> { 0 } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var target = request.Target;
            var go = ProBuilderInput.ResolveTarget(target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            var typedMesh = mesh as UnityEngine.ProBuilder.ProBuilderMesh;
            if (typedMesh == null) throw ProBuilderErrors.OperationFailed("connect-elements", "No ProBuilderMesh on target.");
            // CanonicalMeshSnapshot 在 effect 前冻结 target 与 component identity。

            int[] edgeIndices = request.EdgeIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "edgeIndices")).ToArray();
            ProBuilderInput.EdgePair[] edgePairs = request.Edges?.Select(e => new ProBuilderInput.EdgePair { a = ProBuilderOperationProof.CheckedInt(e.A, "edges.a"), b = ProBuilderOperationProof.CheckedInt(e.B, "edges.b") }).ToArray();
            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).ToArray();

            if ((edgeIndices == null || edgeIndices.Length == 0) &&
                (edgePairs == null || edgePairs.Length == 0) &&
                (faceIndices == null || faceIndices.Length == 0))
                throw ProBuilderErrors.SchemaInvalid("Either edgeIndices, edges, or faceIndices parameter is required for connect-elements.");

            var faceMode = faceIndices != null && faceIndices.Length > 0;
            if (faceMode && ((edgeIndices != null && edgeIndices.Length > 0) || (edgePairs != null && edgePairs.Length > 0)))
                throw ProBuilderErrors.SchemaInvalid("Connect-elements accepts either edge selection or face selection, not both.");
            if (faceMode) ProBuilderOperationProof.ValidateFaceIndices(typedMesh, faceIndices);
            var resolved = faceMode ? null : ProBuilderOperationProof.ValidateEdges(typedMesh, edgeIndices, edgePairs);
            var frozenEdges = resolved == null ? null : resolved.Cast<UnityEngine.ProBuilder.Edge>().ToArray();
            var frozenFaces = faceMode ? faceIndices.Select(index => typedMesh.faces[index]).ToArray() : null;
            var plan = ProBuilderConnectCompletionProof.Capture(go, faceMode, frozenEdges, frozenFaces);
            if (plan == null)
                throw ProBuilderErrors.OperationFailed("connect-elements", "Only a current-format sole triangular face with one bound compiled Mesh, three unique raw/shared vertices, one non-null source material, an empty shared-texture map, finite midpoint/centroid channel arithmetic, or exactly two of its boundary edges, has a unique ProBuilder 5.2.4 completion plan.");

            ctx.CommitEffect();
            try
            {
            Undo.RecordObject(go, "ProBuilder Connect Elements");
            UnityEngine.ProBuilder.Face[] connectedFaces;
            try
            {
                // effect API 只接收 effect 前冻结的 typed Edge[] 或 Face[]，并保留真实返回面用于业务结果和终态归因。
                if (plan.FaceMode)
                    connectedFaces = UnityEngine.ProBuilder.MeshOperations.ConnectElements.Connect(typedMesh, (UnityEngine.ProBuilder.Face[])plan.FrozenFaces);
                else
                    connectedFaces = UnityEngine.ProBuilder.MeshOperations.ConnectElements.Connect(typedMesh, (UnityEngine.ProBuilder.Edge[])plan.FrozenEdges).item1;
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw ProBuilderErrors.OutcomeUnknown("connect-elements", "invoke", "ConnectElements.Connect threw after the effect barrier."); }
            if (connectedFaces == null || connectedFaces.Length != plan.ExpectedFaceCount)
                throw ProBuilderErrors.OutcomeUnknown("connect-elements", "invoke", "ConnectElements.Connect did not return the complete frozen face set.");

            if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(typedMesh))
                throw ProBuilderErrors.OutcomeUnknown("connect-elements", "readback", "ProBuilder mesh refresh failed after effect.");
            EditorSceneManager.MarkSceneDirty(go.scene);
            try
            {
                var freshMesh = CanonicalMeshSnapshot.ResolveFreshMesh(target, plan.Before.Identity);
                if (freshMesh == null)
                    throw ProBuilderErrors.OutcomeUnknown("connect-elements", "readback", "Fresh target identity could not be resolved.");
                var after = CanonicalMeshSnapshot.Capture(freshMesh.gameObject);
                if (after == null)
                    throw ProBuilderErrors.OutcomeUnknown("connect-elements", "readback", "Fresh canonical topology could not be read.");
                if (!ProBuilderConnectCompletionProof.Matches(plan, after))
                    throw ProBuilderErrors.OutcomeUnknown("connect-elements", "postcondition", "The complete frozen ConnectElements terminal plan was not proven by fresh readback.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw ProBuilderErrors.OutcomeUnknown("connect-elements", "postcondition", "Fresh terminal readback could not be proven."); }

                return new System.Threading.Tasks.ValueTask<Result>(new Result { ConnectedCount = connectedFaces.Length });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("connect-elements", "postcondition", exception.Message); }
        }
    }
}
#endif
