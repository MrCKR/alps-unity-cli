#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>controller.add-blend-tree-child — 向 BlendTree 添加子动画。</summary>
    public class ControllerAddBlendTreeChildTool : IUnityCliTool<ControllerAddBlendTreeChildTool.Request, ControllerAddBlendTreeChildTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string StateName { get; set; } public string ClipPath { get; set; } public int? LayerIndex { get; set; } public float? Threshold { get; set; } public float? PositionX { get; set; } public float? PositionY { get; set; } }
        public sealed class Result { public string Path { get; set; } public string StateName { get; set; } public long ChildCount { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.add-blend-tree-child", Summary = "Add a child motion to an existing BlendTree state.", Description = "Add an AnimationClip as a child to a BlendTree state. threshold for 1D, positionX/positionY for 2D.", Group = "controller", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add blend tree child", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", StateName = "Move", ClipPath = "Assets/Animations/Walk.anim", Threshold = 0.5f } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            var layerIndex = r.LayerIndex ?? 0;
            if (layerIndex < 0 || layerIndex >= controller.layers.Length) throw new UnityCliBridge.BridgeCommandException("E_LAYER_INDEX_INVALID", "Layer index out of range.", "Run controller.get-info to inspect layers.");
            var sm = controller.layers[layerIndex].stateMachine;
            var state = AnimationToolSupport.FindStateInLayer(sm, r.StateName);
            if (state == null) throw new UnityCliBridge.BridgeCommandException("E_STATE_NOT_FOUND", "State '" + r.StateName + "' not found in layer " + layerIndex + ".", "Run controller.get-info to inspect states.");
            var blendTree = state.motion as BlendTree;
            if (blendTree == null) throw new UnityCliBridge.BridgeCommandException("E_NOT_A_BLEND_TREE", "State '" + r.StateName + "' does not have a BlendTree motion.", "Use controller.create-blend-tree-1d or create-blend-tree-2d first.");
            if (string.IsNullOrEmpty(r.ClipPath)) throw new UnityCliBridge.BridgeCommandException("E_CLIP_PATH_REQUIRED", "controller.add-blend-tree-child requires clipPath.", "Pass an Assets/*.anim AnimationClip path.");
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(SceneToolSupport.NormalizeAssetPath(r.ClipPath));
            if (clip == null) throw new UnityCliBridge.BridgeCommandException("E_CLIP_NOT_FOUND", "AnimationClip not found at: " + r.ClipPath, "Create the clip first.");
            ctx.CommitEffect();
            Undo.RecordObject(blendTree, "Add BlendTree Child");
            blendTree.AddChild(clip, r.Threshold ?? 0f);
            if (r.PositionX.HasValue || r.PositionY.HasValue)
            {
                var children = blendTree.children;
                var last = children[children.Length - 1];
                last.position = new Vector2(r.PositionX ?? 0f, r.PositionY ?? 0f);
                children[children.Length - 1] = last;
                blendTree.children = children;
            }
            EditorUtility.SetDirty(blendTree); EditorUtility.SetDirty(controller);
            var observedController = AnimationToolSupport.ReloadControllerAsset(path);
            var observedState = observedController == null ? null : AnimationToolSupport.FindStateInLayer(observedController.layers[layerIndex].stateMachine, r.StateName);
            var observedTree = observedState == null ? null : observedState.motion as BlendTree;
            var observedChildren = observedTree == null ? null : observedTree.children;
            var observedChild = observedChildren == null || observedChildren.Length == 0 ? default(ChildMotion) : observedChildren[observedChildren.Length - 1];
            if (observedChildren == null || observedChild.motion != clip || (r.Threshold.HasValue && !Mathf.Approximately(observedChild.threshold, r.Threshold.Value)) || (r.PositionX.HasValue && !Mathf.Approximately(observedChild.position.x, r.PositionX.Value)) || (r.PositionY.HasValue && !Mathf.Approximately(observedChild.position.y, r.PositionY.Value))) throw AnimationToolSupport.StateMismatch("E_CONTROLLER_BLEND_TREE_CHILD_STATE_MISMATCH", "BlendTree child");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, StateName = observedState.name, ChildCount = observedChildren.Length, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.add-blend-tree-child", Path = path, State = observedState.name } } });
        }
    }
}
#endif
