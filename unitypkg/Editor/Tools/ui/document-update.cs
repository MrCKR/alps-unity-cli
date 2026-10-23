#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.document-update — 更新 UXML 文本内容。</summary>
    public class UiDocumentUpdateTool : IUnityCliTool<UiDocumentUpdateTool.Request, UiDocumentUpdateTool.Result>
    {
        public sealed class Request { public string Uxml { get; set; } public string Path { get; set; } public string Content { get; set; } }
        public sealed class Result { public bool DryRun { get; set; } public UiDocumentAssetInfo Document { get; set; } public long SizeBytes { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.document-update", Summary = "Update the text content of an existing .uxml asset.", Description = "Write new content to an existing .uxml file and reimport. dryRun=true previews.", Group = "ui",DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Update UXML", Input = new Request { Uxml = "Assets/UI/Main.uxml", Content = "<ui:UXML />" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = SceneToolSupport.NormalizeAssetPath(r.Uxml ?? r.Path);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".uxml")) throw new UnityCliBridge.BridgeCommandException("E_UI_UXML_PATH_REQUIRED", "Pass an Assets/*.uxml path.", "");
            if (r.Content == null) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_CONTENT_REQUIRED", "ui.document-update requires content.", "Pass --content or structured input content.");
            var dryRun = ctx.DryRun;
            if (dryRun) return new System.Threading.Tasks.ValueTask<Result>(new Result { DryRun = true, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.document-update", Uxml = path } } });
            var fullPath = SceneToolSupport.AssetFullPath(path);
            if (!File.Exists(fullPath)) throw new UnityCliBridge.BridgeCommandException("E_UI_UXML_NOT_FOUND", "VisualTreeAsset not found: " + path, "Pass an existing Assets/*.uxml path.");
            ctx.CommitEffect();
            File.WriteAllText(fullPath, r.Content, System.Text.Encoding.UTF8);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            var observedContent = File.ReadAllText(fullPath, System.Text.Encoding.UTF8);
            var observedAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
            if (observedAsset == null || observedContent != r.Content) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_READBACK_FAILED", "Updated UXML could not be verified: " + path, "Check Unity Console for UXML import errors.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { DryRun = false, Document = new UiDocumentAssetInfo { Path = AssetDatabase.GetAssetPath(observedAsset), Name = observedAsset.name, Exists = File.Exists(fullPath) }, SizeBytes = new FileInfo(fullPath).Length, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.document-update", Uxml = path } } });
        }
    }
}
#endif
