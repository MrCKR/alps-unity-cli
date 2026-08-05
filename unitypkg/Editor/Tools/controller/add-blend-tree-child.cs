#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        public ToolDescriptor Describe() => new() { Name = "controller.add-blend-tree-child", Summary = "Add a child motion to an existing BlendTree state.", Description = "Add an AnimationClip as a child to a BlendTree state. threshold for 1D, positionX/positionY for 2D.", Group = "controller",DestructiveHint = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add blend tree child", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", StateName = "Move", ClipPath = "Assets/Animations/Walk.anim", Threshold = 0.5f } } } };
        /// <summary>按 BlendTree 类型添加 child，并以 GUID、数量增量和请求值证明持久终态。</summary>
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
            var clipPath = SceneToolSupport.NormalizeAssetPath(r.ClipPath);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null) throw new UnityCliBridge.BridgeCommandException("E_CLIP_NOT_FOUND", "AnimationClip not found at: " + r.ClipPath, "Create the clip first.");
            var controllerGuid = AssetDatabase.AssetPathToGUID(path);
            var clipGuid = AssetDatabase.AssetPathToGUID(clipPath);
            if (string.IsNullOrEmpty(controllerGuid) || string.IsNullOrEmpty(clipGuid))
                throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_BLEND_TREE_CHILD_IDENTITY_MISSING", "Controller or clip has no stable asset GUID.", "Reimport the assets before retrying.");

            var isOneDimensional = blendTree.blendType == BlendTreeType.Simple1D;
            var isTwoDimensional = blendTree.blendType == BlendTreeType.SimpleDirectional2D
                || blendTree.blendType == BlendTreeType.FreeformDirectional2D
                || blendTree.blendType == BlendTreeType.FreeformCartesian2D;
            if (!isOneDimensional && !isTwoDimensional)
                throw new UnityCliBridge.BridgeCommandException("E_BLEND_TREE_TYPE_UNSUPPORTED", "controller.add-blend-tree-child supports 1D and 2D BlendTrees only.", "Use a 1D or 2D BlendTree state.");
            if (isOneDimensional && (r.PositionX.HasValue || r.PositionY.HasValue))
                throw new UnityCliBridge.BridgeCommandException("E_BLEND_TREE_CHILD_INPUT_CONFLICT", "positionX/positionY are only valid for 2D BlendTrees.", "Pass threshold for a 1D BlendTree.");
            if (isTwoDimensional && r.Threshold.HasValue)
                throw new UnityCliBridge.BridgeCommandException("E_BLEND_TREE_CHILD_INPUT_CONFLICT", "threshold is only valid for 1D BlendTrees.", "Pass positionX/positionY for a 2D BlendTree.");
            var threshold = r.Threshold ?? 0f;
            var position = new Vector2(r.PositionX ?? 0f, r.PositionY ?? 0f);
            if (float.IsNaN(threshold) || float.IsInfinity(threshold) || float.IsNaN(position.x) || float.IsInfinity(position.x) || float.IsNaN(position.y) || float.IsInfinity(position.y))
                throw new UnityCliBridge.BridgeCommandException("E_BLEND_TREE_CHILD_VALUE_INVALID", "BlendTree child threshold and position must be finite.", "Pass finite numeric values.");
            var beforeChildren = blendTree.children ?? new ChildMotion[0];
            var beforeMatches = beforeChildren.Count(child => MatchesRequestedChild(child, clip, isOneDimensional, threshold, position));

            ctx.CommitEffect();
            Undo.RecordObject(blendTree, "Add BlendTree Child");
            if (isOneDimensional)
            {
                // 显式关闭自动 threshold，防止 Unity 重排后改写请求值。
                blendTree.useAutomaticThresholds = false;
                blendTree.AddChild(clip, threshold);
            }
            else blendTree.AddChild(clip, position);
            EditorUtility.SetDirty(blendTree);
            EditorUtility.SetDirty(controller);

            var observedController = AnimationToolSupport.ReloadControllerAsset(path, controllerGuid);
            var observedClipPath = AssetDatabase.GUIDToAssetPath(clipGuid);
            var observedClip = string.Equals(observedClipPath, clipPath, System.StringComparison.Ordinal) ? AssetDatabase.LoadAssetAtPath<AnimationClip>(observedClipPath) : null;
            var observedState = observedController == null || layerIndex >= observedController.layers.Length ? null : AnimationToolSupport.FindStateInLayer(observedController.layers[layerIndex].stateMachine, r.StateName);
            var observedTree = observedState == null ? null : observedState.motion as BlendTree;
            var observedChildren = observedTree == null ? null : observedTree.children;
            var observedMatches = observedChildren == null || observedClip == null ? 0 : observedChildren.Count(child => MatchesRequestedChild(child, observedClip, isOneDimensional, threshold, position));
            if (observedChildren == null || observedChildren.Length != beforeChildren.Length + 1 || observedMatches != beforeMatches + 1 || (isOneDimensional && observedTree.useAutomaticThresholds))
                throw AnimationToolSupport.StateMismatch("E_CONTROLLER_BLEND_TREE_CHILD_STATE_MISMATCH", "BlendTree child");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, StateName = observedState.name, ChildCount = observedChildren.Length, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.add-blend-tree-child", Path = path, State = observedState.name } } });
        }

        private static bool MatchesRequestedChild(ChildMotion child, AnimationClip clip, bool oneDimensional, float threshold, Vector2 position)
        {
            return child.motion == clip && (oneDimensional
                ? Mathf.Approximately(child.threshold, threshold)
                : Mathf.Approximately(child.position.x, position.x) && Mathf.Approximately(child.position.y, position.y));
        }
    }
}
#endif
