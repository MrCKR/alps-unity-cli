#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animation.play — 播放指定 Animator 状态。</summary>
    public class AnimationPlayTool : IUnityCliTool<AnimationPlayTool.Request, AnimationPlayTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string State { get; set; } public int? Layer { get; set; } public float? NormalizedTime { get; set; } }
        public sealed class Result { public AnimatorInfo Animator { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animation.play", Summary = "Play a named Animator state on a target GameObject.", Description = "Resolve a target with an Animator and play the named state on the given layer.", Group = "animation",DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Play Walk state", Input = new Request { Target = "Player", State = "Walk" } } } };
        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animation play requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            var effectiveLayerCount = AnimationToolSupport.EffectiveAnimatorLayerCount(animator);
            if (animator.runtimeAnimatorController == null || effectiveLayerCount <= 0)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_NO_CONTROLLER", "Animator has no RuntimeAnimatorController assigned or no effective layers: " + GameObjectToolSupport.HierarchyPath(go), "Assign a RuntimeAnimatorController with at least one layer.");
            if (string.IsNullOrEmpty(r.State)) throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_STATE_REQUIRED", "Animation play requires --state <name>.", "Pass an Animator state name.");
            var layer = r.Layer ?? 0;
            if (layer < 0 || layer >= effectiveLayerCount) throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_LAYER_INVALID", "Animator layer is out of range: " + layer, "Run animation.state to inspect layer count.");
            var controller = animator.runtimeAnimatorController as AnimatorController;
            var state = controller == null || layer >= controller.layers.Length ? null : AnimationToolSupport.FindStateInLayer(controller.layers[layer].stateMachine, r.State);
            if (state == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_STATE_UNOBSERVABLE", "Animation state cannot be resolved to a stable Animator readback: " + r.State, "Pass a root state name from the selected AnimatorController layer.");
            var expectedHash = Animator.StringToHash(controller.layers[layer].name + "." + state.name);
            ctx.CommitEffect();
            animator.Play(r.State, animator.layerCount > 0 ? layer : 0, r.NormalizedTime ?? 0f);
            animator.Update(0f);
            var observed = await ctx.Wait.UntilStable(() => animator != null && animator.GetCurrentAnimatorStateInfo(layer).fullPathHash == expectedHash, System.TimeSpan.FromMilliseconds(ctx.TimeoutMs), ct: ct);
            if (!observed) throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_STATE_TIMEOUT", "Animator did not reach the requested state before timeout: " + r.State, "Check Animator transitions and retry after the requested state is reachable.");
            UnityCliSceneMutation.MarkSceneObjectDirty(animator);
            var finalAnimator = AnimationToolSupport.BuildAnimatorInfo(animator, layer);
            return new Result { Animator = finalAnimator, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animation.play", Target = GameObjectToolSupport.HierarchyPath(go), State = r.State } } };
        }
    }
}
#endif
