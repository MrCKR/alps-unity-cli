#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.append-vertices-to-edge — 沿选定边追加新顶点。
    /// </summary>
    public class AppendVerticesToEdgeTool : IUnityCliTool<AppendVerticesToEdgeTool.Request, AppendVerticesToEdgeTool.Result>
    {
        // ── 协议类型 ──
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public long? Count { get; set; }                       // 可选,默认 1
            public List<long> EdgeIndices { get; set; }            // 可选,边索引列表
            public List<EdgeRef> Edges { get; set; }               // 可选,顶点对边列表
        }

        /// <summary>边引用(顶点索引对)。</summary>
        public sealed class EdgeRef
        {
            public long A { get; set; }
            public long B { get; set; }
        }

        public sealed class Result
        {
            public long InsertedCount { get; set; }
        }

        // ── desc ──
        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.append-vertices-to-edge",
            Summary = "Append new vertices along selected edges of a ProBuilder mesh.",
            Description = "Append new vertices along an edge of a ProBuilder mesh. Specify edges by edgeIndices (indices into the unique edge list) or by vertex-pair edges [{a,b}]. Requires at least one edge selection.",
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
                new ToolExample { Description = "沿边 0 追加 1 个顶点", Input = new Request { Target = "MyCube", Count = 1, EdgeIndices = new List<long> { 0 } } }
            }
        };

        // ── handler ──
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("append-vertices-to-edge", "No ProBuilderMesh on target.");

            var count = ProBuilderOperationProof.CheckedInt(request.Count ?? 1, "count");
            int[] edgeIndices = request.EdgeIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "edgeIndices")).ToArray();
            ProBuilderInput.EdgePair[] edgePairs = request.Edges?.Select(e => new ProBuilderInput.EdgePair { a = ProBuilderOperationProof.CheckedInt(e.A, "edges.a"), b = ProBuilderOperationProof.CheckedInt(e.B, "edges.b") }).ToArray();

            if ((edgeIndices == null || edgeIndices.Length == 0) && (edgePairs == null || edgePairs.Length == 0))
                throw ProBuilderErrors.SchemaInvalid("edgeIndices or edges parameter is required for append-vertices-to-edge.");

            if (count < 1)
                throw ProBuilderErrors.SchemaInvalid("count must be at least 1.");
            var edgeArray = ProBuilderOperationProof.ValidateEdges(mesh, edgeIndices, edgePairs);
            var before = ProBuilderOperationProof.Capture(mesh, edges: edgeArray);
            if (before == null) throw ProBuilderErrors.OperationFailed("append-vertices-to-edge", "Requested edge geometry is not readable.");
            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Append Vertices");

            var appended = ProBuilderReflection.AppendVerticesToEdge(mesh, edgeArray, count);
            if (!appended)
                throw ProBuilderErrors.OperationFailed("append-vertices-to-edge", "AppendElements.AppendVerticesToEdge method not found or failed in ProBuilder assembly.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var freshMesh = ProBuilderOperationProof.ResolveFreshMesh(request.Target, go);
            var after = ProBuilderOperationProof.Capture(freshMesh, edges: edgeArray);
            if (!ProBuilderOperationProof.HasInsertedVertices(before, after))
                throw ProBuilderErrors.OperationFailed("append-vertices-to-edge", "Requested edge append did not add readable vertices.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result { InsertedCount = count * edgeArray.Length });
        }
    }
}
#endif
