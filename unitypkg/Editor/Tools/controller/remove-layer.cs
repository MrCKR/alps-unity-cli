#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>controller.remove-layer — 移除 Controller 层。</summary>
    public class ControllerRemoveLayerTool : IUnityCliTool<ControllerRemoveLayerTool.Request, ControllerRemoveLayerTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public int? LayerIndex { get; set; } public string LayerName { get; set; } }
        public sealed class Result { public string Path { get; set; } public long RemovedLayerIndex { get; set; } public string RemovedLayerName { get; set; } public long RemainingLayerCount { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.remove-layer", Summary = "Remove a layer from an AnimatorController by index or name.", Description = "Remove a layer. The base layer (index 0) cannot be removed. Pass layerIndex or layerName.", Group = "controller", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Remove layer", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", LayerIndex = 1 } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            if (!r.LayerIndex.HasValue && string.IsNullOrEmpty(r.LayerName)) throw new UnityCliBridge.BridgeCommandException("E_LAYER_IDENTIFIER_REQUIRED", "controller.remove-layer requires layerIndex or layerName.", "Pass either a layer index or a layer name.");
            var removeIndex = -1; var removeName = "";
            if (r.LayerIndex.HasValue) { removeIndex = r.LayerIndex.Value; if (removeIndex < 0 || removeIndex >= controller.layers.Length) throw new UnityCliBridge.BridgeCommandException("E_LAYER_NOT_FOUND", "Layer index out of range: " + removeIndex, "Run controller.get-info to inspect layers."); removeName = controller.layers[removeIndex].name; }
            else { for (var i = 0; i < controller.layers.Length; i++) if (controller.layers[i].name == r.LayerName) { removeIndex = i; removeName = r.LayerName; break; } if (removeIndex < 0) throw new UnityCliBridge.BridgeCommandException("E_LAYER_NOT_FOUND", "Layer not found: " + r.LayerName, "Run controller.get-info to inspect layers."); }
            if (removeIndex == 0) throw new UnityCliBridge.BridgeCommandException("E_LAYER_BASE_IMMUTABLE", "The base layer (index 0) cannot be removed.", "Remove a non-base layer instead.");
            Undo.RecordObject(controller, "Remove Layer");
            var layers = new List<AnimatorControllerLayer>(controller.layers); layers.RemoveAt(removeIndex); controller.layers = layers.ToArray();
            EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, RemovedLayerIndex = removeIndex, RemovedLayerName = removeName, RemainingLayerCount = controller.layers.Length, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.remove-layer", Path = path } } });
        }
    }
}
#endif
