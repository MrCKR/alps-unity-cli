#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.run-list — 分页读取窗口摘要；只描述每个窗口保留了什么，不重新执行或封闭任何窗口。
    /// </summary>
    public class DebugRunListTool : IUnityCliTool<DebugRunListRequest, DebugRunListResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.run-list",
            Summary = "List evidence windows with their verdict summary, detail availability and remaining run budget.",
            Description = "Read a bounded page of evidence-window summaries in creation order. Optional filters: sessionId, verdict (passed, failed or indeterminate), detailAvailable, limit and cursor. Each item reports runId, sessionId, state, terminal, verdict, verdictReasons, start and seal clocks, startSequence and endSequence, required and failed check counts, whether a first counterexample was published, whether detail is still available, whether segmentation was allowed and the segment count. A window counts as failed only when its verdict is failed: failedCheckCount is the number of required checks that failed inside it, not the window verdict. When the run budget releases a payload, detailAvailable is false and the summary still reports the verdict computed at the closing boundary. Filters advance over raw window order, so total stays the raw window count; nextCursor continues at the first window this page did not examine and is bound to the Editor session and the filters. Listing never recomputes, seals or stops anything, and only windows of the current domain generation are listed. Rejections: E_CONTINUATION_STALE, E_SCHEMA_INVALID.", Notes = DebugDomainNotes.RunRead,
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
                    Description = "List the evidence windows of the current domain generation.",
                    Input = new DebugRunListRequest()
                },
                new ToolExample
                {
                    Description = "List two windows of one session.",
                    Input = new DebugRunListRequest { SessionId = "dbg-1", Limit = 2 }
                },
                new ToolExample
                {
                    Description = "List the failed windows that still keep detailed evidence.",
                    Input = new DebugRunListRequest { Verdict = "failed", DetailAvailable = true }
                }
            }
        };

        public ValueTask<DebugRunListResult> ExecuteAsync(DebugRunListRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            ctx.UseReadBudget(new UnityCliReadBudgetInput
            {
                MaxGetters = UnityCliRequestBudget.AbsoluteMaxGetters,
                MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls,
                MaxScannedRecords = UnityCliRequestBudget.AbsoluteMaxScannedRecords,
                MaxSerializedUtf8Bytes = UnityCliRequestBudget.AbsoluteMaxSerializedUtf8Bytes
            });
            return new ValueTask<DebugRunListResult>(DebugRunHost.List(request));
        }
    }
}
#endif
