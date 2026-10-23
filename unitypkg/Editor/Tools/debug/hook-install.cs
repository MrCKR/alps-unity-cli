#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.hook-install — 为 exact 方法创建并激活一个 Hook Definition。
    /// 回调源码在唯一 effect barrier 之前编译；Patch、持久化与 gate 打开发生在 barrier 之后。
    /// </summary>
    public class MethodHookInstallTool : IUnityCliTool<MethodHookInstallRequest, MethodHookInstallResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.hook-install",
            Summary = "Install a live Editor method hook that captures calls, modifies behaviour, or runs a C# callback on any supported managed thread.",
            Description = "Install and activate one Hook Definition on an exact managed method of the current Editor domain. target must match assemblyName, typeName, methodName, parameterTypes, returnType and isStatic exactly; an ambiguous or unsupported signature is refused with E_SCHEMA_INVALID before any effect. Declare at least one effective behaviour: capture.arguments, capture.result, a static or instance field, or prefixCode/postfixCode; a breakpoint alone is not one. activation=play (default) stays dormant in Edit mode and arms on the next Play run; editor-and-play arms in both. Success proves the definition is persisted, an Adapter exists on the exact target and the gate is open; it does not prove the method ran, a callback succeeded, or any business effect happened. Compiled call sites built before the patch keep the original behaviour, so a valid definition can produce no hits. Side effects: process-local method patch, user C# callbacks, this session's definition snapshot; it does not edit game .cs files, rewrite assemblies or change Enter Play Mode settings. Supports --dry-run, which validates target, capture, listener and capacity without compiling or persisting.", Notes = DebugDomainNotes.HookInstall,
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


