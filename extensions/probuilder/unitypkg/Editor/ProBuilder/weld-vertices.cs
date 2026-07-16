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
            Description = "Weld at least two selected vertices within a radius with the ProBuilder 5.x VertexEditing.WeldVertices API. Radius defaults to 0.01.",
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
                new ToolExample { Description = "以 0.01 半径焊接顶点 0 和 1", Input = new Request { Target = "MyCube", VertexIndices = new List<long> { 0, 1 }, Radius = 0.01 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("weld-vertices", "No ProBuilderMesh on target.");

            int[] vertexIndices = request.VertexIndices?.Select(i => (int)i).Distinct().ToArray();
            var vertexCount = ProBuilderReflection.GetVertexCount(mesh);
            if (vertexIndices == null || vertexIndices.Length < 2)
                throw ProBuilderErrors.SchemaInvalid("At least 2 unique vertexIndices are required for weld-vertices.");
            if (vertexCount < 0 || vertexIndices.Any(index => index < 0 || index >= vertexCount))
                throw ProBuilderErrors.SchemaInvalid("vertexIndices contains an invalid vertex index.");
            var radius = (float)(request.Radius ?? 0.01);
            if (radius <= 0)
                throw ProBuilderErrors.SchemaInvalid("radius must be greater than 0.");

            Undo.RecordObject(go, "ProBuilder Weld Vertices");
            var welded = ProBuilderReflection.WeldVerticesWithRadius(mesh, vertexIndices, radius);
            if (welded < 0)
                throw ProBuilderErrors.OperationFailed("weld-vertices", "ProBuilder 5.x VertexEditing.WeldVertices failed.");
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new System.Threading.Tasks.ValueTask<Result>(new Result { WeldedCount = welded });
        }
    }
}
#endif
