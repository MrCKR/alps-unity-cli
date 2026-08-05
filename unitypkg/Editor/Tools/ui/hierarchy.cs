#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.hierarchy — 列出 uGUI/UI Toolkit 层级。</summary>
    public class UiHierarchyTool : IUnityCliTool<UiHierarchyTool.Request, UiHierarchyTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Parent { get; set; } public int? PageSize { get; set; } }
        public sealed class Result { public List<UiHierarchyItem> Items { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.hierarchy", Summary = "List uGUI and UI Toolkit elements in the scene hierarchy.", Description = "Find Canvas, UIDocument, and visual element GameObjects. Optionally filter by target/parent.", Group = "ui",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "List UI hierarchy", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var items = new List<UiHierarchyItem>();
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            foreach (var c in canvases) items.Add(new UiHierarchyItem { Name = c.gameObject.name, Path = GameObjectToolSupport.HierarchyPath(c.gameObject), Type = "Canvas", ChildCount = c.transform.childCount });
            var docs = UnityEngine.Object.FindObjectsOfType<UIDocument>();
            foreach (var d in docs) items.Add(new UiHierarchyItem { Name = d.gameObject.name, Path = GameObjectToolSupport.HierarchyPath(d.gameObject), Type = "UIDocument", ChildCount = d.transform.childCount });
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Items = items });
        }
    }
}
#endif
