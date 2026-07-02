#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.duplicate-and-flip — 复制选定面并翻转法线。
    /// </summary>
    public class DuplicateAndFlipTool : IUnityCliTool<DuplicateAndFlipTool.Request, DuplicateAndFlipTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
        }

        public sealed class Result
        {
            public long DuplicatedCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.duplicate-and-flip",
            Summary = "Duplicate selected faces and flip their normals on a ProBuilder mesh.",
            Description = "Duplicate selected faces and flip their normals. When faceIndices is omitted, all faces are used. Falls back to flipping normals in place when the ProBuilder DuplicateAndFlip API is unavailable.",
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
                new ToolExample { Description = "复制并翻转面 0", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 } } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("duplicate-and-flip", "No ProBuilderMesh on target.");

            int[] faceIndices = request.FaceIndices?.Select(i => (int)i).ToArray();
            var faceCount = ProBuilderReflection.GetFaceCount(mesh);

            var faces = faceIndices != null && faceIndices.Length > 0
                ? ProBuilderReflection.GetFacesByIndices(mesh, faceIndices)
                : ProBuilderReflection.GetAllFaces(mesh);

            if (faces == null || faces.Length == 0)
                throw ProBuilderErrors.OperationFailed("duplicate-and-flip", "No faces found on target.");

            Undo.RecordObject(go, "ProBuilder Duplicate and Flip");
            var duplicated = ProBuilderReflection.DuplicateAndFlip(mesh, faces);
            if (!duplicated)
            {
                // 回退:DuplicateAndFlip API 不可用时手动翻转
                ProBuilderReflection.FlipFaces(mesh, faceIndices ?? Enumerable.Range(0, faceCount).ToArray());
            }

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new Result { DuplicatedCount = faces.Length };
        }
    }
}
#endif
