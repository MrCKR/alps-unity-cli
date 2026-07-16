#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.document.create — 创建 UXML/USS 资产和 UIDocument GameObject。</summary>
    public class UiDocumentCreateTool : IUnityCliTool<UiDocumentCreateTool.Request, UiDocumentCreateTool.Result>
    {
        public sealed class Request { public string Name { get; set; } public string Uxml { get; set; } public string Uss { get; set; } public string PanelSettings { get; set; } public bool? Overwrite { get; set; } }
        public sealed class Result { public UiDocumentLiveInfo Document { get; set; } public UiElementRef Target { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.document.create", Summary = "Create a UIDocument GameObject with UXML/USS assets and optional PanelSettings.", Description = "Create .uxml (and optional .uss) text assets, then a UIDocument GameObject linking them. overwrite=true to replace existing assets.", Group = "ui", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "asset-write", "scene-modify" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create UIDocument", Input = new Request { Name = "UIDocument", Uxml = "Assets/UI/Main.uxml" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            UiToolSupport.EnsureEditorNotBusy("ui.document.create");
            var name = string.IsNullOrEmpty(r.Name) ? "UIDocument" : r.Name;
            var uxmlPath = UiToolSupport.RequireUxmlPath(r.Uxml);
            var ussPath = SceneToolSupport.NormalizeAssetPath(r.Uss);
            var overwrite = r.Overwrite ?? false;
            UiToolSupport.EnsureTextAsset(uxmlPath, UiToolSupport.DefaultUxml(ussPath), overwrite, "E_UI_UXML_EXISTS");
            if (!string.IsNullOrEmpty(ussPath)) UiToolSupport.EnsureTextAsset(ussPath, UiToolSupport.DefaultUss(), overwrite, "E_UI_USS_EXISTS");
            PanelSettings panelSettings = null;
            if (!string.IsNullOrEmpty(r.PanelSettings)) { panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(SceneToolSupport.NormalizeAssetPath(r.PanelSettings)); if (panelSettings == null) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_NOT_FOUND", "PanelSettings asset not found: " + r.PanelSettings, "Create a PanelSettings asset or pass a valid path."); }
            var go = new GameObject(name, typeof(UIDocument));
            var doc = go.GetComponent<UIDocument>();
            doc.visualTreeAsset = UiToolSupport.LoadVisualTreeAsset(uxmlPath);
            if (panelSettings != null) doc.panelSettings = panelSettings;
            Selection.activeGameObject = go;
            AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Document = new UiDocumentLiveInfo { VisualTreeAsset = doc.visualTreeAsset?.name, PanelSettings = doc.panelSettings?.name, SortingOrder = doc.sortingOrder }, Target = UiToolSupport.BuildUiElementRef(go, "UIDocument"), PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.document.create", Target = GameObjectToolSupport.HierarchyPath(go), Uxml = uxmlPath, Uss = ussPath } } });
        }
    }
}
#endif
