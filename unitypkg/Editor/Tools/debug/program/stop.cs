#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.program.stop — 取消一次运行尚未执行的步骤、协作停止并释放该运行拥有的资源。
    /// 它不删除程序定义，也不触碰该程序的其他运行。
    /// </summary>
    public class DebugProgramStopTool : IUnityCliTool<DebugProgramStopRequest, DebugProgramStopResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.program.stop",
            Summary = "Cancel not-yet-executed steps of one program run, stop cooperatively and release the resources that run owns.",
            Description = "Stop exactly one programRunId. Input: programRunId (required). A pending run loses its execution right immediately and is recorded as cancelled, so it can never execute late; the run resource slot is released, every detour mounted for it is detached through the single hook host, and the declared cleanup entry runs with the reason and cancellation state. A run whose synchronous code has already started is only asked to cancel: the state keeps reporting that the code is still in flight and the result returns terminal=false with the current evidence, so the terminal state is published only after that code returns and cleanup is provable, and no already-observed effect is rolled back. Scope: this Tool does not delete the program definition, does not stop other runs of the same program, does not touch other sessions and does not close the session owner. Once the stopped run was the last non-terminal run of its program, the program closes its future dispatch and reports itself as stopped, which is the state that allows debug.program.remove. Output: the resulting run state, whether it is terminal, whether cancellation was requested, whether the code is still in flight, how many not-yet-executed steps were cancelled, whether the cleanup entry ran, how many other runs were left untouched and the driver facts. Rejections: E_DEBUG_PROGRAM_RUN_NOT_FOUND for an unknown programRunId. " + DebugLimits.ProgramSummary(),
            Group = "debug",
            DestructiveHint = false,
            SideEffects = new[] { "debug-activation" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 60000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Stop a stepping run and release its resources.",
                    Input = new DebugProgramStopRequest { ProgramRunId = "prun-1" }
                }
            }
        };

        public ValueTask<DebugProgramStopResult> ExecuteAsync(DebugProgramStopRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 取消、detach 与资源释放只在一个 effect barrier 之后发生。
            var controller = DebugProgramHost.PrepareStop(request);
            ctx.CommitEffect();
            return new ValueTask<DebugProgramStopResult>(DebugProgramHost.ApplyStop(controller));
        }
    }
}
#endif
