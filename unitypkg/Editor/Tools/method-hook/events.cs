#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// method-hook.events — 只读有界事件续读。
    /// 多个 reader 各自持有 cursor，互不消费；读取不会删除事件。
    /// </summary>
    public class MethodHookEventsTool : IUnityCliTool<MethodHookEventsRequest, MethodHookEventsResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "method-hook.events",
            Summary = "Page bounded hook events by kind, hook and play session with independent cursors.",
            Description = "Read invocation, custom, fault and lifecycle events as bounded business records, ascending by sequence. Optional filters: hookId, kind (prefix, postfix, custom, fault, lifecycle) and playSessionId. Without a cursor the response returns the current last page; with a cursor it returns records after the last sequence that cursor already scanned, so a filter never causes re-scanning of older records. The page defaults to " + MethodHookLimits.EventsDefaultLimit + " events and never exceeds " + MethodHookLimits.EventsMaxLimit + ". A cursor is bound to the Editor session and to the normalized filters used when it was issued: changing instance, restarting the Editor or changing filters returns E_CONTINUATION_STALE, and a cursor older than the retained window returns E_HOOK_EVENT_CURSOR_EXPIRED with oldestAvailableCursor and the missing range. hasMore and gaps report the retained window honestly; events are evicted by count and byte bounds, and a gap is never presented as a complete observation. Reading never consumes a shared cursor, so several readers can each continue independently, and buffered events of a removed definition stay readable. This is a pure read: it never enables autotick, long-polls, or restores definitions. Side effects: none.",
            Group = "method-hook",
            DestructiveHint = false,
            SupportsDryRun = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "Read the current last page of every event.", Input = new MethodHookEventsRequest() },
                new ToolExample { Description = "Read fault events of one hook.", Input = new MethodHookEventsRequest { HookId = "hook-0000000000000000", Kind = "fault" } }
            }
        };

        public ValueTask<MethodHookEventsResult> ExecuteAsync(MethodHookEventsRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
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
            return new ValueTask<MethodHookEventsResult>(MethodHookHost.ReadEvents(request));
        }
    }
}
#endif
