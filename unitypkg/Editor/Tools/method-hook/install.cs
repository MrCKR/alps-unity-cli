#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// method-hook.install — 为 exact 方法创建并激活一个 Hook Definition。
    /// 回调源码在唯一 effect barrier 之前编译；Patch、持久化与 gate 打开发生在 barrier 之后。
    /// </summary>
    public class MethodHookInstallTool : IUnityCliTool<MethodHookInstallRequest, MethodHookInstallResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "method-hook.install",
            Summary = "Install a live Editor method hook that captures calls, modifies behaviour, or runs a C# callback.",
            Description = "Create and activate one Hook Definition on an exact managed method of the current Editor domain. The target must match assemblyName, typeName, methodName, exact parameterTypes, returnType and isStatic completely; ambiguous or unsupported signatures fail before any effect. activation defaults to play, which returns dormant in Edit mode and becomes armed when the next Play run starts; editor-and-play is armed in both scopes. At least one effective behaviour is required: capture.arguments, capture.result, an instance or static field, or prefixCode/postfixCode. Callback source is compiled before the single effect barrier and is not executed during install. Success proves the definition is persisted, an owned Adapter exists on the exact target, and the gate is open; it does NOT prove the method was called, that a callback succeeded, or that any business effect happened. Compiled call sites that were built before the patch keep the original behaviour, so an installed definition may legitimately produce no hits. Side effects: installs a process-local method patch, activates user C# callbacks, and writes this Editor session's definition snapshot. It does not modify game .cs files, does not rewrite the on-disk assembly, does not trigger a project compile, and does not change Enter Play Mode settings. Long-term effects belong to the definition, not to this request; disabling or removing it restores the original behaviour. " + "Callbacks run inside the game call chain of the selected Editor session, so they can interleave with other CLI mutations on that same session; disable the definition first when an isolated observation window is required. Callback overrun is only detected after a callback returns, so it is not a preemptive timeout: an infinite loop, a long sleep or a deadlock inside a callback can still keep the Editor unresponsive, and disable or remove cannot terminate a callback that is already running. " + MethodHookLimits.Summary() + " Supports --dry-run, which only validates the exact target, capture fields and capacity and reports planned effects without compiling, loading, patching or persisting anything.",
            Group = "method-hook",
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


