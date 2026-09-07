#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityCli
{
    /// <summary>profiler.memory-snapshot.list — governed latest-100 catalog 的稳定续页。</summary>
    public class ProfilerMemorySnapshotListTool : IUnityCliTool<ProfilerMemorySnapshotListTool.Request, ProfilerMemorySnapshotListTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRange(1L, 100L)] public int? PageSize { get; set; }
            public string Cursor { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public List<MemorySnapshotEntry> Snapshots { get; set; }
            public string Root { get; set; }
            public long Total { get; set; }
            public string NextCursor { get; set; }
            public bool Truncated { get; set; }
            public string Generation { get; set; }
            public UnityCliRequestBudgetUsage Budget { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.memory-snapshot.list",
            Summary = "Page the governed latest-100 memory snapshot catalog with stable generation continuation.",
            Description = "List only .snap artifacts under Library/UnityCli/memory-snapshots. pageSize is 1..100 (default 25); nextCursor binds the exact bounded catalog generation and stale cursors return E_CONTINUATION_STALE. total is exact because the product catalog is capped at 100 snapshots and 8 GiB. Metadata reads and UTF-8 output use the shared request budget. Read-only in Edit and Play Mode and never scans arbitrary directories.",
            Group = "profiler", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "List the first snapshot page", Input = new Request { PageSize = 25 } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            request = request ?? new Request();
            var budget = ctx.UseReadBudget(request.Budget);
            var files = ProfilerMemorySnapshotSupport.Catalog(budget);
            var generation = ProfilerMemorySnapshotSupport.CatalogGeneration(files, budget);
            var offset = 0;
            if (!string.IsNullOrEmpty(request.Cursor))
            {
                string position;
                if (!UnityCliContinuationToken.TryParse(request.Cursor, "profiler.memory-snapshot.list", generation, out position)
                    || !int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < 0 || offset > files.Count)
                    throw new UnityCliBridge.BridgeCommandException("E_CONTINUATION_STALE", "The memory snapshot continuation does not match the current catalog generation.", "Start a new snapshot list without cursor.");
            }
            var pageSize = request.PageSize ?? 25;
            var snapshots = new List<MemorySnapshotEntry>();
            var index = offset;
            while (index < files.Count && snapshots.Count < pageSize)
            {
                if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeNode() || !budget.TryConsumeGetter() || !budget.TryConsumeGetter()) break;
                var item = MemorySnapshotEntry.FromFileInfo(files[index]);
                if (!budget.TryReserveSerializedUtf8(item.Path) || !budget.TryReserveSerializedUtf8(item.Created)) break;
                snapshots.Add(item);
                index++;
            }
            if (index == offset && index < files.Count)
                throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension ?? "scannedRecords");
            var next = index < files.Count ? UnityCliContinuationToken.Create("profiler.memory-snapshot.list", generation, index.ToString(CultureInfo.InvariantCulture)) : null;
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Snapshots = snapshots, Root = ProfilerMemorySnapshotSupport.Root, Total = files.Count,
                NextCursor = next, Truncated = next != null, Generation = generation, Budget = budget.Usage()
            });
        }
    }
}
#endif
