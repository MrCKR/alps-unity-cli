#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.document.info — 读取 UIDocument 或 UXML 文件信息。</summary>
    public class UiDocumentInfoTool : IUnityCliTool<UiDocumentInfoTool.Request, UiDocumentInfoTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Path { get; set; } public string Uss { get; set; } }
        public sealed class Result { public UiDocumentLiveInfo Document { get; set; } public UiDocumentAssetInfo Asset { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.document.info", Summary = "Inspect a UIDocument or UI document asset in the selected Editor.", Description = "Pass target for a live UIDocument inspection, or an Assets/*.uxml path for AssetDatabase metadata. Both modes execute through the selected open Editor. Use uss for an associated stylesheet asset.", Group = "ui",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read UIDocument", Input = new Request { Target = "UIDocument" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(r.Target))
            {
                var go = UiToolSupport.ResolveGameObjectTarget(r.Target);
                var doc = go.GetComponent<UIDocument>();
                if (doc == null) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_NOT_FOUND", "UIDocument target not found: " + r.Target, "Pass a UIDocument GameObject or an Assets/*.uxml path.");
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Document = new UiDocumentLiveInfo { VisualTreeAsset = doc.visualTreeAsset?.name, PanelSettings = doc.panelSettings?.name, SortingOrder = doc.sortingOrder } });
            }
            var path = SceneToolSupport.NormalizeAssetPath(r.Path);
            if (string.IsNullOrEmpty(path)) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_PATH_REQUIRED", "UIDocument info requires a target or Assets/*.uxml path.", "Pass a target or path.");
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
            var ss = string.IsNullOrEmpty(r.Uss) ? null : AssetDatabase.LoadAssetAtPath<StyleSheet>(r.Uss);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Asset = new UiDocumentAssetInfo { Path = path, Name = Path.GetFileNameWithoutExtension(path), Exists = vta != null, VisualTreeName = vta?.name, StyleSheetName = ss?.name } });
        }
    }
}
#endif
