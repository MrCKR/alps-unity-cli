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
            public long MergedFaceIndex { get; set; }
            public long MergedFaceCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.merge-faces",
            Summary = "Merge selected faces of a ProBuilder mesh into a single face.",
            Description = "Merge selected faces of a ProBuilder mesh into a single face. Requires at least 2 face indices.",
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
                new ToolExample { Description = "合并面 0 和 1", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0, 1 } } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("merge-faces", "No ProBuilderMesh on target.");

            int[] faceIndices = request.FaceIndices?.Select(i => (int)i).ToArray();
            if (faceIndices == null || faceIndices.Length < 2)
                throw ProBuilderErrors.SchemaInvalid("At least 2 faceIndices required for merge.");

            var beforeCount = ProBuilderReflection.GetFaceCount(mesh);
            Undo.RecordObject(go, "ProBuilder Merge Faces");

            ProBuilderReflection.MergeFaces(mesh, faceIndices);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var afterCount = ProBuilderReflection.GetFaceCount(mesh);
            return new Result
            {
                MergedFaceIndex = afterCount > 0 ? afterCount - 1 : 0,
                MergedFaceCount = beforeCount - afterCount + 1
            };
        }
    }
}
#endif
