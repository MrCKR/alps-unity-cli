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
        public sealed class Result { public string Path { get; set; } public bool Updated { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.update-panel-settings", Summary = "Update an existing PanelSettings asset.", Description = "Modify scale mode, reference resolution, or sorting order of an existing PanelSettings. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Update PanelSettings", Input = new Request { Path = "Assets/UI/Panel.asset", ReferenceWidth = 1280 } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = SceneToolSupport.NormalizeAssetPath(r.Path);
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (panel == null) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_NOT_FOUND", "PanelSettings asset not found: " + path, "Create it with ui.create-panel-settings first.");
            var dryRun = ctx.DryRun;
            if (dryRun) return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.update-panel-settings", Path = path } } });
            if (!string.IsNullOrEmpty(r.ScaleMode)) panel.scaleMode = r.ScaleMode.ToLowerInvariant() == "constantpixelsize" ? PanelScaleMode.ConstantPixelSize : PanelScaleMode.ScaleWithScreenSize;
            if (r.ReferenceWidth.HasValue || r.ReferenceHeight.HasValue) panel.referenceResolution = new Vector2Int(r.ReferenceWidth ?? panel.referenceResolution.x, r.ReferenceHeight ?? panel.referenceResolution.y);
            if (r.SortingOrder.HasValue) panel.sortingOrder = r.SortingOrder.Value;
            EditorUtility.SetDirty(panel); AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, Updated = true, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.update-panel-settings", Path = path } } });
        }
    }
}
#endif
