#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.create-panel-settings — 创建 PanelSettings 资产。</summary>
    public class UiCreatePanelSettingsTool : IUnityCliTool<UiCreatePanelSettingsTool.Request, UiCreatePanelSettingsTool.Result>
    {
        public sealed class Request { public string Path { get; set; } public string ScaleMode { get; set; } public int? ReferenceWidth { get; set; } public int? ReferenceHeight { get; set; } public int? SortingOrder { get; set; } }
        public sealed class Result { public UiPanelSettingsInfo PanelSettings { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.create-panel-settings", Summary = "Create a PanelSettings asset for UI Toolkit.", Description = "Create a .asset PanelSettings with scale mode and reference resolution. dryRun=true previews.", Group = "ui",DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create PanelSettings", Input = new Request { Path = "Assets/UI/Panel.asset" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            UiToolSupport.EnsureEditorNotBusy("ui.create-panel-settings");
            var path = AssetToolSupport.RequireAssetPath(r.Path);
            if (!path.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase)) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_PATH_REQUIRED", "Pass an Assets/*.asset path.", "Pass a canonical Assets/*.asset path.");
            if (AssetToolSupport.AssetExists(path)) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_EXISTS", "PanelSettings asset already exists: " + path, "Choose a new path.");
            if (!string.IsNullOrEmpty(r.ScaleMode) && !string.Equals(r.ScaleMode, "constantpixelsize", System.StringComparison.OrdinalIgnoreCase) && !string.Equals(r.ScaleMode, "scalewithscreensize", System.StringComparison.OrdinalIgnoreCase)) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_SCALE_MODE_INVALID", "ScaleMode must be ConstantPixelSize or ScaleWithScreenSize.", "Pass a documented PanelScaleMode value.");
            if ((r.ReferenceWidth.HasValue && r.ReferenceWidth.Value <= 0) || (r.ReferenceHeight.HasValue && r.ReferenceHeight.Value <= 0)) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_RANGE_INVALID", "Reference dimensions must be positive.", "Pass positive reference dimensions.");
            var scaleMode = string.Equals(r.ScaleMode, "constantpixelsize", System.StringComparison.OrdinalIgnoreCase) ? PanelScaleMode.ConstantPixelSize : PanelScaleMode.ScaleWithScreenSize;
            var resolution = new Vector2Int(r.ReferenceWidth ?? 1920, r.ReferenceHeight ?? 1080);
            var sortingOrder = r.SortingOrder ?? 0;
            var dryRun = ctx.DryRun;
            if (dryRun) return new System.Threading.Tasks.ValueTask<Result>(new Result { PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.create-panel-settings", Path = path } } });
            ctx.CommitEffect();
            SceneToolSupport.CreateAssetDirectory(path);
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.scaleMode = scaleMode;
            panel.referenceResolution = resolution;
            panel.sortingOrder = sortingOrder;
            AssetDatabase.CreateAsset(panel, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var observed = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (observed == null || observed.scaleMode != scaleMode || observed.referenceResolution != resolution || observed.sortingOrder != sortingOrder) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_READBACK_FAILED", "Created PanelSettings could not be verified: " + path, "Check Unity Console for asset import errors.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { PanelSettings = new UiPanelSettingsInfo { Path = AssetDatabase.GetAssetPath(observed), ScaleMode = observed.scaleMode.ToString(), ReferenceWidth = observed.referenceResolution.x, ReferenceHeight = observed.referenceResolution.y, SortingOrder = observed.sortingOrder }, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.create-panel-settings", Path = path } } });
        }
    }
}
#endif
