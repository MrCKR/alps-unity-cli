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
            SideEffects = new[] { "scene-modify", "undo-stack" },
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

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("bevel-edges", "No ProBuilderMesh on target.");

            var amount = ProBuilderOperationProof.CheckedFloat(request.Amount ?? 0.1, "amount");
            if (amount <= 0)
                throw ProBuilderErrors.SchemaInvalid("amount must be greater than 0 so bevel completion has observable topology.");
            int[] edgeIndices = request.EdgeIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "edgeIndices")).ToArray();
            ProBuilderInput.EdgePair[] edgePairs = request.Edges?.Select(e => new ProBuilderInput.EdgePair { a = ProBuilderOperationProof.CheckedInt(e.A, "edges.a"), b = ProBuilderOperationProof.CheckedInt(e.B, "edges.b") }).ToArray();

            if ((edgeIndices == null || edgeIndices.Length == 0) && (edgePairs == null || edgePairs.Length == 0))
                throw ProBuilderErrors.SchemaInvalid("edgeIndices or edges parameter is required for bevel-edges.");

            var edgeArray = ProBuilderOperationProof.ValidateEdges(mesh, edgeIndices, edgePairs);
            var before = ProBuilderOperationProof.Capture(mesh, edges: edgeArray);
            if (before == null) throw ProBuilderErrors.OperationFailed("bevel-edges", "Requested edge geometry is not readable.");
            var beforeVertexCount = ProBuilderReflection.GetVertexCount(mesh);
            var beforeFaceCount = ProBuilderReflection.GetFaceCount(mesh);
            var beforeEdgeCount = ProBuilderReflection.GetEdgeCount(mesh);
            if (beforeVertexCount < 0 || beforeFaceCount < 0 || beforeEdgeCount < 0)
                throw ProBuilderErrors.OperationFailed("bevel-edges", "Mesh topology is not readable before bevel.");
            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Bevel Edges");
            var modifiedEdgeCount = ProBuilderReflection.BevelEdges(mesh, edgeIndices, edgePairs, amount);
            if (modifiedEdgeCount == 0)
                throw ProBuilderErrors.OperationFailed("bevel-edges", "Bevel.BevelEdges method not found or failed in ProBuilder assembly.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            // API 返回的数量不是完成证明；倒角后必须从刷新的实际拓扑确认变更。
            var freshMesh = ProBuilderOperationProof.ResolveFreshMesh(request.Target, go);
            var after = ProBuilderOperationProof.Capture(freshMesh, edges: edgeArray);
            var afterVertexCount = ProBuilderReflection.GetVertexCount(freshMesh);
            var afterFaceCount = ProBuilderReflection.GetFaceCount(freshMesh);
            var afterEdgeCount = ProBuilderReflection.GetEdgeCount(freshMesh);
            if (!ProBuilderOperationProof.HasTopologyExpansion(before, after) || afterVertexCount < 0 || afterFaceCount < 0 || afterEdgeCount < 0
                || (afterVertexCount == beforeVertexCount && afterFaceCount == beforeFaceCount && afterEdgeCount == beforeEdgeCount))
                throw ProBuilderErrors.OperationFailed("bevel-edges", "Bevel did not produce observable mesh topology changes.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result { ModifiedEdgeCount = modifiedEdgeCount });
        }
    }
}
#endif
