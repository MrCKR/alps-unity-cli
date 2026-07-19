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
            Description = "Duplicate selected faces and flip their normals with the ProBuilder 5.x AppendElements.DuplicateAndFlip API. When faceIndices is omitted, all faces are used.",
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
                new ToolExample { Description = "复制并翻转面 0", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("duplicate-and-flip", "No ProBuilderMesh on target.");

            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).ToArray();

            var faces = faceIndices != null && faceIndices.Length > 0
                ? ProBuilderReflection.GetFacesByIndices(mesh, faceIndices)
                : ProBuilderReflection.GetAllFaces(mesh);

            if (faces == null || faces.Length == 0)
                throw ProBuilderErrors.OperationFailed("duplicate-and-flip", "No faces found on target.");

            var requestedFaceIndices = faceIndices ?? Enumerable.Range(0, ProBuilderReflection.GetFaceCount(mesh)).ToArray();
            ProBuilderOperationProof.ValidateFaceIndices(mesh, requestedFaceIndices);
            var before = ProBuilderOperationProof.Capture(mesh, faceIndices: requestedFaceIndices);
            if (before == null) throw ProBuilderErrors.OperationFailed("duplicate-and-flip", "Requested face geometry is not readable.");
            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Duplicate and Flip");
            var duplicated = ProBuilderReflection.DuplicateAndFlip(mesh, faces);
            if (!duplicated)
                throw ProBuilderErrors.OperationFailed("duplicate-and-flip", "ProBuilder 5.x AppendElements.DuplicateAndFlip failed.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var freshMesh = ProBuilderOperationProof.ResolveFreshMesh(request.Target, go);
            var after = ProBuilderOperationProof.Capture(freshMesh, faceIndices: requestedFaceIndices);
            if (!ProBuilderOperationProof.HasTopologyExpansion(before, after))
                throw ProBuilderErrors.OperationFailed("duplicate-and-flip", "Requested faces did not produce readable duplicated topology.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result { DuplicatedCount = faces.Length });
        }
    }
}
#endif
