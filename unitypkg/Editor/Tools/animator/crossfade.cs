#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>对 Animator 执行一次 cross-fade，并通过同层状态读回证明目标已进入过渡或当前状态。</summary>
    public class AnimatorCrossfadeTool : IUnityCliTool<AnimatorCrossfadeTool.Request, AnimatorCrossfadeTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string State { get; set; } public float? Duration { get; set; } public int? Layer { get; set; } }
        public sealed class Readback { public int CurrentStateHash { get; set; } public int NextStateHash { get; set; } public bool InTransition { get; set; } public float NormalizedTime { get; set; } }
        public sealed class Result { public CrossfadeInfo Transition { get; set; } public bool Changed { get; set; } public string NoOpReason { get; set; } public Readback State { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "animator.crossfade",
            Summary = "Cross-fade an Animator to a named state with fresh state readback.",
            Description = "Cross-fade a resolved Animator to a named state. The tool validates the controller, layer and state before one effect, then reads current and next state from the same Animator layer before returning.",
            Group = "animator",DestructiveHint = false,
            SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Crossfade to Run", Input = new Request { Target = "Player", State = "Run", Duration = 0.3f } } }
        };

        /// <summary>解析真实 state hash 后执行一次 CrossFade，并跨帧证明同一 Animator 已进入目标 state。</summary>
        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "Animator crossfade requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            var controller = animator.runtimeAnimatorController as AnimatorController;
            var controllerLayers = controller == null || controller.layers == null ? new AnimatorControllerLayer[0] : controller.layers;
            var effectiveLayerCount = animator.layerCount > 0 ? animator.layerCount : controllerLayers.Length;
            if (controller == null || effectiveLayerCount <= 0)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_NO_CONTROLLER", "Animator has no observable AnimatorController: " + GameObjectToolSupport.HierarchyPath(go), "Assign an AnimatorController with at least one layer.");
            if (string.IsNullOrEmpty(request.State))
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_STATE_REQUIRED", "Animator crossfade requires --state <name>.", "Pass an Animator state name.");
            var duration = request.Duration ?? 0.25f;
            var targetLayer = request.Layer ?? 0;
            if (duration < 0f || float.IsNaN(duration) || float.IsInfinity(duration))
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_DURATION_INVALID", "Crossfade duration must be a finite non-negative value.", "Pass duration >= 0.");
            if (targetLayer < 0 || targetLayer >= effectiveLayerCount || targetLayer >= controllerLayers.Length)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_LAYER_INVALID", "Crossfade layer is outside the Animator layer range.", "Pass a valid zero-based layer index.");
            var state = AnimationToolSupport.FindStateInLayer(controllerLayers[targetLayer].stateMachine, request.State);
            if (state == null)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_STATE_UNOBSERVABLE", "Animator state cannot be resolved in the requested layer: " + request.State, "Pass a state name from the selected AnimatorController layer.");

            var expectedHash = Animator.StringToHash(controllerLayers[targetLayer].name + "." + state.name);
            var instanceId = go.GetInstanceID();
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
            var sceneHandle = go.scene.handle;
            var before = Observe(animator, targetLayer, expectedHash);
            if (before != null)
                return BuildResult(go, request.State, duration, targetLayer, false, "target-state-already-current-or-next", before, null);

            ctx.CommitEffect();
            try
            {
                if (animator.layerCount <= targetLayer) { animator.Rebind(); animator.Update(0f); }
                animator.CrossFade(request.State, duration, targetLayer);
                animator.Update(0f);
            }
            catch (System.Exception exception)
            {
                throw Unknown("invoke", exception.Message);
            }

            var reached = await ctx.Wait.UntilStable(() => Observe(animator, targetLayer, expectedHash) != null,
                System.TimeSpan.FromMilliseconds(ctx.TimeoutMs), minimumFrames: 2, ct: ct);
            if (!reached) throw Unknown("postcondition", "Animator did not reach a stable target-state readback before the deadline.");

            var freshGo = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            if (freshGo == null || freshGo.scene.handle != sceneHandle
                || !string.Equals(GlobalObjectId.GetGlobalObjectIdSlow(freshGo).ToString(), globalId, StringComparison.Ordinal))
                throw Unknown("readback", "Animator target identity changed after CrossFade.");
            var freshAnimator = freshGo.GetComponent<Animator>();
            var readback = Observe(freshAnimator, targetLayer, expectedHash);
            if (readback == null) throw Unknown("postcondition", "Fresh Animator state no longer satisfies the requested transition.");
            return BuildResult(freshGo, request.State, duration, targetLayer, true, null, readback,
                new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animator.crossfade", Target = GameObjectToolSupport.HierarchyPath(freshGo), State = request.State } });
        }

        /// <summary>目标可位于 current 或 transition next；normalized time 必须是 fresh 有限值。</summary>
        private static Readback Observe(Animator animator, int layer, int expectedHash)
        {
            if (animator == null || layer < 0 || layer >= animator.layerCount) return null;
            var current = animator.GetCurrentAnimatorStateInfo(layer);
            var next = animator.GetNextAnimatorStateInfo(layer);
            var inTransition = animator.IsInTransition(layer);
            var currentMatches = current.fullPathHash == expectedHash;
            var nextMatches = inTransition && next.fullPathHash == expectedHash;
            if (!currentMatches && !nextMatches) return null;
            var normalizedTime = nextMatches ? next.normalizedTime : current.normalizedTime;
            if (float.IsNaN(normalizedTime) || float.IsInfinity(normalizedTime) || normalizedTime < 0f) return null;
            return new Readback { CurrentStateHash = current.fullPathHash, NextStateHash = next.fullPathHash, InTransition = inTransition, NormalizedTime = normalizedTime };
        }

        private static Result BuildResult(GameObject go, string state, float duration, int layer, bool changed, string noOpReason, Readback readback, List<AnimationPlannedEffect> effects)
        {
            return new Result {
                Transition = new CrossfadeInfo { State = state, Duration = duration, Layer = layer }, Changed = changed,
                NoOpReason = noOpReason, State = readback, PlannedEffects = effects
            };
        }

        private static UnityCliBridge.BridgeCommandException Unknown(string stage, string cause)
        {
            return new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "Animator.CrossFade result cannot be confirmed; inspect the business state before deciding whether to retry.",
                new Dictionary<string, object> { { "stage", stage }, { "cause", cause } }, new[] { "Inspect the Animator state before submitting a new request." });
        }
    }
}
#endif
