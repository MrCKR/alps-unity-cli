#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.insert-vertex — 在边或面上插入新顶点。
    /// </summary>
    public class InsertVertexTool : IUnityCliTool<InsertVertexTool.Request, InsertVertexTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public EdgeRef Edge { get; set; }                       // 可选,边 {a,b}
            public long? FaceIndex { get; set; }                    // 可选,面索引
            public double X { get; set; }                           // 默认 0
            public double Y { get; set; }                           // 默认 0
            public double Z { get; set; }                           // 默认 0
        }

        public sealed class EdgeRef
        {
            public long A { get; set; }
            public long B { get; set; }
        }

        public sealed class Result
        {
            public bool Inserted { get; set; }
            public long VertexCountBefore { get; set; }
            public long VertexCountAfter { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.insert-vertex",
            Summary = "Insert a new vertex on an edge or face of a ProBuilder mesh.",
            Description = "Insert a new vertex on an edge or face of a ProBuilder mesh. Provide exactly one of edge or faceIndex; the insertion point uses target-local X/Y/Z coordinates and must lie strictly inside the selected edge or face. Both modes require initialized sharedTextures and absent UV1 because ProBuilder 5.2.4 dereferences the texture lookup and ToMesh clears UV1. Face mode requires a manual-UV face with raw vertices used by no other face and an empty shared-texture partition. Edge mode requires every affected face to be one manual-UV triangle with raw vertices exclusively owned by that face and no shared-texture mapping because ProBuilder 5.2.4 deletes/reorders boundary vertices without preserving unrelated raw ownership or reordering sharedIndexesUV. Both modes freeze the exact new-point payload and preflight normal, tangent, and smoothing refresh arithmetic.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "在边上插入顶点", Input = new Request { Target = "MyCube", Edge = new EdgeRef { A = 0, B = 1 }, X = 0.5, Y = 0, Z = 0 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("insert-vertex", "No ProBuilderMesh on target.");
            // Canonical insert proof owns the pre-effect identity claim.

            var point = ProBuilderOperationProof.CheckedVector3(request.X, request.Y, request.Z, "point");
            var target = request.Target;
            var edge = request.Edge;
            var faceIndex = request.FaceIndex;

            if ((edge != null) == faceIndex.HasValue)
                throw ProBuilderErrors.SchemaInvalid("Pass exactly one of edge or faceIndex for insert-vertex.");
            var requestedFaces = faceIndex.HasValue ? new[] { ProBuilderOperationProof.CheckedInt(faceIndex.Value, "faceIndex") } : null;
            var requestedEdges = edge == null ? null : ProBuilderOperationProof.ValidateEdges(mesh, null, new[] { new ProBuilderInput.EdgePair { a = ProBuilderOperationProof.CheckedInt(edge.A, "edge.a"), b = ProBuilderOperationProof.CheckedInt(edge.B, "edge.b") } });
            if (requestedFaces != null)
            {
                ProBuilderOperationProof.ValidateFaceIndices(mesh, requestedFaces);
                if (ProBuilderInsertCompletionProof.HasFaceInsertionSharedTextureConflict(go, requestedFaces[0]))
                    throw ProBuilderErrors.SchemaInvalid("face insertion cannot preserve this mesh's shared texture groups with ProBuilder 5.2.4; use edge mode or remove the UV sharing partition explicitly first.");
            }
            var requestedEdge = edge == null ? null : new[] { ProBuilderOperationProof.CheckedInt(edge.A, "edge.a"), ProBuilderOperationProof.CheckedInt(edge.B, "edge.b") };
            var before = ProBuilderInsertCompletionProof.Capture(go, requestedEdge, requestedFaces == null ? -1 : requestedFaces[0], out var captureFailure);
            if (before == null) throw ProBuilderErrors.OperationFailed("insert-vertex", "Requested canonical insertion geometry is not readable: " + captureFailure + ".");
            if (!ProBuilderInsertCompletionProof.PointIsStrictlyInterior(before, point))
                throw ProBuilderErrors.SchemaInvalid("point must be strictly inside the requested edge or face and must not equal an existing point.");
            if (!ProBuilderInsertCompletionProof.TerminalCanRefresh(before, point))
                throw ProBuilderErrors.SchemaInvalid("the exact inserted topology must keep all 5.2.4 normal, tangent, and smoothing refresh arithmetic finite and bounded.");
            var vertexCountBefore = ProBuilderReflection.GetVertexCount(mesh);
            if (vertexCountBefore < 0)
                throw ProBuilderErrors.OperationFailed("insert-vertex", "Pre-effect vertex count is not readable.");
            ctx.CommitEffect();
            try
            {
                Undo.RecordObject(go, "ProBuilder Insert Vertex");
                var inserted = edge != null
                    ? ProBuilderReflection.InsertVertexOnEdgeByPair(mesh, requestedEdge[0], requestedEdge[1], point)
                    : ProBuilderReflection.InsertVertexInFace(mesh, requestedFaces[0], point);
                if (!inserted)
                    throw ProBuilderErrors.OutcomeUnknown("insert-vertex", "invoke", "ProBuilder 5.2.4 AppendElements insertion failed.");
                if (edge != null && !ProBuilderInsertCompletionProof.RestoreEdgeFaceAttributes(mesh, before))
                    throw ProBuilderErrors.OutcomeUnknown("insert-vertex", "invoke", "Affected edge faces could not restore their frozen attributes before refresh.");
                if (!ProBuilderReflection.RefreshMesh(mesh))
                    throw ProBuilderErrors.OutcomeUnknown("insert-vertex", "invoke", "ProBuilder data changed but ToMesh/Refresh did not complete.");
                var freshMesh = CanonicalMeshSnapshot.ResolveFreshMesh(target, before.Before.Identity);
                if (freshMesh == null)
                    throw ProBuilderErrors.OutcomeUnknown("insert-vertex", "readback", "Fresh target topology could not be read.");
                var vertexCountAfter = ProBuilderReflection.GetVertexCount(freshMesh);
                if (vertexCountAfter < 0)
                    throw ProBuilderErrors.OutcomeUnknown("insert-vertex", "readback", "Fresh target topology could not be read.");
                var after = CanonicalMeshSnapshot.Capture(freshMesh.gameObject, Array.Empty<int>());
                if (after == null)
                    throw ProBuilderErrors.OutcomeUnknown("insert-vertex", "readback", "Fresh canonical topology or compiled Mesh could not be read.");
                if (!ProBuilderInsertCompletionProof.Matches(before, after, point, out var proofFailure))
                    throw ProBuilderErrors.OutcomeUnknown("insert-vertex", "postcondition", "Requested point, edge replacement or face child union/winding/material/UV/smoothing were not proven by fresh readback: " + proofFailure + ".");
                EditorSceneManager.MarkSceneDirty(go.scene);
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Inserted = true,
                    VertexCountBefore = vertexCountBefore,
                    VertexCountAfter = vertexCountAfter
                });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception)
            {
                throw ProBuilderErrors.OutcomeUnknown("insert-vertex", "postcondition", exception.Message);
            }
        }
    }
}
#endif
