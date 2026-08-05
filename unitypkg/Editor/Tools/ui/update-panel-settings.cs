#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.update-panel-settings — 更新 PanelSettings 资产。</summary>
    public class UiUpdatePanelSettingsTool : IUnityCliTool<UiUpdatePanelSettingsTool.Request, UiUpdatePanelSettingsTool.Result>
    {
        public sealed class Request { public string Path { get; set; } public string ScaleMode { get; set; } public int? ReferenceWidth { get; set; } public int? ReferenceHeight { get; set; } public int? SortingOrder { get; set; } }
        public sealed class Result { public UiPanelSettingsInfo PanelSettings { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.update-panel-settings", Summary = "Update an existing PanelSettings asset.", Description = "Modify scale mode, reference resolution, or sorting order of an existing PanelSettings. dryRun=true previews.", Group = "ui",DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Update PanelSettings", Input = new Request { Path = "Assets/UI/Panel.asset", ReferenceWidth = 1280 } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            UiToolSupport.EnsureEditorNotBusy("ui.update-panel-settings");
            var path = AssetToolSupport.RequireAssetPath(r.Path);
            if (!path.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase)) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_NOT_FOUND", "Pass an Assets/*.asset PanelSettings path.", "Pass a canonical Assets/*.asset path.");
            if (string.IsNullOrEmpty(r.ScaleMode) && !r.ReferenceWidth.HasValue && !r.ReferenceHeight.HasValue && !r.SortingOrder.HasValue) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_REQUEST_EMPTY", "ui.update-panel-settings requires at least one setting.", "Pass scaleMode, referenceWidth, referenceHeight, or sortingOrder.");
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (panel == null) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_NOT_FOUND", "PanelSettings asset not found: " + path, "Create it with ui.create-panel-settings first.");
            if (!string.IsNullOrEmpty(r.ScaleMode) && !string.Equals(r.ScaleMode, "constantpixelsize", System.StringComparison.OrdinalIgnoreCase) && !string.Equals(r.ScaleMode, "scalewithscreensize", System.StringComparison.OrdinalIgnoreCase))
                throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_SCALE_MODE_INVALID", "ScaleMode must be ConstantPixelSize or ScaleWithScreenSize.", "Pass a documented PanelScaleMode value.");
            if ((r.ReferenceWidth.HasValue && r.ReferenceWidth.Value <= 0) || (r.ReferenceHeight.HasValue && r.ReferenceHeight.Value <= 0))
                throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_RANGE_INVALID", "Reference dimensions must be positive.", "Pass positive referenceWidth/referenceHeight values.");
            var nextScaleMode = string.IsNullOrEmpty(r.ScaleMode) ? panel.scaleMode : (string.Equals(r.ScaleMode, "constantpixelsize", System.StringComparison.OrdinalIgnoreCase) ? PanelScaleMode.ConstantPixelSize : PanelScaleMode.ScaleWithScreenSize);
            var nextResolution = new Vector2Int(r.ReferenceWidth ?? panel.referenceResolution.x, r.ReferenceHeight ?? panel.referenceResolution.y);
            var nextSortingOrder = r.SortingOrder ?? panel.sortingOrder;
            var dryRun = ctx.DryRun;
            if (dryRun) return new System.Threading.Tasks.ValueTask<Result>(new Result { PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.update-panel-settings", Path = path } } });
            ctx.CommitEffect();
            panel.scaleMode = nextScaleMode;
            panel.referenceResolution = nextResolution;
            panel.sortingOrder = nextSortingOrder;
            EditorUtility.SetDirty(panel); AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var observed = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (observed == null) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_READBACK_FAILED", "Updated PanelSettings could not be reloaded: " + path, "Check Unity Console for asset import errors.");
            if (observed.scaleMode != nextScaleMode ||
                observed.referenceResolution != nextResolution ||
                observed.sortingOrder != nextSortingOrder)
                throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_READBACK_FAILED", "Updated PanelSettings does not match the requested values: " + path, "Check Unity Console for asset serialization errors.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { PanelSettings = new UiPanelSettingsInfo { Path = AssetDatabase.GetAssetPath(observed), ScaleMode = observed.scaleMode.ToString(), ReferenceWidth = observed.referenceResolution.x, ReferenceHeight = observed.referenceResolution.y, SortingOrder = observed.sortingOrder }, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.update-panel-settings", Path = path } } });
        }
    }
}
#endif
