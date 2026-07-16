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
        public sealed class Result { public string Path { get; set; } public bool Created { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.create-panel-settings", Summary = "Create a PanelSettings asset for UI Toolkit.", Description = "Create a .asset PanelSettings with scale mode and reference resolution. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create PanelSettings", Input = new Request { Path = "Assets/UI/Panel.asset" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = SceneToolSupport.NormalizeAssetPath(r.Path);
            if (string.IsNullOrEmpty(path)) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_PATH_REQUIRED", "Pass an Assets/*.asset path.", "");
            var dryRun = ctx.DryRun;
            if (dryRun) return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.create-panel-settings", Path = path } } });
            SceneToolSupport.CreateAssetDirectory(path);
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.scaleMode = (r.ScaleMode ?? "scaleWithScreenSize").ToLowerInvariant() == "constantpixelsize" ? PanelScaleMode.ConstantPixelSize : PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(r.ReferenceWidth ?? 1920, r.ReferenceHeight ?? 1080);
            if (r.SortingOrder.HasValue) panel.sortingOrder = r.SortingOrder.Value;
            AssetDatabase.CreateAsset(panel, path);
            AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, Created = true, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.create-panel-settings", Path = path } } });
        }
    }
}
#endif
