#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.pause.resume — 解除一个指定暂停现场；只继续当前调用，不重放之前操作。
    ///
    /// resume 是解除等待而不是抢占：正在执行的同步用户代码仍会把它自己跑完。
    /// </summary>
    public class DebugPauseResumeTool : IUnityCliTool<DebugPauseResumeRequest, DebugPauseResumeResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.pause.resume",
            Summary = "Release exactly one pause site so its current invocation continues without replaying prior operations.",
            Description = "Release exactly one pause site by pauseId. The paused thread resumes at the exact call boundary where it stopped and the current invocation continues: no earlier operation is replayed, no queued request is executed first, and the release is not preemption. The resume signal is a separate flag, so it is never queued behind the site's waiting mailbox requests; a release request that arrives while another operation is executing is honoured as soon as that synchronous user code returns, because resume releases the wait and cannot interrupt running code. Each pauseId releases one site exactly once and is never reusable: a resumed, timed-out or released pauseId returns E_DEBUG_PAUSE_STALE with its terminal state and is never revived. Inputs: pauseId (required), an optional reason recorded in the audit and pause event, and an optional waitMs inside the declared bound for confirming that the thread actually left the paused state. Output: the terminal state, released, the release reason, how many sites remain, the operation that was executing when this call arrived (if any), and explicit replayedPriorOperations=false and preemption=false. If the paused thread is inside a long synchronous operation and has not left the paused state inside the wait window, released stays false and the note states that the site is still held and the wait budget ended. Unbounded pauses are released by this call, by session stop for a site whose breakpoint declared a sessionId, or by the reachable reload, Play exit and Editor shutdown cleanup; a CLI request timeout, a debug.pause.wait expiry and a CLI exit never release them. Execution domain: this Editor session, Edit and Editor Play. Target/thread: signals the paused thread of the named site from whatever thread serves this request; it does not touch user objects. Behaviour: the signal is synchronous and the confirmation is cross-frame through the framework wait service; nothing registers an update callback. Cancellation: the request deadline and cancellation token end the confirmation wait, not the release. Reload and recovery: a Domain Reload, Play exit or Editor shutdown releases the sites it can reach, so a pause that survived a caller's CLI exit is still bounded by those lifecycle events. Risks: resuming continues a business call that was stopped mid-invocation and other threads may already have blocked on locks that call holds; the paused thread is the only one this call releases. Side effects: releases the declared thread suspension for one site. " + DebugLimits.PauseSummary(),
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
