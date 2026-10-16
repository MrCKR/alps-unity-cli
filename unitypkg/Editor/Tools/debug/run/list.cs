#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.run.list — 分页读取窗口摘要；只描述每个窗口保留了什么，不重新执行或封闭任何窗口。
    /// </summary>
    public class DebugRunListTool : IUnityCliTool<DebugRunListRequest, DebugRunListResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.run.list",
            Summary = "List evidence windows with their verdict summary, detail availability and remaining run budget.",
            Description = "Read a bounded page of evidence-window summaries in creation order. Inputs: optional sessionId to restrict the page to one session, an optional limit and an optional cursor. Each item reports the runId, sessionId, state, terminal flag, verdict, verdictReasons, startedAtUtc, sealedAtUtc, startSequence and endSequence, the required check count, the failed check count, whether a first counterexample was published, whether detailed evidence is still available, whether the window allowed segmentation, and the segment count. Summaries exist for the most recently created windows of a session and the detailed evidence of only the newest few of them is kept: when the declared run budget releases a window's detailed evidence payload, detailAvailable is false and the summary still reports the verdict that was computed at the closing boundary, while the released per-check evidence rows are no longer readable. The verdict in a summary is the same value debug.run.end or debug.run.wait publishes for that window; listing never recomputes, seals or stops anything and never changes a Unity state. The page defaults to " + DebugLimits.RunListDefaultLimit + " items and never exceeds " + DebugLimits.RunListMaxLimit + "; continue with the returned nextCursor, which is bound to the current Editor session and to the sessionId filter and fails with E_CONTINUATION_STALE when it no longer applies. Budget: " + DebugLimits.RunSummary() + " Rejections: E_CONTINUATION_STALE for a cursor that no longer matches this Editor session or filter, and E_SCHEMA_INVALID for a limit outside the declared budget. Reliability: the list only contains windows of the current domain generation, so a Domain Reload or Editor restart empties it.",
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
