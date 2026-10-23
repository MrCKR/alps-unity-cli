#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.run-wait — 等待一个窗口封闭，只返回可信状态与 verdict；它不停止窗口，也不重新执行任何东西。
    /// </summary>
    public class DebugRunWaitTool : IUnityCliTool<DebugRunWaitRequest, DebugRunWaitResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.run-wait",
            Summary = "Wait a bounded time for one evidence window to become terminal and return its trusted state and verdict.",
            Description = "Observe exactly one evidence window, optionally waiting a bounded time for it to reach a terminal state. Omitting waitMs only reads the current facts. With waitMs the call yields to the central scheduler between frames and returns when the window becomes sealed or interrupted or the wait budget expires, which reports terminal=false together with the current report and is a normal observation rather than E_EXECUTION_TIMEOUT. Ending the wait only ends the observation: it never seals the window, never stops a resource and never marks an active window complete, so a later debug.run-end or debug.run-wait still sees the real state. Output: waited, waitMs, terminal and the same report as debug.run-info, including state, verdict, verdictReasons, the first counterexample, coverage gaps, per-source coverage, conditions, segments, capture facts and the run budget. This Tool never recomputes evidence from anything except the unified event store, never re-executes a program entry and never changes a verdict an earlier call published. Rejections: E_DEBUG_RUN_NOT_FOUND, E_SCHEMA_INVALID.", Notes = DebugDomainNotes.RunRead,
            Group = "debug",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 60000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Wait up to ten seconds for a window to become terminal.",
                    Input = new DebugRunWaitRequest { RunId = "run-1", WaitMs = 10000 }
                },
                new ToolExample
                {
                    Description = "Read the current evidence of a window without waiting.",
                    Input = new DebugRunWaitRequest { RunId = "run-1" }
                }
            }
        };

        public ValueTask<DebugRunWaitResult> ExecuteAsync(DebugRunWaitRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 纯观察：不跨效果屏障，不封闭窗口，不改变任何 Unity 状态。
            ctx.UseReadBudget(new UnityCliReadBudgetInput
            {
                MaxGetters = UnityCliRequestBudget.AbsoluteMaxGetters,
                MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls,
                MaxScannedRecords = UnityCliRequestBudget.AbsoluteMaxScannedRecords,
                MaxSerializedUtf8Bytes = UnityCliRequestBudget.AbsoluteMaxSerializedUtf8Bytes
            });
            return DebugRunHost.WaitAsync(request, ctx.Wait, ct);
        }
    }
}
#endif
