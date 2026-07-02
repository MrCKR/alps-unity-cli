#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.weld-vertices — 在半径内焊接 ProBuilder 网格的顶点。
    /// </summary>
    public class WeldVerticesTool : IUnityCliTool<WeldVerticesTool.Request, WeldVerticesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> VertexIndices { get; set; }           // 可选,空数组则作用于无(传入空)
            public double? Radius { get; set; }                     // 可选,默认 0.01
        }

        public sealed class Result
        {
            public long WeldedCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.weld-vertices",
            Summary = "Weld vertices of a ProBuilder mesh within a given radius.",
            Description = "Weld vertices within a radius on a ProBuilder mesh. When vertexIndices is omitted, an empty selection is used. Radius defaults to 0.01.",
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
                new ToolExample { Description = "以 0.01 半径焊接顶点 0 和 1", Input = new Request { Target = "MyCube", VertexIndices = new List<long> { 0, 1 }, Radius = 0.01 } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("weld-vertices", "No ProBuilderMesh on target.");

            int[] vertexIndices = request.VertexIndices?.Select(i => (int)i).ToArray();
            var radius = (float)(request.Radius ?? 0.01);

            Undo.RecordObject(go, "ProBuilder Weld Vertices");
            var welded = ProBuilderReflection.WeldVerticesWithRadius(mesh, vertexIndices ?? new int[0], radius);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new Result { WeldedCount = welded };
        }
    }
}
#endif
