#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>profiler.frame-debugger.events — 逐个 raw event 读取，不复制完整事件数组。</summary>
    public class ProfilerFrameDebuggerEventsTool : IUnityCliTool<ProfilerFrameDebuggerEventsTool.Request, ProfilerFrameDebuggerEventsTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRange(1L, 500L)] public int? PageSize { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public long TotalEvents { get; set; }
            [UnityCliItemsRange(0, 500)] public List<FrameDebuggerEvent> Events { get; set; }
            public string Note { get; set; }
            public bool Truncated { get; set; }
            public UnityCliRequestBudgetUsage Budget { get; set; }
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
            Name = "profiler.frame-debugger.events",
            Summary = "Read one bounded, non-resumable Frame Debugger event snapshot under a request budget.",
            Description = "Read raw Frame Debugger event indices directly without copying the full event array. Unity does not expose a reliable frame/capture identity, so this tool intentionally provides no continuation cursor: truncated=true means the caller must start a fresh snapshot and may choose a larger pageSize/budget. pageSize is 1..500 (default 50). Event reflection, getters, scans, elapsed main-thread time and UTF-8 output share the documented request budget. Read-only in Edit and Play Mode.",
            Group = "profiler", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read first raw event page", Input = new Request { PageSize = 50 } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            request = request ?? new Request();
            if (!FrameDebuggerReflection.Available)
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_UNAVAILABLE", "FrameDebuggerUtility is unavailable.", "Use a Unity Editor with Frame Debugger support.");
            var budget = ctx.UseReadBudget(request.Budget);
            var pageSize = request.PageSize ?? 50;
            var total = FrameDebuggerReflection.GetEventCount(budget);
            var index = 0;
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
            if (events.Count == 0 && total > 0) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension ?? "scannedRecords");
            var truncated = index < total;
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                TotalEvents = total,
                Events = events,
                Note = total == 0 ? "Frame Debugger has no events." : truncated ? "Snapshot truncated; Frame Debugger events are intentionally non-resumable because Unity exposes no stable frame identity." : null,
                Truncated = truncated,
                Budget = budget.Usage()
            });
        }
    }
}
#endif
