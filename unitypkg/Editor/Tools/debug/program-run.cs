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
            Description = "Activate exactly one run of an installed program. Inputs: programId, entry, optional JSON args, executionSite, an optional phase that names a real Unity PlayerLoop phase, a required durationMs, and an optional runId that only associates this run with an evidence window. Execution sites are declared per run and are never interchangeable: editor-tick schedules the entry on the main-thread central editor tick, method-prefix and method-postfix claim the next hit of the matching exact target, signal runs when an explicitly delivered signal reaches the bound execution point, worker-thread claims the next hit that happens off the main thread, and player-loop-phase runs on a real Unity PlayerLoop phase while the Editor is playing. player-loop-phase requires phase (before-update, after-update, fixed-update or late-update), is refused in Edit mode with E_DEBUG_PROGRAM_SITE_UNAVAILABLE because no phase exists there, and is never served by the editor tick: the step driver and every framework delivery of that run take their execution right inside that phase, and the resulting trigger event names it in executionPoint instead of reporting the editor tick. An undeclared phase name is refused with E_DEBUG_PLAYER_LOOP_PHASE_UNKNOWN. A run that takes the execution right on the main thread uses bounded synchronous steps: a step entry that returns Continue(wait) keeps its single continuous driver and is resumed on later occurrences of its own declared execution point by the same central scheduler until it returns Finish() or its deadline closes it as cancelled. A dispatch-site run (method-prefix, method-postfix, worker-thread or signal) is a binding activation window: it stays scheduled until the exact target is really hit, then that hitting thread claims it, runs the entry once synchronously and the run reaches completed, so the emitted event carries the hitting thread id and mainThread false for a worker hit; a worker-thread run is a single synchronous call on the thread that made the original call and never moves a cross-frame continuation onto the main thread. programRunId state machine: scheduled to running to completed, failed, cancelled or interrupted. A pending run that expires is closed as cancelled and never executes late; once synchronous code has started, expiry only requests cancellation and the state keeps reporting that the code is still in flight, and the terminal state is published only after the original code returned and cleanup is provable. Duration and cancellation: durationMs is required and has no default; the run is closed when the driver exceeds it, and a dispatch-site run whose target never arrives is closed as cancelled with E_DEBUG_PROGRAM_RUN_EXPIRED when durationMs elapses instead of staying running. Each program has at most one continuous step driver, and the global active run budget counts every non-terminal run. Activation attaches the bindings declared by the current revision through the single hook host, so program-owned bindings and any other arbitrary-intervention owner on the same exact target share one accounting and a conflicting second owner is refused before any effect. Output: programRunId, the starting revision, the actual target, the deadline, whether a step driver was planted, whether the run already reached a terminal state and the attached binding count. terminal is false for a legal dispatch-site run that is still waiting for its execution point: this creation result is an honest bounded non-terminal observation with the current readback, not a claim that the business goal finished, and the caller reads the same programRunId again with debug.program-wait or debug.program-info for the later terminal. The target names the same declared execution point as executionSite and never a different one: the exact binding target when the run has one, the real PlayerLoop phase name for a phase run, and the central editor tick only for an editor-tick run. A scheduled or running program does NOT mean the business goal succeeded: only a terminal run plus a trusted business result shows what the entry did. A main-thread trigger that reaches a declared main-thread pause inside the entry it runs cannot return a terminal response: the main-thread queue stops while that site is held, so that trigger call is answered with outcome \"may-have-started\", " + DebugPauseControl.RejectionCode + ", the live pause facts (pauseId, threadId, timeoutKind, remainingMs) and nextActions that name debug.pause-list and debug.pause-resume. Envelope: that trigger answer is ok false with error.code " + DebugPauseControl.RejectionCode + ", and every classification fact lives in error.details: outcome \"may-have-started\", dispatch \"may-have-started\", replay \"new-decision-required\", pauseId, threadId, timeoutKind, remainingMs, pauseTool and resumeTool, with error.transient false and error.retryAfterMs 0; outcome is never \"unknown\", and a boundary fact is omitted rather than invented when the current publication cannot carry it. That partial outcome is not a failed or cancelled run and never proves the entry did not execute: the run keeps its own state machine, the caller reads the same programRunId again after the site is released, nothing is replayed automatically, and the caller decides whether to submit a new request. Deadlines and the practical rule: durationMs bounds only this run, while this request is bounded by the caller's own --timeout-ms, so a main-thread pause longer than the remaining request budget cannot be observed by the request that is holding it; a single off-main control wait is separately bounded to 30000 ms while the main thread is paused. A caller that needs the terminal answer must therefore trigger the pause from a dispatch execution site (method-prefix, method-postfix or worker-thread) whose creation result is answered before the pause starts, or declare a pauseTimeout.durationMs shorter than that 30000 ms bound, or declare pauseTimeout unbounded and release it with debug.pause-list then debug.pause-resume; when the declared pause is longer, the partial outcome above is the documented answer. Trigger-thread semantics: a trigger runs on the thread of its declared execution site, so an editor-tick or player-loop-phase run runs and pauses the main thread and cannot answer its own request, while a dispatch-site run answers on the thread that made the original call before the entry runs. Rejections: E_DEBUG_PROGRAM_NOT_FOUND, E_DEBUG_PROGRAM_ENTRY_MISSING, E_DEBUG_PROGRAM_SITE_INVALID, E_DEBUG_PROGRAM_SITE_UNAVAILABLE for a phase site while the Editor is not playing, E_DEBUG_PLAYER_LOOP_PHASE_UNKNOWN for a phase name outside the declared set, E_DEBUG_PLAYER_LOOP_UNAVAILABLE when the Editor PlayerLoop cannot host the declared phase, E_DEBUG_PROGRAM_WRONG_STATE for a stopped program, E_DEBUG_PROGRAM_RUN_ACTIVE when this program already owns an active run, E_DEBUG_PROGRAM_DRIVER_CONFLICT when a second continuous driver would be created, E_DEBUG_PROGRAM_BINDING_NOT_PREPARED, E_DEBUG_LIMIT_EXCEEDED for the active run or active resource budget, and E_SCHEMA_INVALID for a durationMs or args size outside the declared bounds. Reload and recovery: a Domain Reload or a Play segment end invalidates every non-terminal run as interrupted and the program returns to dormant; a stopped session neither accepts a new run nor keeps its program resources. Entry ABI: " + UnityCliDebugProgramAbi.SignatureTable() + ". " + UnityCliDebugProgramAbi.StepContextAbi() + " " + DebugLimits.ProgramSummary(),
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
