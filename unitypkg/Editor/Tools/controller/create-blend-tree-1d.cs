#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>controller.create-blend-tree-1d — 创建 1D BlendTree 状态。</summary>
    public class ControllerCreateBlendTree1DTool : IUnityCliTool<ControllerCreateBlendTree1DTool.Request, ControllerCreateBlendTree1DTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string StateName { get; set; } public string BlendParameter { get; set; } public int? LayerIndex { get; set; } }
        public sealed class Result { public string Path { get; set; } public string StateName { get; set; } public string BlendParameter { get; set; } public long LayerIndex { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.create-blend-tree-1d", Summary = "Create a 1D BlendTree state in an AnimatorController layer.", Description = "Add a new state with a 1D BlendTree motion using the specified blend parameter.", Group = "controller",DestructiveHint = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create 1D blend tree", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", StateName = "MatrixMove", BlendParameter = "Speed" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            if (string.IsNullOrEmpty(r.StateName)) throw new UnityCliBridge.BridgeCommandException("E_STATE_NAME_REQUIRED", "controller.create-blend-tree-1d requires stateName.", "Pass a name for the blend tree state.");
            var layerIndex = r.LayerIndex ?? 0;
            if (layerIndex < 0 || layerIndex >= controller.layers.Length) throw new UnityCliBridge.BridgeCommandException("E_LAYER_INDEX_INVALID", "Layer index out of range.", "Run controller.get-info to inspect layers.");
            var sm = controller.layers[layerIndex].stateMachine;
            if (AnimationToolSupport.FindStateInLayer(sm, r.StateName) != null) throw new UnityCliBridge.BridgeCommandException("E_STATE_EXISTS", "State '" + r.StateName + "' already exists.", "Choose a different state name.");
            ctx.CommitEffect();
            Undo.RecordObject(controller, "Create BlendTree 1D");
            var blendTree = new BlendTree { name = r.StateName, blendType = BlendTreeType.Simple1D, blendParameter = r.BlendParameter ?? "" };
            AssetDatabase.AddObjectToAsset(blendTree, controller);
            var state = sm.AddState(r.StateName); state.motion = blendTree;
            EditorUtility.SetDirty(controller);
            var observedController = AnimationToolSupport.ReloadControllerAsset(path);
            var observedState = observedController == null ? null : AnimationToolSupport.FindStateInLayer(observedController.layers[layerIndex].stateMachine, r.StateName);
            var observedTree = observedState == null ? null : observedState.motion as BlendTree;
            if (observedTree == null || observedTree.blendType != BlendTreeType.Simple1D || observedTree.blendParameter != (r.BlendParameter ?? "")) throw AnimationToolSupport.StateMismatch("E_CONTROLLER_BLEND_TREE_1D_STATE_MISMATCH", "1D BlendTree");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, StateName = observedState.name, BlendParameter = observedTree.blendParameter, LayerIndex = layerIndex, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.create-blend-tree-1d", Path = path, State = observedState.name } } });
        }
    }
}
#endif
