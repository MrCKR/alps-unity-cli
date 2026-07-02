#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.bevel-edges — 对选定边进行倒角。
    /// </summary>
    public class BevelEdgesTool : IUnityCliTool<BevelEdgesTool.Request, BevelEdgesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public double? Amount { get; set; }                     // 可选,默认 0.1
            public List<long> EdgeIndices { get; set; }            // 可选
            public List<EdgeRef> Edges { get; set; }               // 可选
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
            Name = "probuilder.bevel-edges",
            Summary = "Bevel selected edges of a ProBuilder mesh by a given amount.",
            Description = "Bevel selected edges of a ProBuilder mesh by a given amount. Specify edges by edgeIndices (indices into the unique edge list) or by vertex-pair edges [{a,b}]. Requires at least one edge selection.",
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
                new ToolExample { Description = "以 0.1 量对边倒角", Input = new Request { Target = "MyCube", Amount = 0.1, Edges = new List<EdgeRef> { new EdgeRef { A = 0, B = 1 } } } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("bevel-edges", "No ProBuilderMesh on target.");

            var amount = (float)(request.Amount ?? 0.1);
            int[] edgeIndices = request.EdgeIndices?.Select(i => (int)i).ToArray();
            ProBuilderInput.EdgePair[] edgePairs = request.Edges?.Select(e => new ProBuilderInput.EdgePair { a = (int)e.A, b = (int)e.B }).ToArray();

            if ((edgeIndices == null || edgeIndices.Length == 0) && (edgePairs == null || edgePairs.Length == 0))
                throw ProBuilderErrors.SchemaInvalid("edgeIndices or edges parameter is required for bevel-edges.");

            Undo.RecordObject(go, "ProBuilder Bevel Edges");
            var modifiedEdgeCount = ProBuilderReflection.BevelEdges(mesh, edgeIndices, edgePairs, amount);
            if (modifiedEdgeCount == 0)
                throw ProBuilderErrors.OperationFailed("bevel-edges", "Bevel.BevelEdges method not found or failed in ProBuilder assembly.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new Result { ModifiedEdgeCount = modifiedEdgeCount };
        }
    }
}
#endif
