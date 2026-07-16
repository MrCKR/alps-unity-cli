#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.merge-faces — 合并 ProBuilder 网格的选定面。
    /// </summary>
    public class MergeFacesTool : IUnityCliTool<MergeFacesTool.Request, MergeFacesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            [UnityCliRequired] public List<long> FaceIndices { get; set; }
        }

        public sealed class Result
        {
            public long BeforeFaceCount { get; set; }
            public long AfterFaceCount { get; set; }
            public long MergedSourceFaceCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.merge-faces",
            Summary = "Merge selected faces of a ProBuilder mesh into a single face.",
            Description = "Merge selected faces of a ProBuilder mesh into a single face. Requires at least 2 face indices.",
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
                new ToolExample { Description = "合并面 0 和 1", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0, 1 } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("merge-faces", "No ProBuilderMesh on target.");

            int[] faceIndices = request.FaceIndices?.Select(i => (int)i).Distinct().ToArray();
            if (faceIndices == null || faceIndices.Length < 2)
                throw ProBuilderErrors.SchemaInvalid("At least 2 unique faceIndices are required for merge.");
            if (ProBuilderReflection.GetFacesByIndices(mesh, faceIndices) == null)
                throw ProBuilderErrors.SchemaInvalid("faceIndices contains an invalid face index.");

            var beforeCount = ProBuilderReflection.GetFaceCount(mesh);
            Undo.RecordObject(go, "ProBuilder Merge Faces");

            if (!ProBuilderReflection.MergeFaces(mesh, faceIndices))
                throw ProBuilderErrors.OperationFailed("merge-faces", "ProBuilder 5.x MergeElements.Merge failed.");
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var afterCount = ProBuilderReflection.GetFaceCount(mesh);
            if (beforeCount < 0 || afterCount != beforeCount - faceIndices.Length + 1)
                throw ProBuilderErrors.OperationFailed("merge-faces", "Face count did not match a single merged face result.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                BeforeFaceCount = beforeCount,
                AfterFaceCount = afterCount,
                MergedSourceFaceCount = faceIndices.Length
            });
        }
    }
}
#endif
