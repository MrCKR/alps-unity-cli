#if UNITY_EDITOR
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
            DestructiveHint = true,
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("delete-faces", "No ProBuilderMesh component on target.");

            int[] faceIndices = request.FaceIndices?.Select(i => (int)i).ToArray();
            if (faceIndices == null || faceIndices.Length == 0)
                throw ProBuilderErrors.SchemaInvalid("faceIndices is required for probuilder.delete-faces.");

            var beforeFaceCount = ProBuilderReflection.GetFaceCount(mesh);
            Undo.RecordObject(go, "ProBuilder Delete Faces");

            ProBuilderReflection.DeleteFaces(mesh, faceIndices);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var afterFaceCount = ProBuilderReflection.GetFaceCount(mesh);

            return new Result
            {
                DeletedCount = beforeFaceCount - afterFaceCount,
                RemainingFaceCount = afterFaceCount
            };
        }
    }
}
#endif
