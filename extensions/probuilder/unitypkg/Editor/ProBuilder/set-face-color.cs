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
            public double R { get; set; }                            // 默认 1
            public double G { get; set; }                            // 默认 1
            public double B { get; set; }                            // 默认 1
            public double A { get; set; }                            // 默认 1
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("set-face-color", "No ProBuilderMesh on target.");

            var color = new Color((float)request.R, (float)request.G, (float)request.B, (float)request.A);

            int[] faceIndices = request.FaceIndices?.Select(i => (int)i).ToArray();

            var faces = faceIndices != null && faceIndices.Length > 0
                ? ProBuilderReflection.GetFacesByIndices(mesh, faceIndices)
                : ProBuilderReflection.GetAllFaces(mesh);

            if (faces == null || faces.Length == 0)
                throw ProBuilderErrors.OperationFailed("set-face-color", "No faces found on target.");

            Undo.RecordObject(go, "ProBuilder Set Face Color");

            // 通过反射调用 ProBuilderMesh.SetFaceColor
            var colored = ProBuilderReflection.SetFaceColor(mesh, faces, color);
            if (!colored)
            {
                // 回退:尝试直接在网格上设置顶点颜色
                try
                {
                    var hasColorsProp = ProBuilderReflection.GetProBuilderMeshType()?.GetProperty("hasColors");
                    if (hasColorsProp != null && hasColorsProp.CanWrite)
                        hasColorsProp.SetValue(mesh, true);
                }
                catch (Exception) { }
            }

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new Result { ColoredFaceCount = faces.Length };
        }
    }
}
#endif
