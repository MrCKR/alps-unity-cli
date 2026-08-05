#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.subdivide — 细分 ProBuilder 网格的选定面。
    /// </summary>
    public class SubdivideTool : IUnityCliTool<SubdivideTool.Request, SubdivideTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
        }

        public sealed class Result
        {
            public long BeforeFaceCount { get; set; }
            public long AfterFaceCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.subdivide",
            Summary = "Subdivide selected faces of a ProBuilder mesh to increase face count.",
            Description = "Subdivide the sole triangular face of a ProBuilder mesh. The mesh must have exactly three raw and shared vertices with unique positions, an empty shared-texture map, and one non-null Renderer material on source submesh 0. UV0 must be complete for all raw vertices or absent for the whole mesh; partial UV0 profiles are rejected. Other selections are rejected before the scene effect.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "细分唯一三角面", Input = new Request { Target = "MyTriangle", FaceIndices = new List<long> { 0 } } }
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
            if (typedMesh == null) throw ProBuilderErrors.OperationFailed("subdivide", "No ProBuilderMesh on target.");
            // Canonical snapshot 在 effect 前冻结 target 与 component identity。

            var beforeCount = ProBuilderReflection.GetFaceCount(typedMesh);
            if (beforeCount < 1)
                throw ProBuilderErrors.OperationFailed("subdivide", "Target has no readable faces.");
            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).ToArray();
            if (faceIndices == null || faceIndices.Length == 0)
                faceIndices = Enumerable.Range(0, beforeCount).ToArray();
            ProBuilderOperationProof.ValidateFaceIndices(typedMesh, faceIndices);
            if (faceIndices.Length != 1)
                throw ProBuilderErrors.OperationFailed("subdivide", "Only one sole triangular face has an exact ProBuilder 5.2.4 subdivision plan.");
            var frozenFaces = new[] { typedMesh.faces[faceIndices[0]] };
            var plan = ProBuilderConnectCompletionProof.CaptureFace(go, frozenFaces);
            if (plan == null)
                throw ProBuilderErrors.OperationFailed("subdivide", "Only the sole triangular face with three unique raw/shared vertices, one non-null source material, and an empty shared-texture map has an exact ProBuilder 5.2.4 subdivision plan.");
            ctx.CommitEffect();
            try
            {
            Undo.RecordObject(go, "ProBuilder Subdivide");
            try { UnityEngine.ProBuilder.MeshOperations.ConnectElements.Connect(typedMesh, (UnityEngine.ProBuilder.Face[])plan.FrozenFaces); }
            catch { throw ProBuilderErrors.OutcomeUnknown("subdivide", "invoke", "ConnectElements.Connect threw after the effect barrier."); }

            if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(typedMesh))
                throw ProBuilderErrors.OutcomeUnknown("subdivide", "readback", "ProBuilder mesh refresh failed after the effect.");
            EditorSceneManager.MarkSceneDirty(go.scene);

            try
            {
                var freshMesh = CanonicalMeshSnapshot.ResolveFreshMesh(target, plan.Before.Identity);
                if (freshMesh == null)
                    throw ProBuilderErrors.OutcomeUnknown("subdivide", "readback", "Fresh target topology could not be read.");
                var afterCount = ProBuilderReflection.GetFaceCount(freshMesh);
                if (afterCount < 0)
                    throw ProBuilderErrors.OutcomeUnknown("subdivide", "readback", "Fresh target topology could not be read.");
                var after = CanonicalMeshSnapshot.Capture(freshMesh.gameObject);
                if (after == null)
                    throw ProBuilderErrors.OutcomeUnknown("subdivide", "readback", "Fresh canonical face geometry could not be read.");
                if (!ProBuilderConnectCompletionProof.Matches(plan, after))
                    throw ProBuilderErrors.OutcomeUnknown("subdivide", "postcondition", "The complete frozen ConnectElements subdivision plan was not proven by fresh readback.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw ProBuilderErrors.OutcomeUnknown("subdivide", "postcondition", "Fresh terminal readback could not be proven."); }
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    BeforeFaceCount = beforeCount,
                    AfterFaceCount = plan.ExpectedFaceCount
                });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("subdivide", "postcondition", exception.Message); }
        }
    }
}
#endif
