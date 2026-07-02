#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.auto-smooth — 按角度阈值自动平滑选定面。
    /// </summary>
    public class AutoSmoothTool : IUnityCliTool<AutoSmoothTool.Request, AutoSmoothTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public double? AngleThreshold { get; set; }             // 可选,默认 30
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
        }

        public sealed class Result
        {
            public long SmoothedFaceCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.auto-smooth",
            Summary = "Auto-apply smoothing groups to ProBuilder faces by an angle threshold.",
            Description = "Automatically apply smoothing to faces based on an angle threshold. When faceIndices is omitted, smoothing is applied to all faces of the target mesh.",
            Group = "probuilder",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            SupportsJob = true,
            ArtifactKinds = new[] { "json" },
            Examples = new[]
            {
                new ToolExample { Description = "以 30 度阈值自动平滑全部面", Input = new Request { Target = "MyCube", AngleThreshold = 30 } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("auto-smooth", "No ProBuilderMesh on target.");

            var angleThreshold = (float)(request.AngleThreshold ?? 30);
            int[] faceIndices = request.FaceIndices?.Select(i => (int)i).ToArray();
            var faceCount = ProBuilderReflection.GetFaceCount(mesh);

            Undo.RecordObject(go, "ProBuilder Auto Smooth");
            if (faceIndices != null && faceIndices.Length > 0)
                ProBuilderReflection.ApplySmoothingGroups(mesh, faceIndices, angleThreshold);
            else
                ProBuilderReflection.ApplySmoothingGroups(mesh, Enumerable.Range(0, faceCount).ToArray(), angleThreshold);

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var smoothed = faceIndices != null ? faceIndices.Length : faceCount;
            return new Result { SmoothedFaceCount = smoothed };
        }
    }
}
#endif
