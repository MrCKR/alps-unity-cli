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
            Description = "Open one evidence window owned by an existing open debug session. A window declares which named checks must hold, where each must be observed, what counts as proof and which condition ends it. begin performs no action on the game: it does not prepare the scene, inject inputs, compile, run code or schedule a program. sessionId must be an open session. requiredChecks is required: each check declares name, source (program, hook, log, trigger or pause), position (an exact executionPoint or \"*\"), quantifier (once, every, whole-window or absent) and optional kind, hookId and requiredSources. sourceRequirements is consumed only by whole-window and absent checks, and such a check whose required source lacks proof coverage is refused with E_SCHEMA_INVALID. endConditions may be manual, check, signal or clock; when omitted, debug.run-end is the only closing path. maxWindowMs is required for a clock condition. Output: runId, start clocks, capture facts, configuration fingerprint, per-check conditions, end conditions and the run budget. Rejections: E_DEBUG_SESSION_NOT_FOUND, E_DEBUG_SESSION_NOT_OPEN, E_SCHEMA_INVALID or E_DEBUG_LIMIT_EXCEEDED.", Notes = DebugDomainNotes.RunBegin,
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
