#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animator.crossfade — 交叉渐变到指定状态。</summary>
    public class AnimatorCrossfadeTool : IUnityCliTool<AnimatorCrossfadeTool.Request, AnimatorCrossfadeTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string State { get; set; } public float? Duration { get; set; } public int? Layer { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public CrossfadeInfo Transition { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator.crossfade", Summary = "Cross-fade an Animator to a named state over a duration.", Description = "Resolve a target with an Animator and cross-fade to the named state. duration defaults to 0.25s. layer=-1 uses all layers.", Group = "animation", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Crossfade to Run", Input = new Request { Target = "Player", State = "Run", Duration = 0.3f } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animator crossfade requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            var controller = animator.runtimeAnimatorController as AnimatorController;
            var controllerLayers = controller == null || controller.layers == null ? new AnimatorControllerLayer[0] : controller.layers;
            var effectiveLayerCount = animator.layerCount > 0 ? animator.layerCount : controllerLayers.Length;
            if (animator.runtimeAnimatorController == null || effectiveLayerCount <= 0)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_NO_CONTROLLER", "Animator has no RuntimeAnimatorController: " + GameObjectToolSupport.HierarchyPath(go), "Assign a RuntimeAnimatorController with at least one layer.");
            if (string.IsNullOrEmpty(r.State)) throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_STATE_REQUIRED", "Animator crossfade requires --state <name>.", "Pass an Animator state name.");
            var duration = r.Duration ?? 0.25f;
            var layer = r.Layer ?? -1;
            if (animator.layerCount <= 0) { animator.Rebind(); animator.Update(0f); }
            animator.CrossFade(r.State, duration, layer);
            animator.Update(0f);
            UnityCliSceneMutation.MarkSceneObjectDirty(animator);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Transition = new CrossfadeInfo { State = r.State, Duration = duration, Layer = layer }, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animator.crossfade", Target = GameObjectToolSupport.HierarchyPath(go), State = r.State } } });
        }
    }
}
#endif
