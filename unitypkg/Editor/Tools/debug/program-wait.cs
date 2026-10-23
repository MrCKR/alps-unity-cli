#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.program-wait — 只观察一次已存在的程序运行，可选有界等待，返回终态与最后可信业务结果。
    /// 它不重新执行、不创建运行，也不改变任何 Unity 状态。
    /// </summary>
    public class DebugProgramWaitTool : IUnityCliTool<DebugProgramWaitRequest, DebugProgramWaitResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.program-wait",
            Summary = "Observe one program run until it is terminal or a bounded wait expires, and return its last trusted business result.",
            Description = "Read the current facts of exactly one programRunId, optionally waiting a bounded time for it to become terminal. Omitting waitMs only reads the current facts. This Tool never re-executes the entry, never creates a run, never cancels anything and never writes persistent state, so it is safe to repeat while a run is pending or in flight. With waitMs it yields to the central scheduler between frames and returns when the run reaches a terminal state or the budget expires; an expired window is a normal observation that reports terminal=false, which distinguishes it from E_EXECUTION_TIMEOUT. Output: state, terminal flag, run revision, execution site and target, step, check and delivery counts, the per-step revision list of a driver, the cancellation flag, whether synchronous code is still in flight, the result trust label and the last trusted business result. The last trusted result is reported only when an entry actually returned a value, so a wait expiry is not a completion and a check count is not a business verdict. The target names the declared execution point of that run. Rejections: E_DEBUG_PROGRAM_RUN_NOT_FOUND, E_SCHEMA_INVALID.", Notes = DebugDomainNotes.ProgramManage,
            Group = "debug",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 60000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Wait up to five seconds for a run to reach a terminal state.",
                    Input = new DebugProgramWaitRequest { ProgramRunId = "prun-1", WaitMs = 5000 }
                },
                new ToolExample
                {
                    Description = "Read the current facts of a run without waiting.",
                    Input = new DebugProgramWaitRequest { ProgramRunId = "prun-1" }
                }
            }
        };

        public ValueTask<DebugProgramWaitResult> ExecuteAsync(DebugProgramWaitRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 纯观察：不跨效果屏障，不改变任何 Unity 状态。
            return DebugProgramHost.WaitAsync(request, ctx.Wait, ct);
        }
    }
}
#endif
