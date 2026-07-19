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

            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).ToArray();

            var faces = faceIndices != null && faceIndices.Length > 0
                ? ProBuilderReflection.GetFacesByIndices(mesh, faceIndices)
                : ProBuilderReflection.GetAllFaces(mesh);

            if (faces == null || faces.Length == 0)
                throw ProBuilderErrors.OperationFailed("set-face-uvs", "No faces found on target.");

            var scaleVal = request.Scale.HasValue ? (float?)ProBuilderOperationProof.CheckedFloat(request.Scale.Value, "scale") : null;
            var offsetU = request.OffsetU.HasValue ? (float?)ProBuilderOperationProof.CheckedFloat(request.OffsetU.Value, "offsetU") : null;
            var offsetV = request.OffsetV.HasValue ? (float?)ProBuilderOperationProof.CheckedFloat(request.OffsetV.Value, "offsetV") : null;
            var rotation = request.Rotation.HasValue ? (float?)ProBuilderOperationProof.CheckedFloat(request.Rotation.Value, "rotation") : null;
            var flipU = request.FlipU;
            var flipV = request.FlipV;
            var faceType = ProBuilderReflection.GetFaceType();
            var uvProperty = faceType?.GetProperty("uv", BindingFlags.Instance | BindingFlags.Public);
            var autoUnwrapType = uvProperty?.PropertyType;
            var scaleField = autoUnwrapType?.GetField("scale", BindingFlags.Instance | BindingFlags.Public);
            var offsetField = autoUnwrapType?.GetField("offset", BindingFlags.Instance | BindingFlags.Public);
            var rotationField = autoUnwrapType?.GetField("rotation", BindingFlags.Instance | BindingFlags.Public);
            var flipUField = autoUnwrapType?.GetField("flipU", BindingFlags.Instance | BindingFlags.Public);
            var flipVField = autoUnwrapType?.GetField("flipV", BindingFlags.Instance | BindingFlags.Public);
            var refreshUvParameterType = faceType == null ? null : typeof(IEnumerable<>).MakeGenericType(faceType);
            var refreshUvMethod = refreshUvParameterType == null ? null : ProBuilderReflection.GetProBuilderMeshType()?.GetMethod(
                "RefreshUV", BindingFlags.Instance | BindingFlags.Public, null, new[] { refreshUvParameterType }, null);
            if (uvProperty == null || !uvProperty.CanRead || !uvProperty.CanWrite || scaleField == null || offsetField == null
                || rotationField == null || flipUField == null || flipVField == null || refreshUvMethod == null
                || refreshUvMethod.ReturnType != typeof(void) || !refreshUvParameterType.IsInstanceOfType(faces)
                || (scaleVal.HasValue && scaleField.FieldType != typeof(Vector2))
                || ((offsetU.HasValue || offsetV.HasValue) && offsetField.FieldType != typeof(Vector2))
                || (rotation.HasValue && rotationField.FieldType != typeof(float))
                || flipUField.FieldType != typeof(bool) || flipVField.FieldType != typeof(bool))
                throw ProBuilderErrors.OperationFailed("set-face-uvs", "Required ProBuilder 5.2.4 UV members are unavailable or have incompatible types.");
            if (faces.Cast<object>().Any(face => face == null || uvProperty.GetValue(face) == null))
                throw ProBuilderErrors.OperationFailed("set-face-uvs", "A requested face has unreadable AutoUnwrapSettings.");

            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Set Face UVs");
            foreach (var face in faces)
            {
                var uvSettings = uvProperty.GetValue(face);
                if (scaleVal.HasValue) scaleField.SetValue(uvSettings, new Vector2(scaleVal.Value, scaleVal.Value));
                if (offsetU.HasValue || offsetV.HasValue) offsetField.SetValue(uvSettings, new Vector2(offsetU ?? 0f, offsetV ?? 0f));
                if (rotation.HasValue) rotationField.SetValue(uvSettings, rotation.Value);
                flipUField.SetValue(uvSettings, flipU);
                flipVField.SetValue(uvSettings, flipV);
                uvProperty.SetValue(face, uvSettings);
            }
            refreshUvMethod.Invoke(mesh, new object[] { faces });

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var freshGo = ProBuilderInput.ResolveTarget(request.Target);
            var freshMesh = ProBuilderReflection.GetProBuilderMesh(freshGo);
            if (!ProBuilderRequestProof.RequestedFacesHaveUv(freshMesh, faceIndices ?? Enumerable.Range(0, faces.Length).ToArray(), scaleVal, offsetU, offsetV, rotation, flipU, flipV))
                throw ProBuilderErrors.OperationFailed("set-face-uvs", "Every requested face did not retain the requested UV fields.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result { UpdatedFaceCount = faces.Length });
        }
    }
}
#endif
