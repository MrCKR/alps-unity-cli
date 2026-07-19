#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.attach-document — 将 UIDocument 挂载到 GameObject。</summary>
    public class UiAttachDocumentTool : IUnityCliTool<UiAttachDocumentTool.Request, UiAttachDocumentTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string SourceAsset { get; set; } public string Uxml { get; set; } public string PanelSettings { get; set; } public int? SortOrder { get; set; } }
        public sealed class Result { public string Command { get; set; } public UiElementRef Target { get; set; } public bool Attached { get; set; } public string SourceAsset { get; set; } public bool HasPanelSettings { get; set; } public double SortOrder { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.attach-document", Summary = "Attach a UIDocument component to a target GameObject.", Description = "Add or configure a UIDocument with optional visualTreeAsset and panelSettings. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Attach UIDocument", Input = new Request { Target = "Player", SourceAsset = "Assets/UI/Main.uxml" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            UiToolSupport.EnsureEditorNotBusy("ui.attach-document");
            var go = UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(r.Target);
            if (go == null) throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "ui.attach-document requires a target GameObject.", "Pass --target <path>.");
            var targetId = go.GetInstanceID();
            var sourceAsset = r.SourceAsset ?? r.Uxml;
            var sortOrder = r.SortOrder ?? 0;
            var requestedUxml = string.IsNullOrEmpty(sourceAsset) ? null : UiToolSupport.LoadVisualTreeAsset(SceneToolSupport.NormalizeAssetPath(sourceAsset));
            if (!string.IsNullOrEmpty(sourceAsset) && requestedUxml == null) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_SOURCE_NOT_FOUND", "VisualTreeAsset not found: " + sourceAsset, "Pass an existing .uxml asset.");
            var requestedPanel = string.IsNullOrEmpty(r.PanelSettings) ? null : AssetDatabase.LoadAssetAtPath<PanelSettings>(SceneToolSupport.NormalizeAssetPath(r.PanelSettings));
            if (!string.IsNullOrEmpty(r.PanelSettings) && requestedPanel == null) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_NOT_FOUND", "PanelSettings asset not found: " + r.PanelSettings, "Pass an existing PanelSettings asset.");
            var dryRun = ctx.DryRun;
            if (dryRun) return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "ui.attach-document", Target = UiToolSupport.BuildUiElementRef(go, "UIDocument"), Attached = go.GetComponent<UIDocument>() != null, SourceAsset = r.SourceAsset ?? r.Uxml, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.attach-document", Target = GameObjectToolSupport.HierarchyPath(go) } } });
            ctx.CommitEffect();
            var uiDoc = go.GetComponent<UIDocument>() ?? go.AddComponent<UIDocument>();
            if (requestedUxml != null) uiDoc.visualTreeAsset = requestedUxml;
            if (requestedPanel != null) uiDoc.panelSettings = requestedPanel;
            else if (uiDoc.panelSettings == null) { var defaults = ScriptableObject.CreateInstance<PanelSettings>(); defaults.scaleMode = PanelScaleMode.ScaleWithScreenSize; defaults.referenceResolution = new Vector2Int(1920, 1080); uiDoc.panelSettings = defaults; }
            uiDoc.sortingOrder = sortOrder;
            UnityCliSceneMutation.MarkSceneObjectDirty(uiDoc);
            UnityCliSceneMutation.MarkSceneDirty(go.scene);
            var observedGo = EditorUtility.InstanceIDToObject(targetId) as GameObject;
            var observed = observedGo == null || observedGo.GetInstanceID() != targetId ? null : observedGo.GetComponent<UIDocument>();
            if (observed == null || (requestedUxml != null && observed.visualTreeAsset != requestedUxml) || (requestedPanel != null && observed.panelSettings != requestedPanel) || observed.sortingOrder != sortOrder)
                throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_READBACK_FAILED", "Attached UIDocument does not match the requested configuration.", "Check Unity Console for scene serialization errors.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "ui.attach-document", Target = UiToolSupport.BuildUiElementRef(observedGo, "UIDocument"), Attached = true, SourceAsset = observed.visualTreeAsset == null ? null : AssetDatabase.GetAssetPath(observed.visualTreeAsset), HasPanelSettings = observed.panelSettings != null, SortOrder = observed.sortingOrder, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.attach-document", Target = GameObjectToolSupport.HierarchyPath(observedGo) } } });
        }
    }
}
#endif
