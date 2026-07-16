#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.validate-mesh — 校验 ProBuilder 网格的健康状况。
    /// </summary>
    public class ValidateMeshTool : IUnityCliTool<ValidateMeshTool.Request, ValidateMeshTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
        }

        public sealed class Result
        {
            public bool Healthy { get; set; }
            public long DegenerateFaceCount { get; set; }
            public long UnusedVertexCount { get; set; }
            public List<string> Issues { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.validate-mesh",
            Summary = "Validate a ProBuilder mesh for degenerate triangles and unused vertices.",
            Description = "Validate a ProBuilder mesh for issues like degenerate triangles and unused vertices. Returns a healthy flag, issue counts, and a list of issue descriptions.",
            Group = "probuilder",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            SupportsJob = false,
            ArtifactKinds = new[] { "json" },
            Examples = new[]
            {
                new ToolExample { Description = "校验网格", Input = new Request { Target = "MyCube" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null)
                throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null)
                throw ProBuilderErrors.OperationFailed("validate-mesh", "GameObject does not have a ProBuilderMesh component.");

            var degenerateFaceCount = ProBuilderReflection.CountDegenerateFaces(mesh);
            var unusedVertexCount = ProBuilderReflection.CountUnusedVertices(mesh);
            if (degenerateFaceCount < 0 || unusedVertexCount < 0)
                throw ProBuilderErrors.OperationFailed("validate-mesh", "Failed to query ProBuilder 5.x mesh validation data.");

            var issues = new List<string>();
            if (degenerateFaceCount > 0) issues.Add("Mesh contains faces with degenerate triangles.");
            if (unusedVertexCount > 0) issues.Add("Mesh contains unused vertices.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Healthy = degenerateFaceCount == 0 && unusedVertexCount == 0,
                DegenerateFaceCount = degenerateFaceCount,
                UnusedVertexCount = unusedVertexCount,
                Issues = issues
            });
        }
    }
}
#endif
