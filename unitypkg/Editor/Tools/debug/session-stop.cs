#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.session-stop — 关闭未来派发并执行有序清理管线，返回清理报告。
    /// 幂等：已 stopped 的定义重复调用只返回当前事实。
    /// </summary>
    public class DebugSessionStopTool : IUnityCliTool<DebugSessionStopRequest, DebugSessionStopResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.session-stop",
            Summary = "Stop a debug session, run its ordered cleanup pipeline and report exactly what was released.",
            Description = "Close future dispatch for one debug session and run its cleanup pipeline in the frozen order: stop new dispatch and pending injections, issue cooperative cancellation and resume, release virtual input, close the owner's observation and listeners, request detach of the owner's hooks and lookups, run explicit and still-applicable finite cleanup entries, release waits and handles, then emit the report. Stage one runs before the first cleanup entry: the session's dispatch gate closes immediately, so a hit on any thread after this call starts never enters user code again, while an in-flight frame completes on its own frame. The definition and its evidence stay loaded; only debug.session-remove deletes it. The report always contains every field category even when it is empty, and it names the entry that served this stop (stopEntry is main-thread for a normal call and pause-control-channel when a held main-thread pause was served off-main). The remaining categories are: stopped future entries, cancelled not-yet-started operations, released pauses, released input, closed observations, removed versus pending detours, how many other sessions still use the same target, active invocation, step and evaluate counts with thread and revision, cleanupPending, restored versus conflicting fields, staleTargets for writes whose target no longer exists, unrecoverableEffects for effects this owner cannot undo, declaredEffects for user code it did execute, untrackedEffectsPossible, unmanagedUnknown with the exact fact that unmanaged threads, tasks and devices cannot be statically proven absent, and the count and byte total of loaded non-unloadable assemblies. A facility that is not registered in this domain reports zero together with an explicit no-owner marker instead of being silently omitted. A state that is open moves open to stopping to stopped; a state that is already stopped is idempotent and returns the same terminal facts with changed=false and the same revision without running the pipeline again. Cleanup only ever touches this session's own resources, so it can never tear down another session's resources. Repeating stop after a pending cleanup is the supported way to retry it; debug.session-remove is refused with E_DEBUG_CLEANUP_PENDING until that cleanup completes. While a main-thread debug pause is held, stop of the session that owns the paused site is served by the pause control channel: it closes that owner's future dispatch and sends the resume signal, while cleanup that needs the main thread is reported as cleanupPending with the exact reason instead of being claimed as done. cleanupPending is derived from the session's real state: it is true whenever the session still has a registered resource, a handle whose release is unconfirmed, or a deferred cleanup entry (for example the main-thread-only store recovery facts that an off-main stop defers to the next main-thread publication), and the report names every such item with its exact reason. It is never false while such an item is listed. Unknown sessionId returns E_DEBUG_SESSION_NOT_FOUND; a publication failure returns E_DEBUG_STORE_FAILED. Side effects: writes this Editor session's session definition file. " + DebugLimits.SessionSummary(),
            Group = "debug",
            DestructiveHint = false,
            SideEffects = new[] { "debug-session" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Stop one session and read its cleanup report.", Input = new DebugSessionStopRequest { SessionId = "dbg-1" } }
            }
        };

        public ValueTask<DebugSessionStopResult> ExecuteAsync(DebugSessionStopRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 未知 session 必须在 effect barrier 之前失败。
            var plan = DebugSessionHost.PrepareStop(request);
            ctx.CommitEffect();
            return new ValueTask<DebugSessionStopResult>(DebugSessionHost.ApplyStop(plan));
        }
    }
}
#endif
