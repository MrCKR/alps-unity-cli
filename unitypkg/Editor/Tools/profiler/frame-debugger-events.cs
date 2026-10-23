#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityCli
{
    /// <summary>profiler.frame-debugger-events — 逐个 raw event 读取，不复制完整事件数组。</summary>
    public class ProfilerFrameDebuggerEventsTool : IUnityCliTool<ProfilerFrameDebuggerEventsTool.Request, ProfilerFrameDebuggerEventsTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRange(1L, 500L)] public int? PageSize { get; set; }
            public string Cursor { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public long TotalEvents { get; set; }
            [UnityCliItemsRange(0, 500)] public List<FrameDebuggerEvent> Events { get; set; }
            public PageInfo PageInfo { get; set; }
            public UnityCliRequestBudgetUsage Budget { get; set; }
        }

        public sealed class PageInfo
        {
            public long ReturnedCount { get; set; }
            public long PageSize { get; set; }
            public string Cursor { get; set; }
            public string NextCursor { get; set; }
            public bool Complete { get; set; }
            public bool Truncated { get; set; }
            public string Generation { get; set; }
        }

        public sealed class FrameDebuggerEvent
        {
            public long Index { get; set; }
            public string Name { get; set; }
            public string ShaderName { get; set; }
            public string PassName { get; set; }
            public string VertexCount { get; set; }
            public string IndexCount { get; set; }
            public string InstanceCount { get; set; }
            public string MeshName { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.frame-debugger-events",
            Summary = "Page one bounded Frame Debugger capture from stable raw event indices.",
            Description = "Read raw Frame Debugger event indices directly without copying the full event array. pageSize is 1..500 (default 50); pass pageInfo.nextCursor unchanged to continue the same capture. The opaque cursor binds the Editor listener, CLI enable/disable generation, event count and capture boundary signature; a changed or deleted capture returns E_CONTINUATION_STALE. Event reflection, getters, scans, elapsed main-thread time and UTF-8 output share the documented request budget. Read-only in Edit and Play Mode.",
            Group = "profiler", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read first raw event page", Input = new Request { PageSize = 50 } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            request = request ?? new Request();
            if (!FrameDebuggerReflection.Available)
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_UNAVAILABLE", "FrameDebuggerUtility is unavailable.", "Use a Unity Editor with Frame Debugger support.");
            return new System.Threading.Tasks.ValueTask<Result>(ReadPage(request, ctx.UseReadBudget(request.Budget)));
        }

        /// <summary>按 raw event index 推进；只有成功 materialize 的事件才会移动 continuation。</summary>
        internal static Result ReadPage(Request request, UnityCliRequestBudget budget)
        {
            var pageSize = request.PageSize ?? 50;
            var total = FrameDebuggerReflection.GetEventCount(budget);
            var generation = FrameDebuggerReflection.CaptureGeneration(total, budget);
            var index = 0;
            if (!string.IsNullOrEmpty(request.Cursor))
            {
                string position;
                if (!UnityCliContinuationToken.TryParse(request.Cursor, "profiler.frame-debugger-events", generation, out position)
                    || !int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out index)
                    || index < 0 || index >= total)
                    throw Stale();
            }

            var events = new List<FrameDebuggerEvent>();
            while (index < total && events.Count < pageSize)
            {
                var current = index;
                if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeNode()) break;
                try
                {
                    var entry = new FrameDebuggerEvent { Index = current, Name = FrameDebuggerReflection.EventName(current, budget) };
                    var data = FrameDebuggerReflection.EventData(current, budget);
                    if (data != null)
                    {
                        entry.ShaderName = FrameDebuggerReflection.ReadField(data, "shaderName", budget);
                        entry.PassName = FrameDebuggerReflection.ReadField(data, "passName", budget);
                        entry.VertexCount = FrameDebuggerReflection.ReadField(data, "vertexCount", budget);
                        entry.IndexCount = FrameDebuggerReflection.ReadField(data, "indexCount", budget);
                        entry.InstanceCount = FrameDebuggerReflection.ReadField(data, "instanceCount", budget);
                        entry.MeshName = FrameDebuggerReflection.ReadField(data, "meshName", budget);
                    }
                    if (!budget.TryReserveSerializedUtf8(entry.Name) || !budget.TryReserveSerializedUtf8(entry.ShaderName)
                        || !budget.TryReserveSerializedUtf8(entry.PassName) || !budget.TryReserveSerializedUtf8(entry.VertexCount)
                        || !budget.TryReserveSerializedUtf8(entry.IndexCount) || !budget.TryReserveSerializedUtf8(entry.InstanceCount)
                        || !budget.TryReserveSerializedUtf8(entry.MeshName)) break;
                    events.Add(entry);
                    index++;
                }
                catch (UnityCliRequestBudgetExceededException) { break; }
            }
            if (events.Count == 0 && index < total) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension ?? "scannedRecords");
            var nextCursor = index < total
                ? UnityCliContinuationToken.Create("profiler.frame-debugger-events", generation, index.ToString(CultureInfo.InvariantCulture))
                : null;
            return new Result
            {
                TotalEvents = total,
                Events = events,
                PageInfo = new PageInfo
                {
                    ReturnedCount = events.Count,
                    PageSize = pageSize,
                    Cursor = request.Cursor,
                    NextCursor = nextCursor,
                    Complete = nextCursor == null,
                    Truncated = nextCursor != null,
                    Generation = generation
                },
                Budget = budget.Usage()
            };
        }

        private static UnityCliBridge.BridgeCommandException Stale()
        {
            return new UnityCliBridge.BridgeCommandException(
                "E_CONTINUATION_STALE",
                "The Frame Debugger continuation no longer identifies the same capture.",
                "Start a new profiler.frame-debugger-events read without cursor.");
        }
    }
}
#endif
