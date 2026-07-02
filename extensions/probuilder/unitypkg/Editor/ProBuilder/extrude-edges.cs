#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.extrude-edges — 挤出 ProBuilder 网格的选定边。
    /// </summary>
    public class ExtrudeEdgesTool : IUnityCliTool<ExtrudeEdgesTool.Request, ExtrudeEdgesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> EdgeIndices { get; set; }            // 可选
            public List<EdgeRef> Edges { get; set; }               // 可选
            public double? Distance { get; set; }                  // 可选,默认 0.5
            public bool? AsGroup { get; set; }                     // 可选,默认 true
        }

        public sealed class EdgeRef
        {
            public long A { get; set; }
            public long B { get; set; }
        }

        public sealed class Result
        {
            public long ModifiedEdgeCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.extrude-edges",
            Summary = "Extrude selected edges of a ProBuilder mesh by a distance.",
            Description = "Extrude selected edges of a ProBuilder mesh. Specify edges by edgeIndices (indices into the unique edge list) or by vertex-pair edges [{a,b}]. Requires at least one edge selection.",
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
                new ToolExample { Description = "挤出边 0.5 距离", Input = new Request { Target = "MyCube", Edges = new List<EdgeRef> { new EdgeRef { A = 0, B = 1 } }, Distance = 0.5 } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("extrude-edges", "No ProBuilderMesh component on target.");

            var distance = (float)(request.Distance ?? 0.5);
            var asGroup = request.AsGroup ?? true;
            int[] edgeIndices = request.EdgeIndices?.Select(i => (int)i).ToArray();
            ProBuilderInput.EdgePair[] edgePairs = request.Edges?.Select(e => new ProBuilderInput.EdgePair { a = (int)e.A, b = (int)e.B }).ToArray();

            if ((edgeIndices == null || edgeIndices.Length == 0) && (edgePairs == null || edgePairs.Length == 0))
                throw ProBuilderErrors.SchemaInvalid("edgeIndices or edges parameter is required for extrude-edges.");

            Undo.RecordObject(go, "ProBuilder Extrude Edges");
            var edgeCount = ProBuilderReflection.ExtrudeEdgesBySelection(mesh, edgeIndices, edgePairs, distance, asGroup);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new Result { ModifiedEdgeCount = edgeCount };
        }
    }
}
#endif
