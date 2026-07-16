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
        public sealed class Result { public string Command { get; set; } public UiElementRef Target { get; set; } public string SourceAsset { get; set; } public bool HasPanelSettings { get; set; } public long SortOrder { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.attach-document", Summary = "Attach a UIDocument component to a target GameObject.", Description = "Add or configure a UIDocument with optional visualTreeAsset and panelSettings. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Attach UIDocument", Input = new Request { Target = "Player", SourceAsset = "Assets/UI/Main.uxml" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            UiToolSupport.EnsureEditorNotBusy("ui.attach-document");
            var go = UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(r.Target);
            if (go == null) throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "ui.attach-document requires a target GameObject.", "Pass --target <path>.");
            var dryRun = ctx.DryRun;
            if (dryRun) return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "ui.attach-document", Target = UiToolSupport.BuildUiElementRef(go, "UIDocument"), SourceAsset = r.SourceAsset ?? r.Uxml, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.attach-document", Target = GameObjectToolSupport.HierarchyPath(go) } } });
            var sourceAsset = r.SourceAsset ?? r.Uxml;
            var sortOrder = r.SortOrder ?? 0;
            var uiDoc = go.GetComponent<UIDocument>() ?? go.AddComponent<UIDocument>();
            if (!string.IsNullOrEmpty(sourceAsset)) { var uxml = AssetDatabase.LoadMainAssetAtPath(sourceAsset) as VisualTreeAsset; if (uxml != null) uiDoc.visualTreeAsset = uxml; }
            if (!string.IsNullOrEmpty(r.PanelSettings)) { var panel = AssetDatabase.LoadMainAssetAtPath(r.PanelSettings) as PanelSettings; if (panel != null) uiDoc.panelSettings = panel; }
            else if (uiDoc.panelSettings == null) { var defaults = ScriptableObject.CreateInstance<PanelSettings>(); defaults.scaleMode = PanelScaleMode.ScaleWithScreenSize; defaults.referenceResolution = new Vector2Int(1920, 1080); uiDoc.panelSettings = defaults; }
            uiDoc.sortingOrder = sortOrder;
            UnityCliSceneMutation.MarkSceneDirty(go.scene);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "ui.attach-document", Target = UiToolSupport.BuildUiElementRef(go, "UIDocument"), SourceAsset = sourceAsset, HasPanelSettings = uiDoc.panelSettings != null, SortOrder = sortOrder, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.attach-document", Target = GameObjectToolSupport.HierarchyPath(go) } } });
        }
    }
}
#endif
