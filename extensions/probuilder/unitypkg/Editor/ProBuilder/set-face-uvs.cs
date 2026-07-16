#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.set-face-uvs — 设置 ProBuilder 网格选定面的 UV 参数。
    /// </summary>
    public class SetFaceUvsTool : IUnityCliTool<SetFaceUvsTool.Request, SetFaceUvsTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
            public double? Scale { get; set; }                      // 可选
            public double? OffsetU { get; set; }                    // 可选
            public double? OffsetV { get; set; }                    // 可选
            public double? Rotation { get; set; }                   // 可选
            public bool FlipU { get; set; }                          // 默认 false
            public bool FlipV { get; set; }                          // 默认 false
        }

        public sealed class Result
        {
            public long UpdatedFaceCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.set-face-uvs",
            Summary = "Set UV parameters for selected faces on a ProBuilder mesh.",
            Description = "Set UV parameters (scale, offset, rotation, flipU, flipV) for selected faces on a ProBuilder mesh by modifying each Face's AutoUnwrapSettings. When faceIndices is omitted, all faces are updated.",
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
                new ToolExample { Description = "将面 0 UV 缩放设为 2", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 }, Scale = 2 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("set-face-uvs", "No ProBuilderMesh on target.");

            int[] faceIndices = request.FaceIndices?.Select(i => (int)i).ToArray();

            var faces = faceIndices != null && faceIndices.Length > 0
                ? ProBuilderReflection.GetFacesByIndices(mesh, faceIndices)
                : ProBuilderReflection.GetAllFaces(mesh);

            if (faces == null || faces.Length == 0)
                throw ProBuilderErrors.OperationFailed("set-face-uvs", "No faces found on target.");

            var scaleVal = request.Scale.HasValue ? (float?)request.Scale.Value : null;
            var offsetU = request.OffsetU.HasValue ? (float?)request.OffsetU.Value : null;
            var offsetV = request.OffsetV.HasValue ? (float?)request.OffsetV.Value : null;
            var rotation = request.Rotation.HasValue ? (float?)request.Rotation.Value : null;
            var flipU = request.FlipU;
            var flipV = request.FlipV;

            Undo.RecordObject(go, "ProBuilder Set Face UVs");

            // 通过反射修改 Face.uv 属性
            try
            {
                var faceType = ProBuilderReflection.GetFaceType();
                if (faceType != null)
                {
                    var uvProperty = faceType.GetProperty("uv");
                    if (uvProperty != null)
                    {
                        var autoUnwrapType = uvProperty.PropertyType;

                        // 解析 AutoUnwrapSettings 类型上的字段/属性
                        var scaleField = autoUnwrapType.GetField("scale") ?? (MemberInfo)autoUnwrapType.GetProperty("scale");
                        var offsetField = autoUnwrapType.GetField("offset");
                        var rotField = autoUnwrapType.GetField("rotation");
                        var flipUField = autoUnwrapType.GetField("flipU");
                        var flipVField = autoUnwrapType.GetField("flipV");

                        foreach (var face in faces)
                        {
                            var uvSettings = uvProperty.GetValue(face);
                            if (uvSettings == null) continue;

                            if (scaleVal.HasValue && scaleField is FieldInfo scaleFi)
                                scaleFi.SetValue(uvSettings, new Vector2(scaleVal.Value, scaleVal.Value));
                            else if (scaleVal.HasValue && scaleField is PropertyInfo scalePi)
                                scalePi.SetValue(uvSettings, new Vector2(scaleVal.Value, scaleVal.Value));

                            if (offsetU.HasValue || offsetV.HasValue)
                            {
                                if (offsetField != null)
                                    offsetField.SetValue(uvSettings, new Vector2(offsetU ?? 0f, offsetV ?? 0f));
                            }

                            if (rotation.HasValue && rotField != null)
                                rotField.SetValue(uvSettings, rotation.Value);

                            if (flipU && flipUField != null)
                                flipUField.SetValue(uvSettings, true);

                            if (flipV && flipVField != null)
                                flipVField.SetValue(uvSettings, true);

                            uvProperty.SetValue(face, uvSettings);
                        }

                        // 刷新 UV
                        var refreshUVMethod = ProBuilderReflection.GetProBuilderMeshType()?.GetMethod("RefreshUV",
                            BindingFlags.Instance | BindingFlags.Public);
                        if (refreshUVMethod != null)
                        {
                            var allFaces = ProBuilderReflection.GetAllFaces(mesh);
                            refreshUVMethod.Invoke(mesh, new[] { allFaces });
                        }
                    }
                }
            }
            catch (Exception) { }

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new System.Threading.Tasks.ValueTask<Result>(new Result { UpdatedFaceCount = faces.Length });
        }
    }
}
#endif
