#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.merge-vertices — 合并 ProBuilder 网格的选定顶点。
    /// </summary>
    public class MergeVerticesTool : IUnityCliTool<MergeVerticesTool.Request, MergeVerticesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            [UnityCliRequired] public List<long> VertexIndices { get; set; }
            public bool CollapseToFirst { get; set; }               // 默认 false
        }

        public sealed class Result
        {
            public long MergedVertexCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.merge-vertices",
            Summary = "Merge selected vertices of a ProBuilder mesh into a single vertex.",
            Description = "Merge selected vertices on a ProBuilder mesh into a single vertex. Requires at least 2 vertex indices. When collapseToFirst is true, merged vertices collapse to the first selected index.",
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
                new ToolExample { Description = "合并顶点 0 和 1", Input = new Request { Target = "MyCube", VertexIndices = new List<long> { 0, 1 } } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("merge-vertices", "No ProBuilderMesh on target.");

            int[] vertexIndices = request.VertexIndices?.Select(i => (int)i).ToArray();
            if (vertexIndices == null || vertexIndices.Length < 2)
                throw ProBuilderErrors.SchemaInvalid("At least 2 vertexIndices required for merge-vertices.");

            Undo.RecordObject(go, "ProBuilder Merge Vertices");
            var merged = ProBuilderReflection.MergeVerticesWithCollapse(mesh, vertexIndices, request.CollapseToFirst);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new Result { MergedVertexCount = merged };
        }
    }
}
#endif
