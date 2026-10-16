#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.run.wait — 等待一个窗口封闭，只返回可信状态与 verdict；它不停止窗口，也不重新执行任何东西。
    /// </summary>
    public class DebugRunWaitTool : IUnityCliTool<DebugRunWaitRequest, DebugRunWaitResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.run.wait",
            Summary = "Wait a bounded time for one evidence window to become terminal and return its trusted state and verdict.",
            Description = "Observe exactly one evidence window, optionally waiting a bounded time for it to reach a terminal state. Inputs: runId (required) and an optional waitMs inside the declared wait budget; omitting waitMs only reads the current facts. Cross-frame behaviour: with waitMs the call yields to the central scheduler between frames and returns when the window becomes sealed or interrupted, or when the wait budget expires; without waitMs it returns immediately. A bounded wait that expires is a normal observation, not a failure: the result reports terminal false together with the current report, which is what distinguishes it from a host request execution timeout (E_EXECUTION_TIMEOUT). Requesting the end of the wait only ends the observation: it never seals the window, never stops a resource and never marks an active window as complete, so a later debug.run.end or debug.run.wait still sees the real state. Output: waited, waitMs, terminal and the same report as debug.run.info, including state, verdict, verdictReasons, the first counterexample, coverage gaps, per-source coverage, conditions, segments, the capture facts and the run budget. wait never recomputes evidence from anything except the unified event store, never re-executes a program entry and never changes a verdict an earlier call already published. Verdict semantics are the same as debug.run.end: failed needs a complete counterexample, passed needs every required check satisfied by its own quantifier rule (once and every by a retained matching event, whole-window and absent additionally by the caller-declared coverage proof) over a readable interval plus an observed end condition, and indeterminate names the exact missing fact. Budget: " + DebugLimits.RunSummary() + " Rejections: E_DEBUG_RUN_NOT_FOUND for an unknown runId or a runId from a previous domain generation, and E_SCHEMA_INVALID for a waitMs outside the declared budget.",
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
