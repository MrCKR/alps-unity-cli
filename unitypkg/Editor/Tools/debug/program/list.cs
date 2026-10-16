#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.program.list — 按 program 序号分页读取程序定义摘要。它不编译、不激活、不改变 Unity 状态。
    /// </summary>
    public class DebugProgramListTool : IUnityCliTool<DebugProgramListRequest, DebugProgramListResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.program.list",
            Summary = "Page through debug program definitions with their revision, state, bindings and budget without compiling.",
            Description = "Read a page of program definition summaries. Inputs: an optional sessionId filter, an optional limit and an optional cursor returned by a previous call of this same Tool. Each item reports the programId, owning sessionId, label, entry type, state, revision, source SHA-256, state epoch and sizes, in-flight callback count, whether a continuous step driver is planted, the resolved entries with their signatures and every binding with whether it is attached. This is a pure read: it does not compile, does not attach a detour, does not restore a definition and does not write any persistent file, so it is safe to call at any time. The cursor is bound to the current Editor session and to the filter, and a cursor from a different session or a changed definition table is rejected as stale instead of returning a silently shifted page. Rejections: E_CONTINUATION_STALE for a stale cursor and E_SCHEMA_INVALID for a limit outside the declared page bounds. " + DebugLimits.ProgramSummary(),
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
                    Description = "Read the first page of program definitions of one session.",
                    Input = new DebugProgramListRequest { SessionId = "dbg-1", Limit = 20 }
                }
            }
        };

        public ValueTask<DebugProgramListResult> ExecuteAsync(DebugProgramListRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 纯读取：不跨效果屏障，不编译。
            return new ValueTask<DebugProgramListResult>(DebugProgramHost.List(request));
        }
    }
}
#endif
