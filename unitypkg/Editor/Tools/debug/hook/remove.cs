#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.hook.remove — 删除定义并在没有其他定义共享 target 时撤销自己的 Attachment。
    /// 缓冲内的历史事件仍然可读。
    /// </summary>
    public class MethodHookRemoveTool : IUnityCliTool<MethodHookRemoveRequest, MethodHookRemoveResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.hook.remove",
            Summary = "Delete one hook definition that dispatches on any supported managed thread and release its own patch when no other definition shares the target.",
            Description = "Delete one Hook Definition from the current Editor session and revoke this CLI's Attachment when this was the last definition on that exact target. Requires the editorSessionId and expectedRevision that debug.hook.list, debug.hook.info or debug.hook.install report for that definition: pass the item's editorSessionId as-is and its revision as expectedRevision, because that revision is the exact value this call compares against. An unknown hookId returns E_HOOK_NOT_FOUND and is never reported as a second success. The buffered events of the removed definition stay readable through debug.events.read, but the definition no longer appears in debug.hook.list. Removal does not require the target method to still exist or to still match, so faulted definitions can always be cleaned up. Success proves the definition is gone, the snapshot was published and the owned patch was released when nothing else needed it; it does not unload the callback assembly, because Mono assemblies already loaded in this domain cannot be unloaded individually. Removing a hook does not undo business effects that earlier invocations produced. Side effects: writes this Editor session's definition snapshot and releases the owned patch for this target when it is the last one. " + "Thread scope: hits on any supported managed thread are dispatched on that same thread. Removal closes this definition to new entries, keeps calls already inside on the revision they acquired at their prefix, and detaching the patch never claims that in-flight original calls have ended; another owner's observation of the same target is left in place. " + DebugLimits.Summary() + " Supports --dry-run, which validates session and revision and reports planned effects without deleting definitions, patches or events.",
            Group = "debug",
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
                    Description = "Remove a definition and let the owned patch be released.",
                    Input = new MethodHookRemoveRequest
                    {
                        HookId = "hook-0000000000000000",
                        EditorSessionId = "session-0000000000000000000000000000000000000000000000000000000000000000",
                        ExpectedRevision = 1
                    }
                }
            }
        };

        public ValueTask<MethodHookRemoveResult> ExecuteAsync(MethodHookRemoveRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            var plan = MethodHookHost.PrepareRemove(request);
            if (ctx.DryRun) return new ValueTask<MethodHookRemoveResult>(MethodHookHost.DryRunRemove(plan));
            ctx.CommitEffect();
            return new ValueTask<MethodHookRemoveResult>(MethodHookHost.ApplyRemove(plan));
        }
    }
}
#endif
