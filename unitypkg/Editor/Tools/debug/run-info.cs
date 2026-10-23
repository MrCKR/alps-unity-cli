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
            Description = "Read the complete report of one evidence window. A terminal window reports the report frozen when it closed: verdict, reasons, coverage facts and the bounded evidence payload are retained by runId until the run budget releases it. An open window is read live from the events the store retained inside it at that moment. Output: state (open, sealed or interrupted), terminal, verdict, verdictReasons, terminationReason, start and seal clocks, startSequence and endSequence, the capture object, per-check results with declared source, position, quantifier, status (satisfied, counterexample or insufficient), match, passed and failed counts, coverageRequired, coverageProven, sample-point-only, truncation, the retained matching evidence, the first counterexample with expected and actual values, coverage gaps, per-source facts, end conditions with observed and declaredOnly flags, per-segment results, the configuration fingerprint before and after, the environment readback and the run budget. An open window reports terminal=false with verdict indeterminate and the window-not-complete reason. Pure read. Rejections: E_DEBUG_RUN_NOT_FOUND, E_SCHEMA_INVALID.", Notes = DebugDomainNotes.RunRead,
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
