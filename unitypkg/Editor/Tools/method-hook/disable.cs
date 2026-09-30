#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// method-hook.disable — 关闭定义之后的 hook 效果，但保留定义、revision 与已有事件。
    /// </summary>
    public class MethodHookDisableTool : IUnityCliTool<MethodHookDisableRequest, MethodHookDisableResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "method-hook.disable",
            Summary = "Stop one hook's future dispatch while keeping its definition, revision and buffered events.",
            Description = "Close the logical gate of one Hook Definition so later hits are no longer dispatched to its callbacks, while the definition, its revision history and buffered events stay readable. Requires the editorSessionId and expectedRevision from install, info or list. Disabling an already disabled definition is a no-op with fresh readback: changed=false and no revision increment. The owned Adapter on the exact target is only released when no other definition of this CLI still uses that target, so other definitions are unaffected. Success proves the definition is persisted as disabled and this CLI no longer dispatches it; it does not roll back effects that earlier invocations already produced, and it does not guarantee termination of a callback that is currently running inside the Editor. This tool does not execute user callbacks and never requires the target method to still exist, so it stays usable for faulted or changed targets. Side effects: writes this Editor session's definition snapshot and may release the owned patch. " + MethodHookLimits.Summary() + " Supports --dry-run, which validates session and revision and reports planned effects without touching the gate, patch or snapshot.",
            Group = "method-hook",
            DestructiveHint = false,
            SupportsDryRun = true,
            SideEffects = new[] { "method-patch", "session-definition" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Disable a definition without removing it.",
                    Input = new MethodHookDisableRequest
                    {
                        HookId = "hook-0000000000000000",
                        EditorSessionId = "session-0000000000000000000000000000000000000000000000000000000000000000",
                        ExpectedRevision = 3
                    }
                }
            }
        };

        public ValueTask<MethodHookDisableResult> ExecuteAsync(MethodHookDisableRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            var plan = MethodHookHost.PrepareDisable(request);
            if (ctx.DryRun) return new ValueTask<MethodHookDisableResult>(MethodHookHost.DryRunDisable(plan));
            ctx.CommitEffect();
            return new ValueTask<MethodHookDisableResult>(MethodHookHost.ApplyDisable(plan));
        }
    }
}
#endif
