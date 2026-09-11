#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animator-controller.assign — 将 Controller 分配到目标 Animator。</summary>
    public class AnimatorControllerAssignTool : IUnityCliTool<AnimatorControllerAssignTool.Request, AnimatorControllerAssignTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string Target { get; set; } }
        public sealed class Result { public string ControllerName { get; set; } public string ControllerPath { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator-controller.assign", Summary = "Assign an AnimatorController asset to a target GameObject's Animator.", Description = "Resolve a target GameObject, add an Animator if missing, and assign the controller asset.", Group = "animator-controller",DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Assign controller", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", Target = "Player" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with animator-controller.create.");
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "animator-controller.assign requires target.");
            var animator = go.GetComponent<Animator>();
            ctx.CommitEffect();
            if (animator == null) animator = UnityCliSceneMutation.AddComponent<Animator>(go);
            UnityCliSceneMutation.RecordObject(animator, "Assign AnimatorController");
            animator.runtimeAnimatorController = controller;
            UnityCliSceneMutation.MarkSceneObjectDirty(animator);
            UnityCliSceneMutation.MarkSceneDirty(go.scene);
            var observedTarget = EditorUtility.InstanceIDToObject(go.GetInstanceID()) as GameObject;
            var observedAnimator = observedTarget == null ? null : observedTarget.GetComponent<Animator>();
            var observedController = observedAnimator == null ? null : observedAnimator.runtimeAnimatorController;
            var observedPath = observedController == null ? null : AssetDatabase.GetAssetPath(observedController);
            if (observedController != controller || observedPath != path) throw AnimationToolSupport.StateMismatch("E_CONTROLLER_ASSIGN_STATE_MISMATCH", "Animator controller assignment");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { ControllerName = observedController.name, ControllerPath = observedPath, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animator-controller.assign", Path = observedPath, Target = GameObjectToolSupport.HierarchyPath(go) } } });
        }
    }
}
#endif
