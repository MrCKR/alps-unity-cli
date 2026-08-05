#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCli
{
    /// <summary>editor.console — 读取 Console 日志(只读)。</summary>
    public class EditorConsoleTool : IUnityCliTool<EditorConsoleTool.Request, EditorConsoleTool.Result>
    {
        public sealed class Request { public string Type { get; set; } public int? Count { get; set; } }
        public sealed class Result { public bool Cleared { get; set; } public string Type { get; set; } public List<ConsoleEntry> Items { get; set; } public PageInfo PageInfo { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.console", Summary = "Read recent Unity Console log entries.", Description = "Read-only: returns recent Console entries filtered by type. Use console.clear to clear.", Group = "editor",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read console", Input = new Request { Count = 20 } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var count = Math.Max(1, Math.Min(r.Count ?? 50, 500));
            var items = EditorToolSupport.ReadConsoleEntries(r.Type, null, count);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Cleared = false, Type = r.Type, Items = items, PageInfo = new PageInfo { PageSize = count, Cursor = 0, Total = items.Count } });
        }
    }
}
#endif
