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
            Description = "Close future dispatch for one debug session, run its cleanup pipeline and emit the report. A state that is open moves open to stopping to stopped; an already stopped session is idempotent and returns the same terminal facts with changed=false without running the pipeline again. The definition and its evidence stay loaded; only debug.session-remove deletes it. The report always contains every field category even when empty, and it names the entry that served this stop (main-thread, or pause-control-channel when a held main-thread pause was served off-main). Categories include stopped future entries, cancelled not-yet-started operations, released pauses and input, closed observations, removed versus pending detours, other sessions using the same target, active invocation, step and evaluate counts, cleanupPending, restored versus conflicting fields, staleTargets, unrecoverableEffects, untrackedEffectsPossible, unmanagedUnknown and the count and bytes of loaded non-unloadable assemblies. Unknown sessionId returns E_DEBUG_SESSION_NOT_FOUND; a publication failure returns E_DEBUG_STORE_FAILED.", Notes = DebugDomainNotes.SessionManage,
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
