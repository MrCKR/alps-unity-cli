#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.list-documents — 列出 UI 文档资产。</summary>
    public class UiListDocumentsTool : IUnityCliTool<UiListDocumentsTool.Request, UiListDocumentsTool.Result>
    {
        public sealed class Request { public string Kind { get; set; } public int? PageSize { get; set; } }
        public sealed class Result { public List<UiDocumentAssetInfo> Items { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.list-documents", Summary = "List UXML and USS assets in the project.", Description = "Search AssetDatabase for .uxml and .uss assets. kind=uxml or uss to filter.", Group = "ui", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "List UXML assets", Input = new Request { Kind = "uxml" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var items = new List<UiDocumentAssetInfo>();
            var kind = r.Kind ?? "all";
            if (kind == "all" || kind == "uxml")
            {
                var guids = AssetDatabase.FindAssets("t:VisualTreeAsset");
                foreach (var g in guids) { var p = AssetDatabase.GUIDToAssetPath(g); items.Add(new UiDocumentAssetInfo { Path = p, Name = Path.GetFileNameWithoutExtension(p), Exists = true }); }
            }
            if (kind == "all" || kind == "uss")
            {
                var guids = AssetDatabase.FindAssets("t:StyleSheet");
                foreach (var g in guids) { var p = AssetDatabase.GUIDToAssetPath(g); items.Add(new UiDocumentAssetInfo { Path = p, Name = Path.GetFileNameWithoutExtension(p), Exists = true }); }
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Items = items });
        }
    }
}
#endif
