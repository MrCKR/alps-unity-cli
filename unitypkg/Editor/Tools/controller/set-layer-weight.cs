#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>controller.set-layer-weight — 设置层权重。</summary>
    public class ControllerSetLayerWeightTool : IUnityCliTool<ControllerSetLayerWeightTool.Request, ControllerSetLayerWeightTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public int? LayerIndex { get; set; } public string LayerName { get; set; } public float? Weight { get; set; } }
        public sealed class Result { public string Path { get; set; } public long LayerIndex { get; set; } public string LayerName { get; set; } public double Weight { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.set-layer-weight", Summary = "Set the default weight of an AnimatorController layer.", Description = "Set the default weight of a layer by index or name. Weight 0 disables the layer.", Group = "controller", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set layer weight", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", LayerIndex = 1, Weight = 0.5f } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            var idx = -1;
            if (r.LayerIndex.HasValue) idx = r.LayerIndex.Value;
            else if (!string.IsNullOrEmpty(r.LayerName)) { for (var i = 0; i < controller.layers.Length; i++) if (controller.layers[i].name == r.LayerName) { idx = i; break; } }
            if (idx < 0 || idx >= controller.layers.Length) throw new UnityCliBridge.BridgeCommandException("E_LAYER_NOT_FOUND", "Layer not found.", "Run controller.get-info to inspect layers.");
            var weight = r.Weight ?? 1f;
            Undo.RecordObject(controller, "Set Layer Weight");
            var layers = controller.layers; layers[idx].defaultWeight = weight; controller.layers = layers;
            EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, LayerIndex = idx, LayerName = controller.layers[idx].name, Weight = weight, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.set-layer-weight", Path = path } } });
        }
    }
}
#endif
