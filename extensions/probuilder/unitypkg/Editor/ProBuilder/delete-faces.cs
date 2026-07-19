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
    /// probuilder.delete-faces — 删除 ProBuilder 网格上的选定面。
    /// </summary>
    public class DeleteFacesTool : IUnityCliTool<DeleteFacesTool.Request, DeleteFacesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            [UnityCliRequired] public List<long> FaceIndices { get; set; }
        }

        public sealed class Result
        {
            public long DeletedCount { get; set; }
            public long RemainingFaceCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.delete-faces",
            Summary = "Delete selected faces from a ProBuilder mesh.",
            Description = "Delete selected faces from a ProBuilder mesh. Requires at least one face index. Destructive: removes geometry from the mesh.",
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
                new ToolExample { Description = "删除面 0", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("delete-faces", "No ProBuilderMesh component on target.");

            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).Distinct().ToArray();
            if (faceIndices == null || faceIndices.Length == 0)
                throw ProBuilderErrors.SchemaInvalid("faceIndices is required for probuilder.delete-faces.");
            if (ProBuilderReflection.GetFacesByIndices(mesh, faceIndices) == null)
                throw ProBuilderErrors.SchemaInvalid("faceIndices contains an invalid face index.");

            var before = ProBuilderOperationProof.Capture(mesh, faceIndices: faceIndices);
            if (before == null) throw ProBuilderErrors.OperationFailed("delete-faces", "Requested face geometry is not readable.");
            var beforeFaceCount = ProBuilderReflection.GetFaceCount(mesh);
            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Delete Faces");

            if (!ProBuilderReflection.DeleteFaces(mesh, faceIndices))
                throw ProBuilderErrors.OperationFailed("delete-faces", "ProBuilder 5.x DeleteElements.DeleteFaces failed.");
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var freshMesh = ProBuilderOperationProof.ResolveFreshMesh(request.Target, go);
            var afterFaceCount = ProBuilderReflection.GetFaceCount(freshMesh);
            var after = ProBuilderOperationProof.Capture(freshMesh, faceIndices: Enumerable.Range(0, Math.Max(0, afterFaceCount)).ToArray());
            if (!ProBuilderOperationProof.HasRemovedRequestedFaces(before, after) || beforeFaceCount < 0 || afterFaceCount != beforeFaceCount - faceIndices.Length)
                throw ProBuilderErrors.OperationFailed("delete-faces", "Requested faces were not removed from readable mesh topology.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                DeletedCount = beforeFaceCount - afterFaceCount,
                RemainingFaceCount = afterFaceCount
            });
        }
    }
}
#endif
