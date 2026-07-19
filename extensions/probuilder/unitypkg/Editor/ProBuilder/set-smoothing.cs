#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.set-smoothing — 为选定面设置平滑组。
    /// </summary>
    public class SetSmoothingTool : IUnityCliTool<SetSmoothingTool.Request, SetSmoothingTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
            [UnityCliRequired] public long SmoothingGroup { get; set; }
        }

        public sealed class Result
        {
            public long ModifiedFaceCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.set-smoothing",
            Summary = "Set the smoothing group for selected faces of a ProBuilder mesh.",
            Description = "Set the smoothing group for selected faces on a ProBuilder mesh. Requires a non-negative smoothingGroup. When faceIndices is omitted, all faces are assigned the smoothing group.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            SupportsJob = true,
            ArtifactKinds = new[] { "json" },
            Examples = new[]
            {
                new ToolExample { Description = "将面 0 设为平滑组 1", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 }, SmoothingGroup = 1 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("set-smoothing", "No ProBuilderMesh on target.");

            var smoothingGroup = ProBuilderOperationProof.CheckedInt(request.SmoothingGroup, "smoothingGroup");
            if (smoothingGroup < 0)
                throw ProBuilderErrors.SchemaInvalid("smoothingGroup must be a non-negative integer.");

            var faceCount = ProBuilderReflection.GetFaceCount(mesh);
            if (faceCount < 1)
                throw ProBuilderErrors.OperationFailed("set-smoothing", "Target has no readable faces.");
            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).Distinct().ToArray();
            if (faceIndices == null || faceIndices.Length == 0)
                faceIndices = Enumerable.Range(0, faceCount).ToArray();
            if (ProBuilderReflection.GetFacesByIndices(mesh, faceIndices) == null)
                throw ProBuilderErrors.SchemaInvalid("faceIndices contains an invalid face index.");
            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Set Smoothing");
            if (!ProBuilderReflection.ApplySmoothingGroup(mesh, faceIndices, smoothingGroup))
                throw ProBuilderErrors.OperationFailed("set-smoothing", "Failed to set ProBuilder 5.x Face.smoothingGroup.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var freshGo = ProBuilderInput.ResolveTarget(request.Target);
            var freshMesh = ProBuilderReflection.GetProBuilderMesh(freshGo);
            if (!ProBuilderRequestProof.RequestedFacesHaveSmoothingGroup(freshMesh, faceIndices, smoothingGroup))
                throw ProBuilderErrors.OperationFailed("set-smoothing", "Requested smoothing group was not written to every selected face.");

            var modified = faceIndices.Length;
            return new System.Threading.Tasks.ValueTask<Result>(new Result { ModifiedFaceCount = modified });
        }
    }
}
#endif
