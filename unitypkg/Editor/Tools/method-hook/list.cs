#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// method-hook.list — 只读分页摘要当前 Editor Session 的 Hook Definition。
    /// 纯读取，不恢复、不编译、不清理。
    /// </summary>
    public class MethodHookListTool : IUnityCliTool<MethodHookListRequest, MethodHookListResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "method-hook.list",
            Summary = "List hook definitions of the current Editor session with state, label and paged cursors.",
            Description = "Read a bounded page of Hook Definition summaries for the current Editor session, ordered by install order. Optional state filters armed, dormant, restoring, disabled or faulted, and an optional label filter matches the label case-insensitively. The page defaults to " + MethodHookLimits.ListDefaultLimit + " items and never exceeds " + MethodHookLimits.ListMaxLimit + "; continue with the returned nextCursor, which is bound to the current Editor session and the normalized filters and fails with E_CONTINUATION_STALE when either changes. Removed definitions are absent from this list even while their buffered events stay readable. This is a pure read: it does not recover, recompile, reattach or clean anything, so a faulted definition stays faulted until method-hook.enable is called. Side effects: none.",
            Group = "method-hook",
            DestructiveHint = false,
            SupportsDryRun = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "List the first page of every definition.", Input = new MethodHookListRequest() },
                new ToolExample { Description = "List only faulted definitions.", Input = new MethodHookListRequest { State = "faulted" } }
            }
        };

        public ValueTask<MethodHookListResult> ExecuteAsync(MethodHookListRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 满页定义摘要同样需要显式声明 read budget，避免终端校验阶段被默认上限拒绝。
            var pageSize = request == null || request.Limit == null ? MethodHookLimits.ListDefaultLimit : Math.Min(Math.Max(1, request.Limit.Value), MethodHookLimits.ListMaxLimit);
            ctx.UseReadBudget(new UnityCliReadBudgetInput
            {
                MaxGetters = UnityCliRequestBudget.AbsoluteMaxGetters,
                MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls,
                MaxScannedRecords = UnityCliRequestBudget.AbsoluteMaxScannedRecords
            });
            return new ValueTask<MethodHookListResult>(MethodHookHost.List(request));
        }
    }
}
#endif
