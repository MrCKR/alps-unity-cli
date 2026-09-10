#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animator-controller.remove-layer — 移除 Controller 层。</summary>
    public class ControllerRemoveLayerTool : IUnityCliTool<ControllerRemoveLayerTool.Request, ControllerRemoveLayerTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public int? LayerIndex { get; set; } public string LayerName { get; set; } }
        public sealed class Result { public string Path { get; set; } public long RemovedLayerIndex { get; set; } public string RemovedLayerName { get; set; } public long RemainingLayerCount { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator-controller.remove-layer", Summary = "Remove a layer from an AnimatorController by index or name.", Description = "Remove a layer. The base layer (index 0) cannot be removed. Pass layerIndex or layerName.", Group = "animator-controller",DestructiveHint = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Remove layer", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", LayerIndex = 1 } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            if (!r.LayerIndex.HasValue && string.IsNullOrEmpty(r.LayerName)) throw new UnityCliBridge.BridgeCommandException("E_LAYER_IDENTIFIER_REQUIRED", "animator-controller.remove-layer requires layerIndex or layerName.", "Pass either a layer index or a layer name.");
            var removeIndex = -1; var removeName = "";
            if (r.LayerIndex.HasValue) { removeIndex = r.LayerIndex.Value; if (removeIndex < 0 || removeIndex >= controller.layers.Length) throw new UnityCliBridge.BridgeCommandException("E_LAYER_NOT_FOUND", "Layer index out of range: " + removeIndex, "Run animator-controller.get-info to inspect layers."); removeName = controller.layers[removeIndex].name; }
            else { for (var i = 0; i < controller.layers.Length; i++) if (controller.layers[i].name == r.LayerName) { removeIndex = i; removeName = r.LayerName; break; } if (removeIndex < 0) throw new UnityCliBridge.BridgeCommandException("E_LAYER_NOT_FOUND", "Layer not found: " + r.LayerName, "Run animator-controller.get-info to inspect layers."); }
            if (removeIndex == 0) throw new UnityCliBridge.BridgeCommandException("E_LAYER_BASE_IMMUTABLE", "The base layer (index 0) cannot be removed.", "Remove a non-base layer instead.");
            var controllerGuid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(controllerGuid)) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController has no stable asset GUID: " + path, "Check the controller asset path and retry.");
            var removedIdentity = AnimationToolSupport.CaptureLayerIdentity(controller.layers[removeIndex]);
            var expectedRemaining = new List<ControllerLayerIdentity>();
            for (var index = 0; index < controller.layers.Length; index++)
                if (index != removeIndex) expectedRemaining.Add(AnimationToolSupport.CaptureLayerIdentity(controller.layers[index]));
            ctx.CommitEffect();
            Undo.RecordObject(controller, "Remove Layer");
            var layers = new List<AnimatorControllerLayer>(controller.layers); layers.RemoveAt(removeIndex); controller.layers = layers.ToArray();
            EditorUtility.SetDirty(controller);
            var observedController = AnimationToolSupport.ReloadControllerAsset(path, controllerGuid);
            var observedLayers = observedController == null ? null : observedController.layers;
            var removalProven = observedLayers != null && observedLayers.Length == expectedRemaining.Count;
            if (removalProven)
            {
                for (var index = 0; index < observedLayers.Length; index++)
                {
                    var observedIdentity = AnimationToolSupport.CaptureLayerIdentity(observedLayers[index]);
                    if (AnimationToolSupport.SameLayerIdentity(observedIdentity, removedIdentity)
                        || !AnimationToolSupport.SameLayerIdentity(observedIdentity, expectedRemaining[index]))
                    {
                        removalProven = false;
                        break;
                    }
                }
            }
            if (!removalProven) throw AnimationToolSupport.StateMismatch("E_CONTROLLER_REMOVE_LAYER_STATE_MISMATCH", "AnimatorController layer removal");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, RemovedLayerIndex = removeIndex, RemovedLayerName = removeName, RemainingLayerCount = observedController.layers.Length, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animator-controller.remove-layer", Path = path } } });
        }
    }
}
#endif
