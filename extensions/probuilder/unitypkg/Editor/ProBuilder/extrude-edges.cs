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
            Description = "Extrude one or more boundary edges in local space. Specify edges by edgeIndices (indices into the unique edge list) or vertex-pair edges [{a,b}]. Every selected edge must have exactly one readable face incidence; all referenced raw vertices require cached finite normals, UV1 must be absent, every existing triangle requires fully finite 5.2.4 UV0 tangent reciprocal, direction, and raw accumulation arithmetic, distance must be non-zero, and the complete 5.2.4 append terminal must be fully finite and provable before mutation. asGroup defaults to true. Each created side quad receives a deterministic manual UV0 layout before tangent refresh.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "挤出边 0.5 距离", Input = new Request { Target = "MyCube", Edges = new List<EdgeRef> { new EdgeRef { A = 0, B = 1 } }, Distance = 0.5 } }
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
            if (mesh == null) throw ProBuilderErrors.OperationFailed("extrude-edges", "No ProBuilderMesh component on target.");

            var distance = ProBuilderOperationProof.CheckedFloat(request.Distance ?? 0.5, "distance");
            var asGroup = request.AsGroup ?? true;
            int[] edgeIndices = request.EdgeIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "edgeIndices")).ToArray();
            ProBuilderInput.EdgePair[] edgePairs = request.Edges?.Select(e => new ProBuilderInput.EdgePair { a = ProBuilderOperationProof.CheckedInt(e.A, "edges.a"), b = ProBuilderOperationProof.CheckedInt(e.B, "edges.b") }).ToArray();

            if ((edgeIndices == null || edgeIndices.Length == 0) && (edgePairs == null || edgePairs.Length == 0))
                throw ProBuilderErrors.SchemaInvalid("edgeIndices or edges parameter is required for extrude-edges.");

            if (distance == 0f) throw ProBuilderErrors.SchemaInvalid("distance must not be zero.");
            var edgeArray = ProBuilderOperationProof.ValidateEdges(mesh, edgeIndices, edgePairs);
            var source = ProBuilderEdgeCompletionProof.Capture(go, mesh, edgeArray);
            if (!ProBuilderEdgeCompletionProof.TryPlanExtrude(source, distance, asGroup))
                throw ProBuilderErrors.SchemaInvalid("extrude-edges requires boundary edges, cached finite normals, absent UV1, fully finite 5.2.4 UV0 tangent arithmetic for every existing triangle, and a fully finite provable append plan.");
            ctx.CommitEffect();
            try
            {
            bool invoked; int edgeCount;
            try
            {
                Undo.RecordObject(go, "ProBuilder Extrude Edges");
                invoked = ProBuilderReflection.TryExtrudeFrozenEdges(mesh, edgeArray, distance, asGroup, out edgeCount);
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw ProBuilderErrors.OutcomeUnknown("extrude-edges", "invoke", "ProBuilder edge extrusion threw after the effect barrier."); }
            if (!invoked || edgeCount != edgeArray.Length)
                throw ProBuilderErrors.OutcomeUnknown("extrude-edges", "invoke", "ProBuilder edge extrusion did not return every frozen requested edge.");
            if (!ProBuilderOperationProof.ApplyDeterministicQuadUvs(mesh, source[0].Before.Faces.Length, edgeCount))
                throw ProBuilderErrors.OutcomeUnknown("extrude-edges", "invoke", "The created side quads could not receive their deterministic manual UV0 layout.");
            try
            {
                if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(mesh)) throw new System.InvalidOperationException();
                EditorSceneManager.MarkSceneDirty(go.scene);
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw ProBuilderErrors.OutcomeUnknown("extrude-edges", "readback", "ProBuilder mesh refresh failed after effect."); }
            var freshMesh = ProBuilderEdgeCompletionProof.ResolveFresh(source[0], target);
            if (freshMesh == null)
                throw ProBuilderErrors.OutcomeUnknown("extrude-edges", "readback", "Fresh target identity could not be resolved.");
            try
            {
                if (!ProBuilderEdgeCompletionProof.ExtrudeMatches(source, go, freshMesh, distance, asGroup, edgeCount))
                    throw ProBuilderErrors.OutcomeUnknown("extrude-edges", "postcondition", "Requested outer edges and side faces were not proven by fresh readback.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("extrude-edges", "postcondition", exception.Message); }

                return new System.Threading.Tasks.ValueTask<Result>(new Result { ModifiedEdgeCount = edgeCount });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("extrude-edges", "postcondition", exception.Message); }
        }
    }
}
#endif
