#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.hook.list — 只读分页摘要当前 Editor Session 的 Hook Definition。
    /// 纯读取，不恢复、不编译、不清理。
    /// </summary>
    public class MethodHookListTool : IUnityCliTool<MethodHookListRequest, MethodHookListResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.hook.list",
            Summary = "List hook definitions of the current Editor session with state, label, thread scope and paged cursors.",
            Description = "Read a bounded page of Hook Definition summaries for the current Editor session, ordered by install order. Each item reports the hookId, the live revision, the owning editorSessionId, the declared listener (trigger, phase, hit budget and consumed hits) and the breakpoint summary: the revision in an item is exactly the expectedRevision that debug.hook.remove, debug.hook.replace, debug.hook.enable and debug.hook.disable expect, and the editorSessionId is the session those calls require, so a client that holds only this page can clean up a definition in one round trip instead of calling debug.hook.info first. Optional state filters armed, dormant, restoring, disabled or faulted, and an optional label filter matches the label case-insensitively. The page defaults to " + DebugLimits.ListDefaultLimit + " items and never exceeds " + DebugLimits.ListMaxLimit + "; continue with the returned nextCursor, which is bound to the current Editor session and the normalized filters and fails with E_CONTINUATION_STALE when either changes. Removed definitions are absent from this list even while their buffered events stay readable. Definitions dispatch on any supported managed thread, so hitCount counts method hits from every thread, while a listener that declares trigger player-loop-phase fires on its declared PlayerLoop phase in Editor Play and reports its consumed hits under listener.hits. This is a pure read: it does not recover, recompile, reattach or clean anything, so a faulted definition stays faulted until debug.hook.enable is called. Side effects: none.",
            Group = "debug",
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
            var pageSize = request == null || request.Limit == null ? DebugLimits.ListDefaultLimit : Math.Min(Math.Max(1, request.Limit.Value), DebugLimits.ListMaxLimit);
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
