#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.pause-resume — 解除一个指定暂停现场；只继续当前调用，不重放之前操作。
    ///
    /// resume 是解除等待而不是抢占：正在执行的同步用户代码仍会把它自己跑完。
    /// </summary>
    public class DebugPauseResumeTool : IUnityCliTool<DebugPauseResumeRequest, DebugPauseResumeResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.pause-resume",
            Summary = "Release exactly one pause site so its current invocation continues without replaying prior operations.",
            Description = "Release exactly one pause site by pauseId. The paused thread resumes at the exact call boundary where it stopped and the current invocation continues: no earlier operation is replayed, no queued request runs first, and it is not preemption. The resume signal is a separate flag, so it is never queued behind the site's waiting mailbox requests; a release that arrives while another operation executes is honoured as soon as that synchronous user code returns. pauseId is required; reason is optional and recorded in the audit and pause event; waitMs optionally bounds the confirmation that the thread left the paused state. Output reports the terminal state released, the reason, how many sites remain, the operation that was executing when this call arrived, and explicit replayedPriorOperations=false and preemption=false. If the paused thread is inside a long synchronous operation and has not left inside the wait window, released stays false and the note states that the site is still held.", Notes = DebugDomainNotes.PauseWrite,
            Group = "debug",
            DestructiveHint = false,
            SideEffects = new[] { "debug-pause" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "Release one pause site.", Input = new DebugPauseResumeRequest { PauseId = "pause-1" } },
                new ToolExample { Description = "Release one site and record why.", Input = new DebugPauseResumeRequest { PauseId = "pause-1", Reason = "evidence captured" } }
            }
        };

        public async ValueTask<DebugPauseResumeResult> ExecuteAsync(DebugPauseResumeRequest request, UnityCliProjectToolContext ctx, CancellationToken ct)
        {
            // 现场存在性在 effect 之前判定：过期 pauseId 不产生任何状态变化。
            var site = DebugPauseHost.FindSite(request == null ? null : request.PauseId);
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? DebugPauseReleaseReasons.Resume : request.Reason;
            var waitMs = request.WaitMs != null ? request.WaitMs.Value : DebugLimits.PauseMaxWaitForReleaseMs;
            // 解除线程暂停是真实 effect。
            ctx.CommitEffect();
            DebugPauseHost.RequestResume(site.PauseId, reason);
            await ctx.Wait.Until(() => site.IsTerminal, TimeSpan.FromMilliseconds(waitMs), ct);
            var released = site.IsTerminal;
            var view = DebugPauseHost.View(site);
            return new DebugPauseResumeResult
            {
                PauseId = site.PauseId,
                State = view.State,
                Released = released,
                ReleaseReason = released ? view.ReleaseReason : string.Empty,
                ActiveSitesRemaining = DebugPauseHost.ActiveSiteCount,
                ExecutingOperation = view.ExecutingOperation,
                ReplayedPriorOperations = false,
                Preemption = false,
                Note = released
                    ? "The site left the paused state; the current invocation continues from its own boundary and no earlier operation was replayed."
                    : "The site is still held: the paused thread is inside a synchronous operation that cannot be interrupted. It will leave the paused state as soon as that code returns, because the release signal is already set.",
                EditorSessionId = UnityCliBridge.EditorSessionId()
            };
        }
    }
}
#endif
