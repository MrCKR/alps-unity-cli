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
    /// probuilder.set-face-color — 设置 ProBuilder 网格选定面的顶点颜色。
    /// </summary>
    public class SetFaceColorTool : IUnityCliTool<SetFaceColorTool.Request, SetFaceColorTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
            public double? R { get; set; }                           // 默认 1
            public double? G { get; set; }                           // 默认 1
            public double? B { get; set; }                           // 默认 1
            public double? A { get; set; }                           // 默认 1
        }

        public sealed class Result
        {
            public long ColoredFaceCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.set-face-color",
            Summary = "Set vertex color of selected faces on a ProBuilder mesh.",
            Description = "Set the vertex color of selected faces on a ProBuilder mesh. Color is specified as R/G/B/A components (0-1). When faceIndices is omitted, all faces are colored.",
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
                new ToolExample { Description = "将面 0 设为红色", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 }, R = 1, G = 0, B = 0, A = 1 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("set-face-color", "No ProBuilderMesh on target.");

            var red = ProBuilderOperationProof.CheckedFloat(request.R ?? 1d, "r");
            var green = ProBuilderOperationProof.CheckedFloat(request.G ?? 1d, "g");
            var blue = ProBuilderOperationProof.CheckedFloat(request.B ?? 1d, "b");
            var alpha = ProBuilderOperationProof.CheckedFloat(request.A ?? 1d, "a");
            if (red < 0 || red > 1 || green < 0 || green > 1 || blue < 0 || blue > 1 || alpha < 0 || alpha > 1)
                throw ProBuilderErrors.SchemaInvalid("Color channels must be between 0 and 1.");
            var color = new Color(red, green, blue, alpha);

            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).ToArray();

            var faces = faceIndices != null && faceIndices.Length > 0
                ? ProBuilderReflection.GetFacesByIndices(mesh, faceIndices)
                : ProBuilderReflection.GetAllFaces(mesh);

            if (faces == null || faces.Length == 0)
                throw ProBuilderErrors.OperationFailed("set-face-color", "No faces found on target.");

            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Set Face Color");

            var colored = ProBuilderReflection.SetFaceColor(mesh, faces, color);
            if (!colored)
                throw ProBuilderErrors.OperationFailed("set-face-color", "ProBuilder 5.x ProBuilderMesh.SetFaceColor failed.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var freshGo = ProBuilderInput.ResolveTarget(request.Target);
            var freshMesh = ProBuilderReflection.GetProBuilderMesh(freshGo);
            if (!ProBuilderRequestProof.RequestedFacesHaveColor(freshMesh, faceIndices ?? Enumerable.Range(0, faces.Length).ToArray(), color))
                throw ProBuilderErrors.OperationFailed("set-face-color", "Every requested face did not retain the requested vertex color.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result { ColoredFaceCount = faces.Length });
        }
    }
}
#endif
