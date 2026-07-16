#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.get-visual-tree — 读取 UIDocument 可视树结构。</summary>
    public class UiGetVisualTreeTool : IUnityCliTool<UiGetVisualTreeTool.Request, UiGetVisualTreeTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Parent { get; set; } public int? MaxDepth { get; set; } }
        public sealed class Result { public string Command { get; set; } public List<UiHierarchyItem> Items { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.get-visual-tree", Summary = "Read the visual tree hierarchy of a live UIDocument.", Description = "Traverse the VisualElement hierarchy of a UIDocument's root. Returns element names and types.", Group = "ui", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read visual tree", Input = new Request { Target = "UIDocument" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = UiToolSupport.ResolveGameObjectTarget(r.Target);
            var doc = go.GetComponent<UIDocument>();
            if (doc == null || doc.rootVisualElement == null) return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "ui.get-visual-tree", Items = new List<UiHierarchyItem>() });
            var items = new List<UiHierarchyItem>();
            TraverseVisualElement(doc.rootVisualElement, "", 0, r.MaxDepth ?? 10, items);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "ui.get-visual-tree", Items = items });
        }
        private static void TraverseVisualElement(VisualElement ve, string parentPath, int depth, int maxDepth, List<UiHierarchyItem> items)
        {
            if (depth > maxDepth) return;
            var path = string.IsNullOrEmpty(parentPath) ? ve.name : parentPath + "/" + ve.name;
            items.Add(new UiHierarchyItem { Name = ve.name, Path = path, Type = ve.GetType().Name, ChildCount = ve.childCount });
            foreach (var child in ve.Children()) TraverseVisualElement(child, path, depth + 1, maxDepth, items);
        }
    }
}
#endif
