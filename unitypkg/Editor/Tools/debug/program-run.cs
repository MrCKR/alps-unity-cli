#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.program-run — 激活一次有限运行：挂载声明的 bindings、排定运行并建立唯一 step driver。
    /// 返回的是 scheduled/running 的实际状态与 deadline，不是业务结果。
    /// </summary>
    public class DebugProgramRunTool : IUnityCliTool<DebugProgramRunRequest, DebugProgramRunResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.program-run",
            Summary = "Activate one bounded program run at a declared execution point and return its programRunId, revision, target and deadline.",
            Description = "Activate exactly one run of an installed program. Inputs: programId, entry, optional args, executionSite, optional phase and required durationMs. Execution sites are declared per run and are never interchangeable: editor-tick schedules the entry on the main-thread central editor tick; method-prefix and method-postfix claim the next hit of the matching exact target; signal runs when a delivered signal reaches the bound point; worker-thread claims the next hit that happens off the main thread; player-loop-phase runs on a real PlayerLoop phase while the Editor is playing and requires phase. Output: programRunId, starting revision, target, deadline, whether a step driver was planted, attached binding count, and terminal=false when a dispatch-site run still waits for its execution point, which is an honest bounded observation and not proof that the business goal finished. Rejections: E_DEBUG_PROGRAM_SITE_INVALID, E_DEBUG_PROGRAM_RUN_ACTIVE, E_DEBUG_PROGRAM_DRIVER_CONFLICT or E_SCHEMA_INVALID.", Notes = DebugDomainNotes.ProgramRun,
            Group = "debug",
            DestructiveHint = true,
            SideEffects = new[] { "debug-activation", "user-code-execution" },
            Permissions = new[] { "execute-user-code" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 60000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Run a stepping entry at the declared execution point editor-tick for the required durationMs of 2000 milliseconds.",
                    Input = new DebugProgramRunRequest
                    {
                        ProgramId = "prog-1",
                        Entry = "Step",
                        ExecutionSite = "editor-tick",
                        DurationMs = 2000,
                        RunId = "run-1"
                    }
                },
                new ToolExample
                {
                    Description = "Run a method callback once at the next hit of its bound exact target.",
                    Input = new DebugProgramRunRequest
                    {
                        ProgramId = "prog-2",
                        Entry = "OnHit",
                        Args = new System.Collections.Generic.Dictionary<string, object> { { "reason", "manual" } },
                        ExecutionSite = "method-prefix",
                        DurationMs = 15000
                    }
                }
            }
        };

        public ValueTask<DebugProgramRunResult> ExecuteAsync(DebugProgramRunRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 定义状态、entry、执行点、时长与运行预算全部在 effect barrier 之前判定。
            var plan = DebugProgramHost.PrepareRun(request);
            ctx.CommitEffect();
            return new ValueTask<DebugProgramRunResult>(DebugProgramHost.ApplyRun(plan));
        }
    }
}
#endif
