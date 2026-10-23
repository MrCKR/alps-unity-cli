#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.thread-list — 读取 debug 设施实际观察到的线程事实。
    ///
    /// 纯读取：不创建线程记录、不追加事件、不注册回调、不读取 Unity 帧号。
    /// 它不声称枚举进程全部 managed thread。
    /// </summary>
    public class DebugThreadListTool : IUnityCliTool<DebugThreadListRequest, DebugThreadListResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.thread-list",
            Summary = "List the threads the debug facility actually observed, with per-thread record, frame and main-thread facts.",
            Description = "Read the threads this Editor domain actually observed while debugging. A thread appears only when it produced a retained debug event or currently holds an in-flight hook hit frame, so the observed scope is stated explicitly in the response and the process thread list is never presented as the managed thread set: a thread that produced nothing is absent rather than reported as idle. Each entry reports a stable per-Editor-run managed thread id, whether that thread is proven to be the main thread, how many retained debug events carry its id, how many hook hit frames it currently holds, whether it occupies a hook thread record, and which observation sources produced the entry. Threads are sorted main-thread first and then by ascending thread id, so paging positions are stable for one Editor session; limit defaults to " + DebugLimits.ThreadListDefaultLimit + " and never exceeds " + DebugLimits.ThreadListMaxLimit + ", and cursor is bound to the Editor session and to this observation scope, so a cursor from another Editor session returns E_CONTINUATION_STALE. Counts describe the retained debug window honestly: truncated reports that the retained window is incomplete and that evicted events no longer contribute to any thread count, and the exact eviction and coverage counts stay readable from debug.events-read. Main-thread identity is only reported for a thread already bound on an observed main-thread path in this domain generation; while no identity is bound, mainThread is false for every entry and mainThreadEvidence says so. Execution domain: this Editor session, Edit and Play. Target/thread: reads state owned by this Editor domain; it may be called from the main thread and reports other threads' facts without touching them. Behaviour: one synchronous bounded read on the calling thread; it registers no update callback and waits for nothing. Cancellation: the request deadline and cancellation token bound the read itself. Reload and recovery: nothing is persisted, so a Domain Reload or Editor restart simply removes the observed records and the next call reports the new domain's facts. Side effects: none. " + DebugLimits.ThreadSummary(),
            Group = "debug",
            DestructiveHint = false,
            SupportsDryRun = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "Read the first page of observed threads.", Input = new DebugThreadListRequest() },
                new ToolExample { Description = "Read at most ten observed threads.", Input = new DebugThreadListRequest { Limit = 10 } }
            }
        };

        public ValueTask<DebugThreadListResult> ExecuteAsync(DebugThreadListRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 纯读取：不提交 effect、不写存储、不注册任何回调。
            return new ValueTask<DebugThreadListResult>(DebugThreads.List(request, UnityCliBridge.EditorSessionId()));
        }
    }
}
#endif
