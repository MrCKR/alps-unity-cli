#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// debug.session.list 的状态过滤、跨过滤游标失效，以及 stop/remove 对会话定义槽位的可见语义。
    ///
    /// 这些用例直接调用 host：过滤发生在分页之前，而分页与 continuation scope 都属于 host 的职责，
    /// 只在 registry 层断言无法覆盖"游标是否绑定了同一次查询"。
    /// host 使用进程内共享的定义表，因此每个用例都按 label 断言自己创建的定义，
    /// 并在 TearDown 里无条件回收，避免占用共享的 16 个槽位影响其它用例。
    /// </summary>
    public sealed class DebugSessionListTests
    {
        private readonly List<string> created = new List<string>();

        [TearDown]
        public void ReleaseCreatedSessions()
        {
            foreach (var sessionId in created) Release(sessionId);
            created.Clear();
        }

        [Test]
        public void StateFilterNarrowsThePageToTheMatchingStateOnly()
        {
            var openLabel = UniqueLabel("open");
            var stoppedLabel = UniqueLabel("stopped");
            Create(openLabel, out _);
            Create(stoppedLabel, out var stoppedSession);
            Stop(stoppedSession);

            var stoppedPage = DebugSessionHost.List(new DebugSessionListRequest { State = UnityCliDebugSessionStates.Stopped });
            Assert.That(stoppedPage.Items.Select(item => item.Label), Does.Contain(stoppedLabel));
            Assert.That(stoppedPage.Items.Select(item => item.Label), Does.Not.Contain(openLabel));
            Assert.That(stoppedPage.Items.All(item => item.State == UnityCliDebugSessionStates.Stopped), Is.True,
                "a state filter must not leak definitions in another state");

            var openPage = DebugSessionHost.List(new DebugSessionListRequest { State = UnityCliDebugSessionStates.Open });
            Assert.That(openPage.Items.Select(item => item.Label), Does.Contain(openLabel));
            Assert.That(openPage.Items.Select(item => item.Label), Does.Not.Contain(stoppedLabel));
            Assert.That(openPage.Items.All(item => item.State == UnityCliDebugSessionStates.Open), Is.True);

            var all = DebugSessionHost.List(new DebugSessionListRequest());
            Assert.That(all.Items.Select(item => item.Label), Does.Contain(openLabel).And.Contain(stoppedLabel));
            Assert.That(all.Total, Is.GreaterThanOrEqualTo(stoppedPage.Total + openPage.Total),
                "an unfiltered read must count at least as many definitions as the two filtered reads together");
        }

        [Test]
        public void ACursorIssuedForOneFilterIsStaleUnderAnotherFilter()
        {
            Create(UniqueLabel("cursor-a"), out _);
            Create(UniqueLabel("cursor-b"), out _);

            var firstPage = DebugSessionHost.List(new DebugSessionListRequest { State = UnityCliDebugSessionStates.Open, Limit = 1 });
            Assert.That(firstPage.NextCursor, Is.Not.Null.And.Not.Empty,
                "two open definitions with a page size of one must produce a continuation");

            // 同一过滤下续页仍然有效。
            var sameFilter = DebugSessionHost.List(new DebugSessionListRequest
            {
                State = UnityCliDebugSessionStates.Open,
                Limit = 1,
                Cursor = firstPage.NextCursor
            });
            Assert.That(sameFilter.Items.Count, Is.EqualTo(1));

            // 换一个过滤必须失败，而不是把原集合的偏移混进新集合。
            var stale = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugSessionHost.List(new DebugSessionListRequest
            {
                State = UnityCliDebugSessionStates.Stopped,
                Limit = 1,
                Cursor = firstPage.NextCursor
            }));
            Assert.That(stale.Code, Is.EqualTo("E_CONTINUATION_STALE"));
        }

        [Test]
        public void StopReportsTheHeldSlotAndRemoveReleasesIt()
        {
            Create(UniqueLabel("budget"), out var sessionId);

            var afterStop = Stop(sessionId);
            Assert.That(afterStop.Budget.SessionsUsed, Is.GreaterThanOrEqualTo(1L),
                "a stopped definition still occupies its session slot");
            Assert.That(afterStop.Budget.SessionsRemaining, Is.EqualTo(afterStop.Budget.MaxSessions - afterStop.Budget.SessionsUsed),
                "the stop result must report the slot usage so the caller sees the slot is still held");

            var afterRemove = DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = sessionId }));
            created.Remove(sessionId);
            Assert.That(afterRemove.SessionsRemaining, Is.EqualTo(afterStop.Budget.SessionsRemaining + 1),
                "remove is the action that releases the session definition slot");
            Assert.That(afterRemove.SessionsUsed, Is.EqualTo(afterStop.Budget.SessionsUsed - 1));
        }

        private static string UniqueLabel(string prefix)
        {
            return "list-" + prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private void Create(string label, out string sessionId)
        {
            sessionId = DebugSessionHost.ApplyCreate(DebugSessionHost.PrepareCreate(new DebugSessionCreateRequest
            {
                Label = label,
                ActivationScope = UnityCliDebugActivationScopes.EditorAndPlay,
                ClockKind = UnityCliDebugClocks.Monotonic
            })).SessionId;
            created.Add(sessionId);
        }

        private static DebugSessionStopResult Stop(string sessionId)
        {
            return DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
        }

        private static void Release(string sessionId)
        {
            try
            {
                DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
            }
            catch
            {
                // 已停止或已删除都无需再停止。
            }
            try
            {
                DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = sessionId }));
            }
            catch
            {
                // 定义已删除，或仍有未完成清理时保持现状。
            }
        }
    }
}
#endif
