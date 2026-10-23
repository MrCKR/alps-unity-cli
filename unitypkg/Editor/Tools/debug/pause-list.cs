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
            Description = "Read the pause sites this Editor domain holds right now. A site exists only while a hook definition that declared a pause breakpoint is hit and stopped, so this is live debugger state and not a list of configured breakpoints; use debug.hook-info for a declared breakpoint. Each entry reports pauseId, state, the stopped managed thread id with mainThread proof, owning hookId and revision, invocationId, runtimeId, domainGeneration, method and target, position (prefix before the original body, postfix after it returned), owning sessionId, declared timeout kind and duration, effective bound, elapsed pause time, mailbox load, whether a resume was requested, and served, refused and suppressed operation counts. Filters sessionId, hookId, threadId, state, invocationId and mainThread advance over raw site order, so total stays the raw site count; nextCursor continues at the first site this page did not examine and fails with E_CONTINUATION_STALE when its scope changes. Coverage is reported as skippedSites and suppressedNestedHits with bounded reasons. Pure read on the calling thread: no callback, no waiting.", Notes = DebugDomainNotes.PauseRead,
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
