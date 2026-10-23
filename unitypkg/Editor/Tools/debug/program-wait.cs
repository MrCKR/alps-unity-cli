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
            Description = "Read the current facts of exactly one programRunId, optionally waiting a bounded time for it to become terminal. Inputs: programRunId (required) and an optional waitMs inside the declared wait budget; omitting waitMs only reads the current facts. This Tool never re-executes the entry, never creates a run, never cancels anything and never writes persistent state, so it is safe to call repeatedly while a run is pending or in flight. Cross-frame behaviour: with waitMs the call yields to the central scheduler between frames and returns when the run reaches a terminal state or the wait budget expires; without waitMs it returns immediately. A bounded wait window that expires is a normal observation, not a failure: the result reports terminal=false with the current evidence, which is what distinguishes it from a host request execution timeout (E_EXECUTION_TIMEOUT). Output: state, terminal flag, the run revision, the execution site and target, step, check and delivery counts, the per-step revision list of a driver, the cancellation flag, whether synchronous code is still in flight, the result trust label and the last trusted business result. The step count is the number of step invocations that actually executed for that run and the per-step revision list holds exactly one revision per executed step, so an entry that finished early is separable from a satisfied one by comparing the returned step count and its last step revision with what the entry declares. The target names the declared execution point of that run: the exact binding target when the run has one, the real PlayerLoop phase name for a phase run, and the central editor tick only for a run that really is served by it. Trust rules: the last trusted result is only reported when an entry actually returned a value, so waitMs reaching its budget is not a completion, a check count is not a business verdict, and only the terminal state plus the entry result and its recorded evidence describe what happened. The result field is a bounded projection materialised without running user code. Rejections: E_DEBUG_PROGRAM_RUN_NOT_FOUND for an unknown programRunId and E_SCHEMA_INVALID for a waitMs outside the declared budget. " + DebugLimits.ProgramSummary(),
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
