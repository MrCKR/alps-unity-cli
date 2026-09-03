#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>editor.console — Console 读取别名，保持与 console.get 相同的有界非续页快照。</summary>
    public class EditorConsoleTool : IUnityCliTool<EditorConsoleTool.Request, EditorConsoleTool.Result>
    {
        public sealed class Request
        {
            [UnityCliStringEnum("all", "error", "warning", "assert", "log")]
            public string Type { get; set; }
            [UnityCliRange(1L, 500L)] public int? Count { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public bool Cleared { get; set; }
            [UnityCliItemsRange(0, 500)] public List<ConsoleEntry> Items { get; set; }
            public long RawEntryCount { get; set; }
            public bool Truncated { get; set; }
            public string Note { get; set; }
            public UnityCliRequestBudgetUsage Budget { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "editor.console",
            Summary = "Read one bounded, non-resumable Unity Console snapshot.",
            Description = "Read-only Console snapshot using the same non-materializing request-wide budgets as console.get. count defaults to 50 and is limited to 1..500. Unity exposes no reliable generation covering external clear/rebuild, so truncated results intentionally provide no continuation. rawEntryCount is the unfiltered Unity count and no filtered total is fabricated. Use console.clear to clear entries. Available in Edit and Play Mode.",
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
            var page = EditorToolSupport.ReadConsolePage(request.Type, null, request.Count ?? 50, ctx.UseReadBudget(request.Budget));
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Cleared = false,
                Items = page.Items,
                RawEntryCount = page.RawEntryCount,
                Truncated = page.Truncated,
                Note = page.Note,
                Budget = page.Budget
            });
        }
    }
}
#endif
