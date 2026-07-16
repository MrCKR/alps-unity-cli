#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.document.delete — 删除 UXML/USS 资产。</summary>
    public class UiDocumentDeleteTool : IUnityCliTool<UiDocumentDeleteTool.Request, UiDocumentDeleteTool.Result>
    {
        public sealed class Request { public string Uxml { get; set; } public string Path { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public UiDocumentAssetInfo Document { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.document.delete", Summary = "Delete a .uxml or .uss UI document asset.", Description = "Delete the asset via AssetDatabase.DeleteAsset. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Delete UXML", Input = new Request { Uxml = "Assets/UI/Main.uxml" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = SceneToolSupport.NormalizeAssetPath(r.Uxml ?? r.Path);
            if (string.IsNullOrEmpty(path)) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_PATH_REQUIRED", "Pass an Assets/*.uxml path.", "");
            var dryRun = ctx.DryRun;
            if (dryRun) return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "ui.document.delete", DryRun = true, Document = new UiDocumentAssetInfo { Path = path, Exists = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null }, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.document.delete", Path = path } } });
            if (!AssetDatabase.DeleteAsset(path)) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_DELETE_FAILED", "Failed to delete UI document asset: " + path, "Check Unity Console and file permissions.");
            AssetDatabase.Refresh();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "ui.document.delete", DryRun = false, Document = new UiDocumentAssetInfo { Path = path, Exists = false }, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.document.delete", Path = path } } });
        }
    }
}
#endif
