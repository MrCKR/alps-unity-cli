#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animation.stop — 停止/重置目标 Animator。</summary>
    public class AnimationStopTool : IUnityCliTool<AnimationStopTool.Request, AnimationStopTool.Result>
    {
        public sealed class Request { public string Target { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public AnimatorInfo Animator { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animation.stop", Summary = "Stop and rebind an Animator on a target GameObject.", Description = "Call Animator.Rebind() and Update(0) to reset the Animator to its initial state.", Group = "animation", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Stop animation", Input = new Request { Target = "Player" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animation stop requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            animator.Rebind(); animator.Update(0f); UnityCliSceneMutation.MarkSceneObjectDirty(animator);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Animator = AnimationToolSupport.BuildAnimatorInfo(animator, 0), PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animation.stop", Target = GameObjectToolSupport.HierarchyPath(go) } } });
        }
    }
}
#endif
