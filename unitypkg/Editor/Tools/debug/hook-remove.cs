#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.hook-remove — 删除定义并在没有其他定义共享 target 时撤销自己的 Attachment。
    /// 缓冲内的历史事件仍然可读。
    /// </summary>
    public class MethodHookRemoveTool : IUnityCliTool<MethodHookRemoveRequest, MethodHookRemoveResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.hook-remove",
            Summary = "Delete one hook definition that dispatches on any supported managed thread and release its own patch when no other definition shares the target.",
            Description = "Delete one Hook Definition from the current Editor session and release this CLI's Attachment when it was the last definition on that exact target. Requires the editorSessionId and expectedRevision that debug.hook-list, debug.hook-info or debug.hook-install report: pass the item's editorSessionId as-is and its revision as expectedRevision. An unknown hookId returns E_HOOK_NOT_FOUND and is never reported as a second success. Buffered events of the removed definition stay readable through debug.events-read. Removal does not require the target method to still exist or still match, so faulted definitions can always be cleaned up. Success proves the definition is gone, the snapshot was published and the owned patch was released when nothing else needed it; it does not unload the callback assembly and does not undo business effects that earlier invocations produced. Supports --dry-run, which validates session and revision and reports planned effects without deleting definitions, patches or events.", Notes = DebugDomainNotes.HookManage,
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
