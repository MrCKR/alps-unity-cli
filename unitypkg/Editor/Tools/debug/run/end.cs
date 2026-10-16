#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.run.end — 封闭窗口并计算判定，返回终态、verdict、首个反例、缺口、条件与清理状态。
    /// 它不终止尚未完成的程序，也不代表自动清理整个 session。
    /// </summary>
    public class DebugRunEndTool : IUnityCliTool<DebugRunEndRequest, DebugRunEndResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.run.end",
            Summary = "Seal one evidence window at an atomic capture boundary, compute its passed/failed/indeterminate verdict and return the first counterexample, gaps, conditions and cleanup status.",
            Description = "Seal exactly one evidence window and compute its verdict. Input: runId (required). Sealing is the only way a window moves from open to sealed; a window is never reopened, and calling end again returns the frozen verdict with sealed false and alreadySealed true. Before sealing, end takes one atomic capture: the boundary sequence from the single event store, the same instant's dropped count and append-failure count, and the central scheduler queue watermark (pendingDeliveries, queueHighWaterMark, expiredDeliveries, droppedDeliveries and drivers). That is how end confirms which events had already entered the store: everything with sequence at or below boundarySequence is inside the window, and when the scheduler still holds pending deliveries or drivers at that boundary the events they will produce are outside this window and the verdict reports queue-not-drained instead of pretending to be complete. Output: state (sealed, or interrupted when the runtime segment or session changed first), terminal, verdict (passed, failed or indeterminate), verdictReasons with stable machine-readable reason codes (counterexample, no-evidence, coverage-gap, sampled-only, evidence-truncated, window-not-complete, window-interrupted, configuration-changed, configuration-boundary-missing, too-many-segments, queue-not-drained, detail-evicted, coverage-not-required), terminationReason, the start and end sequences, the capture object, per-check results with match, passed and failed counts, a coverageRequired flag that states whether that quantifier consumes a caller-declared coverage requirement, and a coverageProven flag for the interval of that check's required sources, the first counterexample with its clock, thread, expected and actual value and its evidence reference into the event store, the coverage gaps that name unreadable sequence ranges, per-source coverage facts, the end conditions with observed and declaredOnly flags, per-segment results when the window allowed segmentation, the configuration fingerprint before and after, and the run budget. Verdict rules: failed is returned as soon as a complete counterexample exists and is never downgraded by later unrelated gaps; passed requires every required check satisfied by its own quantifier rule (once and every by a retained matching event, whole-window and absent additionally by the caller-declared coverage proof) over an interval whose coverage is readable, an observed end condition and a drained capture boundary, so a check that consumes no declared coverage proof is never held back by coverageRequired false; indeterminate is returned when the retained evidence cannot prove the checks, when the interval of a check's required sources has a gap, when the matching events come from a sampled stream, when evidence was truncated, when a related configuration changed inside a strict window, when the window was interrupted, when the declared coverage requirement is missing for a whole-window or absent check, or when the window's detailed evidence was evicted by the budget. An assertion failure is NOT an error: this call answers ok true with verdict failed, and there is no assertion-failed error code; only an unknown runId or an unavailable session state makes this call fail. Sealing closes evidence only: it does not stop a running program, does not release a session resource and does not clean up the session, so read debug.program.info and use debug.program.stop when the program must also end. Budget: " + DebugLimits.RunSummary() + " Rejections: E_DEBUG_RUN_NOT_FOUND for an unknown runId or a runId from a previous domain generation, E_SCHEMA_INVALID for a missing runId. Reliability: a sealed window keeps its frozen boundary, capture and bounded evidence payload, so repeating this call reports the same facts until the declared run budget releases that payload; a Domain Reload or Editor restart removes the window and an old runId is answered with E_DEBUG_RUN_NOT_FOUND.",
            Group = "debug",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Seal a window and read its verdict.",
                    Input = new DebugRunEndRequest { RunId = "run-1" }
                }
            }
        };

        public ValueTask<DebugRunEndResult> ExecuteAsync(DebugRunEndRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 封闭只写本域机制状态：不跨效果屏障，不写存储文件，不改变 Unity 状态。
            ctx.UseReadBudget(new UnityCliReadBudgetInput
            {
                MaxGetters = UnityCliRequestBudget.AbsoluteMaxGetters,
                MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls,
                MaxScannedRecords = UnityCliRequestBudget.AbsoluteMaxScannedRecords,
                MaxSerializedUtf8Bytes = UnityCliRequestBudget.AbsoluteMaxSerializedUtf8Bytes
            });
            return new ValueTask<DebugRunEndResult>(DebugRunHost.End(request));
        }
    }
}
#endif
