#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityCli
{
    /// <summary>profiler.frame-debugger.events — 逐个 raw event 读取，不复制完整事件数组。</summary>
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
            public List<FrameDebuggerEvent> Events { get; set; }
            public string Note { get; set; }
            public string NextCursor { get; set; }
            public bool Truncated { get; set; }
            public string Generation { get; set; }
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
            Summary = "Page Frame Debugger draw events directly by stable raw event position under one request budget.",
            Description = "Read raw Frame Debugger event indices directly; the tool never calls GetFrameEvents or copies the full event array. pageSize is 1..500 (default 50). totalEvents is Unity's exact raw count. nextCursor binds the Frame Debugger generation and next raw index; an old cursor returns E_CONTINUATION_STALE. Event reflection, getters, scans, elapsed main-thread time and UTF-8 output share budget defaults 10000/4096/2048/4096/250 ms/1 MiB and maxima 100000/20000/10000/20000/2000 ms/8 MiB. Read-only in Edit and Play Mode.",
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
            var firstName = total > 0 ? FrameDebuggerReflection.EventName(0, budget) : string.Empty;
            var lastName = total > 1 ? FrameDebuggerReflection.EventName(total - 1, budget) : firstName;
            var generation = UnityCliContinuationToken.Fingerprint(UnityCliReadGeneration.Session("profiler.frame-debugger.events") + "\0"
                + FrameDebuggerReflection.Generation.ToString(CultureInfo.InvariantCulture) + "\0" + total.ToString(CultureInfo.InvariantCulture)
                + "\0" + firstName + "\0" + lastName);
            var index = 0;
            if (!string.IsNullOrEmpty(request.Cursor))
            {
                string position;
                if (!UnityCliContinuationToken.TryParse(request.Cursor, "profiler.frame-debugger.events", generation, out position)
                    || !int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0 || index > total)
                    throw new UnityCliBridge.BridgeCommandException("E_CONTINUATION_STALE", "The Frame Debugger continuation does not match the current event generation.", "Start a new event read without cursor.");
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
                    if (!budget.TryReserveSerializedUtf8(entry.Name) || !budget.TryReserveSerializedUtf8(entry.ShaderName) || !budget.TryReserveSerializedUtf8(entry.PassName)) break;
                    events.Add(entry);
                    index++;
                }
                catch (UnityCliRequestBudgetExceededException) { break; }
            }
            var next = index < total ? UnityCliContinuationToken.Create("profiler.frame-debugger.events", generation, index.ToString(CultureInfo.InvariantCulture)) : null;
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                TotalEvents = total,
                Events = events,
                Note = total == 0 ? "Frame Debugger has no events." : null,
                NextCursor = next,
                Truncated = next != null,
                Generation = generation,
                Budget = budget.Usage()
            });
        }
    }
}
#endif
