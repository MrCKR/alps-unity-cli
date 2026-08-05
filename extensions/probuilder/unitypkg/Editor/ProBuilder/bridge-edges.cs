#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.bridge-edges — 在两条边之间创建桥接面。
    /// </summary>
    public class BridgeEdgesTool : IUnityCliTool<BridgeEdgesTool.Request, BridgeEdgesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            [UnityCliRequired] public EdgeRef EdgeA { get; set; }
            [UnityCliRequired] public EdgeRef EdgeB { get; set; }
            public bool AllowNonManifold { get; set; }
        }

        public sealed class EdgeRef
        {
            public long A { get; set; }
            public long B { get; set; }
        }

        public sealed class Result
        {
            public long NewFaceCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.bridge-edges",
            Summary = "Bridge two edges on a ProBuilder mesh to create a connecting face.",
            Description = "Create exactly one non-degenerate quad between two existing edges. The edges must resolve to four distinct shared endpoints, may not already bound the same face, and must not require the ProBuilder triangle-bridge branch. UV1 must be absent, and every existing triangle must have UV0 inputs whose full 5.2.4 tangent reciprocal, direction, and raw accumulation arithmetic stays finite. By default each edge must be a boundary edge; allowNonManifold permits one or both edges to have two readable incidences, but all other exact-plan restrictions still apply. The created quad receives a deterministic manual UV0 layout before tangent refresh.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "在闭合网格上桥接两条边", Input = new Request { Target = "MyCube", EdgeA = new EdgeRef { A = 0, B = 1 }, EdgeB = new EdgeRef { A = 5, B = 7 }, AllowNonManifold = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var target = request.Target;
            var allowNonManifold = request.AllowNonManifold;
            var go = ProBuilderInput.ResolveTarget(target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("bridge-edges", "No ProBuilderMesh on target.");

            if (request.EdgeA == null || request.EdgeB == null)
                throw ProBuilderErrors.SchemaInvalid("edgeA and edgeB parameters are required (as {a, b} vertex index pairs).");
            var edgeA = new ProBuilderInput.EdgePair { a = ProBuilderOperationProof.CheckedInt(request.EdgeA.A, "edgeA.a"), b = ProBuilderOperationProof.CheckedInt(request.EdgeA.B, "edgeA.b") };
            var edgeB = new ProBuilderInput.EdgePair { a = ProBuilderOperationProof.CheckedInt(request.EdgeB.A, "edgeB.a"), b = ProBuilderOperationProof.CheckedInt(request.EdgeB.B, "edgeB.b") };
            var edgeArray = ProBuilderOperationProof.ValidateEdges(mesh, null, new[] { edgeA, edgeB });
            var source = ProBuilderEdgeCompletionProof.Capture(go, mesh, edgeArray);
            if (!ProBuilderEdgeCompletionProof.TryPlanBridge(source, allowNonManifold))
                throw ProBuilderErrors.SchemaInvalid("bridge requires two distinct real edges with four shared endpoints and a complete provable quad plan.");
            var frozenEdgeA = edgeArray.GetValue(0); var frozenEdgeB = edgeArray.GetValue(1);
            ctx.CommitEffect();
            try
            {
            bool invoked, bridged;
            try
            {
                Undo.RecordObject(go, "ProBuilder Bridge Edges");
                invoked = ProBuilderReflection.TryBridgeFrozenEdges(mesh, frozenEdgeA, frozenEdgeB, allowNonManifold, out bridged);
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw ProBuilderErrors.OutcomeUnknown("bridge-edges", "invoke", "AppendElements.Bridge threw after the effect barrier."); }
            if (!invoked || !bridged)
                throw ProBuilderErrors.OutcomeUnknown("bridge-edges", "invoke", "AppendElements.Bridge did not create the planned face.");
            if (!ProBuilderOperationProof.ApplyDeterministicQuadUvs(mesh, source[0].Before.Faces.Length, 1))
                throw ProBuilderErrors.OutcomeUnknown("bridge-edges", "invoke", "The created bridge quad could not receive its deterministic manual UV0 layout.");

            try
            {
                if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(mesh)) throw new System.InvalidOperationException();
                EditorSceneManager.MarkSceneDirty(go.scene);
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw ProBuilderErrors.OutcomeUnknown("bridge-edges", "readback", "ProBuilder mesh refresh failed after effect."); }
            var freshMesh = ProBuilderEdgeCompletionProof.ResolveFresh(source[0], target);
            if (freshMesh == null)
                throw ProBuilderErrors.OutcomeUnknown("bridge-edges", "readback", "Fresh target identity could not be resolved.");
            try
            {
                if (!ProBuilderEdgeCompletionProof.BridgeMatches(source, go, freshMesh, allowNonManifold, out var mismatch))
                    throw ProBuilderErrors.OutcomeUnknown("bridge-edges", "postcondition", "Requested bridge face was not proven by fresh topology readback: " + mismatch + ".");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("bridge-edges", "postcondition", exception.Message); }

                return new System.Threading.Tasks.ValueTask<Result>(new Result { NewFaceCount = 1 });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("bridge-edges", "postcondition", exception.Message); }
        }
    }
}
#endif
