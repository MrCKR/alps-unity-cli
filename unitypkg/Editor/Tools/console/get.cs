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
    /// <summary>console.get — 读取 Console 日志(支持过滤和分页)。</summary>
    public class ConsoleGetTool : IUnityCliTool<ConsoleGetTool.Request, ConsoleGetTool.Result>
    {
        public sealed class Request { public string Type { get; set; } public string Filter { get; set; } public int? Count { get; set; } public int? Cursor { get; set; } }
        public sealed class Result { public string Command { get; set; } public List<ConsoleEntry> Items { get; set; } public string Filter { get; set; } public PageInfo PageInfo { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "console.get", Summary = "Read Unity Console entries with type filter, text search, and cursor paging.", Description = "Read Console entries newest-first. type filters by error/warning/log. filter does case-insensitive text search. cursor for pagination.", Group = "console", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read errors", Input = new Request { Type = "error", Count = 10 } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var count = Math.Max(1, Math.Min(r.Count ?? 50, 500));
            var cursor = Math.Max(0, r.Cursor ?? 0);
            var items = EditorToolSupport.ReadConsoleEntries(r.Type, r.Filter, count + cursor);
            var paged = items.Skip(cursor).Take(count).ToList();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "console.get", Items = paged, Filter = r.Filter, PageInfo = new PageInfo { PageSize = count, Cursor = cursor, Total = items.Count, NextCursor = cursor + paged.Count < items.Count ? (long?)(cursor + paged.Count) : null } });
        }
    }
}
#endif
