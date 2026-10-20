#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.events.read — 统一托管调试事件流的唯一只读入口。
    ///
    /// 多个 reader 各自持有 cursor，互不消费；读取不会删除事件。
    /// 它不是 Console 快照读取：debug.console.read 保留自己的语义与不可续读边界。
    /// </summary>
    public class DebugEventsReadTool : IUnityCliTool<DebugEventsRequest, DebugEventsResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.events.read",
            Summary = "Read the unified debug event stream by source, kind, identity, sequence range, thread, run and clock range with independent cursors.",
            Description = "Read the current Editor domain's managed debug events as bounded business records, ascending by sequence. This is the single non-consuming read over every debug source: hook and lifecycle events exist today, and program, log, check, pause and input sources are added later without a schema change. Filters: sourceId, kind, hookId, playSessionId, threadId, revision, invocationId, executionPoint, methodIdentityPrefix, mainThread, the inclusive sequence range fromSequence/toSequence and the inclusive clock range fromClockValue/toClockValue. methodIdentityPrefix is a prefix match on the reported methodIdentity; program events identify themselves as programId:entry, so that prefix narrows a read to one program, and an invocationId narrows it to one call. mainThread=true reads only main-thread events and mainThread=false reads only worker-thread events. fromSequence greater than toSequence returns E_SCHEMA_INVALID. Without a cursor the response returns the current last page; with a cursor it returns the records after the last sequence that cursor already scanned, so a filter never causes re-scanning of older records. The page defaults to " + DebugLimits.EventsDefaultLimit + " events and never exceeds " + DebugLimits.EventsMaxLimit + ". pass waitMs (bounded to " + DebugLimits.EventsMaxWaitMs + ") to wait for new events; the wait returns the current tail on expiry instead of failing, and reporting waitedMilliseconds is the only difference from an immediate read. A cursor is bound to the Editor session and to the normalized filters used when it was issued: changing the Editor session or changing filters returns E_CONTINUATION_STALE, and a cursor older than the retained window returns E_HOOK_EVENT_CURSOR_EXPIRED with oldestAvailableCursor in error.details. Coverage is reported honestly: dropped, gaps, truncated, sampled and sampleEvery describe the retained window, appendFailures counts emissions that a debug source could not record at all so a lost emission is never silent, and every counted failure leaves both a fixed log line and an event-not-recorded coverage fact carrying the lost source, kind and name in the same retained window, latestSequence is the current tail, and a gap is never presented as a complete observation. Every event records its source, capture point, thread, clockKind and clockValue; off-main events carry monotonically measured time plus lastObservedMainFrame because a worker thread must not read Unity main-thread clocks. This is the counterpart of debug.console.read, which reads the Console snapshot with its own log-type filters and has no reliable cursor: use debug.events.read for structured events with sequence-based continuation, not for console text. This is a pure read: it never enables autotick, never restores definitions, and buffered events of a removed definition stay readable. Side effects: none.",
            Group = "debug",
            DestructiveHint = false,
            SupportsDryRun = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "Read the current last page of every debug event.", Input = new DebugEventsRequest() },
                new ToolExample { Description = "Read the fault events of one hook.", Input = new DebugEventsRequest { SourceId = "hook", HookId = "hook-0000000000000000", Kind = "fault" } },
                new ToolExample { Description = "Wait up to five seconds for new hook events after the current tail.", Input = new DebugEventsRequest { SourceId = "hook", WaitMs = 5000 } },
                new ToolExample { Description = "Read one program's events inside a sequence window.", Input = new DebugEventsRequest { SourceId = "program", MethodIdentityPrefix = "prog-0001:", FromSequence = 120, ToSequence = 480 } }
            }
        };

        public ValueTask<DebugEventsResult> ExecuteAsync(DebugEventsRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 满页事件的 member 读取在同一次调用内会被预算多次计费；公开页上限才是本动作的真实工作量上界，
            // 因此这里声明框架允许的绝对读取预算，由 EventsMaxLimit 保证实际成本不会无界增长。
            ctx.UseReadBudget(new UnityCliReadBudgetInput
            {
                MaxGetters = UnityCliRequestBudget.AbsoluteMaxGetters,
                MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls,
                MaxScannedRecords = UnityCliRequestBudget.AbsoluteMaxScannedRecords,
                MaxSerializedUtf8Bytes = UnityCliRequestBudget.AbsoluteMaxSerializedUtf8Bytes
            });
            var waitMs = DebugEventStore.WaitMilliseconds(request == null ? (int?)null : request.WaitMs);
            if (waitMs <= 0) return new ValueTask<DebugEventsResult>(DebugEventStore.ReadEvents(request));
            return ReadAfterBoundedWaitAsync(request, waitMs, ctx, ct);
        }

        /// <summary>有界等待新事件后读取；到期返回当前尾部而不是失败，等待时间不计入主线程读取预算。</summary>
        private static async ValueTask<DebugEventsResult> ReadAfterBoundedWaitAsync(DebugEventsRequest request, int waitMs, UnityCliProjectToolContext ctx, CancellationToken ct)
        {
            var baseline = DebugEventStore.LatestSequence;
            var watch = Stopwatch.StartNew();
            await ctx.Wait.Until(() => DebugEventStore.HasEventsAfter(baseline), TimeSpan.FromMilliseconds(waitMs), ct);
            watch.Stop();
            ctx.ReadBudget.ResumeAfterWait();
            return DebugEventStore.ReadEvents(request, watch.Elapsed.TotalMilliseconds);
        }
    }
}
#endif
