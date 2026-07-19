#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.document.create — 创建 UXML/USS 资产和 UIDocument GameObject。</summary>
    public class UiDocumentCreateTool : IUnityCliTool<UiDocumentCreateTool.Request, UiDocumentCreateTool.Result>
    {
        public sealed class Request { public string Name { get; set; } public string Uxml { get; set; } public string Uss { get; set; } public string PanelSettings { get; set; } public bool? Overwrite { get; set; } }
        public sealed class Result { public UiDocumentLiveInfo Document { get; set; } public UiElementRef Target { get; set; } public bool Attached { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.document.create", Summary = "Create a UIDocument GameObject with UXML/USS assets and optional PanelSettings.", Description = "Create .uxml (and optional .uss) text assets, then a UIDocument GameObject linking them. overwrite=true to replace existing assets.", Group = "ui", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "asset-write", "scene-modify" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create UIDocument", Input = new Request { Name = "UIDocument", Uxml = "Assets/UI/Main.uxml" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            UiToolSupport.EnsureEditorNotBusy("ui.document.create");
            var name = string.IsNullOrEmpty(r.Name) ? "UIDocument" : r.Name;
            var uxmlPath = UiToolSupport.RequireUxmlPath(r.Uxml);
            var ussPath = string.IsNullOrEmpty(r.Uss) ? null : UiToolSupport.RequireUssPath(r.Uss);
            var overwrite = r.Overwrite ?? false;
            UiToolSupport.ValidateTextAssetCreation(uxmlPath, overwrite, "E_UI_UXML_EXISTS");
            if (!string.IsNullOrEmpty(ussPath)) UiToolSupport.ValidateTextAssetCreation(ussPath, overwrite, "E_UI_USS_EXISTS");
            PanelSettings panelSettings = null;
            if (!string.IsNullOrEmpty(r.PanelSettings)) { var panelSettingsPath = AssetToolSupport.RequireExistingAssetPath(r.PanelSettings); panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelSettingsPath); if (panelSettings == null) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_NOT_FOUND", "PanelSettings asset not found: " + panelSettingsPath, "Create a PanelSettings asset or pass a valid path."); }
            ctx.CommitEffect();
            UiToolSupport.EnsureTextAsset(uxmlPath, UiToolSupport.DefaultUxml(ussPath));
            if (!string.IsNullOrEmpty(ussPath)) UiToolSupport.EnsureTextAsset(ussPath, UiToolSupport.DefaultUss());
            var go = new GameObject(name, typeof(UIDocument));
            var documentObjectId = go.GetInstanceID();
            var panelSettingsId = panelSettings == null ? 0 : panelSettings.GetInstanceID();
            var doc = go.GetComponent<UIDocument>();
            doc.visualTreeAsset = UiToolSupport.LoadVisualTreeAsset(uxmlPath);
            if (panelSettings != null) doc.panelSettings = panelSettings;
            Selection.activeGameObject = go;
            EditorUtility.SetDirty(doc);
            AssetDatabase.SaveAssets();
            var observedUxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var observedUss = string.IsNullOrEmpty(ussPath) ? null : AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath);
            var observedGo = EditorUtility.InstanceIDToObject(documentObjectId) as GameObject;
            var observedPanelSettings = panelSettingsId == 0 ? null : EditorUtility.InstanceIDToObject(panelSettingsId) as PanelSettings;
            var observedDocument = observedGo == null || observedGo.GetInstanceID() != documentObjectId ? null : observedGo.GetComponent<UIDocument>();
            if (observedUxml == null || observedDocument == null || observedDocument.visualTreeAsset != observedUxml || (panelSettingsId != 0 && (observedPanelSettings == null || observedDocument.panelSettings != observedPanelSettings)) || (!string.IsNullOrEmpty(ussPath) && observedUss == null))
                throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_READBACK_FAILED", "Created UIDocument could not be verified.", "Check Unity Console for asset import or scene serialization errors.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Document = new UiDocumentLiveInfo { VisualTreeAsset = observedDocument.visualTreeAsset == null ? null : AssetDatabase.GetAssetPath(observedDocument.visualTreeAsset), PanelSettings = observedDocument.panelSettings == null ? null : AssetDatabase.GetAssetPath(observedDocument.panelSettings), SortingOrder = observedDocument.sortingOrder }, Target = UiToolSupport.BuildUiElementRef(observedGo, "UIDocument"), Attached = true, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.document.create", Target = GameObjectToolSupport.HierarchyPath(observedGo), Uxml = uxmlPath, Uss = ussPath } } });
        }
    }
}
#endif
