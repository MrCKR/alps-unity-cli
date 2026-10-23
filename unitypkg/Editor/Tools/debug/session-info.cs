#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.session-info — 只读读取一个 session 定义的完整事实。
    /// 不写 session 文件、不运行清理、不改变 Unity 状态。
    /// </summary>
    public class DebugSessionInfoTool : IUnityCliTool<DebugSessionInfoRequest, DebugSessionInfoResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.session-info",
            Summary = "Read one debug session definition with its resources, handles, budget, last run and cleanup status.",
            Description = "Read the complete current facts of one session definition: label, activationScope, clockKind, state (open, stopping or stopped), revision, timestamps, active resources still attached, the handle-count summary, the remaining budget, the most recent run summary and the cleanup status. Every field category is always present; facility owners not registered in this domain report zero with an explicit no-owner marker instead of being omitted. Pure read: it does not write the session file, run the cleanup pipeline, publish anything or change Unity state, and resource states are reported as already observed. An unknown sessionId returns E_DEBUG_SESSION_NOT_FOUND with storeUsable and storeReason so a missing id is distinguishable from an unloaded store; the store is unusable when the file is empty, over budget, unparsable, carries a different format, editorSessionId, domainGeneration or checksum, or a regressed publication, and affected sessions stay unloaded. Use debug.session-list to discover ids and debug.session-stop to run cleanup.", Notes = DebugDomainNotes.SessionManage,
            Group = "debug",
            DestructiveHint = false,
            SupportsDryRun = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "Read the full facts of one session.", Input = new DebugSessionInfoRequest { SessionId = "dbg-1" } }
            }
        };

        public ValueTask<DebugSessionInfoResult> ExecuteAsync(DebugSessionInfoRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 结果 DTO 会在同一请求内被框架多次有预算地读取，因此声明框架允许的绝对读取预算。
            ctx.UseReadBudget(new UnityCliReadBudgetInput
            {
                MaxGetters = UnityCliRequestBudget.AbsoluteMaxGetters,
                MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls,
                MaxScannedRecords = UnityCliRequestBudget.AbsoluteMaxScannedRecords,
                MaxSerializedUtf8Bytes = UnityCliRequestBudget.AbsoluteMaxSerializedUtf8Bytes
            });
            return new ValueTask<DebugSessionInfoResult>(DebugSessionHost.Info(request));
        }
    }
}
#endif
