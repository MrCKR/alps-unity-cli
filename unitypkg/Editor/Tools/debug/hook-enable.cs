#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.hook-enable — 激活已保留的定义。
    /// armed/dormant 是带 fresh readback 的 no-op；faulted 定义做一次完整重解析与必要重编译。
    /// </summary>
    public class MethodHookEnableTool : IUnityCliTool<MethodHookEnableRequest, MethodHookEnableResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.hook-enable",
            Summary = "Enable one hook definition again, re-resolving and recompiling it when it is faulted, for hits on any supported managed thread.",
            Description = "Activate a definition that is currently disabled or faulted in the current Editor session. Requires the editorSessionId and expectedRevision from install, info or list. Enabling an already armed or dormant definition is a no-op with fresh readback: the response reports changed=false and the revision does not move. Enabling a faulted definition is a real recovery attempt: the exact target is re-resolved, callback source is recompiled when needed, and any failure returns a stable error while the definition stays faulted instead of being reported as active. Success reports the resulting armed or dormant state, which follows the definition's activation against the current Edit or Play scope. Side effects: installs or reuses the owned Adapter on the exact target, may load a new callback assembly, and writes this Editor session's definition snapshot. It does not trigger a project compile or change Enter Play Mode settings. " + "Callbacks run inside the game call chain of the selected Editor session, so they can interleave with other CLI mutations on that same session; disable the definition first when an isolated observation window is required. Callback overrun is only detected after a callback returns, so it is not a preemptive timeout: an infinite loop, a long sleep or a deadlock inside a callback can still keep the Editor unresponsive, and disable or remove cannot terminate a callback that is already running. " + "Conflict rule: enabling a definition with callback code requires that no other callback definition is active on the same exact target, otherwise E_DEBUG_RESOURCE_CONFLICT is returned before any effect; capture-only observations on that target are unaffected. " + "Thread scope: hits are dispatched on whatever supported managed thread made the original call, and callbacks run inline on that same thread. A callback fault on a worker thread closes dispatch for that definition at the revision it happened under; the fault state, the snapshot and the patch release are committed by the framework on the main thread. " + DebugLimits.Summary() + " Supports --dry-run, which validates session, revision, target and capacity without resolving callbacks, patching or persisting anything.",
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
                    Description = "Enable a definition that was previously disabled.",
                    Input = new MethodHookEnableRequest
                    {
                        HookId = "hook-0000000000000000",
                        EditorSessionId = "session-0000000000000000000000000000000000000000000000000000000000000000",
                        ExpectedRevision = 2
                    }
                }
            }
        };

        public async ValueTask<MethodHookEnableResult> ExecuteAsync(MethodHookEnableRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            var plan = MethodHookHost.PrepareEnable(request);
            if (ctx.DryRun) return MethodHookHost.DryRunEnable(plan);
            MethodHookPreparedCallbacks prepared = null;
            var definition = plan.Definition;
            var needsCompile = !plan.NoOp && definition.HasCallbacks && definition.Callback == null;
            if (needsCompile)
                prepared = await MethodHookHost.CompileCallbacksAsync(new MethodHookCallbackSpec { PrefixCode = definition.PrefixCode, PostfixCode = definition.PostfixCode }, ctx.Wait, TimeSpan.FromMilliseconds(ctx.TimeoutMs), ct);
            ctx.CommitEffect();
            return MethodHookHost.ApplyEnable(plan, prepared);
        }
    }
}
#endif


