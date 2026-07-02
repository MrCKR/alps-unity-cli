#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
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
            public long DegenerateTriangles { get; set; }
            public long UnusedVertices { get; set; }
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null)
                throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null)
                throw ProBuilderErrors.OperationFailed("validate-mesh", "GameObject does not have a ProBuilderMesh component.");

            var validationResult = ProBuilderReflection.Validate(mesh);
            var issues = new List<string>();
            var degenerate = 0;
            var unused = 0;

            if (validationResult != null)
            {
                try
                {
                    var resultType = validationResult.GetType();
                    var degProp = resultType.GetProperty("degenerateTriangles");
                    if (degProp != null) degenerate = Convert.ToInt32(degProp.GetValue(validationResult), CultureInfo.InvariantCulture);
                    var unusedProp = resultType.GetProperty("unusedVertices");
                    if (unusedProp != null) unused = Convert.ToInt32(unusedProp.GetValue(validationResult), CultureInfo.InvariantCulture);
                    var issuesProp = resultType.GetProperty("issues");
                    if (issuesProp != null)
                    {
                        var issuesArr = issuesProp.GetValue(validationResult) as IEnumerable<string>;
                        if (issuesArr != null) issues.AddRange(issuesArr);
                    }
                }
                catch (Exception) { }
            }

            var healthy = degenerate == 0 && unused == 0 && issues.Count == 0;

            return new Result
            {
                Healthy = healthy,
                DegenerateTriangles = degenerate,
                UnusedVertices = unused,
                Issues = issues
            };
        }
    }
}
#endif
