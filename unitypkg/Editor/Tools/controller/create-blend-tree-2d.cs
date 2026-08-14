#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>controller.create-blend-tree-2d — 创建 2D BlendTree 状态。</summary>
    public class ControllerCreateBlendTree2DTool : IUnityCliTool<ControllerCreateBlendTree2DTool.Request, ControllerCreateBlendTree2DTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string StateName { get; set; } public string BlendParameterX { get; set; } public string BlendParameterY { get; set; } public int? LayerIndex { get; set; } public string BlendType { get; set; } }
        public sealed class Result { public string Path { get; set; } public string StateName { get; set; } public string BlendType { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.create-blend-tree-2d", Summary = "Create a 2D BlendTree state in an AnimatorController layer.", Description = "Add a new state with a 2D BlendTree motion. blendType: simpledirectional2d, simpledirectional, freesystemlessdirectional.", Group = "controller",DestructiveHint = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create 2D blend tree", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", StateName = "Locomotion", BlendParameterX = "SpeedX", BlendParameterY = "SpeedY" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            if (string.IsNullOrEmpty(r.StateName)) throw new UnityCliBridge.BridgeCommandException("E_STATE_NAME_REQUIRED", "controller.create-blend-tree-2d requires stateName.", "Pass a name for the blend tree state.");
            var layerIndex = r.LayerIndex ?? 0;
            if (layerIndex < 0 || layerIndex >= controller.layers.Length) throw new UnityCliBridge.BridgeCommandException("E_LAYER_INDEX_INVALID", "Layer index out of range.", "Run controller.get-info to inspect layers.");
            var sm = controller.layers[layerIndex].stateMachine;
            if (AnimationToolSupport.FindStateInLayer(sm, r.StateName) != null) throw new UnityCliBridge.BridgeCommandException("E_STATE_EXISTS", "State '" + r.StateName + "' already exists.", "Choose a different state name.");
            var bt = (r.BlendType ?? "simpledirectional2d").ToLowerInvariant();
            var blendType = bt == "freedirectional" || bt == "freesystemlessdirectional" ? BlendTreeType.FreeformDirectional2D : bt == "freecartesian" || bt == "freesystemlesscartesian" ? BlendTreeType.FreeformCartesian2D : BlendTreeType.SimpleDirectional2D;
            ctx.CommitEffect();
            Undo.RecordObject(controller, "Create BlendTree 2D");
            var blendTree = new BlendTree { name = r.StateName, blendType = blendType, blendParameter = r.BlendParameterX ?? "", blendParameterY = r.BlendParameterY ?? "" };
            AssetDatabase.AddObjectToAsset(blendTree, controller);
            var state = sm.AddState(r.StateName); state.motion = blendTree;
            EditorUtility.SetDirty(controller);
            var observedController = AnimationToolSupport.ReloadControllerAsset(path);
            var observedState = observedController == null ? null : AnimationToolSupport.FindStateInLayer(observedController.layers[layerIndex].stateMachine, r.StateName);
            var observedTree = observedState == null ? null : observedState.motion as BlendTree;
            if (observedTree == null || observedTree.blendType != blendType || observedTree.blendParameter != (r.BlendParameterX ?? "") || observedTree.blendParameterY != (r.BlendParameterY ?? "")) throw AnimationToolSupport.StateMismatch("E_CONTROLLER_BLEND_TREE_2D_STATE_MISMATCH", "2D BlendTree");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, StateName = observedState.name, BlendType = observedTree.blendType.ToString(), PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.create-blend-tree-2d", Path = path, State = observedState.name } } });
        }
    }
}
#endif
