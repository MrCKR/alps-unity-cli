#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.session-remove — 删除一个已完成清理的 session 定义并释放它的槽位。
    /// 未停止或仍有待清理项时返回 E_DEBUG_CLEANUP_PENDING 与待清理明细。
    /// </summary>
    public class DebugSessionRemoveTool : IUnityCliTool<DebugSessionRemoveRequest, DebugSessionRemoveResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.session-remove",
            Summary = "Delete one stopped debug session definition and free its session slot.",
            Description = "Delete one session definition from the current Editor session and free the slot it occupied. It succeeds only when the definition is already stopped and has no pending cleanup; an open or stopping session, or a stopped session that still has a registered resource, a handle whose release is unconfirmed or a deferred cleanup entry, returns E_DEBUG_CLEANUP_PENDING together with the current state, the active resource count and the pending cleanup detail, and is never reported as a partial success. A removed session therefore never leaves a resource registered under its name. Run debug.session-stop first, and repeat stop to retry a pending cleanup. An unknown sessionId returns E_DEBUG_SESSION_NOT_FOUND. Freed slots are reported as sessionsUsed and sessionsRemaining, so a caller can retry debug.session-create. Removing a session never affects another session's resources and never deletes the shared session file. A publication failure returns E_DEBUG_STORE_FAILED and the definition stays loaded. Side effects: writes this Editor session's session definition file. This is the only action that releases the session definition slot: debug.session-stop keeps the definition and its evidence, so a stopped definition keeps holding its slot until this call succeeds. " + DebugLimits.SessionSummary(),
            Group = "debug",
            DestructiveHint = false,
            SideEffects = new[] { "debug-session" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "Remove a stopped session.", Input = new DebugSessionRemoveRequest { SessionId = "dbg-1" } }
            }
        };

        public ValueTask<DebugSessionRemoveResult> ExecuteAsync(DebugSessionRemoveRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 状态与待清理判定全部在 effect barrier 之前完成。
            var plan = DebugSessionHost.PrepareRemove(request);
            ctx.CommitEffect();
            return new ValueTask<DebugSessionRemoveResult>(DebugSessionHost.ApplyRemove(plan));
        }
    }
}
#endif
