#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;

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
            Description = "Bevel one frozen typed edge of a ProBuilder mesh by a given amount. Specify the edge by edgeIndices (an index into the unique edge list) or by one vertex-pair edge [{a,b}]. The target must use the current ProBuilder 5.2.4 serialized mesh format with initialized sharedTextures and contain exactly two non-degenerate triangular face occurrences sharing that edge; each endpoint may occur only on those faces, with no additional raw, face, or position coincidence. Its Renderer must have nonempty sharedMaterials, every face submeshIndex must be in that range, and every material slot must be used so ToMesh cannot compact slots. The 5.2.4 amount clamp must be bit-exactly the request and at least 0.001; all other requests are rejected before scene mutation because their terminal topology cannot be uniquely proven.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "对两三角面共边倒角", Input = new Request { Target = "MyTwoTriangleWedge", Amount = 0.1, Edges = new List<EdgeRef> { new EdgeRef { A = 0, B = 1 } } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var target = request.Target;
            var go = ProBuilderInput.ResolveTarget(target);
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
            var frozenEdges = edgeArray as Edge[];
            var typedMesh = mesh as ProBuilderMesh;
            if (frozenEdges == null || typedMesh == null || frozenEdges.Length != edgeArray.Length)
                throw ProBuilderErrors.SchemaInvalid("bevel-edges requires a typed ProBuilder Edge[] selection.");
            var plan = ProBuilderBevelCompletionProof.Capture(go, typedMesh, frozenEdges, amount);
            if (plan == null)
                throw ProBuilderErrors.SchemaInvalid("bevel-edges only accepts the deterministic 5.2.4 two-sided manifold domain; this request cannot be uniquely planned before mutation.");
            ctx.CommitEffect();
            try
            {
            Undo.RecordObject(go, "ProBuilder Bevel Edges");
            try { Bevel.BevelEdges(typedMesh, plan.FrozenEdges, amount); }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw ProBuilderErrors.OutcomeUnknown("bevel-edges", "invoke", "Bevel.BevelEdges threw after the effect barrier."); }

            if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(typedMesh))
                throw ProBuilderErrors.OutcomeUnknown("bevel-edges", "readback", "ProBuilder mesh payload-preserving refresh failed after effect.");
            EditorSceneManager.MarkSceneDirty(go.scene);

            try
            {
                // effect 前 identity fresh-resolve 后完整 capture；任何比较或 capture 异常都是未知终态。
                var freshMesh = CanonicalMeshSnapshot.ResolveFreshMesh(target, plan.Before.Identity);
                var after = freshMesh == null ? null : CanonicalMeshSnapshot.Capture(freshMesh.gameObject);
                if (!ProBuilderBevelCompletionProof.Matches(plan, after))
                {
                    var mismatch = ProBuilderBevelCompletionProof.Mismatch(plan, after) ?? "unknown";
                    throw ProBuilderErrors.OutcomeUnknown("bevel-edges", "postcondition", "Fresh mesh does not equal the frozen 5.2.4 bevel terminal plan at " + mismatch + ".");
                }
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch
            {
                throw ProBuilderErrors.OutcomeUnknown("bevel-edges", "postcondition", "Bevel terminal-state readback threw after the effect barrier.");
            }

                return new System.Threading.Tasks.ValueTask<Result>(new Result { ModifiedEdgeCount = plan.FrozenEdges.Length });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("bevel-edges", "postcondition", exception.Message); }
        }
    }
}
#endif
