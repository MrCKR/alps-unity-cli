#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.weld-vertices — 在半径内焊接 ProBuilder 网格的顶点。
    /// </summary>
    public class WeldVerticesTool : IUnityCliTool<WeldVerticesTool.Request, WeldVerticesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> VertexIndices { get; set; }           // 可选,空数组则作用于无(传入空)
            public double? Radius { get; set; }                     // 可选,默认 0.01
        }

        public sealed class Result
        {
            public bool Changed { get; set; }
            public string NoOpReason { get; set; }
            public long WeldedCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.weld-vertices",
            Summary = "Weld vertices of a ProBuilder mesh within a given radius.",
            Description = "Weld at least two selected vertices within a radius with the ProBuilder 5.2.4 VertexEditing.WeldVertices API. Radius defaults to 0.01. An effectful weld must have no UV1 because ToMesh clears it; an exact no-op returns before ToMesh. Exact completion proof requires a fully finite predicted terminal and accepts only transitive radial components containing at most two distinct shared handles; larger clusters and non-transitive neighborhoods are rejected before mutation because KdTree accumulation order is not a public stable contract.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "以 0.01 半径焊接顶点 0 和 1", Input = new Request { Target = "MyCube", VertexIndices = new List<long> { 0, 1 }, Radius = 0.01 } }
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
            if (mesh == null) throw ProBuilderErrors.OperationFailed("weld-vertices", "No ProBuilderMesh on target.");
            var identity = ProBuilderOperationProof.CaptureIdentity(go);
            if (identity == null) throw ProBuilderErrors.SchemaInvalid("Target must belong to a valid loaded scene.");

            int[] vertexIndices = request.VertexIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "vertexIndices")).ToArray();
            var vertexCount = ProBuilderReflection.GetVertexCount(mesh);
            if (vertexIndices == null || vertexIndices.Length < 2 || vertexIndices.Distinct().Count() != vertexIndices.Length)
                throw ProBuilderErrors.SchemaInvalid("At least 2 unique vertexIndices are required for weld-vertices.");
            if (vertexCount < 0 || vertexIndices.Any(index => index < 0 || index >= vertexCount))
                throw ProBuilderErrors.SchemaInvalid("vertexIndices contains an invalid vertex index.");
            var radius = ProBuilderOperationProof.CheckedFloat(request.Radius ?? 0.01, "radius");
            if (radius <= 0)
                throw ProBuilderErrors.SchemaInvalid("radius must be greater than 0.");
            var before = ProBuilderVertexObjectCompletionProof.CaptureWeld(mesh, vertexIndices, radius);
            if (before == null)
                throw ProBuilderErrors.SchemaInvalid("An effectful weld requires absent UV1, finite radius arithmetic, and a completely finite readable predicted terminal.");
            if (!ProBuilderVertexObjectCompletionProof.WeldPlanIsUnambiguous(before))
                throw ProBuilderErrors.SchemaInvalid("weld-vertices accepts only transitive radial components of at most two shared handles; larger or non-transitive 5.2.4 KdTree neighborhoods are not uniquely provable.");
            if (ProBuilderVertexObjectCompletionProof.WeldIsNoOp(before))
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Changed = false, NoOpReason = "no-weldable-cluster-or-already-shared", WeldedCount = 0 });

            ctx.CommitEffect();
            try
            {
            int welded;
            try
            {
                Undo.RecordObject(go, "ProBuilder Weld Vertices");
                if (!ProBuilderReflection.TryWeldVerticesWithRadius(mesh, vertexIndices, radius, out welded)) throw ProBuilderErrors.OutcomeUnknown("weld-vertices", "invoke", "ProBuilder 5.x VertexEditing.WeldVertices could not be invoked.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("weld-vertices", "invoke", exception.Message); }
            object freshMesh;
            try
            {
                if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(mesh)) throw ProBuilderErrors.OutcomeUnknown("weld-vertices", "readback", "ProBuilder mesh refresh failed after the effect.");
                EditorSceneManager.MarkSceneDirty(go.scene);
                freshMesh = ProBuilderReflection.GetProBuilderMesh(ProBuilderOperationProof.ResolveFreshObject(target, identity));
                if (freshMesh == null) throw ProBuilderErrors.OutcomeUnknown("weld-vertices", "readback", "Fresh target identity could not be resolved.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("weld-vertices", "readback", exception.Message); }
            try
            {
                if (!ProBuilderVertexObjectCompletionProof.WeldMatches(before, freshMesh, welded)) throw ProBuilderErrors.OutcomeUnknown("weld-vertices", "postcondition", "The complete ProBuilder 5.2.4 weld terminal state was not proven by fresh readback.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("weld-vertices", "postcondition", exception.Message); }
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Changed = true, WeldedCount = welded });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("weld-vertices", "postcondition", exception.Message); }
        }
    }
}
#endif
