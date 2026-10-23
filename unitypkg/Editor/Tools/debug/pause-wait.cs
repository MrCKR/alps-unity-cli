#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.pause-wait — 等待一个匹配的暂停现场；等待结束不解除该现场的暂停。
    ///
    /// 纯读取：只观察现场是否出现，绝不 resume、不服务现场、不改变任何状态。
    /// </summary>
    public class DebugPauseWaitTool : IUnityCliTool<DebugPauseWaitRequest, DebugPauseWaitResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.pause-wait",
            Summary = "Wait a bounded time for a matching pause site to appear and return its facts without resuming it.",
            Description = "Observe a pause site, waiting a bounded time for one to appear. With pauseId the call reads exactly that site and fails with E_DEBUG_PAUSE_STALE when it is not active, so a stale id is never reported as not-yet. With only sessionId, hookId or threadId it returns immediately when a matching site is already stopped, otherwise it yields to the central scheduler and returns as soon as a matching site appears or the wait budget expires. Expiry is a normal observation, not a failure: matched is false and the result states how long it waited. This Tool never resumes a site, never services the site mailbox, never reads user objects and never runs code, so it is safe to poll for a breakpoint that a business action is about to hit. Output: matched, the site facts when matched (the same projection debug.pause-list returns), the actual waited time and resumedByWait=false. Omitting waitMs only reads the current sites.", Notes = DebugDomainNotes.PauseRead,
            Group = "debug",
            DestructiveHint = false,
            SupportsDryRun = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 60000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "Read the current sites without waiting.", Input = new DebugPauseWaitRequest() },
                new ToolExample { Description = "Wait up to five seconds for the next pause site of one hook.", Input = new DebugPauseWaitRequest { HookId = "hook-0000000000000000", WaitMs = 5000 } },
                new ToolExample { Description = "Read one exact pause site.", Input = new DebugPauseWaitRequest { PauseId = "pause-1" } }
            }
        };

        public async ValueTask<DebugPauseWaitResult> ExecuteAsync(DebugPauseWaitRequest request, UnityCliProjectToolContext ctx, CancellationToken ct)
        {
            // 纯观察：不跨效果屏障、不 resume、不写存储。
            ctx.UseReadBudget(new UnityCliReadBudgetInput
            {
                MaxGetters = UnityCliRequestBudget.AbsoluteMaxGetters,
                MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls,
                MaxScannedRecords = UnityCliRequestBudget.AbsoluteMaxScannedRecords,
                MaxSerializedUtf8Bytes = UnityCliRequestBudget.AbsoluteMaxSerializedUtf8Bytes
            });
            var editorSessionId = UnityCliBridge.EditorSessionId();
            var started = DebugEventClocks.MonotonicMilliseconds;
            if (request != null && !string.IsNullOrEmpty(request.PauseId))
            {
                // 精确 pauseId：已结束或从未发布时立即给出稳定 E_DEBUG_PAUSE_STALE，不伪装成尚未出现。
                var exact = DebugPauseHost.FindSite(request.PauseId);
                return Result(exact, DebugEventClocks.MonotonicMilliseconds - started, editorSessionId);
            }
            var matched = DebugPauseHost.FindMatch(request);
            if (matched == null && request != null && request.WaitMs != null)
            {
                await ctx.Wait.Until(() => DebugPauseHost.FindMatch(request) != null, TimeSpan.FromMilliseconds(request.WaitMs.Value), ct);
                matched = DebugPauseHost.FindMatch(request);
            }
            return Result(matched, DebugEventClocks.MonotonicMilliseconds - started, editorSessionId);
        }

        private static DebugPauseWaitResult Result(DebugPauseSite site, double waitedMilliseconds, string editorSessionId)
        {
            return new DebugPauseWaitResult
            {
                Matched = site != null,
                Site = site == null ? null : DebugPauseHost.View(site),
                WaitedMilliseconds = waitedMilliseconds < 0d ? 0d : waitedMilliseconds,
                ResumedByWait = false,
                EditorSessionId = editorSessionId,
                DomainGeneration = DebugSessionHost.DomainGeneration()
            };
        }
    }
}
#endif
