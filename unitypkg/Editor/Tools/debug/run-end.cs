#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.run-end — 封闭窗口并计算判定，返回终态、verdict、首个反例、缺口、条件与清理状态。
    /// 它不终止尚未完成的程序，也不代表自动清理整个 session。
    /// </summary>
    public class DebugRunEndTool : IUnityCliTool<DebugRunEndRequest, DebugRunEndResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.run-end",
            Summary = "Seal one evidence window at an atomic capture boundary, compute its passed/failed/indeterminate verdict and return the first counterexample, gaps, conditions and cleanup status.",
            Description = "Seal exactly one evidence window and compute its verdict. Sealing is the only way a window moves from open to sealed; it is never reopened, and calling end again returns the frozen verdict with sealed=false and alreadySealed=true. Output: state (sealed, or interrupted when the runtime segment or session changed first), terminal, verdict (passed, failed or indeterminate), verdictReasons with stable reason codes, terminationReason, start and end sequences, per-check results with match, passed and failed counts plus coverageRequired and coverageProven, the first counterexample with clock, thread, expected, actual and evidence reference, coverage gaps, per-source facts, end conditions with observed and declaredOnly flags, per-segment results when segmentation was allowed, and the configuration fingerprint before and after. An assertion failure is not an error: this call answers ok:true with verdict failed, and only an unknown runId or unavailable session state makes it fail. Sealing closes evidence only: it does not stop a running program, release a session resource or clean up. Rejections: E_DEBUG_RUN_NOT_FOUND, E_SCHEMA_INVALID.", Notes = DebugDomainNotes.RunRead,
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
