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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("split-vertices", "No ProBuilderMesh on target.");

            int[] vertexIndices = request.VertexIndices?.Select(i => (int)i).ToArray();
            if (vertexIndices == null || vertexIndices.Length == 0)
                throw ProBuilderErrors.SchemaInvalid("vertexIndices is required for split-vertices.");

            Undo.RecordObject(go, "ProBuilder Split Vertices");
            var split = ProBuilderReflection.SplitVerticesWithList(mesh, vertexIndices);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new Result { SplitCount = split };
        }
    }
}
#endif
