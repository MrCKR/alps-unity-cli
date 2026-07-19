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
            public long MergedToIndex { get; set; }
            public long SourceVertexCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.merge-vertices",
            Summary = "Merge selected vertices of a ProBuilder mesh into a single vertex.",
            Description = "Merge selected vertices on a ProBuilder mesh into a single vertex. Requires at least 2 vertex indices. When collapseToFirst is true, merged vertices collapse to the first selected index.",
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
                new ToolExample { Description = "合并顶点 0 和 1", Input = new Request { Target = "MyCube", VertexIndices = new List<long> { 0, 1 } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("merge-vertices", "No ProBuilderMesh on target.");

            int[] vertexIndices = request.VertexIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "vertexIndices")).Distinct().ToArray();
            var vertexCount = ProBuilderReflection.GetVertexCount(mesh);
            if (vertexIndices == null || vertexIndices.Length < 2)
                throw ProBuilderErrors.SchemaInvalid("At least 2 unique vertexIndices are required for merge-vertices.");
            if (vertexCount < 0 || vertexIndices.Any(index => index < 0 || index >= vertexCount))
                throw ProBuilderErrors.SchemaInvalid("vertexIndices contains an invalid vertex index.");
            var before = ProBuilderOperationProof.Capture(mesh, vertexIndices: vertexIndices);
            if (before == null) throw ProBuilderErrors.OperationFailed("merge-vertices", "Requested vertex geometry is not readable.");
            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Merge Vertices");
            var mergedToIndex = ProBuilderReflection.MergeVerticesWithCollapse(mesh, vertexIndices, request.CollapseToFirst);
            if (mergedToIndex < 0)
                throw ProBuilderErrors.OperationFailed("merge-vertices", "ProBuilder 5.x VertexEditing.MergeVertices failed.");
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var freshMesh = ProBuilderOperationProof.ResolveFreshMesh(request.Target, go);
            var after = ProBuilderOperationProof.Capture(freshMesh, vertexIndices: vertexIndices);
            if (!ProBuilderOperationProof.HasMergedVertices(before, after))
                throw ProBuilderErrors.OperationFailed("merge-vertices", "Requested vertices did not merge in readable topology.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result { MergedToIndex = mergedToIndex, SourceVertexCount = vertexIndices.Length });
        }
    }
}
#endif
