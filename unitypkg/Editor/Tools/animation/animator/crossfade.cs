#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>仅由 preserved batch delivery 使用的 Animator cross-fade handler。</summary>
    public class AnimatorCrossfadeTool : IUnityCliTool<AnimatorCrossfadeTool.Request, AnimatorCrossfadeTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string State { get; set; } public float? Duration { get; set; } public int? Layer { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public CrossfadeInfo Transition { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "animator.crossfade",
            Summary = "Cross-fade an Animator only inside the frozen batch delivery.",
            Description = "Ordinary direct invocation is refused because Animator.CrossFade has no reliable synchronous business-state proof. The existing frozen batch delivery may cross-fade a resolved Animator to a named state.",
            Group = "animation", Version = "1.0.0", DestructiveHint = false,
            SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Crossfade to Run in a batch step", Input = new Request { Target = "Player", State = "Run", Duration = 0.3f } } }
        };

        /// <summary>完成目标、controller、state、duration 和 layer 校验后进入单一 batch effect boundary。</summary>
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "Animator crossfade requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            var controller = animator.runtimeAnimatorController as AnimatorController;
            var controllerLayers = controller == null || controller.layers == null ? new AnimatorControllerLayer[0] : controller.layers;
            var effectiveLayerCount = animator.layerCount > 0 ? animator.layerCount : controllerLayers.Length;
            if (animator.runtimeAnimatorController == null || effectiveLayerCount <= 0)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_NO_CONTROLLER", "Animator has no RuntimeAnimatorController: " + GameObjectToolSupport.HierarchyPath(go), "Assign a RuntimeAnimatorController with at least one layer.");
            if (string.IsNullOrEmpty(request.State))
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_STATE_REQUIRED", "Animator crossfade requires --state <name>.", "Pass an Animator state name.");
            var duration = request.Duration ?? 0.25f;
            var layer = request.Layer ?? -1;
            if (duration < 0f || float.IsNaN(duration) || float.IsInfinity(duration))
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_DURATION_INVALID", "Crossfade duration must be a finite non-negative value.", "Pass duration >= 0.");
            if (layer < -1 || layer >= effectiveLayerCount)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_LAYER_INVALID", "Crossfade layer is outside the Animator layer range.", "Pass -1 or a valid zero-based layer index.");

            ctx.CommitEffect();
            if (animator.layerCount <= 0) { animator.Rebind(); animator.Update(0f); }
            animator.CrossFade(request.State, duration, layer);
            animator.Update(0f);
            UnityCliSceneMutation.MarkSceneObjectDirty(animator);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) },
                Transition = new CrossfadeInfo { State = request.State, Duration = duration, Layer = layer },
                PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animator.crossfade", Target = GameObjectToolSupport.HierarchyPath(go), State = request.State } }
            });
        }
    }
}
#endif
