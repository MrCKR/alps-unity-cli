#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.run-begin — 建立一个证据窗口：冻结必需检查、覆盖要求与结束条件，返回 runId 与捕获起点。
    /// 它不执行准备、不注入输入、不调度任何程序，也不编译任何代码。
    /// </summary>
    public class DebugRunBeginTool : IUnityCliTool<DebugRunBeginRequest, DebugRunBeginResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.run-begin",
            Summary = "Open an evidence window with declared required checks, coverage requirements and end conditions, and return its runId and capture boundary.",
            Description = "Open one evidence window owned by an existing open debug session. A window is a declared claim about the live Editor: you state which named checks must hold, where each check must be observed, what counts as proof, and which condition ends the window. begin itself performs no action on the game: it does not prepare the scene, does not inject inputs, does not compile or run code and does not schedule a program, so the caller keeps full control over preparation and execution. Inputs: sessionId (required, must be an open session); requiredChecks (required, at least one) with name (the exact event name, for a program check the name passed to context.Check), source (program, hook, log, trigger or pause, which is the real sourceId in the unified event store), position (the exact executionPoint that must be hit inside that source, or \"*\" for any position in that source; read real values with debug.events-read), quantifier (once, every, whole-window or absent), optional kind filter, optional hookId filter and optional requiredSources (defaults to the check's own source); sourceRequirements with source and proof, where proof coverage demands proven store coverage over the window interval and proof declared only records a caller statement and never counts as proof; only a whole-window or absent check consumes this input, so it is optional for every other quantifier; endConditions with kind manual (satisfied by debug.run-end), check (satisfied by a retained event with that name), signal (satisfied by a retained trigger fact with that signal name) or clock (satisfied when the window is sealed after maxWindowMs elapsed); endConditions is optional and, when it is omitted, debug.run-end is the only way to close the window and is recorded as one manual condition; optional maxWindowMs, required when a clock condition is declared; optional allowSegments (default false); optional preparation, inputs and seedControl, which are recorded and hashed but never executed or injected. Quantifier semantics and what counts as proof: once needs at least one retained matching event with passed=true; every needs at least one retained match and every retained match passed; whole-window additionally requires caller-declared coverage proof and an observed end condition; absent requires that no matching event exists AND that coverage is proven, because a negative claim is only provable with proven coverage. Declared coverage per quantifier, so no declaration is a hidden precondition: only whole-window and absent consume sourceRequirements, and begin rejects such a check whose required source has no sourceRequirements entry with proof coverage (E_SCHEMA_INVALID), so that requirement is always visible in the declaration you submit. once and every are decided by a retained matching event and are never rejected, never downgraded and never made indeterminate for omitting sourceRequirements; their interval only has to be readable, meaning no coverage gap, no sampling and no truncation. Every per-check result therefore carries two independent facts: coverageRequired states whether that quantifier consumes a caller-declared coverage requirement, and coverageProven states whether the window interval over the sources that check requires was readable; the source entries state the same thing with proof coverage, declared or none together with proofRequired and coverageProven. Configuration segments: install the program, hook or assertion this claim observes before calling begin, because a program or hook installed, replaced, enabled or disabled inside the window opens a new configuration segment, and then a strict window (allowSegments false) reports configuration-changed and indeterminate while allowSegments true reports the change per segment instead. A retained matching event with passed=false is a complete counterexample for once, every and whole-window, and for absent any matching event at all is the counterexample. Polling or a sampled stream only proves sample points: a matching event from a sampled stream never proves a check, so the check reports sampled-only and stays indeterminate. A boolean produced by arbitrary user code never becomes coverage proof by itself; the coverage text a program check declares is returned as declaredCoverage and is never counted as proof. Assertion failures are NOT errors: the later debug.run-end, debug.run-wait and debug.run-info calls answer ok true with a verdict field of passed, failed or indeterminate, and there is no assertion-failed error code. Output: runId of the form run-<n> (monotonic inside the session, never reused in one domain generation), startedAtUtc, startedAtMonotonicMs, startSequence, the atomic capture facts (boundarySequence, droppedAtBoundary, appendFailuresAtBoundary plus the scheduler queue watermark pendingDeliveries, queueHighWaterMark and drivers), the environment readback (editorSessionId, runtimeId, domainGeneration, sceneName, scenePath, isPlaying, unityFrame and the declared seedControl), the definition hash of the declared preparation and inputs, the configuration fingerprint with the per-entry program and hook revisions, each required check with verified or declared-only conditions, the source requirements, the end conditions, and the run budget. End-condition honesty: a signal condition is recorded as declaredOnly because a future delivery cannot be proven at begin; manual, check and clock conditions are verified as enforceable when they are consistent with the declared window, and whether they were really observed is reported by end, wait and info. Budget: " + DebugLimits.RunSummary() + " An existing window is never reopened and a new attempt always gets a new runId. Rejections: E_DEBUG_SESSION_NOT_FOUND for an unknown sessionId, E_DEBUG_SESSION_NOT_OPEN when the session is stopped, E_SCHEMA_INVALID for a missing name, an unknown source, quantifier or condition kind, an unknown required source, a missing coverage requirement for a whole-window or absent check, a signal or check condition without a name, or a clock condition without maxWindowMs, and E_DEBUG_LIMIT_EXCEEDED when the detailed evidence budget cannot hold another window. Reliability: the window belongs to the current domain generation. A Domain Reload or an Editor restart removes it, and an old runId is then answered with E_DEBUG_RUN_NOT_FOUND instead of being reattached to a different window. A program check name is exactly the name passed to context.Check, and the step entry context members are: " + UnityCliDebugProgramAbi.StepContextAbi() + " ",
            Group = "debug",
            DestructiveHint = false,
            SideEffects = new[] { "debug-session" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Prove once that a program check named damage-applied passed anywhere in the window, with no sourceRequirements entry and an explicit manual end condition. Install that program before begin, run it inside the window and close the window with debug.run-end: the window then reports verdict passed with coverageRequired false and coverageProven true.",
                    Input = new DebugRunBeginRequest
                    {
                        SessionId = "dbg-1",
                        RequiredChecks = new System.Collections.Generic.List<DebugRunCheckSpec>
                        {
                            new DebugRunCheckSpec { Name = "damage-applied", Source = "program", Position = "*", Quantifier = "once" }
                        },
                        EndConditions = new System.Collections.Generic.List<DebugRunEndConditionSpec>
                        {
                            new DebugRunEndConditionSpec { Kind = "manual" }
                        }
                    }
                },
                new ToolExample
                {
                    Description = "Prove that an attack path never logged an error in the whole window, with declared coverage proof.",
                    Input = new DebugRunBeginRequest
                    {
                        SessionId = "dbg-1",
                        RequiredChecks = new System.Collections.Generic.List<DebugRunCheckSpec>
                        {
                            new DebugRunCheckSpec { Name = "error", Source = "log", Position = "*", Quantifier = "absent" }
                        },
                        SourceRequirements = new System.Collections.Generic.List<DebugRunSourceRequirementSpec>
                        {
                            new DebugRunSourceRequirementSpec { Source = "log", Proof = "coverage" }
                        },
                        EndConditions = new System.Collections.Generic.List<DebugRunEndConditionSpec>
                        {
                            new DebugRunEndConditionSpec { Kind = "signal", Name = "attack-finished" }
                        },
                        MaxWindowMs = 30000
                    }
                }
            }
        };

        public ValueTask<DebugRunBeginResult> ExecuteAsync(DebugRunBeginRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 声明、session 状态、预算与范围全部在唯一 effect barrier 之前判定。
            var plan = DebugRunHost.PrepareBegin(request);
            ctx.CommitEffect();
            return new ValueTask<DebugRunBeginResult>(DebugRunHost.ApplyBegin(plan));
        }
    }
}
#endif
