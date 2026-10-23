#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.program-install — 编译一个调试程序、解析 entry ABI 并准备 bindings。
    /// 编译与 ABI 校验发生在唯一 effect barrier 之前；定义建立之后仍是 dormant，不激活任何 listener。
    /// </summary>
    public class DebugProgramInstallTool : IUnityCliTool<DebugProgramInstallRequest, DebugProgramInstallResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.program-install",
            Summary = "Compile a session-owned debug program, resolve its named entry ABI and prepare its bindings as a dormant definition.",
            Description = "Register one debug program inside an explicit debug session. Inputs: sessionId (required), a label that is unique inside that session, entryType (the single supported program form is code), exactly one of source or file, entries (name plus category plus the fully qualified declaringType inside the compiled unit), optional bindings, an optional revision state schema and an optional initial revision state. The host reads a file once during preparation; the running program never re-reads that path, and the file is never written into Assets, a business Package or a business assembly. Source is compiled through the single dynamic compiler owner with the current generation reference snapshot, so program source shares one compile budget with hook callbacks, code execution and pause evaluate. Entry ABI: " + UnityCliDebugProgramAbi.SignatureTable() + ". " + UnityCliDebugProgramAbi.StepContextAbi() + " Each category has exactly one legal signature; overloads of the same name must resolve to exactly one matching signature, an async state machine is rejected, and an instance entry is accepted only on a type marked with UnityCliDebugProgramWrapperAttribute that declares exactly one parameterless constructor, which the host creates once while preparing. Bindings are declared data, never probed by running user configuration code: a prefix or postfix binding needs a method entry and is prepared as a callback that only calls the program dispatch entry, and a signal binding needs a listener entry and installs no detour. Installation only prepares bindings and compiles: it does not attach a detour, does not activate a listener and does not run business code, so the definition reports dormant. Success proves the definition exists, the source was compiled and every entry was resolved by metadata; it does NOT prove the program works, that any method is patched, or that any business goal is reached. Order and failure behaviour: all validation, budget admission, compilation and ABI resolution happen before the single effect barrier, so a rejection leaves no definition, no prepared binding and no new resource. Budgets: " + DebugLimits.ProgramSummary() + " Rejections: E_DEBUG_PROGRAM_SESSION_REQUIRED or E_DEBUG_PROGRAM_SESSION_STOPPED for the owning session, E_DEBUG_PROGRAM_LABEL_CONFLICT for a duplicate label, E_DEBUG_PROGRAM_ABI_INVALID for any entry that does not match its category signature, E_DEBUG_PROGRAM_BINDING_INVALID for a binding whose phase and entry category disagree, E_COMPILER_BUDGET_EXHAUSTED when the shared compile budget cannot load another artifact, E_DEBUG_LIMIT_EXCEEDED when the program or source budget is reached, E_CODE_COMPILE_FAILED with diagnostics when the source does not compile, and E_DEBUG_PROGRAM_SOURCE_UNREADABLE when the declared file cannot be read. Lifecycle: the definition belongs to one sessionId in the current domain. A Domain Reload invalidates program definitions and their resolved entries, so the caller re-installs instead of relying on stale identities; an Editor restart has the same effect. A step entry reads its own index from context.StepIndex: the first executed step of a run has StepIndex 1 and every further executed step increases it by one, so the example below performs exactly three steps because its Finish test fires on StepIndex 3. Minimal legal program source: " + UnityCliDebugProgramAbi.ExampleSource() + " Declare it with entries [{ name: \"Step\", category: \"step\", declaringType: \"Probe\" }] and no bindings. " + DebugLimits.ProgramSummary(),
            Group = "debug",
            DestructiveHint = true,
            SideEffects = new[] { "debug-session", "artifact-write" },
            Permissions = new[] { "execute-user-code" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 120000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Install a dormant stepping program with one declared entry.",
                    Input = new DebugProgramInstallRequest
                    {
                        SessionId = "dbg-1",
                        Label = "probe-steps",
                        EntryType = "code",
                        Source = "using UnityCli;\npublic static class Probe\n{\n    public static UnityCliDebugStepResult Step(UnityCliDebugStepContext context) { return UnityCliDebugStepResult.Finish(); }\n}",
                        Entries = new System.Collections.Generic.List<DebugProgramEntrySpec>
                        {
                            new DebugProgramEntrySpec { Name = "Step", Category = "step", DeclaringType = "Probe" }
                        },
                        State = new System.Collections.Generic.Dictionary<string, object> { { "attempt", 0 } }
                    }
                },
                new ToolExample
                {
                    Description = "Install a program whose method callback is bound to one exact method prefix.",
                    Input = new DebugProgramInstallRequest
                    {
                        SessionId = "dbg-1",
                        Label = "movement-observer",
                        EntryType = "code",
                        File = "C:/work/programs/MovementObserver.cs",
                        Entries = new System.Collections.Generic.List<DebugProgramEntrySpec>
                        {
                            new DebugProgramEntrySpec { Name = "OnHit", Category = "method", DeclaringType = "MovementObserver" }
                        },
                        Bindings = new System.Collections.Generic.List<DebugProgramBindingSpec>
                        {
                            new DebugProgramBindingSpec
                            {
                                Phase = "prefix",
                                Entry = "OnHit",
                                Target = new MethodHookTargetSpec
                                {
                                    AssemblyName = "Assembly-CSharp",
                                    TypeName = "Game.AiController",
                                    MethodName = "BeginMovement",
                                    ParameterTypes = new System.Collections.Generic.List<string>(),
                                    ReturnType = "System.Void",
                                    IsStatic = false
                                }
                            }
                        }
                    }
                }
            }
        };

        public async ValueTask<DebugProgramInstallResult> ExecuteAsync(DebugProgramInstallRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 校验、预算、源码读取、编译与 ABI 解析全部在 effect barrier 之前完成。
            var plan = await DebugProgramHost.PrepareInstallAsync(request, ctx.Wait, TimeSpan.FromMilliseconds(ctx.TimeoutMs), ct);
            ctx.CommitEffect();
            return DebugProgramHost.ApplyInstall(plan);
        }
    }
}
#endif
