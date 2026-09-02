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
            public string Cursor { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public List<ConsoleEntry> Items { get; set; }
            public long RawEntryCount { get; set; }
            public string NextCursor { get; set; }
            public bool Truncated { get; set; }
            public string Generation { get; set; }
            public UnityCliRequestBudgetUsage Budget { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "console.get",
            Summary = "Read filtered Unity Console entries with stable raw-position continuation and request-wide bounds.",
            Description = "Read Console entries newest-first without materializing the Console. count defaults to 50 and is limited to 1..500. Filtering advances over raw positions, so sparse 100k-entry Consoles remain bounded by budget.maxScannedRecords (default 10000, maximum 100000), maxReflectionCalls (default 4096, maximum 20000), maxMainThreadMilliseconds (default 250, maximum 2000), and maxSerializedUtf8Bytes (default 1 MiB, maximum 8 MiB). A non-null nextCursor is a sentinel that identifies the next raw entry even when no matching item was returned; pass it unchanged. rawEntryCount is Unity's raw count, not a filtered total. Old cursors fail with E_CONTINUATION_STALE after the bound Console generation changes. Read-only in Edit and Play Mode.",
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
            var page = EditorToolSupport.ReadConsolePage("console.get", request.Type, request.Filter, pageSize, request.Cursor, ctx.UseReadBudget(request.Budget));
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
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
