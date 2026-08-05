#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;

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
            Description = "Append new vertices along an edge of a ProBuilder mesh. Specify edges by edgeIndices (indices into the unique edge list) or by vertex-pair edges [{a,b}]. Requires at least one edge selection. The mesh must have initialized sharedTextures. Selected edges must not rebuild the same face, and every raw vertex of an affected face must be owned exclusively by that face and not referenced by any retained face; this restriction preserves the deterministic ProBuilder 5.2.4 planning order required for complete readback proof. The target requires a Renderer with nonempty sharedMaterials, every face submeshIndex in that material-slot range, and at least one face exactly referencing every material slot; this complete material slot coverage restriction prevents ToMesh from compacting submeshes and reordering materials.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
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

            var target = request.Target;
            var go = ProBuilderInput.ResolveTarget(target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("append-vertices-to-edge", "No ProBuilderMesh on target.");

            var count = ProBuilderOperationProof.CheckedInt(request.Count ?? 1, "count");
            int[] edgeIndices = request.EdgeIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "edgeIndices")).ToArray();
            ProBuilderInput.EdgePair[] edgePairs = request.Edges?.Select(e => new ProBuilderInput.EdgePair { a = ProBuilderOperationProof.CheckedInt(e.A, "edges.a"), b = ProBuilderOperationProof.CheckedInt(e.B, "edges.b") }).ToArray();

            if ((edgeIndices == null || edgeIndices.Length == 0) && (edgePairs == null || edgePairs.Length == 0))
                throw ProBuilderErrors.SchemaInvalid("edgeIndices or edges parameter is required for append-vertices-to-edge.");

            if (count < 1 || count > 512)
                throw ProBuilderErrors.SchemaInvalid("count must be between 1 and 512 (the ProBuilder 5.2.4 AppendElements limit).");
            var edgeArray = ProBuilderOperationProof.ValidateEdges(mesh, edgeIndices, edgePairs);
            var frozenEdges = edgeArray.Cast<Edge>().Select(edge => new Edge(edge.a, edge.b)).ToArray();
            var plan = ProBuilderAppendCompletionProof.TryCreate(go, mesh as ProBuilderMesh, frozenEdges, count);
            if (plan == null)
                throw ProBuilderErrors.SchemaInvalid("append-vertices-to-edge requires a uniquely ordered ProBuilder 5.2.4 terminal-state plan.");
            ctx.CommitEffect();
            try
            {
            Undo.RecordObject(go, "ProBuilder Append Vertices");

            bool appended;
            // effect 只消费 effect 前冻结的 typed Edge[]；禁止从 mutation 后 mesh 重建选区。
            try { appended = ProBuilderReflection.AppendVerticesToEdge(mesh, plan.FrozenEdges, plan.Count); }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw ProBuilderErrors.OutcomeUnknown("append-vertices-to-edge", "invoke", "AppendElements.AppendVerticesToEdge threw after the effect barrier."); }
            if (!appended)
                throw ProBuilderErrors.OutcomeUnknown("append-vertices-to-edge", "invoke", "AppendElements.AppendVerticesToEdge method not found or failed in ProBuilder assembly.");

            if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(mesh))
                throw ProBuilderErrors.OutcomeUnknown("append-vertices-to-edge", "readback", "ProBuilder mesh payload-preserving refresh failed after effect.");
            EditorSceneManager.MarkSceneDirty(go.scene);
            try
            {
                // complete proof 内部按 effect 前 identity fresh-resolve，并从 freshMesh.gameObject 捕获完整 canonical snapshot。
                if (!ProBuilderAppendCompletionProof.Matches(plan, target))
                    throw ProBuilderErrors.OutcomeUnknown("append-vertices-to-edge", "postcondition", "The complete ProBuilder 5.2.4 append terminal state was not proven by fresh readback.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception)
            {
                throw ProBuilderErrors.OutcomeUnknown("append-vertices-to-edge", "postcondition", "Append terminal-state readback threw after the effect barrier.");
            }

                return new System.Threading.Tasks.ValueTask<Result>(new Result { InsertedCount = plan.InsertedCount });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("append-vertices-to-edge", "postcondition", exception.Message); }
        }
    }
}
#endif
