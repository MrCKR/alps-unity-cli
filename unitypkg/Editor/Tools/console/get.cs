#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>console.get — 按稳定 raw Console 位置执行有界过滤与续页。</summary>
    public class ConsoleGetTool : IUnityCliTool<ConsoleGetTool.Request, ConsoleGetTool.Result>
    {
        public sealed class Request
        {
            [UnityCliStringEnum("all", "error", "warning", "assert", "log")]
            public string Type { get; set; }
            public string Filter { get; set; }
            [UnityCliRange(1L, 500L)] public int? Count { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            [UnityCliItemsRange(0, 500)] public List<ConsoleEntry> Items { get; set; }
            public long RawEntryCount { get; set; }
            public bool Truncated { get; set; }
            public string Note { get; set; }
            public UnityCliRequestBudgetUsage Budget { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "console.get",
            Summary = "Read one bounded, non-resumable Unity Console snapshot under request-wide limits.",
            Description = "Read Console entries newest-first from one Unity snapshot window without materializing the Console. count defaults to 50 and is limited to 1..500. Filtering advances over raw positions and every entry uses the shared scan/reflection/getter/main-thread/UTF-8 budget. Unity exposes no reliable generation covering external Console clear/rebuild, so this tool intentionally has no cursor; truncated=true means start a fresh snapshot with a larger count or budget. rawEntryCount is Unity's raw count, not a filtered total. Read-only in Edit and Play Mode.",
            Group = "console",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Read a bounded error page", Input = new Request { Type = "error", Count = 50 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            request = request ?? new Request();
            var pageSize = request.Count ?? 50;
            var page = EditorToolSupport.ReadConsolePage(request.Type, request.Filter, pageSize, ctx.UseReadBudget(request.Budget));
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
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
