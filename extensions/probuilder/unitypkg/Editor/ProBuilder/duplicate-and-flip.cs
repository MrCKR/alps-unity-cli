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
    /// probuilder.duplicate-and-flip — 复制选定面并翻转法线。
    /// </summary>
    public class DuplicateAndFlipTool : IUnityCliTool<DuplicateAndFlipTool.Request, DuplicateAndFlipTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
        }

        public sealed class Result
        {
            public long DuplicatedCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.duplicate-and-flip",
            Summary = "Duplicate selected faces and flip their normals on a ProBuilder mesh.",
            Description = "Duplicate selected faces and flip their normals with the ProBuilder 5.x AppendElements.DuplicateAndFlip API. When faceIndices is omitted, all faces are used. Complete terminal verification requires a Renderer with non-empty sharedMaterials, valid face submesh indices, and every material slot used by at least one face. UV0 must be complete for every raw vertex or absent for the whole mesh; partial UV0 profiles are rejected.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "复制并翻转面 0", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 } } }
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
            if (mesh == null) throw ProBuilderErrors.OperationFailed("duplicate-and-flip", "No ProBuilderMesh on target.");
            // The canonical snapshot freezes target/component identity before the effect.

            var typedMesh = mesh as ProBuilderMesh;
            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).ToArray();
            if (typedMesh == null || typedMesh.faces == null || typedMesh.faces.Count == 0)
                throw ProBuilderErrors.OperationFailed("duplicate-and-flip", "No faces found on target.");
            var requestedFaceIndices = faceIndices == null || faceIndices.Length == 0 ? Enumerable.Range(0, typedMesh.faces.Count).ToArray() : faceIndices;
            // 同一 occurrence 仅消费一次，保留首次请求顺序。
            requestedFaceIndices = requestedFaceIndices.Distinct().ToArray();
            ProBuilderOperationProof.ValidateFaceIndices(mesh, requestedFaceIndices);
            // effect 前按请求顺序冻结 exact occurrence；effect 不得重新解析 index。
            var frozenFaces = requestedFaceIndices.Select(index => typedMesh.faces[index]).ToArray();
            var plan = ProBuilderFlipCompletionProof.CaptureDuplicateAndFlip(go, frozenFaces, requestedFaceIndices);
            if (plan == null) throw ProBuilderErrors.OperationFailed("duplicate-and-flip", "Requested complete terminal state is not readable.");
            ctx.CommitEffect();
            try
            {
            Undo.RecordObject(go, "ProBuilder Duplicate and Flip");
            try { AppendElements.DuplicateAndFlip(typedMesh, plan.FrozenFaces); }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw ProBuilderErrors.OutcomeUnknown("duplicate-and-flip", "invoke", "AppendElements.DuplicateAndFlip threw after the effect barrier."); }

            if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(typedMesh))
                throw ProBuilderErrors.OutcomeUnknown("duplicate-and-flip", "readback", "ProBuilder mesh payload-preserving refresh failed after the effect.");
            EditorSceneManager.MarkSceneDirty(go.scene);
            try
            {
                if (!ProBuilderFlipCompletionProof.DuplicateAndFlipMatches(plan, target))
                    throw ProBuilderErrors.OutcomeUnknown("duplicate-and-flip", "postcondition", "The complete AppendElements.DuplicateAndFlip terminal state was not proven by fresh readback.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception)
            {
                throw ProBuilderErrors.OutcomeUnknown("duplicate-and-flip", "postcondition", "Duplicate-and-flip terminal-state readback threw after the effect barrier.");
            }

                return new System.Threading.Tasks.ValueTask<Result>(new Result { DuplicatedCount = plan.FrozenFaces.Length });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("duplicate-and-flip", "postcondition", exception.Message); }
        }
    }
}
#endif
