#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animator.stop — 停止/重置目标 Animator。</summary>
    public class AnimationStopTool : IUnityCliTool<AnimationStopTool.Request, AnimationStopTool.Result>
    {
        public sealed class Request { public string Target { get; set; } }
        public sealed class Result { public AnimatorInfo Animator { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator.stop", Summary = "Stop and rebind an Animator on a target GameObject.", Description = "Call Animator.Rebind() and Update(0) to reset the Animator to its initial state.", Group = "animator",DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Stop animation", Input = new Request { Target = "Player" } } } };
        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animation stop requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            var controller = animator.runtimeAnimatorController as AnimatorController;
            if (controller == null || controller.layers.Length == 0 || controller.layers[0].stateMachine.defaultState == null)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_STATE_UNOBSERVABLE", "Animator has no root-layer default state that can prove reset completion.", "Assign an AnimatorController with a root-layer default state.");
            var defaultState = controller.layers[0].stateMachine.defaultState;
            var expectedHash = Animator.StringToHash(controller.layers[0].name + "." + defaultState.name);
            var targetId = go.GetInstanceID();
            ctx.CommitEffect();
            animator.Rebind();
            animator.Update(0f);
            var observed = await ctx.Wait.UntilStable(() =>
            {
                var target = EditorUtility.InstanceIDToObject(targetId) as GameObject;
                var freshAnimator = target == null ? null : target.GetComponent<Animator>();
                return freshAnimator != null && freshAnimator.GetCurrentAnimatorStateInfo(0).fullPathHash == expectedHash;
            }, System.TimeSpan.FromMilliseconds(ctx.TimeoutMs), ct: ct);
            if (!observed) throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_RESET_TIMEOUT", "Animator did not return to its default state before timeout.", "Inspect the AnimatorController default state and transitions.");
            var observedTarget = EditorUtility.InstanceIDToObject(targetId) as GameObject;
            var observedAnimator = observedTarget == null ? null : observedTarget.GetComponent<Animator>();
            if (observedAnimator == null || observedAnimator.GetCurrentAnimatorStateInfo(0).fullPathHash != expectedHash) throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_RESET_TIMEOUT", "Animator reset proof was not available after completion.", "Inspect the AnimatorController default state and transitions.");
            UnityCliSceneMutation.MarkSceneObjectDirty(observedAnimator);
            var finalAnimator = AnimationToolSupport.BuildAnimatorInfo(observedAnimator, 0);
            return new Result { Animator = finalAnimator, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animator.stop", Target = GameObjectToolSupport.HierarchyPath(observedTarget), State = defaultState.name } } };
        }
    }
}
#endif
