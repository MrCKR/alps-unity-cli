#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.hook-replace — 替换同一 Hook Definition 的捕获与回调配置。
    /// 目标与 activation 不变，因此不会重复安装 detour，也不会扩大原授权范围。
    /// </summary>
    public class MethodHookReplaceTool : IUnityCliTool<MethodHookReplaceRequest, MethodHookReplaceResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.hook-replace",
            Summary = "Replace one hook's capture and callback configuration while keeping its target patch and its any-thread dispatch.",
            Description = "Update the capture configuration, callback source, priority or hit-event switch of one Hook Definition. Requires the editorSessionId and expectedRevision returned by install, info or list; a stale session or revision fails before any effect. The exact target and activation are immutable: to intercept another method or widen the activation scope, remove this definition and install a new one instead of replacing it. New callback source is compiled before the effect barrier, so a compile failure leaves the previous definition fully active and unchanged. Success proves the new configuration is persisted and read back; it does not prove the changed callback is exercised by future calls, and a definition that was disabled or faulted keeps its state and still needs an explicit enable. Side effects: writes callback pointers and this Editor session's definition snapshot, and may load a new callback assembly; it does not reinstall the target patch, rewrite game .cs files, or trigger a project compile. " + "Callbacks run inside the game call chain of the selected Editor session, so they can interleave with other CLI mutations on that same session; disable the definition first when an isolated observation window is required. Callback overrun is only detected after a callback returns, so it is not a preemptive timeout: an infinite loop, a long sleep or a deadlock inside a callback can still keep the Editor unresponsive, and disable or remove cannot terminate a callback that is already running. " + "Conflict rule: replacing a definition with callback code requires that no other callback definition is active on the same exact target, otherwise the request is rejected with E_DEBUG_RESOURCE_CONFLICT before any effect. " + "Listener and ownership: passing listener replaces the whole listener configuration, including its conditions, thread filter, instance filter and finite hit budget; omitting the field keeps the current listener, and a replace that clears the listener releases its planned injection. A replacement that declares trigger player-loop-phase is validated exactly like install, including the shared phase-site refusal and E_DEBUG_PHASE_TRIGGER_REQUIRES_INVOCATION for filters a phase arrival cannot evaluate. The hit budget always restarts from zero on a successful replace. A definition owned by a program binding is refused with E_HOOK_OWNED_BY_PROGRAM, because a direct replace would silently change a program-owned revision; change the program through debug.program-replace instead. " + "Thread scope: hits are dispatched on whatever supported managed thread made the original call; a call that is already inside keeps the revision it acquired at its prefix and finishes with it, so a replacement never rewrites the meaning of an invocation in flight. " + DebugLimits.Summary() + " Supports --dry-run, which validates revision, session, capture fields and capacity without compiling, loading, patching or persisting anything.",
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


