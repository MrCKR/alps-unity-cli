#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>controller.add-state — 在指定层添加状态。</summary>
    public class ControllerAddStateTool : IUnityCliTool<ControllerAddStateTool.Request, ControllerAddStateTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string StateName { get; set; } public string ClipPath { get; set; } public int? LayerIndex { get; set; } public float? Speed { get; set; } public bool? IsDefault { get; set; } }
        public sealed class Result { public string Path { get; set; } public string StateName { get; set; } public long LayerIndex { get; set; } public bool HasMotion { get; set; } public string MotionName { get; set; } public double Speed { get; set; } public bool IsDefault { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.add-state", Summary = "Add a new state to an AnimatorController layer.", Description = "Add a state to the specified layer. Optionally assign a motion clip and set as default state.", Group = "controller", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add MatrixWalk state", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", StateName = "MatrixWalk" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            if (string.IsNullOrEmpty(r.StateName)) throw new UnityCliBridge.BridgeCommandException("E_STATE_NAME_REQUIRED", "controller.add-state requires stateName.", "Pass a name for the new state.");
            var layerIndex = r.LayerIndex ?? 0;
            if (layerIndex < 0 || layerIndex >= controller.layers.Length) throw new UnityCliBridge.BridgeCommandException("E_LAYER_INDEX_INVALID", "Layer index out of range.", "Run controller.get-info to inspect available layers.");
            var sm = controller.layers[layerIndex].stateMachine;
            if (AnimationToolSupport.FindStateInLayer(sm, r.StateName) != null) throw new UnityCliBridge.BridgeCommandException("E_STATE_EXISTS", "State '" + r.StateName + "' already exists in layer " + layerIndex + ".", "Choose a different state name.");
            AnimationClip clip = null;
            if (!string.IsNullOrEmpty(r.ClipPath)) { clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(SceneToolSupport.NormalizeAssetPath(r.ClipPath)); if (clip == null) throw new UnityCliBridge.BridgeCommandException("E_CLIP_NOT_FOUND", "AnimationClip not found at: " + r.ClipPath, "Create the clip first or omit clipPath."); }
            ctx.CommitEffect();
            Undo.RecordObject(controller, "Add Animator State");
            var state = sm.AddState(r.StateName);
            if (clip != null) state.motion = clip;
            state.speed = r.Speed ?? 1f;
            var isDefault = r.IsDefault ?? false;
            if (isDefault) sm.defaultState = state;
            EditorUtility.SetDirty(controller);
            var observedController = AnimationToolSupport.ReloadControllerAsset(path);
            var observedState = observedController == null || layerIndex >= observedController.layers.Length ? null : AnimationToolSupport.FindStateInLayer(observedController.layers[layerIndex].stateMachine, r.StateName);
            if (observedState == null || !Mathf.Approximately(observedState.speed, r.Speed ?? 1f) || (clip != null && observedState.motion != clip) || (isDefault && observedController.layers[layerIndex].stateMachine.defaultState != observedState))
                throw AnimationToolSupport.StateMismatch("E_CONTROLLER_ADD_STATE_STATE_MISMATCH", "AnimatorController state");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, StateName = observedState.name, LayerIndex = layerIndex, HasMotion = observedState.motion != null, MotionName = observedState.motion?.name, Speed = observedState.speed, IsDefault = observedController.layers[layerIndex].stateMachine.defaultState == observedState, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.add-state", Path = path, State = observedState.name } } });
        }
    }
}
#endif
