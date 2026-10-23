#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>editor.menu-list — 只读枚举真实 Editor 菜单路径，带筛选、有界分页与截断证据。</summary>
    public class EditorMenuListTool : IUnityCliTool<EditorMenuListTool.Request, EditorMenuListTool.Result>
    {
        public sealed class Request
        {
            public string Root { get; set; }
            public string Filter { get; set; }
            [UnityCliRange(1L, 500L)] public int? Count { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            [UnityCliItemsRange(0, 500)] public List<string> Items { get; set; }
            public long Total { get; set; }
            public bool Truncated { get; set; }
            public bool ScanExhausted { get; set; }
            [UnityCliItemsRange(0, 256)] public List<string> Roots { get; set; }
            public string Note { get; set; }
            public UnityCliRequestBudgetUsage Budget { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "editor.menu-list",
            Summary = "List live Unity Editor menu paths with a case-insensitive filter and a bounded page plus truncation evidence.",
            Description = "Read the live Editor menu as a flat, de-duplicated, ordinal-sorted list of full paths, covering native menus such as File/..., Assets/... and GameObject/... plus attributed project and package menus such as Tools/<your tool>. root restricts expansion to one menu root, filter keeps only paths that contain the text case-insensitively, count is 1..500 and defaults to 100. total is the number of matching paths found by this scan, truncated means more matches exist than the returned page, and scanExhausted means the read budget stopped the scan, so an empty page with scanExhausted=true is not proof that nothing matches. roots reports up to " + EditorMenuSupport.MaxReportedRoots + " expanded roots and note says when that list is capped. Menu separators and CONTEXT/ context menus are excluded because they are not part of the menu bar. Unless budget overrides them, the scan runs with the maximum scanned-record and reflection budgets, because menu registration metadata is small and version-cached. This is a pure read: it executes no menu item and calls no menu validation function, so it never changes Editor, scene or asset state. Feed a returned path to editor.menu-execute. When the running Unity version does not expose the internal menu enumeration API, the call fails with E_MENU_ENUMERATION_UNAVAILABLE.",
            Group = "editor",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Find project menu paths containing Serializer", Input = new Request { Filter = "Serializer" } },
                new ToolExample { Description = "List the first 50 entries under the Tools root", Input = new Request { Root = "Tools", Count = 50 } }
            }
        };

        /// <summary>只读扫描菜单注册元数据；不提交 effect，也不调用菜单 validate 函数。</summary>
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            request = request ?? new Request();
            var budget = ctx.UseReadBudget(request.Budget ?? new UnityCliReadBudgetInput
            {
                MaxScannedRecords = UnityCliRequestBudget.AbsoluteMaxScannedRecords,
                MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls
            });
            var scan = EditorMenuSupport.Scan(request.Root, request.Filter, request.Count ?? 100, budget);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Items = scan.Items,
                Total = scan.Total,
                Truncated = scan.Truncated,
                ScanExhausted = scan.ScanExhausted,
                Roots = scan.Roots,
                Note = scan.Note,
                Budget = budget.Usage()
            });
        }
    }
}
#endif
