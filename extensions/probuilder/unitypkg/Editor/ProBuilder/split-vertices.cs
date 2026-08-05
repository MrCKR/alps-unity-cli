#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace UnityCli.ProBuilder
{
    public class SplitVerticesTool : IUnityCliTool<SplitVerticesTool.Request, SplitVerticesTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public List<long> VertexIndices { get; set; } }
        public sealed class Result { public long SplitCount { get; set; } }
        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.split-vertices",
            Summary = "Split selected shared vertices.",
            Description = "Split at least one unique valid ProBuilder vertex index.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "拆分唯一共享 raw 顶点", Input = new Request { Target = "TwoTrianglesSharedCorner", VertexIndices = new List<long> { 0 } } }
            }
        };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var go = ProBuilderInput.ResolveTarget(request.Target); if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target); var mesh = ProBuilderReflection.GetProBuilderMesh(go); if (mesh == null) throw ProBuilderErrors.OperationFailed("split-vertices", "No ProBuilderMesh on target.");
            var indices = request.VertexIndices?.Select(value => ProBuilderOperationProof.CheckedInt(value, "vertexIndices")).ToArray();
            var vertexCount = ProBuilderReflection.GetVertexCount(mesh);
            if (indices == null || indices.Length == 0 || indices.Distinct().Count() != indices.Length || vertexCount < 0 || indices.Any(value => value < 0 || value >= vertexCount)) throw ProBuilderErrors.SchemaInvalid("vertexIndices must contain unique valid vertices.");
            var plan = ProBuilderAtomicReadback.CaptureSplitVertexClaim(mesh, indices);
            if (plan == null) throw ProBuilderErrors.OperationFailed("split-vertices", "Requested shared vertices are unreadable.");
            if (!plan.HasObservableEffect) return new System.Threading.Tasks.ValueTask<Result>(new Result { SplitCount = 0 });
            var id = go.GetInstanceID();
            ctx.CommitEffect();
            int split;
            try
            {
                ProBuilderAtomicReadback.RegisterMeshUndo(go, mesh, "ProBuilder Split Vertices");
                split = ProBuilderReflection.SplitVerticesWithList(mesh, indices);
                if (split < 0) throw ProBuilderErrors.OutcomeUnknown("split-vertices", "invoke", "SplitVertices failed.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("split-vertices", "invoke", ex.Message); }
            try
            {
                if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(mesh)) throw ProBuilderErrors.OutcomeUnknown("split-vertices", "readback", "ProBuilder refresh failed.");
                EditorSceneManager.MarkSceneDirty(go.scene);
                var fresh = ProBuilderReflection.GetProBuilderMesh(EditorUtility.InstanceIDToObject(id) as GameObject);
                if (!ProBuilderAtomicReadback.SplitVerticesMatch(fresh, plan)) throw ProBuilderErrors.OutcomeUnknown("split-vertices", "postcondition", "Fresh shared-vertex partitions did not confirm the requested split.");
                return new System.Threading.Tasks.ValueTask<Result>(new Result { SplitCount = split });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("split-vertices", "readback", ex.Message); }
        }
    }
}
#endif
