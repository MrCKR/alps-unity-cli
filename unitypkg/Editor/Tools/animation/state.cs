#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animation.state — 读取目标 Animator 的当前状态信息。</summary>
    public class AnimationStateTool : IUnityCliTool<AnimationStateTool.Request, AnimationStateTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public int? Layer { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public AnimatorInfo Animator { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animation.state", Summary = "Read the current Animator state for a target GameObject.", Description = "Resolve a target with an Animator and return current state info (layer, state hash, normalized time).", Group = "animation", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read animation state", Input = new Request { Target = "Player" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animation state requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            var layer = Mathf.Clamp(r.Layer ?? 0, 0, Mathf.Max(0, AnimationToolSupport.EffectiveAnimatorLayerCount(animator) - 1));
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Animator = AnimationToolSupport.BuildAnimatorInfo(animator, layer) });
        }
    }
}
#endif
