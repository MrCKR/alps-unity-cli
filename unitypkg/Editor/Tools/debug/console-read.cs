#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>debug.console-read — 按稳定 raw Console 位置执行有界过滤与续页。</summary>
    public class ConsoleReadTool : IUnityCliTool<ConsoleReadTool.Request, ConsoleReadTool.Result>
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

            /// <summary>扫描是否因请求预算耗尽而提前停止；为 true 时本页不是完整匹配集。</summary>
            public bool ScanExhausted { get; set; }

            /// <summary>本次实际检查过的 raw 条目数；用于判断过滤是否过窄。</summary>
            public long ScannedEntries { get; set; }

            public string Note { get; set; }
            public UnityCliRequestBudgetUsage Budget { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "debug.console-read",
            Summary = "Read one bounded, non-resumable Unity Console snapshot and report whether the budget stopped the scan.",
            Description = "Read Console entries newest-first from one Unity snapshot window without materializing the Console. count defaults to 50 and is limited to 1..500; type and filter narrow the scan. The Tool has no cursor because Unity exposes no reliable generation covering external Console clear or rebuild. truncated=true means entries older than the returned page were not examined; scanExhausted=true with scannedEntries means the shared request budget stopped the scan itself, so an empty page is not proof that no entry matches. rawEntryCount is Unity's raw count, not a filtered total. Read-only in Edit and Play Mode.", Notes = DebugDomainNotes.ConsoleReadBudget,
            Group = "debug",
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
            // 预算完全耗尽时框架无法发布任何终态结果（发布本身需要 getter/字节预算），
            // 因此这里不假装能返回数据；降级只发生在扫描阶段停止但预算仍可发布时。
            var page = EditorToolSupport.ReadConsolePage(request.Type, request.Filter, pageSize, ctx.UseReadBudget(request.Budget));
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Items = page.Items,
                RawEntryCount = page.RawEntryCount,
                Truncated = page.Truncated,
                ScanExhausted = page.ScanExhausted,
                ScannedEntries = page.ScannedEntries,
                Note = page.Note,
                Budget = page.Budget
            });
        }
    }
}
#endif
