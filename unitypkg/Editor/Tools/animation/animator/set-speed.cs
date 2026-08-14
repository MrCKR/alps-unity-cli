#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animator.set-speed — 设置 Animator.speed。</summary>
    public class AnimatorSetSpeedTool : IUnityCliTool<AnimatorSetSpeedTool.Request, AnimatorSetSpeedTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public float? Speed { get; set; } }
        public sealed class Result { public double Speed { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator.set-speed", Summary = "Set the playback speed of an Animator component.", Description = "Resolve a target with an Animator and set animator.speed. Defaults to 1.0.", Group = "animation",DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set speed to 2x", Input = new Request { Target = "Player", Speed = 2f } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animator set-speed requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            var speed = r.Speed ?? 1f;
            ctx.CommitEffect();
            UnityCliSceneMutation.RecordObject(animator, "Set Animator Speed");
            animator.speed = speed;
            UnityCliSceneMutation.MarkSceneObjectDirty(animator);
            var observedTarget = EditorUtility.InstanceIDToObject(go.GetInstanceID()) as GameObject;
            var observedAnimator = observedTarget == null ? null : observedTarget.GetComponent<Animator>();
            var observedSpeed = observedAnimator == null ? 0f : observedAnimator.speed;
            if (observedAnimator == null || !Mathf.Approximately(observedSpeed, speed)) throw AnimationToolSupport.StateMismatch("E_ANIMATOR_SPEED_STATE_MISMATCH", "Animator speed");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Speed = observedSpeed, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animator.set-speed", Target = GameObjectToolSupport.HierarchyPath(go), Value = observedSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture) } } });
        }
    }
}
#endif
