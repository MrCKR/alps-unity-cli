#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.hook.install — 为 exact 方法创建并激活一个 Hook Definition。
    /// 回调源码在唯一 effect barrier 之前编译；Patch、持久化与 gate 打开发生在 barrier 之后。
    /// </summary>
    public class MethodHookInstallTool : IUnityCliTool<MethodHookInstallRequest, MethodHookInstallResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.hook.install",
            Summary = "Install a live Editor method hook that captures calls, modifies behaviour, or runs a C# callback on any supported managed thread.",
            Description = "Create and activate one Hook Definition on an exact managed method of the current Editor domain. The target must match assemblyName, typeName, methodName, exact parameterTypes, returnType and isStatic completely; ambiguous or unsupported signatures fail before any effect. activation defaults to play, which returns dormant in Edit mode and becomes armed when the next Play run starts; editor-and-play is armed in both scopes. At least one effective behaviour is required: capture.arguments, capture.result, an instance or static field, or prefixCode/postfixCode. A breakpoint is a hit behaviour of the definition and not an effective behaviour of its own, so a pause breakpoint declared without any capture field and without callback code is still refused with E_SCHEMA_INVALID before any effect; declare at least one capture field or callback together with the breakpoint. Callback source is compiled before the single effect barrier and is not executed during install. Success proves the definition is persisted, an owned Adapter exists on the exact target, and the gate is open; it does NOT prove the method was called, that a callback succeeded, or that any business effect happened. Compiled call sites that were built before the patch keep the original behaviour, so an installed definition may legitimately produce no hits. Side effects: installs a process-local method patch, activates user C# callbacks, and writes this Editor session's definition snapshot. It does not modify game .cs files, does not rewrite the on-disk assembly, does not trigger a project compile, and does not change Enter Play Mode settings. Long-term effects belong to the definition, not to this request; disabling or removing it restores the original behaviour. " + "Listener: the optional listener object filters this definition before any callback runs and is the only supported way to condition a hook on hit count, thread, instance identity, argument or field value. It requires the sessionId of an open debug session, which owns the planned injection. trigger defaults to method-hit; log reacts to the threaded log callback, host-signal to an explicit host signal, and player-loop-phase to a real Unity PlayerLoop phase in Editor Play, which requires the phase field (before-update, after-update, fixed-update or late-update) and installs that framework-owned phase site; the editor tick is not a listener trigger, so a main-thread listener is run through debug.program.run instead. A phase site is refused in Edit mode with E_DEBUG_PROGRAM_SITE_UNAVAILABLE, and an undeclared phase name is refused with E_DEBUG_PLAYER_LOOP_PHASE_UNKNOWN. A phase arrival has no method instance and no arguments, so such a listener can only be satisfied by field conditions: an argument condition or a declared instanceIdentity is refused with E_DEBUG_PHASE_TRIGGER_REQUIRES_INVOCATION before any phase site is installed. At every satisfied phase arrival the definition records a trigger event with the full delivery facts (triggeredAt, queuedAt, startedAt, trigger thread, execution thread, revision, expired) and the executionPoint trigger:player-loop-phase:<phase>, captures the declared static fields, records each declared action that needs a method instance or its arguments (capture.arguments, capture.result, capture.instanceFields, callbacks, breakpoint) as a coverage event named phase-action-requires-invocation instead of silently skipping it, and consumes one listener hit; when a finite maxHits is exhausted the phase site is released through the same path as remove, disable and session stop. maxHits is a finite hit budget where 1 means one-shot, and the budget is not rearmed after a domain reload, so a bounded listener must be enabled again. Breakpoint duration and the trigger thread: a pause breakpoint must declare pauseTimeout with no default value, bounded requires a positive integer durationMs and unbounded forbids it, a bounded pause releases itself when its monotonic budget elapses (a CLI request timeout, Unity pause or timeScale never extends it) and records pause-timeout, and one off-main control wait is separately bounded to 30000 ms while the main thread is paused. A pause stops the thread that hit it, so the trigger-thread choice decides whether the triggering request can still be answered: a main-thread hit stops the Editor queue that would answer it, and that request then fails with ok false, error.code " + DebugPauseControl.RejectionCode + " and error.details outcome and dispatch \"may-have-started\", replay \"new-decision-required\", pauseId, threadId, timeoutKind and remainingMs (pauseFacts reports not-read-in-time instead of invented boundary facts when the current publication could not be read in time), with error.transient false and error.retryAfterMs 0, so the dispatch stays unproven rather than failed and is never replayed automatically; a hit on a worker thread pauses only that worker thread and leaves the main thread and the pause control channel serving. A caller that needs a terminal answer, a business-effect proof or a timely readback must therefore either keep durationMs below the 30000 ms off-main bound, or let the hit arrive through a dispatch execution site whose creation result is answered before the pause starts, or declare pauseTimeout unbounded and release it with debug.pause.list then debug.pause.resume. threadId restricts the hit thread, instanceIdentity must equal the identity string the events report, and each condition compares the projected value of one argument index or field with the canonical JSON of a literal value using equals or not-equals. A hit that fails a condition or arrives after the budget is exhausted is counted as filtered and never runs the callback. A definition owned by a program binding cannot be changed by debug.hook.install, debug.hook.replace, debug.hook.enable, debug.hook.disable or debug.hook.remove; those calls are refused with E_HOOK_OWNED_BY_PROGRAM and the program must be changed through debug.program.replace or released through debug.program.stop. " + DebugLimits.TriggerSummary() + " " + "Callbacks run inside the game call chain of the selected Editor session, so they can interleave with other CLI mutations on that same session; disable the definition first when an isolated observation window is required. Callback overrun is only detected after a callback returns, so it is not a preemptive timeout: an infinite loop, a long sleep or a deadlock inside a callback can still keep the Editor unresponsive, and disable or remove cannot terminate a callback that is already running. " + "Conflict rule: on one exact target at most one definition with callback code may be active, because a callback body can write anything and its write scope cannot be proven statically; a second one is rejected with E_DEBUG_RESOURCE_CONFLICT before any effect, while capture-only observations may coexist in a deterministic order (priority descending, then install order). What cannot be proven statically and stays the caller's responsibility: whether a callback mutates shared state outside the target, and what a captured field value means when a coexisting callback also writes that field during the same invocation. " + "Thread scope: a hit is dispatched on whatever supported managed thread made the original call, and callbacks run inline on that same thread, so the recorded thread id is the original call's thread. Only main-thread hits carry the Unity frame number; worker-thread hits carry the monotonic clock plus the last frame observed on the main thread, and a Unity object identity that cannot be read on a worker thread is delivered later as a separate, timestamped supplement event instead of being reported as the value captured at hit time. " + DebugLimits.Summary() + " Supports --dry-run, which only validates the exact target, capture fields, listener and capacity and reports planned effects without compiling, loading, patching or persisting anything.",
            Group = "debug",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "method-patch", "execute-user-code", "session-definition" },
            Permissions = new[] { "execute-user-code" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 120000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Observe a private instance method with no callback source; replace the example identity with one discovered from the current project.",
                    Input = new MethodHookInstallRequest
                    {
                        Label = "ai-movement-order",
                        Target = new MethodHookTargetSpec
                        {
                            AssemblyName = "Assembly-CSharp",
                            TypeName = "Game.AiController",
                            MethodName = "BeginMovement",
                            ParameterTypes = new System.Collections.Generic.List<string>(),
                            ReturnType = "System.Void",
                            IsStatic = false
                        },
                        Activation = "editor-and-play",
                        Capture = new MethodHookCaptureSpec
                        {
                            Arguments = true,
                            InstanceFields = new System.Collections.Generic.List<MethodHookCaptureFieldSpec>
                            {
                                new MethodHookCaptureFieldSpec { FieldName = "aiMove" }
                            }
                        }
                    }
                },
                new ToolExample
                {
                    Description = "Preview the same definition without any patch or persistence.",
                    Input = new MethodHookInstallRequest
                    {
                        Label = "ai-movement-order",
                        Target = new MethodHookTargetSpec
                        {
                            AssemblyName = "Assembly-CSharp",
                            TypeName = "Game.AiController",
                            MethodName = "BeginMovement",
                            ParameterTypes = new System.Collections.Generic.List<string>(),
                            ReturnType = "System.Void",
                            IsStatic = false
                        },
                        Callbacks = new MethodHookCallbackSpec { PrefixCode = "ctx.Emit(\"enter\", ctx.Arguments);" }
                    },
                    Execution = new ToolExampleExecution { DryRun = true }
                },
                new ToolExample
                {
                    Description = "Run the callback only for calls whose first argument is not zero, and stop after the first matching hit. Replace the sessionId with one read from the current Editor session.",
                    Input = new MethodHookInstallRequest
                    {
                        Target = new MethodHookTargetSpec
                        {
                            AssemblyName = "Assembly-CSharp",
                            TypeName = "Game.AiController",
                            MethodName = "BeginMovement",
                            ParameterTypes = new System.Collections.Generic.List<string> { "System.Int32" },
                            ReturnType = "System.Void",
                            IsStatic = false
                        },
                        Activation = "editor-and-play",
                        Callbacks = new MethodHookCallbackSpec { PrefixCode = "ctx.Emit(\"filtered-hit\", ctx.Arguments);" },
                        Listener = new MethodHookListenerSpec
                        {
                            SessionId = "dbg-1",
                            Trigger = "method-hit",
                            MaxHits = 1,
                            Conditions = new System.Collections.Generic.List<MethodHookConditionSpec>
                            {
                                new MethodHookConditionSpec { Kind = "argument", Name = "0", Operator = "not-equals", Value = 0 }
                            }
                        }
                    }
                }
            }
        };

        public async ValueTask<MethodHookInstallResult> ExecuteAsync(MethodHookInstallRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            var plan = MethodHookHost.PrepareInstall(request);
            if (ctx.DryRun) return MethodHookHost.DryRunInstall(plan);
            var prepared = await MethodHookHost.CompileCallbacksAsync(plan.Callbacks, ctx.Wait, TimeSpan.FromMilliseconds(ctx.TimeoutMs), ct);
            ctx.CommitEffect();
            return MethodHookHost.ApplyInstall(plan, prepared);
        }
    }
}
#endif


