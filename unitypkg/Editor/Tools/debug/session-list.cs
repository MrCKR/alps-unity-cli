#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.session-list — 只读分页读取当前 Editor Session 的 session 定义摘要。
    /// 不写 session 文件、不运行清理、不改变 Unity 状态。
    /// </summary>
    public class DebugSessionListTool : IUnityCliTool<DebugSessionListRequest, DebugSessionListResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.session-list",
            Summary = "List debug session definitions of the current Editor session with resources, cleanup and budget summary.",
            Description = "Read a bounded page of session definition summaries for the current Editor session, ordered by session id. Each item reports the definition (label, activationScope, clockKind, state, revision, timestamps), the active resources still attached to it, the handle-count summary, whether cleanup is pending, and whether the definition was restored by a Domain Reload without any activation. Facility owners that are not registered in this domain report zero together with an explicit no-owner marker instead of being omitted. The page defaults to " + DebugLimits.SessionListDefaultLimit + " items and never exceeds " + DebugLimits.SessionListMaxLimit + "; continue with the returned nextCursor, which is bound to the current Editor session and fails with E_CONTINUATION_STALE when it no longer applies. The response also reports storeUsable with storeReason and the remaining budget; a storeUsable of false means the single session file was left unloaded (empty, over budget, unparsable, wrong format, editorSessionId, domainGeneration, checksum, or a regressed publication) or belongs to a previous Editor session, and the next successful mutation republishes it. This is a pure read: it does not write the session file, does not run the cleanup pipeline and does not change Unity state. Removed sessions are absent from this list immediately. An optional state filter (open, stopping or stopped) narrows the page to one state: total, cursor and hasMore then describe only the matching definitions, and a cursor from a different filter returns E_CONTINUATION_STALE instead of mixing two sets. A stopped definition still holds its session slot until debug.session-remove deletes it, so state stopped is the filter that finds reclaimable definitions. Side effects: none. " + DebugLimits.SessionSummary(),
            Group = "debug",
            DestructiveHint = false,
            SupportsDryRun = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "List every loaded session definition.", Input = new DebugSessionListRequest() },
                new ToolExample { Description = "List a single-item page.", Input = new DebugSessionListRequest { Limit = 1 } }
            }
        };

        public ValueTask<DebugSessionListResult> ExecuteAsync(DebugSessionListRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 满页摘要同样需要显式声明读取预算，避免终端校验阶段被默认上限拒绝。
            ctx.UseReadBudget(new UnityCliReadBudgetInput
            {
                MaxGetters = UnityCliRequestBudget.AbsoluteMaxGetters,
                MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls,
                MaxScannedRecords = UnityCliRequestBudget.AbsoluteMaxScannedRecords,
                MaxSerializedUtf8Bytes = UnityCliRequestBudget.AbsoluteMaxSerializedUtf8Bytes
            });
            return new ValueTask<DebugSessionListResult>(DebugSessionHost.List(request));
        }
    }
}
#endif
