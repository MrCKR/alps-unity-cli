#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>editor.console — Console 读取别名，保持与 console.get 相同的有界 raw pagination。</summary>
    public class EditorConsoleTool : IUnityCliTool<EditorConsoleTool.Request, EditorConsoleTool.Result>
    {
        public sealed class Request
        {
            [UnityCliStringEnum("all", "error", "warning", "assert", "log")]
            public string Type { get; set; }
            [UnityCliRange(1L, 500L)] public int? Count { get; set; }
            public string Cursor { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public bool Cleared { get; set; }
            public List<ConsoleEntry> Items { get; set; }
            public long RawEntryCount { get; set; }
            public string NextCursor { get; set; }
            public bool Truncated { get; set; }
            public string Generation { get; set; }
            public UnityCliRequestBudgetUsage Budget { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "editor.console",
            Summary = "Read recent Unity Console entries with stable raw-position continuation.",
            Description = "Read-only Console page using the same raw-position and request-wide budgets as console.get. count defaults to 50 and is limited to 1..500; pass nextCursor unchanged. rawEntryCount is the unfiltered Unity count and no filtered total is fabricated. Use console.clear to clear entries. Available in Edit and Play Mode.",
            Group = "editor",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read Console", Input = new Request { Count = 20 } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            request = request ?? new Request();
            var page = EditorToolSupport.ReadConsolePage("editor.console", request.Type, null, request.Count ?? 50, request.Cursor, ctx.UseReadBudget(request.Budget));
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Cleared = false,
                Items = page.Items,
                RawEntryCount = page.RawEntryCount,
                NextCursor = page.NextCursor,
                Truncated = page.Truncated,
                Generation = page.Generation,
                Budget = page.Budget
            });
        }
    }
}
#endif
