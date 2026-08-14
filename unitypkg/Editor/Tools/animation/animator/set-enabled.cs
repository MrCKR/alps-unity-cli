#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animator.set-enabled — 启用/禁用 Animator 组件。</summary>
    public class AnimatorSetEnabledTool : IUnityCliTool<AnimatorSetEnabledTool.Request, AnimatorSetEnabledTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public bool? Value { get; set; } }
        public sealed class Result { public bool Enabled { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator.set-enabled", Summary = "Enable or disable an Animator component on a target GameObject.", Description = "Resolve a target with an Animator and set animator.enabled. Defaults to true.", Group = "animation",DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Disable animator", Input = new Request { Target = "Player", Value = false } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animator set-enabled requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            var enabled = r.Value ?? true;
            ctx.CommitEffect();
            UnityCliSceneMutation.RecordObject(animator, "Set Animator Enabled");
            animator.enabled = enabled;
            UnityCliSceneMutation.MarkSceneObjectDirty(animator);
            var observedTarget = EditorUtility.InstanceIDToObject(go.GetInstanceID()) as GameObject;
            var observedAnimator = observedTarget == null ? null : observedTarget.GetComponent<Animator>();
            var observedEnabled = observedAnimator != null && observedAnimator.enabled;
            if (observedAnimator == null || observedEnabled != enabled) throw AnimationToolSupport.StateMismatch("E_ANIMATOR_ENABLED_STATE_MISMATCH", "Animator enabled state");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Enabled = observedEnabled, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animator.set-enabled", Target = GameObjectToolSupport.HierarchyPath(go), Value = observedEnabled.ToString().ToLowerInvariant() } } });
        }
    }
}
#endif
