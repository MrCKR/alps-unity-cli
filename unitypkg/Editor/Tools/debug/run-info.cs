#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.run-info — 读取一个窗口的完整报告：终态、verdict、首反例、缺口、条件与清理状态。
    /// 它是纯读取：不封闭窗口、不改变判定、不写任何文件。
    /// </summary>
    public class DebugRunInfoTool : IUnityCliTool<DebugRunInfoRequest, DebugRunInfoResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.run-info",
            Summary = "Read one evidence window in full: state, verdict, first counterexample, coverage gaps, conditions and cleanup status.",
            Description = "Read the complete report of one evidence window. Input: runId (required). A terminal window (sealed or interrupted) reports the report that was frozen when it closed: the verdict, reasons, coverage facts and the bounded evidence payload are retained by runId until the declared run budget releases that payload, so a later read never silently loses a proven counterexample and never re-derives a different verdict from a shrinking event ring. An open window is read live instead, from the events the single unified event store actually retained inside the window at that moment. Output: state (open, sealed or interrupted), terminal, verdict (passed, failed or indeterminate), verdictReasons with stable reason codes, terminationReason, startedAtUtc, sealedAtUtc, startSequence and endSequence, the capture object (boundarySequence, droppedAtBoundary, appendFailuresAtBoundary, pendingDeliveries, queueHighWaterMark, drivers and queueDrained), per-check results with the declared source, position and quantifier, status (satisfied, counterexample or insufficient), match, passed and failed counts, a coverageRequired flag that states whether that quantifier consumes a caller-declared coverage requirement, a coverageProven flag for the interval of that check's required sources, sample-point-only flag, truncation flag, the retained matching evidence with the real event fields (sequence, thread, clock kind and value, executionPoint, revision, methodIdentity and passed), each check's declaredCoverage text recorded but never counted as proof, the first counterexample with its expected and actual value plus its evidence reference into the event store, coverage gaps that name unreadable sequence ranges, per-source coverage facts including whether proven coverage was even required, end conditions with observed and declaredOnly flags, per-segment results when configuration changed inside a segment-allowed window, the configuration fingerprint before and after, the environment readback, and the run budget. An open window is reported honestly: terminal false with verdict indeterminate and the window-not-complete reason, unless a complete counterexample already exists, in which case the verdict is already failed. An assertion failure is never an error code: this call answers ok true with the verdict in the payload. Verdict rules: failed requires a complete counterexample, passed requires every required check satisfied by its own quantifier rule (once and every by a retained matching event, whole-window and absent additionally by the caller-declared coverage proof) over a readable interval plus an observed end condition, and indeterminate names every missing fact, including coverage gaps, sampled-only streams, truncated evidence, missing coverage declarations for whole-window or absent checks, related configuration changes, an interrupted window, an undrained capture boundary and released detail. When the declared run budget releases this window's detailed evidence payload, detailAvailable is false, the reason detail-evicted is reported and the released per-check evidence rows, the first counterexample payload and the per-segment check statuses are no longer readable. Budget: " + DebugLimits.RunSummary() + " Rejections: E_DEBUG_RUN_NOT_FOUND for an unknown runId or a runId from a previous domain generation, and E_SCHEMA_INVALID for a missing runId. Reliability: this is a pure read and safe to call repeatedly, including while programs run inside the window.",
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
                    Description = "Read the current report of a window.",
                    Input = new DebugRunInfoRequest { RunId = "run-1" }
                }
            }
        };

        public ValueTask<DebugRunInfoResult> ExecuteAsync(DebugRunInfoRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            ctx.UseReadBudget(new UnityCliReadBudgetInput
            {
                MaxGetters = UnityCliRequestBudget.AbsoluteMaxGetters,
                MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls,
                MaxScannedRecords = UnityCliRequestBudget.AbsoluteMaxScannedRecords,
                MaxSerializedUtf8Bytes = UnityCliRequestBudget.AbsoluteMaxSerializedUtf8Bytes
            });
            return new ValueTask<DebugRunInfoResult>(DebugRunHost.Info(request));
        }
    }
}
#endif
