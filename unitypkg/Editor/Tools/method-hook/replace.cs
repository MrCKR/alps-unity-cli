#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// method-hook.replace — 替换同一 Hook Definition 的捕获与回调配置。
    /// 目标与 activation 不变，因此不会重复安装 detour，也不会扩大原授权范围。
    /// </summary>
    public class MethodHookReplaceTool : IUnityCliTool<MethodHookReplaceRequest, MethodHookReplaceResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "method-hook.replace",
            Summary = "Replace one hook's capture and callback configuration without reinstalling its target patch.",
            Description = "Update the capture configuration, callback source, priority or hit-event switch of one Hook Definition. Requires the editorSessionId and expectedRevision returned by install, info or list; a stale session or revision fails before any effect. The exact target and activation are immutable: to intercept another method or widen the activation scope, remove this definition and install a new one instead of replacing it. New callback source is compiled before the effect barrier, so a compile failure leaves the previous definition fully active and unchanged. Success proves the new configuration is persisted and read back; it does not prove the changed callback is exercised by future calls, and a definition that was disabled or faulted keeps its state and still needs an explicit enable. Side effects: writes callback pointers and this Editor session's definition snapshot, and may load a new callback assembly; it does not reinstall the target patch, rewrite game .cs files, or trigger a project compile. " + "Callbacks run inside the game call chain of the selected Editor session, so they can interleave with other CLI mutations on that same session; disable the definition first when an isolated observation window is required. Callback overrun is only detected after a callback returns, so it is not a preemptive timeout: an infinite loop, a long sleep or a deadlock inside a callback can still keep the Editor unresponsive, and disable or remove cannot terminate a callback that is already running. " + MethodHookLimits.Summary() + " Supports --dry-run, which validates revision, session, capture fields and capacity without compiling, loading, patching or persisting anything.",
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
                    Description = "Replace the callback body of an existing definition using the revision returned by install.",
                    Input = new MethodHookReplaceRequest
                    {
                        HookId = "hook-0000000000000000",
                        EditorSessionId = "session-0000000000000000000000000000000000000000000000000000000000000000",
                        ExpectedRevision = 1,
                        Callbacks = new MethodHookCallbackSpec { PrefixCode = "ctx.Emit(\"enter\", ctx.Arguments);" }
                    }
                }
            }
        };

        public async ValueTask<MethodHookReplaceResult> ExecuteAsync(MethodHookReplaceRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            var plan = MethodHookHost.PrepareReplace(request);
            if (ctx.DryRun) return MethodHookHost.DryRunReplace(plan);
            var prepared = await MethodHookHost.CompileCallbacksAsync(plan.Callbacks, ctx.Wait, TimeSpan.FromMilliseconds(ctx.TimeoutMs), ct);
            ctx.CommitEffect();
            return MethodHookHost.ApplyReplace(plan, prepared);
        }
    }
}
#endif


