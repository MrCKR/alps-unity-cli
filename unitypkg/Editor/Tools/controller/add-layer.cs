#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>controller.add-layer — 添加层到 Controller。</summary>
    public class ControllerAddLayerTool : IUnityCliTool<ControllerAddLayerTool.Request, ControllerAddLayerTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string LayerName { get; set; } public float? Weight { get; set; } public string BlendingMode { get; set; } }
        public sealed class Result { public string Path { get; set; } public string LayerName { get; set; } public long LayerIndex { get; set; } public double Weight { get; set; } public string BlendingMode { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.add-layer", Summary = "Add a new layer to an AnimatorController.", Description = "Add a layer with a name, weight (default 1), and blending mode (override or additive).", Group = "controller", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add layer", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", LayerName = "UpperBody" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            if (string.IsNullOrEmpty(r.LayerName)) throw new UnityCliBridge.BridgeCommandException("E_LAYER_NAME_REQUIRED", "controller.add-layer requires layerName.", "Pass a name for the new layer.");
            var weight = r.Weight ?? 1f;
            var blendingMode = (r.BlendingMode ?? "override").ToLowerInvariant() == "additive" ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
            Undo.RecordObject(controller, "Add Layer");
            controller.AddLayer(r.LayerName);
            var layers = controller.layers;
            var newLayer = layers[layers.Length - 1];
            newLayer.defaultWeight = weight; newLayer.blendingMode = blendingMode;
            layers[layers.Length - 1] = newLayer; controller.layers = layers;
            EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, LayerName = r.LayerName, LayerIndex = layers.Length - 1, Weight = weight, BlendingMode = blendingMode.ToString(), PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.add-layer", Path = path } } });
        }
    }
}
#endif
