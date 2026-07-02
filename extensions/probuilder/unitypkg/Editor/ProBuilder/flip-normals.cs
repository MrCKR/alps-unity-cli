#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.flip-normals — 翻转 ProBuilder 网格选定面的法线。
    /// </summary>
    public class FlipNormalsTool : IUnityCliTool<FlipNormalsTool.Request, FlipNormalsTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
        }

        public sealed class Result
        {
            public long FlippedCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.flip-normals",
            Summary = "Flip normals of selected faces on a ProBuilder mesh.",
            Description = "Flip the normals of selected faces on a ProBuilder mesh. When faceIndices is omitted, all faces are flipped.",
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
                new ToolExample { Description = "翻转面 0 法线", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 } } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("flip-normals", "No ProBuilderMesh on target.");

            int[] faceIndices = request.FaceIndices?.Select(i => (int)i).ToArray();
            var faceCount = ProBuilderReflection.GetFaceCount(mesh);
            Undo.RecordObject(go, "ProBuilder Flip Normals");

            if (faceIndices != null && faceIndices.Length > 0)
                ProBuilderReflection.FlipFaces(mesh, faceIndices);
            else
                ProBuilderReflection.FlipFaces(mesh, Enumerable.Range(0, faceCount).ToArray());

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var flipped = faceIndices != null ? faceIndices.Length : faceCount;
            return new Result { FlippedCount = flipped };
        }
    }
}
#endif
