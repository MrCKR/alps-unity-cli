#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.split-vertices — 拆分 ProBuilder 网格的共享顶点。
    /// </summary>
    public class SplitVerticesTool : IUnityCliTool<SplitVerticesTool.Request, SplitVerticesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            [UnityCliRequired] public List<long> VertexIndices { get; set; }
        }

        public sealed class Result
        {
            public long SplitCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.split-vertices",
            Summary = "Split shared vertices on a ProBuilder mesh into separate per-face vertices.",
            Description = "Split shared vertices on a ProBuilder mesh into separate vertices per face. Requires at least one vertex index.",
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
                new ToolExample { Description = "拆分顶点 0", Input = new Request { Target = "MyCube", VertexIndices = new List<long> { 0 } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("split-vertices", "No ProBuilderMesh on target.");

            int[] vertexIndices = request.VertexIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "vertexIndices")).Distinct().ToArray();
            var vertexCount = ProBuilderReflection.GetVertexCount(mesh);
            if (vertexIndices == null || vertexIndices.Length == 0)
                throw ProBuilderErrors.SchemaInvalid("vertexIndices is required for split-vertices.");
            if (vertexCount < 0 || vertexIndices.Any(index => index < 0 || index >= vertexCount))
                throw ProBuilderErrors.SchemaInvalid("vertexIndices contains an invalid vertex index.");

            var before = ProBuilderOperationProof.Capture(mesh, vertexIndices: vertexIndices);
            if (before == null) throw ProBuilderErrors.OperationFailed("split-vertices", "Requested shared vertices are not readable.");
            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Split Vertices");
            var split = ProBuilderReflection.SplitVerticesWithList(mesh, vertexIndices);
            if (split < 0)
                throw ProBuilderErrors.OperationFailed("split-vertices", "ProBuilder 5.x VertexEditing.SplitVertices failed.");
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var freshMesh = ProBuilderOperationProof.ResolveFreshMesh(request.Target, go);
            var after = ProBuilderOperationProof.Capture(freshMesh, vertexIndices: vertexIndices);
            if (!ProBuilderOperationProof.HasSplitVertices(before, after))
                throw ProBuilderErrors.OperationFailed("split-vertices", "Requested vertices did not split in readable shared-vertex topology.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result { SplitCount = split });
        }
    }
}
#endif
