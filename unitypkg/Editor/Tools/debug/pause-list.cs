#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.pause-list — 读取当前仍停住的暂停现场与它们的 timeout、线程、调用位置和状态。
    ///
    /// 纯读取：不服务现场、不解除现场、不追加事件、不注册回调。
    /// </summary>
    public class DebugPauseListTool : IUnityCliTool<DebugPauseListRequest, DebugPauseListResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.pause-list",
            Summary = "List the pause sites that are stopped right now, with timeout, thread, call location and state.",
            Description = "Read the pause sites this Editor domain is holding at this moment. A pause site exists only because a hook definition declared a pause breakpoint and that exact target is currently hit and stopped, so this is a read of live debugger state and not a list of configured breakpoints: use debug.hook-info to read a definition's declared breakpoint. Each entry reports the pauseId, its state, the stopped managed thread id and whether that thread is proven to be the main thread, the owning hookId and its fixed revision, the invocationId, the runtimeId and domainGeneration the site is bound to, the exact method and target, the position (prefix means before the original method, postfix means after it returned), the owning sessionId when the breakpoint declared one, the declared timeout kind and duration, the effective bound (an unbounded site is reported as unbounded, and a session deadline would be reported here as its bound instead), the elapsed pause time, the current mailbox load (waiting requests and the executing operation), whether a resume has already been requested, and how many operations this site has served, refused and suppressed. A resumed or timed-out pauseId is never listed again and can never be reused; calling another pause tool with it returns E_DEBUG_PAUSE_STALE with its terminal state. Filters: sessionId, hookId, threadId, state (paused or resuming), invocationId and mainThread advance over the raw site order, so total stays the raw site count and nextCursor continues at the first site this page did not examine; the cursor is bound to the current Editor session and to every filter above and returns E_CONTINUATION_STALE when that scope no longer applies. Coverage is reported honestly: skippedSites counts hits that were not paused because the simultaneous-site limit was already reached, suppressedNestedHits counts hits rejected only because the hit thread was already serving a site, plus their bounded reason breakdown. Execution domain: this Editor session, Edit and Editor Play. Target/thread: reads state owned by this Editor domain from whatever thread the request is served on; it does not touch the paused threads. Behaviour: one synchronous bounded read on the calling thread; it registers no update callback and waits for nothing, so it is safe to call while other threads are paused. Cancellation: the request deadline and cancellation token bound the read itself. Reload and recovery: pause sites belong to one Editor domain generation and one Play segment, so a Domain Reload, Play exit or Editor restart removes them and the next call reports the new generation's facts. Risks: none beyond reading this domain's own state; other threads, servers, external clocks and object mutation continue while a thread is paused. Side effects: none. " + DebugLimits.PauseSummary(),
            Group = "debug",
            DestructiveHint = false,
            SupportsDryRun = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "Read every pause site that is stopped now.", Input = new DebugPauseListRequest() },
                new ToolExample { Description = "Read the pause sites owned by one hook definition.", Input = new DebugPauseListRequest { HookId = "hook-0000000000000000" } },
                new ToolExample { Description = "Read the pause sites of one session and one thread.", Input = new DebugPauseListRequest { SessionId = "dbg-1", ThreadId = 17 } },
                new ToolExample { Description = "Read the main-thread sites that are already resuming.", Input = new DebugPauseListRequest { State = "resuming", MainThread = true } }
            }
        };

        public ValueTask<DebugPauseListResult> ExecuteAsync(DebugPauseListRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 纯读取：不跨效果屏障、不服务现场、不写存储。
            ctx.UseReadBudget(new UnityCliReadBudgetInput
            {
                MaxGetters = UnityCliRequestBudget.AbsoluteMaxGetters,
                MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls,
                MaxScannedRecords = UnityCliRequestBudget.AbsoluteMaxScannedRecords,
                MaxSerializedUtf8Bytes = UnityCliRequestBudget.AbsoluteMaxSerializedUtf8Bytes
            });
            return new ValueTask<DebugPauseListResult>(DebugPauseHost.List(request, UnityCliBridge.EditorSessionId()));
        }
    }
}
#endif
