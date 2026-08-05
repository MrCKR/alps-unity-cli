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
    /// probuilder.flip-normals — 翻转 ProBuilder 网格选定面的法线。
    /// </summary>
    public class FlipNormalsTool : IUnityCliTool<FlipNormalsTool.Request, FlipNormalsTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
        }

        public sealed class Result
        {
            public long FlippedCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.flip-normals",
            Summary = "Flip normals of selected faces on a ProBuilder mesh.",
            Description = "Flip the normals of selected faces on a ProBuilder mesh. When faceIndices is omitted, all faces are flipped. Complete terminal verification requires a Renderer with non-empty sharedMaterials, valid face submesh indices, and every material slot used by at least one face. UV0 must be complete for every raw vertex or absent for the whole mesh; partial UV0 profiles are rejected.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "翻转面 0 法线", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 } } }
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
            if (mesh == null) throw ProBuilderErrors.OperationFailed("flip-normals", "No ProBuilderMesh on target.");
            // The canonical snapshot freezes target/component identity before the effect.

            var typedMesh = mesh as ProBuilderMesh;
            var faceCount = typedMesh == null ? -1 : typedMesh.faces.Count;
            if (faceCount < 1)
                throw ProBuilderErrors.OperationFailed("flip-normals", "Target has no readable faces.");
            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).ToArray();
            if (faceIndices == null || faceIndices.Length == 0)
                faceIndices = Enumerable.Range(0, faceCount).ToArray();
            // 同一 occurrence 仅消费一次，保留首次请求顺序。
            faceIndices = faceIndices.Distinct().ToArray();
            ProBuilderOperationProof.ValidateFaceIndices(mesh, faceIndices);
            // effect 前按请求顺序冻结 exact occurrence；后续不得再按 index 解析选区。
            var frozenFaces = faceIndices.Select(index => typedMesh.faces[index]).ToArray();
            var plan = ProBuilderFlipCompletionProof.CaptureFlip(go, frozenFaces, faceIndices);
            if (plan == null) throw ProBuilderErrors.OperationFailed("flip-normals", "Requested complete terminal state is not readable.");
            ctx.CommitEffect();
            try
            {
            Undo.RecordObject(go, "ProBuilder Flip Normals");
            try { foreach (var face in plan.FrozenFaces) face.Reverse(); }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw ProBuilderErrors.OutcomeUnknown("flip-normals", "invoke", "Face.Reverse threw after the effect barrier."); }

            if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(typedMesh))
                throw ProBuilderErrors.OutcomeUnknown("flip-normals", "readback", "ProBuilder mesh payload-preserving refresh failed after the effect.");
            EditorSceneManager.MarkSceneDirty(go.scene);
            try
            {
                if (!ProBuilderFlipCompletionProof.FlipMatches(plan, target))
                    throw ProBuilderErrors.OutcomeUnknown("flip-normals", "postcondition", "The complete Face.Reverse terminal state was not proven by fresh readback.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception)
            {
                throw ProBuilderErrors.OutcomeUnknown("flip-normals", "postcondition", "Flip terminal-state readback threw after the effect barrier.");
            }

            var flipped = plan.FrozenFaces.Length;
                return new System.Threading.Tasks.ValueTask<Result>(new Result { FlippedCount = flipped });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("flip-normals", "postcondition", exception.Message); }
        }
    }
}
#endif
