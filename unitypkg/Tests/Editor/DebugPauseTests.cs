#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// 现场调试代码可以引用的公开探针。
    /// 暂停现场的真实执行线程在生成程序集内运行，因此探针必须是 public；它只服务于测试夹具，不进入产品核心。
    /// </summary>
    public static class UnityCliPauseProbe
    {
        /// <summary>阻塞闸门：现场调试代码等待它变为 true 后才返回。</summary>
        public static volatile bool Gate;

        /// <summary>现场用户同步代码已经开始运行的标记；夹具据此区分"已进入用户代码"与"仍在编译"。</summary>
        public static volatile bool Entered;

        /// <summary>由现场用户同步代码的第一条语句调用；它只标记运行事实，不改变任何状态。</summary>
        public static void MarkEntered() { Entered = true; }

        /// <summary>在一次现场服务中再次命中同一断点；未被抑制时它会阻塞到自己的 bounded 超时。</summary>
        public static string NestedHit()
        {
            var binding = UnityCliPauseProbeState.NestedBinding;
            var invocation = UnityCliPauseProbeState.CreateInvocation(binding, 7);
            DebugPauseHost.Hit(binding, invocation, new object[] { 7 }, true, false);
            return "nested-hit-returned";
        }
    }

    /// <summary>探针的接线状态；由测试在断言前设置。</summary>
    internal static class UnityCliPauseProbeState
    {
        internal static MethodHookBinding NestedBinding;

        internal static MethodHookInvocation CreateInvocation(MethodHookBinding binding, int argument)
        {
            return new MethodHookInvocation(binding, null, new object[] { argument }, null, new MethodHookInterceptionStamp(), true, false);
        }
    }

    /// <summary>
    /// pause core 与 pause mailbox 的定向测试。
    ///
    /// 测试直接驱动命中路径（<see cref="DebugPauseHost.Hit"/>）与 mailbox，不安装真实 detour：
    /// 断点本来就是 hook 的一种命中行为，因此命中线程就是这里启动的后台线程。
    /// </summary>
    public sealed class DebugPauseTests
    {
        private readonly List<Thread> hosts = new List<Thread>();
        private readonly List<DebugPauseSite> sites = new List<DebugPauseSite>();
        private bool channelWasPublished;

        public int ProbeTarget(int delta) { return delta; }

        [SetUp]
        public void ResetPauseState()
        {
            channelWasPublished = DebugPauseControl.Available;
            UnityCliPauseProbe.Gate = false;
            UnityCliPauseProbe.Entered = false;
            UnityCliPauseProbeState.NestedBinding = null;
            DebugPauseHost.Reset();
            // 暂停现场以控制通道的解除路径为前提，而发布它的唯一产品入口是 Bridge 启动路径：
            // 夹具在这里补上与真实桥相同的发布，否则现场无法建立，后续断言观察到的是另一种状态。
            DebugPauseControl.Activate();
            // 暂停事实与其它调试事件共用同一个 store；夹具在每例前后重置，
            // 否则先执行（按名称顺序）的用例会污染后面用例的“恰好一次”断言。
            DebugEventStore.Reset();
            hosts.Clear();
            sites.Clear();
        }

        [TearDown]
        public void ReleaseEverySite()
        {
            UnityCliPauseProbe.Gate = true;
            DebugPauseHost.ReleaseAll(DebugPauseReleaseReasons.Resume);
            DebugPauseHost.Reset();
            // 控制通道是本次启动的真实机制状态：夹具只能恢复它，绝不能在桥仍运行时把它关掉。
            if (channelWasPublished) DebugPauseControl.Activate();
            DebugEventStore.Reset();
        }

        // ------------------------------------------------------------------
        // 位置语义
        // ------------------------------------------------------------------

        [Test]
        public void DebugPausePrefixAndPostfixPositionsOnlyMatchTheirOwnBoundary()
        {
            var prefixBinding = Binding(DebugPausePositions.Prefix, PauseTimeout(60000L, false));
            // 位置不匹配：postfix 断点不会在 prefix 边界命中，且不建立任何现场。
            DebugPauseHost.Hit(prefixBinding, Invocation(prefixBinding, 1), new object[] { 1 }, false, false);
            Assert.AreEqual(0L, DebugPauseHost.ActiveSiteCount, "a prefix breakpoint must not match a postfix boundary");

            var site = HitInBackground(prefixBinding);
            Assert.AreEqual(DebugPausePositions.Prefix, site.Position);
            DebugPauseHost.RequestResume(site.PauseId, DebugPauseReleaseReasons.Resume);
            JoinHosts();
            Assert.IsTrue(site.IsTerminal, "a resumed prefix site must be terminal");

            var postfixBinding = Binding(DebugPausePositions.Postfix, PauseTimeout(60000L, false));
            DebugPauseHost.Hit(postfixBinding, Invocation(postfixBinding, 1), new object[] { 1 }, true, false);
            Assert.AreEqual(0L, DebugPauseHost.ActiveSiteCount, "a postfix breakpoint must not match a prefix boundary");
            var postfixSite = HitInBackground(postfixBinding, false);
            Assert.AreEqual(DebugPausePositions.Postfix, postfixSite.Position);
            DebugPauseHost.RequestResume(postfixSite.PauseId, DebugPauseReleaseReasons.Resume);
            JoinHosts();
        }

        [Test]
        public void DebugPauseSnapshotBreakpointNeverBlocksTheHitThread()
        {
            var binding = Binding(DebugPausePositions.Prefix, new MethodHookBreakpoint
            {
                Kind = DebugPauseBreakpointKinds.Snapshot,
                Position = DebugPausePositions.Prefix,
                StackDepth = 8
            });
            var started = DebugEventClocks.MonotonicMilliseconds;
            DebugPauseHost.Hit(binding, Invocation(binding, 3), new object[] { 3 }, true, false);
            Assert.Less(DebugEventClocks.MonotonicMilliseconds - started, 2000d, "a snapshot breakpoint must continue without waiting for a client");
            Assert.AreEqual(0L, DebugPauseHost.ActiveSiteCount, "a snapshot breakpoint must not publish a pause site");
            var snapshot = PauseEvents("snapshot");
            Assert.AreEqual(1, snapshot.Count, "a snapshot breakpoint must record exactly one snapshot fact");
            Assert.AreEqual("snapshot", Convert.ToString(snapshot[0]["name"]));
        }

        // ------------------------------------------------------------------
        // 有限与无限时间边界
        // ------------------------------------------------------------------

        [Test]
        public void DebugPauseBoundedTimeoutAutoReleasesAndRecordsPauseTimeout()
        {
            var site = HitInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(250L, false)));
            var started = DebugEventClocks.MonotonicMilliseconds;
            Assert.IsTrue(WaitFor(() => site.IsTerminal, 10000), "a bounded pause must release itself without any client request");
            Assert.AreEqual(DebugPauseStates.TimedOut, site.State);
            Assert.AreEqual(DebugPauseReleaseReasons.Timeout, site.TerminalReason);
            Assert.IsFalse(site.ResumeRequested, "a bounded expiry is not a resume");
            // 本测试自己的时钟在 HitInBackground 返回后才开始计时，而现场的 deadline 在创建时就已启动，
            // 因此不能用它作为下界（会短少几毫秒）。下界改用产品自身测量的 PausedForMs，测试时钟只用于证明未挂死。
            Assert.Less(DebugEventClocks.MonotonicMilliseconds - started, 10000d, "a bounded expiry must not hang the wait");
            var terminal = PauseEvents("timed-out");
            Assert.AreEqual(1, terminal.Count, "a bounded expiry must record a pause-timeout fact");
            var view = DebugPauseHost.View(site);
            Assert.GreaterOrEqual(view.PausedForMs, 250d, "the site's own paused duration must reach its declared bounded window");
            Assert.AreEqual(DebugPauseTimeoutKinds.Bounded, view.TimeoutKind);
            Assert.AreEqual(250L, view.TimeoutDurationMs);
            Assert.IsTrue(view.HasDeadline);
            JoinHosts();
        }

        [Test]
        public void DebugPauseUnboundedIsNotReleasedByWaitEndAndReportsNoDeadline()
        {
            var site = HitInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(0L, true)));
            Assert.IsTrue(WaitFor(() => DebugPauseHost.ActiveSiteCount == 1L, 5000));
            // 等待窗口结束、CLI 退出都不解除无限暂停。
            Thread.Sleep(400);
            Assert.IsTrue(site.IsPaused, "an unbounded pause must survive the end of any wait");
            var view = DebugPauseHost.View(site);
            Assert.AreEqual(DebugPauseTimeoutKinds.Unbounded, view.TimeoutKind);
            Assert.AreEqual("unbounded", view.EffectiveBound);
            Assert.IsFalse(view.HasDeadline, "an unbounded pause must report that it has no deadline");
            DebugPauseHost.RequestResume(site.PauseId, DebugPauseReleaseReasons.Resume);
            Assert.IsTrue(WaitFor(() => site.IsTerminal, 5000), "an explicit resume must release an unbounded pause");
            JoinHosts();
        }

        [Test]
        public void DebugPauseTimeoutIsRequiredAndNeverDefaultsToInfinite()
        {
            var path = "breakpoint";
            Assert.IsNull(DebugPauseHost.Prepare(null, path), "an omitted breakpoint must produce no plan");

            AssertSchema("pause-timeout-missing", () => DebugPauseHost.Prepare(new MethodHookBreakpointSpec { Kind = "pause" }, path), path + ".pauseTimeout");
            AssertSchema(
                "duration-missing",
                () => DebugPauseHost.Prepare(new MethodHookBreakpointSpec { Kind = "pause", PauseTimeout = new MethodHookPauseTimeoutSpec { Kind = "bounded" } }, path),
                path + ".pauseTimeout.durationMs");
            AssertSchema(
                "duration-null",
                () => DebugPauseHost.Prepare(new MethodHookBreakpointSpec { Kind = "pause", PauseTimeout = new MethodHookPauseTimeoutSpec { Kind = "bounded", DurationMs = null } }, path),
                path + ".pauseTimeout.durationMs");
            AssertSchema(
                "duration-zero",
                () => DebugPauseHost.Prepare(new MethodHookBreakpointSpec { Kind = "pause", PauseTimeout = new MethodHookPauseTimeoutSpec { Kind = "bounded", DurationMs = 0L } }, path),
                path + ".pauseTimeout.durationMs");
            AssertSchema(
                "duration-negative",
                () => DebugPauseHost.Prepare(new MethodHookBreakpointSpec { Kind = "pause", PauseTimeout = new MethodHookPauseTimeoutSpec { Kind = "bounded", DurationMs = -5L } }, path),
                path + ".pauseTimeout.durationMs");
            AssertSchema(
                "unbounded-with-duration",
                () => DebugPauseHost.Prepare(new MethodHookBreakpointSpec { Kind = "pause", PauseTimeout = new MethodHookPauseTimeoutSpec { Kind = "unbounded", DurationMs = 10L } }, path),
                path + ".pauseTimeout.durationMs");
            AssertSchema(
                "unknown-timeout-kind",
                () => DebugPauseHost.Prepare(new MethodHookBreakpointSpec { Kind = "pause", PauseTimeout = new MethodHookPauseTimeoutSpec { Kind = "infinite" } }, path),
                path + ".pauseTimeout.kind");
            AssertSchema(
                "unknown-kind",
                () => DebugPauseHost.Prepare(new MethodHookBreakpointSpec { Kind = "watch" }, path),
                path + ".kind");
            AssertSchema(
                "snapshot-with-timeout",
                () => DebugPauseHost.Prepare(new MethodHookBreakpointSpec { Kind = "snapshot", PauseTimeout = new MethodHookPauseTimeoutSpec { Kind = "unbounded" } }, path),
                path + ".pauseTimeout");

            var bounded = DebugPauseHost.Prepare(new MethodHookBreakpointSpec { Kind = "pause", PauseTimeout = new MethodHookPauseTimeoutSpec { Kind = "bounded", DurationMs = 1234L } }, path);
            Assert.AreEqual(1234L, bounded.Breakpoint.DurationMs);
            Assert.IsFalse(bounded.Breakpoint.Unbounded);
            var unbounded = DebugPauseHost.Prepare(new MethodHookBreakpointSpec { Kind = "pause", PauseTimeout = new MethodHookPauseTimeoutSpec { Kind = "unbounded" } }, path);
            Assert.IsTrue(unbounded.Breakpoint.Unbounded);
            Assert.AreEqual(0L, unbounded.Breakpoint.DurationMs);
            // 超上限的 stackDepth 被夹到声明上限，而不是拒绝整个配置。
            var deep = DebugPauseHost.Prepare(new MethodHookBreakpointSpec { Kind = "snapshot", StackDepth = 100000 }, path);
            Assert.AreEqual(DebugLimits.MaxPauseStackFrames, deep.Breakpoint.StackDepth);
        }

        // ------------------------------------------------------------------
        // pauseId 身份与解除
        // ------------------------------------------------------------------

        [Test]
        public void DebugPauseIdIsSingleUseAndStaleAfterResume()
        {
            var first = HitInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(60000L, false)));
            DebugPauseHost.RequestResume(first.PauseId, DebugPauseReleaseReasons.Resume);
            JoinHosts();
            Assert.IsTrue(WaitFor(() => first.IsTerminal, 5000));
            var failure = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugPauseHost.FindSite(first.PauseId));
            Assert.AreEqual("E_DEBUG_PAUSE_STALE", failure.Code);
            Assert.AreEqual(DebugPauseStates.Resumed, Convert.ToString(((IDictionary<string, object>)failure.Details)["state"]));
            var unknown = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugPauseHost.FindSite("pause-9999"));
            Assert.AreEqual("E_DEBUG_PAUSE_STALE", unknown.Code);

            var second = HitInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(60000L, false)));
            Assert.AreNotEqual(first.PauseId, second.PauseId, "a resumed pauseId must never be reused");
            DebugPauseHost.RequestResume(second.PauseId, DebugPauseReleaseReasons.Resume);
            JoinHosts();
        }

        [Test]
        public void DebugPauseResumeReleasesExactlyOneSite()
        {
            var first = HitInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(60000L, false)));
            var second = HitInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(60000L, false)));
            Assert.IsTrue(WaitFor(() => DebugPauseHost.ActiveSiteCount == 2L, 5000));
            DebugPauseHost.RequestResume(first.PauseId, DebugPauseReleaseReasons.Resume);
            Assert.IsTrue(WaitFor(() => first.IsTerminal, 5000));
            Assert.IsTrue(second.IsPaused, "releasing one site must not release another");
            Assert.AreEqual(1L, DebugPauseHost.ActiveSiteCount);
            DebugPauseHost.RequestResume(second.PauseId, DebugPauseReleaseReasons.Resume);
            Assert.IsTrue(WaitFor(() => second.IsTerminal, 5000));
            JoinHosts();
        }

        [Test]
        public void DebugPauseResumeIsNeverQueuedBehindWaitingRequestsAndAnswersStrandedRequests()
        {
            // 直接驱动 mailbox：现场已经收到 resume 时不得再接受新请求。
            var synthetic = SyntheticSite("pause-mailbox");
            var accepted = new DebugPauseOperation(DebugPauseOperationKinds.Inspect, "test:mailbox");
            Assert.IsNull(DebugPauseMailbox.TryEnqueue(synthetic, accepted, out var enqueued));
            Assert.IsTrue(enqueued);
            synthetic.ResumeRequested = true;
            var refused = DebugPauseMailbox.TryEnqueue(synthetic, new DebugPauseOperation(DebugPauseOperationKinds.Inspect, "test:mailbox"), out var secondEnqueued);
            Assert.IsFalse(secondEnqueued);
            Assert.AreEqual("E_DEBUG_PAUSE_STALE", refused.Code);
            DebugPauseMailbox.Terminalize(synthetic, DebugPauseStates.Resumed, DebugPauseReleaseReasons.Resume);
            Assert.IsTrue(accepted.Completed, "a stranded request must be answered instead of hanging its caller");
            Assert.AreEqual("E_DEBUG_PAUSE_STALE", accepted.Failure.Code);
        }

        [Test]
        public void DebugPauseMailboxRefusesTheNinthWaitingRequestWithALimitError()
        {
            var site = SyntheticSite("pause-mailbox-limit");
            for (var index = 0; index < DebugLimits.MaxPauseMailboxWaiting; index++)
            {
                var failure = DebugPauseMailbox.TryEnqueue(site, new DebugPauseOperation(DebugPauseOperationKinds.Inspect, "test:mailbox-" + index), out var enqueued);
                Assert.IsNull(failure);
                Assert.IsTrue(enqueued);
            }
            Assert.AreEqual((long)DebugLimits.MaxPauseMailboxWaiting, site.WaitingCount);
            var overflow = DebugPauseMailbox.TryEnqueue(site, new DebugPauseOperation(DebugPauseOperationKinds.Inspect, "test:overflow"), out var overflowEnqueued);
            Assert.IsFalse(overflowEnqueued);
            Assert.AreEqual("E_DEBUG_LIMIT_EXCEEDED", overflow.Code);
            Assert.AreEqual(1L, site.RefusedRequests);
            var details = (IDictionary<string, object>)overflow.Details;
            Assert.AreEqual("pause-mailbox-waiting", Convert.ToString(details["limit"]));
            Assert.AreEqual((long)DebugLimits.MaxPauseMailboxWaiting, Convert.ToInt64(details["used"]));
        }

        // ------------------------------------------------------------------
        // off-main 服务通道：暂停期间的可达 Tool 必须在后台线程上真正完成并答复自己的请求
        // ------------------------------------------------------------------

        [Test]
        public void DebugPauseOffMainListSeesAHeldSiteAndItsReplyIsCorrelated()
        {
            var site = HitMainThreadPauseInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(0L, true)));
            DebugMainThreadPauseView pause;
            Assert.IsTrue(DebugPauseControl.TryGetMainThreadPause(out pause), "a held main-thread site must be published as the blocker source");
            Assert.AreEqual(site.PauseId, pause.PauseId);

            // 后台线程：真实桥的服务线程不是主线程，因此在主线程专属 Unity API 上会像产品一样失败。
            var envelope = OffMain(() => Serve(WrappedRequest("debug.pause-list", "{}"), "debug.pause-list", pause));
            Assert.AreEqual("req-under-test", Convert.ToString(envelope["requestId"]), "the off-main reply must carry the request's own id");
            Assert.IsTrue(Convert.ToBoolean(envelope["ok"]), "debug.pause-list must be served while a site is held");
            var data = TerminalResult(envelope);
            Assert.AreEqual(1L, Convert.ToInt64(data["total"]), "the off-main list must see the held site");
            var listed = ((IEnumerable<object>)data["sites"]).Select(item => (IDictionary<string, object>)item).ToList();
            Assert.AreEqual(site.PauseId, Convert.ToString(listed[0]["pauseId"]));
            Assert.AreEqual((long)site.ThreadId, Convert.ToInt64(listed[0]["threadId"]));
            Assert.IsTrue(Convert.ToBoolean(listed[0]["mainThread"]));
            Assert.AreEqual(DebugPauseTimeoutKinds.Unbounded, Convert.ToString(listed[0]["timeoutKind"]));
            Assert.AreEqual(pause.PausedForMs, Convert.ToDouble(listed[0]["pausedForMs"]), 1000d, "the published pause duration must be the same fact health reads");

            // 业务变更仍在任何 effect 之前被拒绝：控制通道只服务冻结名单。
            foreach (var tool in new[] { "debug.session-stop", "debug.hook-install", "debug.code-execute" })
            {
                string nestedTool;
                Assert.IsFalse(DebugPauseControl.IsReachable(WrappedRequest(tool, "{}"), out nestedTool, out _), tool + " must stay refused while the main thread is held");
                Assert.AreEqual(tool, nestedTool);
            }
            Assert.AreEqual(DebugPauseControl.RejectionCode, DebugPauseControl.MainThreadPaused("catalog.editor.run", "debug.session-stop", pause).Code);

            DebugPauseHost.RequestResume(site.PauseId, DebugPauseReleaseReasons.Resume);
            JoinHosts();
        }

        [Test]
        public void DebugPauseOffMainResumeReleasesTheSiteAndThePausedThreadContinues()
        {
            var binding = Binding(DebugPausePositions.Prefix, PauseTimeout(0L, true));
            var completed = false;
            var thread = new Thread(() =>
            {
                try { DebugPauseHost.Hit(binding, Invocation(binding, 9), new object[] { 9 }, true, true); }
                catch { /* 命中线程的异常不得影响断言线程 */ }
                completed = true;
            }) { IsBackground = true };
            thread.Start();
            hosts.Add(thread);
            Assert.IsTrue(WaitFor(() => DebugPauseHost.ActiveSiteCount == 1L, 10000), "the hit thread must publish a main-thread pause site");
            var site = DebugPauseHost.List(new DebugPauseListRequest(), UnityCliBridge.EditorSessionId()).Sites.First();
            Assert.IsFalse(completed, "an unbounded pause must hold its thread until it is released");

            DebugMainThreadPauseView pause;
            Assert.IsTrue(DebugPauseControl.TryGetMainThreadPause(out pause));
            // 解除路径必须在后台线程上真正到达现场，而不是死在主线程专属 API 上。
            var envelope = OffMain(() => Serve(WrappedRequest("debug.pause-resume", "{\"pauseId\":\"" + site.PauseId + "\"}"), "debug.pause-resume", pause));
            Assert.AreEqual("req-under-test", Convert.ToString(envelope["requestId"]), "the resume reply must carry the request's own id");
            Assert.IsTrue(Convert.ToBoolean(envelope["ok"]), "an unbounded pause must be releasable by an explicit resume while it is held");
            var data = TerminalResult(envelope);
            Assert.IsTrue(Convert.ToBoolean(data["released"]), "the resume reply must report the site as released");
            Assert.AreEqual(DebugPauseReleaseReasons.Resume, Convert.ToString(data["releaseReason"]));
            Assert.IsFalse(Convert.ToBoolean(data["preemption"]), "resume releases the wait and never claims preemption");

            Assert.IsTrue(thread.Join(10000), "the paused thread must continue after the resume");
            Assert.IsTrue(completed, "the held call must return once the site is released");
            Assert.AreEqual(0L, DebugPauseHost.ActiveSiteCount, "the released site must leave the active registry");
            // 现场已终结：按同一 pauseId 反查必须得到“已解除且原因是 resume”的确定事实。
            var stale = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugPauseHost.FindSite(site.PauseId));
            Assert.AreEqual("E_DEBUG_PAUSE_STALE", stale.Code);
            var staleDetails = (IDictionary<string, object>)stale.Details;
            Assert.AreEqual(DebugPauseStates.Resumed, Convert.ToString(staleDetails["state"]));
            Assert.AreEqual(DebugPauseReleaseReasons.Resume, Convert.ToString(staleDetails["reason"]));
        }

        [Test]
        public void DebugPauseOffMainAllowlistedReadsAreAllAnsweredWithTheirOwnRequestId()
        {
            Assert.IsTrue(DebugPauseControl.Available, "the control channel must be published before a site can be installed");
            var site = HitMainThreadPauseInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(0L, true)));
            DebugMainThreadPauseView pause;
            Assert.IsTrue(DebugPauseControl.TryGetMainThreadPause(out pause));

            // 名单里的只读能力必须逐一在后台线程上得到带正确 requestId 的成功答复。
            foreach (var tool in new[]
            {
                "debug.pause-list", "debug.pause-wait", "debug.events-read", "debug.thread-list",
                "debug.hook-list", "debug.session-list", "debug.program-list"
            })
            {
                var envelope = OffMain(() => Serve(WrappedRequest(tool, "{}"), tool, pause));
                Assert.AreEqual("req-under-test", Convert.ToString(envelope["requestId"]), tool + " must answer with its own request id");
                Assert.IsTrue(Convert.ToBoolean(envelope["ok"]), tool + " must be served off-main during a pause; got " + FieldText(envelope, "error"));
            }

            // 现场读取走到暂停线程上的 mailbox：答复同样必须与请求相关。
            var inspect = OffMain(() => Serve(WrappedRequest("debug.pause-inspect", "{\"pauseId\":\"" + site.PauseId + "\"}"), "debug.pause-inspect", pause));
            Assert.AreEqual("req-under-test", Convert.ToString(inspect["requestId"]));
            Assert.IsTrue(Convert.ToBoolean(inspect["ok"]), "debug.pause-inspect must be served while a site is held");
            Assert.AreEqual(site.PauseId, Convert.ToString(TerminalResult(inspect)["pauseId"]));

            // 命名了未知 id 的信息读取必须给出“已服务”的确定性答复，而不是死在写回复之前。
            var unknown = OffMain(() => Serve(WrappedRequest("debug.hook-info", "{\"hookId\":\"no-such-hook\"}"), "debug.hook-info", pause));
            Assert.AreEqual("req-under-test", Convert.ToString(unknown["requestId"]));
            Assert.IsFalse(Convert.ToBoolean(unknown["ok"]));
            var failure = (IDictionary<string, object>)unknown["error"];
            Assert.IsFalse(string.IsNullOrEmpty(Convert.ToString(failure["code"])));
            Assert.AreNotEqual("E_BRIDGE_INTERNAL_ERROR", Convert.ToString(failure["code"]), "an unknown id must be answered deliberately, not by swallowing an off-main failure");

            DebugPauseHost.RequestResume(site.PauseId, DebugPauseReleaseReasons.Resume);
            JoinHosts();
        }

        // ------------------------------------------------------------------
        // 暂停期间的 session.stop：只有持有现场的 owner 可达，且需要主线程的清理必须报告 pending
        // ------------------------------------------------------------------

        [Test]
        public void DebugPauseOffMainSessionStopOfTheOwningSessionReleasesTheSiteAndReportsDeferredCleanup()
        {
            var sessionId = CreatePauseOwningSession();
            // 现场所属的 session 必须先按产品路径保留 pause 资源：释放现场的就是这条资源清理委托。
            var breakpoint = DebugPauseHost.Prepare(new MethodHookBreakpointSpec
            {
                Kind = DebugPauseBreakpointKinds.Pause,
                Position = DebugPausePositions.Prefix,
                SessionId = sessionId,
                PauseTimeout = new MethodHookPauseTimeoutSpec { Kind = DebugPauseTimeoutKinds.Unbounded }
            }, "breakpoint");
            Assert.IsNotNull(breakpoint.Owner, "a session-owned pause breakpoint must reserve its resource before any effect");
            var binding = Binding(DebugPausePositions.Prefix, PauseTimeout(0L, true));
            binding.Breakpoint.SessionId = sessionId;
            DebugPauseHost.CommitPlan(breakpoint, binding.HookId);

            // 两个清理条目：一个声明需要主线程（detour 撤除），一个 off-main 安全（释放等待与句柄）。
            var deferredRan = false;
            var offMainSafeRan = false;
            DebugSessionHost.RegisterCleanupStep(
                sessionId,
                UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.DetachHooks),
                "test-detach-entry",
                UnityCliDebugFacilityOwners.Hook,
                report => { deferredRan = true; return true; },
                true);
            DebugSessionHost.RegisterCleanupStep(
                sessionId,
                UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.ReleaseWaits),
                "test-release-entry",
                UnityCliDebugFacilityOwners.Run,
                report => { offMainSafeRan = true; return true; });

            try
            {
                var site = HitMainThreadPauseInBackground(binding);
                DebugMainThreadPauseView pause;
                Assert.IsTrue(DebugPauseControl.TryGetMainThreadPause(out pause), "a held main-thread site must be published as the blocker source");
                Assert.AreEqual(sessionId, pause.SessionId, "the held site must name the session that owns it");

                // 精确放宽：只有 stop 持有现场的那个 session 可达；其它 session 与其它变更仍在效果之前被拒。
                string nestedTool;
                Assert.IsTrue(
                    DebugPauseControl.IsReachable(WrappedRequest("debug.session-stop", "{\"sessionId\":\"" + sessionId + "\"}"), out nestedTool, out _),
                    "stop of the session that owns the held site must be reachable while the main thread is paused");
                Assert.IsFalse(
                    DebugPauseControl.IsReachable(WrappedRequest("debug.session-stop", "{\"sessionId\":\"dbg-other\"}"), out nestedTool, out _),
                    "a stop that names another session must stay refused");
                Assert.IsFalse(
                    DebugPauseControl.IsReachable(WrappedRequest("debug.session-stop", "{}"), out nestedTool, out _),
                    "a stop that names no session must stay refused");
                foreach (var tool in new[] { "debug.hook-install", "debug.hook-remove", "debug.code-execute", "debug.hook-disable" })
                {
                    Assert.IsFalse(DebugPauseControl.IsReachable(WrappedRequest(tool, "{}"), out nestedTool, out _), tool + " must stay refused while the main thread is held");
                    Assert.AreEqual(tool, nestedTool);
                }

                // 真实服务路径：后台线程上的控制通道服务同一个 stop。
                var envelope = OffMain(() => Serve(WrappedRequest("debug.session-stop", "{\"sessionId\":\"" + sessionId + "\"}"), "debug.session-stop", pause));
                Assert.AreEqual("req-under-test", Convert.ToString(envelope["requestId"]), "the off-main stop reply must carry the request's own id");
                Assert.IsTrue(Convert.ToBoolean(envelope["ok"]), "the owning session stop must be served off-main; got " + FieldText(envelope, "error"));
                var data = TerminalResult(envelope);
                Assert.AreEqual(sessionId, Convert.ToString(data["sessionId"]));
                Assert.AreEqual(UnityCliDebugSessionStates.Stopped, Convert.ToString(data["state"]), "stop must close future dispatch for that owner");
                Assert.IsTrue(Convert.ToBoolean(data["changed"]), "the first stop of an open session must report a real state change");
                Assert.IsTrue(Convert.ToBoolean(data["cleanupPending"]), "an entry that needs the main thread must be reported pending");

                // 诚实报告：需要主线程的条目绝不执行，off-main 安全的条目照常完成。
                Assert.IsFalse(deferredRan, "an entry that needs the main thread must not run off-main");
                Assert.IsTrue(offMainSafeRan, "an off-main-safe entry must still run");
                var cleanup = (IDictionary<string, object>)data["cleanup"];
                Assert.AreEqual("pending", Convert.ToString(cleanup["status"]));
                Assert.AreEqual(1L, Convert.ToInt64(cleanup["releasedPauses"]), "the pause resource cleanup must release the held site");
                var steps = ((IEnumerable<object>)cleanup["steps"]).Select(item => (IDictionary<string, object>)item).ToList();
                var deferredStep = steps.Single(step => string.Equals(Convert.ToString(step["name"]), UnityCliDebugCleanupSteps.DetachHooks, StringComparison.Ordinal));
                Assert.IsFalse(Convert.ToBoolean(deferredStep["succeeded"]), "a deferred entry must not claim success");
                var note = Convert.ToString(deferredStep["note"]);
                Assert.IsTrue(note.Contains(UnityCliDebugSessionRegistry.MainThreadDeferredReason), "the step must carry the exact deferral reason; got " + note);
                Assert.IsTrue(Convert.ToBoolean(deferredStep["ownerRegistered"]), "the deferred step must name its real owner instead of reporting zero");
                Assert.IsTrue(((IEnumerable<object>)cleanup["unmanagedFacts"]).Select(Convert.ToString).Any(fact => fact.Contains("test-detach-entry")), "the report must name the deferred entry");
                var releasedStep = steps.Single(step => string.Equals(Convert.ToString(step["name"]), UnityCliDebugCleanupSteps.ReleaseWaits, StringComparison.Ordinal));
                Assert.IsTrue(Convert.ToBoolean(releasedStep["succeeded"]), "an off-main-safe step must report success");

                // 暂停信号真的送达：被暂停的线程继续执行，而没有声称抢占。
                Assert.IsTrue(WaitFor(() => site.IsTerminal, 10000), "the owning session's stop must release the held site instead of abandoning its thread");
                Assert.AreEqual(DebugPauseReleaseReasons.SessionStop, site.TerminalReason);
                Assert.AreEqual(0L, DebugPauseHost.ActiveSiteCount, "the released site must leave the active registry");
                Assert.IsTrue(threadsReturned(), "the paused thread must return once its site is released");
                Assert.AreEqual(0L, DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = sessionId }).Budget.ActiveResourcesUsed, "the released pause resource must return its slot");

                // 线程恢复后由原 owner 重试同一 stop：被推迟的条目现在真的执行，pending 收敛。
                var retry = DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
                Assert.IsTrue(deferredRan, "repeating stop after the thread resumed must complete the deferred entry");
                Assert.IsFalse(retry.CleanupPending, "the retried stop must converge to a complete cleanup");
            }
            finally
            {
                DebugPauseHost.ReleaseAll(DebugPauseReleaseReasons.Resume);
                JoinHosts();
                try
                {
                    DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
                    DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = sessionId }));
                }
                catch (Exception exception)
                {
                    UnityCliBridge.WriteLog("debug_pause_off_main_stop_cleanup_failed session=" + sessionId + " error=" + exception.GetType().Name);
                }
            }
        }

        [Test]
        public void DebugPauseOffMainSessionStopDefersTheMainThreadOnlyStoreFactsAndCompletesThemAfterResume()
        {
            // 主线程专属的 store 恢复事实（SessionState 两键）不得由 off-main 服务写入：
            // 否则一个已经落盘的 stop 会在写入这一步抛 UnityException，调用方拿到 E_BRIDGE_INTERNAL_ERROR
            // 而现场其实已经被改变。推迟必须被如实命名，并在线程恢复后的主线程发布里真的落地。
            DebugSessionStore.PublishRecoveryFacts();
            Assert.IsFalse(DebugSessionStore.RecoveryFactsPending, "the store must start this case with no pending recovery fact");

            var sessionId = CreatePauseOwningSession();
            var breakpoint = DebugPauseHost.Prepare(new MethodHookBreakpointSpec
            {
                Kind = DebugPauseBreakpointKinds.Pause,
                Position = DebugPausePositions.Prefix,
                SessionId = sessionId,
                PauseTimeout = new MethodHookPauseTimeoutSpec { Kind = DebugPauseTimeoutKinds.Unbounded }
            }, "breakpoint");
            Assert.IsNotNull(breakpoint.Owner, "a session-owned pause breakpoint must reserve its resource before any effect");
            var binding = Binding(DebugPausePositions.Prefix, PauseTimeout(0L, true));
            binding.Breakpoint.SessionId = sessionId;
            DebugPauseHost.CommitPlan(breakpoint, binding.HookId);

            try
            {
                var site = HitMainThreadPauseInBackground(binding);
                DebugMainThreadPauseView pause;
                Assert.IsTrue(DebugPauseControl.TryGetMainThreadPause(out pause), "a held main-thread site must be published as the blocker source");

                var envelope = OffMain(() => Serve(WrappedRequest("debug.session-stop", "{\"sessionId\":\"" + sessionId + "\"}"), "debug.session-stop", pause));
                Assert.IsTrue(Convert.ToBoolean(envelope["ok"]), "the owning session stop must be served off-main; got " + FieldText(envelope, "error"));
                var data = TerminalResult(envelope);
                Assert.AreEqual(UnityCliDebugSessionStates.Stopped, Convert.ToString(data["state"]), "stop must close future dispatch for that owner");
                Assert.IsTrue(DebugSessionStore.RecoveryFactsPending,
                    "the main-thread-only store recovery facts must be deferred instead of written from the off-main stop");

                // 推迟是命名事实：报告给出条目名与精确原因，而不是静默丢弃这个未完成的写入。
                var cleanup = (IDictionary<string, object>)data["cleanup"];
                var facts = ((IEnumerable<object>)cleanup["unmanagedFacts"]).Select(Convert.ToString).ToList();
                Assert.IsTrue(facts.Any(fact => fact.Contains(DebugSessionStore.RecoveryFactsEntryName) && fact.Contains(DebugSessionStore.RecoveryFactsDeferredReason)),
                    "the report must name the deferred store write and its exact reason; got " + string.Join(" | ", facts));

                // 现场照旧被释放，被暂停的线程继续执行；随后在主线程上重试同一 stop，待写事实必须完成。
                Assert.IsTrue(WaitFor(() => site.IsTerminal, 10000), "the stop must release the held site even while its store facts stay deferred");
                DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
                Assert.IsFalse(DebugSessionStore.RecoveryFactsPending,
                    "repeating stop after the thread resumed must complete the deferred store write");
            }
            finally
            {
                DebugPauseHost.ReleaseAll(DebugPauseReleaseReasons.Resume);
                JoinHosts();
                try
                {
                    DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
                    DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = sessionId }));
                }
                catch (Exception exception)
                {
                    UnityCliBridge.WriteLog("debug_pause_off_main_store_deferral_cleanup_failed session=" + sessionId + " error=" + exception.GetType().Name);
                }
            }
        }

        [Test]
        public void DebugPauseOffMainSessionStopReportsPendingAndRefusesRemovalUntilTheDeferredEntryCompletes()
        {
            // ISSUE-093：off-main stop 的报告与 remove 判定必须同源。
            // 推迟的主线程专属恢复事实就是一个未完成条目：它必须让报告 cleanupPending，并拒绝 remove。
            DebugSessionStore.PublishRecoveryFacts();
            Assert.IsFalse(DebugSessionStore.RecoveryFactsPending, "the store must start this case with no pending recovery fact");

            var sessionId = CreatePauseOwningSession();
            var breakpoint = DebugPauseHost.Prepare(new MethodHookBreakpointSpec
            {
                Kind = DebugPauseBreakpointKinds.Pause,
                Position = DebugPausePositions.Prefix,
                SessionId = sessionId,
                PauseTimeout = new MethodHookPauseTimeoutSpec { Kind = DebugPauseTimeoutKinds.Unbounded }
            }, "breakpoint");
            Assert.IsNotNull(breakpoint.Owner, "a session-owned pause breakpoint must reserve its resource before any effect");
            var binding = Binding(DebugPausePositions.Prefix, PauseTimeout(0L, true));
            binding.Breakpoint.SessionId = sessionId;
            DebugPauseHost.CommitPlan(breakpoint, binding.HookId);

            try
            {
                var site = HitMainThreadPauseInBackground(binding);
                DebugMainThreadPauseView pause;
                Assert.IsTrue(DebugPauseControl.TryGetMainThreadPause(out pause), "a held main-thread site must be published as the blocker source");
                Assert.AreEqual(sessionId, pause.SessionId, "the held site must name the session that owns it");

                var envelope = OffMain(() => Serve(WrappedRequest("debug.session-stop", "{\"sessionId\":\"" + sessionId + "\"}"), "debug.session-stop", pause));
                Assert.IsTrue(Convert.ToBoolean(envelope["ok"]), "the owning session stop must be served off-main; got " + FieldText(envelope, "error"));
                var data = TerminalResult(envelope);
                Assert.AreEqual(UnityCliDebugSessionStates.Stopped, Convert.ToString(data["state"]));
                Assert.IsTrue(Convert.ToBoolean(data["cleanupPending"]), "a deferred cleanup entry must be reported pending, not complete");
                var cleanup = (IDictionary<string, object>)data["cleanup"];
                Assert.AreEqual("pending", Convert.ToString(cleanup["status"]), "the reported status must match the reported pending state");
                Assert.IsTrue(Convert.ToBoolean(cleanup["cleanupPending"]), "the cleanup detail must carry the same pending state");

                // 同源判定：线程恢复前 remove 必须被 E_DEBUG_CLEANUP_PENDING 拒绝，并给出待清理明细与恢复指引。
                var refusal = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = sessionId }));
                Assert.AreEqual("E_DEBUG_CLEANUP_PENDING", refusal.Code);
                // 异常对象的 Details 携带的是产品 DTO 本身；调用方看到的是桥把它序列化后的 JSON 对象。
                // 因此这里用同一序列化器做投影，断言的仍是公开 wire 形状，而不是内存里的类型。
                var refusalDetails = (IDictionary<string, object>)UnityCliSerializer.Parse(UnityCliSerializer.Serialize(refusal.Details));
                Assert.IsTrue(Convert.ToBoolean(refusalDetails["cleanupPending"]), "the refusal must report the same pending state as the stop report");
                var refusalCleanup = (IDictionary<string, object>)refusalDetails["cleanup"];
                Assert.AreEqual("pending", Convert.ToString(refusalCleanup["status"]));
                Assert.IsTrue(Convert.ToBoolean(refusalCleanup["cleanupPending"]), "the refusal cleanup must carry the same pending state as the stop report cleanup");
                Assert.IsTrue(refusal.NextActions.Count > 0, "the refusal must tell the caller how to complete the pending work");
                Assert.IsTrue(DebugSessionStore.RecoveryFactsPending, "this stop deferred the main-thread-only store facts, so the pending entry is real");

                // 现场照旧被释放；线程恢复后的主线程 stop 必须真的完成被推迟的条目并收敛 pending。
                Assert.IsTrue(WaitFor(() => site.IsTerminal, 10000), "the stop must release the held site even while its store facts stay deferred");
                var retry = DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
                Assert.IsFalse(DebugSessionStore.RecoveryFactsPending, "repeating stop after the thread resumed must complete the deferred store write");
                Assert.IsFalse(retry.CleanupPending, "the retried stop must converge to a complete cleanup");
                Assert.AreEqual(0L, retry.Budget.ActiveResourcesUsed, "a converged stop must leave no registered resource");
                var removed = DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = sessionId }));
                Assert.IsTrue(removed.Removed, "remove must succeed once the pending work is complete");
                var gone = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
                Assert.AreEqual("E_DEBUG_SESSION_NOT_FOUND", gone.Code, "a removed session must be gone with no resource left under its name");
            }
            finally
            {
                DebugPauseHost.ReleaseAll(DebugPauseReleaseReasons.Resume);
                JoinHosts();
                try
                {
                    DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
                    DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = sessionId }));
                }
                catch (Exception exception)
                {
                    UnityCliBridge.WriteLog("debug_pause_off_main_pending_removal_cleanup_failed session=" + sessionId + " error=" + exception.GetType().Name);
                }
            }
        }

        /// <summary>建立一个真实的 pause owner session；现场必须属于一个已存在的定义。</summary>
        private static string CreatePauseOwningSession()
        {
            var created = DebugSessionHost.ApplyCreate(DebugSessionHost.PrepareCreate(new DebugSessionCreateRequest
            {
                Label = "pause-stop-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                ActivationScope = UnityCliDebugActivationScopes.EditorAndPlay,
                ClockKind = UnityCliDebugClocks.Monotonic
            }));
            return created.SessionId;
        }

        /// <summary>当前夹具启动的全部命中线程是否都已返回；用于证明暂停信号真的送达到了被暂停的线程。</summary>
        private bool threadsReturned()
        {
            foreach (var thread in hosts)
            {
                if (!thread.Join(5000)) return false;
            }
            hosts.Clear();
            return true;
        }

        // ------------------------------------------------------------------
        // 公开终态契约：off-main 的成功响应必须是 CLI 投影接受的那一个形状
        // ------------------------------------------------------------------

        [Test]
        public void DebugPauseOffMainToolSuccessCarriesTheSameResultTerminalTheCliProjects()
        {
            var site = HitMainThreadPauseInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(0L, true)));
            DebugMainThreadPauseView pause;
            Assert.IsTrue(DebugPauseControl.TryGetMainThreadPause(out pause));

            // 公开 CLI 只接受携带 result 包装的 catalog.editor.run 成功终态；缺少它时一个已经生效的响应
            // 会被报成 malformed terminal，调用方拿不到事实却看到效果。
            var listed = OffMain(() => Serve(WrappedRequest("debug.pause-list", "{}"), "debug.pause-list", pause));
            Assert.IsTrue(Convert.ToBoolean(listed["ok"]), "debug.pause-list must be served off-main while a site is held");
            var listTerminal = (IDictionary<string, object>)listed["data"];
            Assert.AreEqual(1, listTerminal.Count, "a successful catalog.editor.run terminal must carry exactly the result wrapper");
            Assert.IsTrue(listTerminal.ContainsKey("result"), "the off-main Tool terminal must carry the result the CLI projects");
            var listedPayload = (IDictionary<string, object>)listTerminal["result"];
            Assert.AreEqual(1L, Convert.ToInt64(listedPayload["total"]), "the pause list payload must stay reachable underneath the wrapper");
            var listedSite = ((IEnumerable<object>)listedPayload["sites"]).Select(item => (IDictionary<string, object>)item).First();
            Assert.AreEqual(site.PauseId, Convert.ToString(listedSite["pauseId"]));

            // 解除路径同理：现场真的被释放时，调用方必须拿到被投影接受的终态而不是响应非法。
            var resumed = OffMain(() => Serve(WrappedRequest("debug.pause-resume", "{\"pauseId\":\"" + site.PauseId + "\"}"), "debug.pause-resume", pause));
            Assert.IsTrue(Convert.ToBoolean(resumed["ok"]), "debug.pause-resume must be served off-main while a site is held");
            var resumeTerminal = (IDictionary<string, object>)resumed["data"];
            Assert.AreEqual(1, resumeTerminal.Count);
            Assert.IsTrue(resumeTerminal.ContainsKey("result"));
            var resumedPayload = (IDictionary<string, object>)resumeTerminal["result"];
            Assert.IsTrue(Convert.ToBoolean(resumedPayload["released"]), "the resume payload must stay reachable underneath the wrapper");
            Assert.AreEqual(DebugPauseReleaseReasons.Resume, Convert.ToString(resumedPayload["releaseReason"]));
            JoinHosts();
        }

        // ------------------------------------------------------------------
        // 闸门时序与有限暂停可见性
        // ------------------------------------------------------------------

        [Test]
        public void DebugPauseBoundedHitIsListedWithItsDeclaredDeadlineAndAutoReleasesWithTheTimeoutFact()
        {
            var held = DebugEventClocks.MonotonicMilliseconds;
            var site = HitMainThreadPauseInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(4000L, false)));
            DebugMainThreadPauseView pause;
            Assert.IsTrue(DebugPauseControl.TryGetMainThreadPause(out pause), "a held bounded site must be published as the blocker source");
            Assert.AreEqual(DebugPauseTimeoutKinds.Bounded, pause.TimeoutKind);

            // 有限现场必须在它自己的窗口内可读：拿到 hasDeadline、声明时长与仍在推进的 pausedForMs。
            var envelope = OffMain(() => Serve(WrappedRequest("debug.pause-list", "{}"), "debug.pause-list", pause));
            Assert.IsTrue(Convert.ToBoolean(envelope["ok"]), "a bounded site must be readable through the off-main list while it is held");
            var data = TerminalResult(envelope);
            var listed = ((IEnumerable<object>)data["sites"]).Select(item => (IDictionary<string, object>)item)
                .Single(item => string.Equals(Convert.ToString(item["pauseId"]), site.PauseId, StringComparison.Ordinal));
            Assert.AreEqual(DebugPauseTimeoutKinds.Bounded, Convert.ToString(listed["timeoutKind"]));
            Assert.AreEqual("bounded", Convert.ToString(listed["effectiveBound"]));
            Assert.IsTrue(Convert.ToBoolean(listed["hasDeadline"]), "a bounded site must declare a deadline");
            Assert.AreEqual(4000L, Convert.ToInt64(listed["timeoutDurationMs"]), "the declared bound must be readable as declared");
            Assert.Greater(Convert.ToDouble(listed["remainingMs"]), 0d, "a bounded site that is still held must report a remaining window");

            // 到期自动解除并记录 pause-timeout 事实：不需要任何客户端请求，也不需要抢占。
            Assert.IsTrue(WaitFor(() => site.IsTerminal, 20000), "a bounded site must release itself at its declared bound");
            Assert.AreEqual(DebugPauseStates.TimedOut, site.State);
            Assert.AreEqual(DebugPauseReleaseReasons.Timeout, site.TerminalReason);
            Assert.IsFalse(site.ResumeRequested, "a bounded expiry is not a resume");
            var view = DebugPauseHost.View(site);
            Assert.GreaterOrEqual(view.PausedForMs, 4000d, "the site's own paused duration must reach its declared bound");
            Assert.Less(DebugEventClocks.MonotonicMilliseconds - held, 4000d + 10000d, "a bounded hold must end at its own bound, not later");
            var terminal = PauseEvents(DebugPauseStates.TimedOut)
                .Where(entry => string.Equals(Convert.ToString(((IDictionary<string, object>)entry["value"])["pauseId"]), site.PauseId, StringComparison.Ordinal))
                .ToList();
            Assert.AreEqual(1, terminal.Count, "a bounded expiry must record one pause-timeout fact for its own site");
            Assert.AreEqual(DebugPauseReleaseReasons.Timeout, Convert.ToString(((IDictionary<string, object>)terminal[0]["value"])["reason"]),
                "the terminal event must carry the pause-timeout reason");
            JoinHosts();
        }

        [Test]
        public void DebugPauseRequestQueuedBeforeThePauseIsServedByTheControlChannelInsteadOfWaitingBehindIt()
        {
            // 没有暂停时不得伪造答复：等待只由请求自己的 deadline 结束，队列项保持原样。
            var idle = QueuedRequestItem(WrappedRequestWithDeadline("debug.pause-list", "{}", DateTimeOffset.UtcNow.AddMilliseconds(1200L)));
            var idleOutcome = WaitQueuedRequest(idle);
            Assert.IsFalse(idleOutcome.Served, "a queued request must never be answered before a pause exists");
            Assert.AreEqual(WaitHandle.WaitTimeout, idleOutcome.Signal, "the wait must end on the request's own deadline");
            Assert.IsNull(idleOutcome.Envelope, "no answer may be produced without a live pause");
            Assert.IsNull(idle.Response, "no response may be fabricated for a request whose start was never confirmed");
            Assert.IsFalse(idle.HasStarted, "the queued item must stay un-started");

            // 暂停在请求入队之后才发布：同一队列项必须在发起请求的线程上被服务，而不是继续等在主线程队列里。
            var queued = QueuedRequestItem(WrappedRequest("debug.pause-list", "{}"));
            var site = HitMainThreadPauseInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(4000L, false)));
            var outcome = WaitQueuedRequest(queued);
            Assert.IsTrue(outcome.Served, "a reachable request queued before the pause must be served by the control channel");
            var envelope = UnityCliSerializer.Parse(outcome.Envelope) as IDictionary<string, object>;
            Assert.IsNotNull(envelope, "the served answer must be a JSON envelope");
            Assert.AreEqual(queued.Request.RequestId, Convert.ToString(envelope["requestId"]), "the answer must carry the request's own id");
            Assert.IsTrue(Convert.ToBoolean(envelope["ok"]), "the diverted request must be answered successfully; got " + FieldText(envelope, "error"));
            var data = TerminalResult(envelope);
            Assert.IsTrue(((IEnumerable<object>)data["sites"]).Select(item => (IDictionary<string, object>)item)
                    .Any(item => string.Equals(Convert.ToString(item["pauseId"]), site.PauseId, StringComparison.Ordinal)),
                "the diverted request must see the site that is holding the main thread");
            Assert.IsFalse(queued.HasStarted, "a diverted queue item must never start on the main thread afterwards");
            Assert.AreEqual(outcome.Envelope, queued.Response, "the queue item must keep the same answer so it converges without a second execution");
            DebugPauseHost.ReleaseAll(DebugPauseReleaseReasons.Resume);
            JoinHosts();
        }

        [Test]
        public void DebugPauseRequestQueuedBeforeThePauseIsNotTakenOverOutsideTheFrozenList()
        {
            // 不在冻结名单上的请求不得被控制通道接管：名单不得因入队时序而被放宽，也不得被静默取消。
            var queued = QueuedRequestItem(WrappedRequestWithDeadline("debug.session-stop", "{\"sessionId\":\"no-such-session\"}", DateTimeOffset.UtcNow.AddMilliseconds(1200L)));
            HitMainThreadPauseInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(60000L, false)));
            var outcome = WaitQueuedRequest(queued);
            Assert.IsFalse(outcome.Served,
                "a request outside the frozen list must keep waiting instead of being served by the control channel");
            Assert.AreEqual(WaitHandle.WaitTimeout, outcome.Signal);
            Assert.IsNull(outcome.Envelope);
            Assert.IsNull(queued.Response, "the control channel must not fabricate an answer for a request it did not serve");
            Assert.IsFalse(queued.HasStarted, "the queued item must stay un-started");
            DebugPauseHost.ReleaseAll(DebugPauseReleaseReasons.Resume);
            JoinHosts();
        }

        // ------------------------------------------------------------------
        // 上限与覆盖缺口
        // ------------------------------------------------------------------

        [Test]
        public void DebugPauseSiteLimitSkipsFurtherHitsAndReportsCoverageGap()
        {
            var binding = Binding(DebugPausePositions.Prefix, PauseTimeout(60000L, false));
            for (var index = 0; index < DebugLimits.MaxPauseSites; index++) HitInBackground(binding);
            Assert.IsTrue(WaitFor(() => DebugPauseHost.ActiveSiteCount == DebugLimits.MaxPauseSites, 20000), "every site below the limit must be published");

            // 超出上限的命中保持原方法语义：Hit 立即返回且不建立现场。
            var started = DebugEventClocks.MonotonicMilliseconds;
            DebugPauseHost.Hit(binding, Invocation(binding, 11), new object[] { 11 }, true, false);
            Assert.Less(DebugEventClocks.MonotonicMilliseconds - started, 2000d, "a skipped hit must not block the hit thread");
            Assert.AreEqual((long)DebugLimits.MaxPauseSites, DebugPauseHost.ActiveSiteCount);
            var coverage = DebugPauseHost.Coverage();
            Assert.AreEqual(1L, coverage.SkippedSites);
            Assert.IsTrue(coverage.SkipReasons.Any(reason => reason.Reason == "site-limit" && reason.Count == 1L), "the coverage gap must name the site-limit reason");
            var skipped = PauseEvents("pause-skipped");
            Assert.AreEqual(1, skipped.Count, "a skipped hit must record a pause-skipped fact");
            DebugPauseHost.ReleaseAll(DebugPauseReleaseReasons.Resume);
            JoinHosts();
        }

        [Test]
        public void DebugPauseUninstallablePauseIsCountedAndReadableThroughTheControlChannel()
        {
            // 现场上限写满；其中一个必须是主线程现场，否则控制通道闸门不会启用。
            var binding = Binding(DebugPausePositions.Prefix, PauseTimeout(60000L, false));
            HitMainThreadPauseInBackground(binding);
            for (var index = 1; index < DebugLimits.MaxPauseSites; index++) HitInBackground(binding);
            Assert.IsTrue(WaitFor(() => DebugPauseHost.ActiveSiteCount == DebugLimits.MaxPauseSites, 20000), "every site below the limit must be published");

            // 超出上限的命中无法建立声明的暂停：必须当场、在任何效果之前留下精确原因，命中线程保持原方法语义。
            var started = DebugEventClocks.MonotonicMilliseconds;
            DebugPauseHost.Hit(binding, Invocation(binding, 21), new object[] { 21 }, true, false);
            Assert.Less(DebugEventClocks.MonotonicMilliseconds - started, 2000d, "an uninstallable declared pause must not block the hit thread");
            Assert.AreEqual((long)DebugLimits.MaxPauseSites, DebugPauseHost.ActiveSiteCount, "an uninstallable pause must not publish a site");

            // 同一事实必须能通过暂停控制通道读到：覆盖缺口带精确 reason 与计数，而不是静默通过。
            DebugMainThreadPauseView pause;
            Assert.IsTrue(DebugPauseControl.TryGetMainThreadPause(out pause));
            var envelope = OffMain(() => Serve(WrappedRequest("debug.pause-list", "{}"), "debug.pause-list", pause));
            Assert.IsTrue(Convert.ToBoolean(envelope["ok"]), "the off-main list must stay readable while the main thread is held");
            var coverage = (IDictionary<string, object>)TerminalResult(envelope)["coverage"];
            Assert.AreEqual(1L, Convert.ToInt64(coverage["skippedSites"]), "an uninstallable declared pause must be counted");
            var reasons = ((IEnumerable<object>)coverage["skipReasons"]).Select(entry => (IDictionary<string, object>)entry).ToList();
            Assert.AreEqual(1, reasons.Count, "the coverage gap must name exactly one reason");
            Assert.AreEqual("site-limit", Convert.ToString(reasons[0]["reason"]), "the coverage gap must carry the exact reason");
            Assert.AreEqual(1L, Convert.ToInt64(reasons[0]["count"]));
            DebugPauseHost.ReleaseAll(DebugPauseReleaseReasons.Resume);
            JoinHosts();
        }

        [Test]
        public void DebugPauseStackCaptureIsBoundedAndMarksOtherFrameLocalsUnavailable()
        {
            var frames = DebugPauseHost.CaptureStack(100000);
            Assert.IsTrue(frames.Count > 0, "capturing a stack on the calling thread must produce frames");
            Assert.LessOrEqual(frames.Count, DebugLimits.MaxPauseStackFrames, "the captured stack must respect the declared frame limit");
            foreach (var frame in frames)
            {
                Assert.IsFalse(Convert.ToBoolean(frame["localsAvailable"]), "locals of other frames must never be claimed as readable");
                Assert.IsFalse(string.IsNullOrEmpty(Convert.ToString(frame["unavailableReason"])));
            }
            Assert.AreEqual(0, DebugPauseHost.CaptureStack(0).Count, "a zero depth must capture no frame");
        }

        // ------------------------------------------------------------------
        // inspect：只在暂停线程上执行
        // ------------------------------------------------------------------

        [Test]
        public void DebugPauseInspectReadsOnThePausedThreadAndListsUnavailableFacts()
        {
            var site = HitInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(60000L, false), 8));
            var request = new DebugPauseInspectRequest
            {
                PauseId = site.PauseId,
                Fields = new List<DebugPauseFieldRequest> { new DebugPauseFieldRequest { FieldName = "does-not-exist" } }
            };
            var operation = DebugPauseHost.EnqueueInspect(site, request, "test:inspect");
            Assert.IsTrue(WaitFor(() => operation.Completed, 10000), "the paused thread must serve a queued inspect request");
            Assert.IsNull(operation.Failure);
            var result = operation.InspectResult;
            Assert.IsTrue(result.ServedOnPausedThread);
            Assert.AreEqual(site.ThreadId, result.ServedOnThreadId);
            Assert.IsTrue(result.StackCaptured);
            Assert.LessOrEqual(result.Stack.Count, DebugLimits.MaxPauseStackFrames);
            Assert.IsTrue(result.Stack.All(frame => !frame.LocalsAvailable));
            Assert.IsTrue(result.Unavailable.Any(note => note.Contains("Locals of frames other than the paused")), "other-frame locals must be listed as unavailable");
            Assert.AreEqual("static", result.InstanceIdentity);
            Assert.IsNull(result.Result, "a prefix inspect must not report a result");
            Assert.IsTrue(result.Unavailable.Any(note => note.Contains("prefix boundary")));
            Assert.AreEqual(1, result.Fields.Count);
            Assert.IsFalse(result.Fields[0].Readable, "an unresolvable field must be reported without a fabricated value");
            Assert.IsFalse(string.IsNullOrEmpty(result.Fields[0].UnavailableReason));
            DebugPauseHost.RequestResume(site.PauseId, DebugPauseReleaseReasons.Resume);
            JoinHosts();
        }

        // ------------------------------------------------------------------
        // evaluate：同步用户代码不返回时只能报告 pending
        // ------------------------------------------------------------------

        [Test]
        public void DebugPausePendingReportIsHonestAndResumeNeverClaimsPreemption()
        {
            var site = HitInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(60000L, false)));
            DebugPauseOperation operation;
            // 现场服务循环在同一把 Gate 上取下一个请求：持有 Gate 才能确定该请求在解除信号之前确实未被服务，
            // 从而真实覆盖"已排队但从未开始的请求在解除时按 stale 答复"这条路径（它不依赖共享编译器）。
            lock (site.Gate)
            {
                // 声明形状必须合法，否则现场编译会先报出 schema 错误，pending 分支永远不会被走到。
                operation = DebugPauseHost.EnqueueEvaluate(
                    site,
                    new DebugPauseEvaluateRequest { PauseId = site.PauseId, SourceText = "return \"never-served\";" },
                    "test:pending");
                // 尚未被服务：现场仍是 paused，操作未完成。
                var pending = DebugPauseHost.PendingEvaluateResult(site, operation, false);
                Assert.AreEqual(DebugPauseExecutionStates.Pending, pending.ExecutionState);
                Assert.IsFalse(pending.Completed);
                Assert.IsTrue(pending.PendingNote.Contains("was not resumed"), "the pending report must state that the thread was not resumed");
                Assert.IsTrue(pending.PendingNote.Contains("unknown"), "the pending report must state that the execution state is unknown");
                DebugPauseHost.RequestResume(site.PauseId, DebugPauseReleaseReasons.Resume);
            }
            Assert.IsTrue(WaitFor(() => operation.Completed, 10000), "a stranded evaluation must be answered");
            Assert.AreEqual("E_DEBUG_PAUSE_STALE", operation.Failure.Code);
            JoinHosts();
        }

        [Test]
        public void DebugPauseSynchronousCodeThatDoesNotReturnKeepsSitePausedAndHonoursResume()
        {
            var site = HitInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(30000L, false)));
            var operation = DebugPauseHost.EnqueueEvaluate(
                site,
                new DebugPauseEvaluateRequest
                {
                    PauseId = site.PauseId,
                    SourceText = "UnityCli.UnityCliPauseProbe.MarkEntered(); while (!UnityCli.UnityCliPauseProbe.Gate) { System.Threading.Thread.Sleep(5); } return \"gate-opened\";",
                    WaitMs = DebugLimits.PauseOperationTimeoutMs
                },
                "test:blocking-evaluate");
            Assert.IsTrue(WaitFor(() => site.Executing != null, 20000), "the paused thread must pick up the queued evaluation");
            // 现场编译与加载在暂停线程上有界推进：只有用户同步代码真的开始运行，才能断言 resume 没有抢占它。
            if (!WaitFor(() => UnityCliPauseProbe.Entered, 30000))
            {
                // 编译不可用（真实编译失败）或共享编译没在暂停操作预算内产出结果，都只说明用户代码从未开始。
                // 夹具不假设冷编译一定在 10 s 内完成，但必须证明现场仍保持、可 inspect、可 resume。
                AssertSiteSurvivesCompileThatNeverRan(site, operation, "on-site-evaluate");
                Assert.Inconclusive("the on-site compiler did not run the user code in this environment; the pending branch was not exercised");
            }
            Thread.Sleep(300);
            Assert.IsFalse(operation.Completed, "synchronous code that has not returned must not be reported as completed");
            Assert.AreEqual(DebugPauseOperationKinds.Evaluate, DebugPauseHost.View(site).ExecutingOperation);
            // 现场忙时等待请求有界：第 9 个被拒绝，且 resume 不排在它们之后。
            var queued = new List<DebugPauseOperation>();
            for (var index = 0; index < DebugLimits.MaxPauseMailboxWaiting; index++)
            {
                queued.Add(DebugPauseHost.EnqueueInspect(site, new DebugPauseInspectRequest { PauseId = site.PauseId }, "test:queued-inspect-" + index));
                Assert.IsTrue(WaitFor(() => (int)site.WaitingCount == index + 1, 5000), "each queued request must wait while the site is busy");
            }
            var overflow = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugPauseHost.EnqueueInspect(site, new DebugPauseInspectRequest { PauseId = site.PauseId }, "test:overflow"));
            Assert.AreEqual("E_DEBUG_LIMIT_EXCEEDED", overflow.Code);

            DebugPauseHost.RequestResume(site.PauseId, DebugPauseReleaseReasons.Resume);
            // resume 是信号而不是抢占：同步用户代码仍在跑，现场不会立刻终结，操作也不会被标记完成。
            Thread.Sleep(300);
            Assert.IsFalse(operation.Completed, "resume must not preempt running synchronous code");
            Assert.IsTrue(site.IsPaused, "the site stays paused while its synchronous code runs");

            UnityCliPauseProbe.Gate = true;
            Assert.IsTrue(WaitFor(() => operation.Completed, 30000), "the evaluation must complete once its synchronous code returns");
            Assert.IsNull(operation.Failure);
            Assert.AreEqual(DebugPauseExecutionStates.Completed, operation.EvaluateResult.ExecutionState);
            Assert.AreEqual("gate-opened", Convert.ToString(operation.EvaluateResult.ReturnValue));
            // resume 没有被排在等待请求之后：排队的请求全部按 stale 答复，而不是先被服务。
            Assert.IsTrue(WaitFor(() => site.IsTerminal, 10000));
            Assert.AreEqual(DebugPauseReleaseReasons.Resume, site.TerminalReason);
            foreach (var queuedOperation in queued)
            {
                Assert.IsTrue(queuedOperation.Completed, "every stranded request must be answered");
                Assert.AreEqual("E_DEBUG_PAUSE_STALE", queuedOperation.Failure.Code);
            }
            JoinHosts();
        }

        [Test]
        public void DebugPauseNestedPauseOnTheServicingThreadIsSuppressedAndRecorded()
        {
            var nestedBinding = Binding(DebugPausePositions.Prefix, PauseTimeout(400L, false));
            UnityCliPauseProbeState.NestedBinding = nestedBinding;
            var site = HitInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(30000L, false)));
            var operation = DebugPauseHost.EnqueueEvaluate(
                site,
                new DebugPauseEvaluateRequest { PauseId = site.PauseId, SourceText = "return UnityCli.UnityCliPauseProbe.NestedHit();" },
                "test:origin:debug.pause-evaluate");
            Assert.IsTrue(WaitFor(() => operation.Completed, 30000), "the evaluation must return; a nested pause would block it to its own timeout");
            if (operation.Failure != null)
            {
                // 嵌套命中分支只在用户代码真的跑起来时才有对象：编译没跑完时不得声称抑制已经发生。
                AssertSiteSurvivesCompileThatNeverRan(site, operation, "nested-pause");
                Assert.Inconclusive("the on-site compiler did not run the user code in this environment; the nested-pause branch was not exercised");
            }
            Assert.AreEqual("nested-hit-returned", Convert.ToString(operation.EvaluateResult.ReturnValue));
            Assert.AreEqual(1L, DebugPauseHost.ActiveSiteCount, "a servicing thread must not enter a nested pause");
            var coverage = DebugPauseHost.Coverage();
            Assert.AreEqual(1L, coverage.SuppressedNestedHits);
            Assert.IsTrue(coverage.SuppressOrigins.Any(origin => origin.Reason == "test:origin:debug.pause-evaluate"), "the suppression must record the request origin");
            Assert.AreEqual(1L, DebugPauseHost.View(site).SuppressedNestedHits, "the site must report its own suppressed nested hits");
            var suppressed = PauseEvents("pause-suppressed");
            Assert.AreEqual(1, suppressed.Count);
            DebugPauseHost.RequestResume(site.PauseId, DebugPauseReleaseReasons.Resume);
            JoinHosts();
        }

        // ------------------------------------------------------------------
        // 生命周期解除：reload/quit 清理不得遗弃现场，也不得阻塞调用方
        // ------------------------------------------------------------------

        [Test]
        public void DebugPauseDomainReloadReleasesEverySiteBeforeTheRegistryIsReset()
        {
            // 无限暂停没有自己的时间边界：它唯一的解除路径就是显式 resume 与 reload/quit 清理。
            var site = HitInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(0L, true)));
            Assert.IsTrue(WaitFor(() => DebugPauseHost.ActiveSiteCount == 1L, 5000));
            // 走真实重载入口：重载通知必须在丢弃现场注册表之前发出解除信号。
            MethodHookHost.BeforeReload();
            Assert.IsTrue(WaitFor(() => site.IsTerminal, 5000), "a domain reload must release an unbounded site instead of abandoning its hit thread");
            Assert.AreEqual(DebugPauseReleaseReasons.DomainReload, site.TerminalReason);
            Assert.IsTrue(site.ResumeRequested, "the reload release must be a real release signal, not a silent registry reset");
            Assert.AreEqual(0L, DebugPauseHost.ActiveSiteCount, "a reset registry must not report a site that was never released");
            MethodHookHost.InitializeAfterReload();
            JoinHosts();
        }

        [Test]
        public void DebugPauseLifecycleReleaseNeverWaitsForRunningUserCode()
        {
            var site = HitInBackground(Binding(DebugPausePositions.Prefix, PauseTimeout(60000L, false)));
            var operation = DebugPauseHost.EnqueueEvaluate(
                site,
                new DebugPauseEvaluateRequest
                {
                    PauseId = site.PauseId,
                    SourceText = "UnityCli.UnityCliPauseProbe.MarkEntered(); while (!UnityCli.UnityCliPauseProbe.Gate) { System.Threading.Thread.Sleep(5); } return \"released-once-code-returned\";",
                    WaitMs = DebugLimits.PauseOperationTimeoutMs
                },
                "test:lifecycle-release");
            Assert.IsTrue(WaitFor(() => site.Executing != null, 20000), "the paused thread must pick up the queued evaluation");
            // 只有用户同步代码真的开始运行，"解除不等待运行中的代码"才有对象：编译仍可能在现场推进中。
            if (!WaitFor(() => UnityCliPauseProbe.Entered, 30000))
            {
                // 同 on-site-evaluate：编译没跑完时现场仍必须保持、可 inspect、可 resume，且不得冒充抢占声明。
                AssertSiteSurvivesCompileThatNeverRan(site, operation, "lifecycle-release");
                Assert.Inconclusive("the on-site compiler did not run the user code in this environment; the busy-site branch was not exercised");
            }

            // 现场正停在无法抢占的用户同步代码里：生命周期解除必须立即返回，不得把它自己挂在释放确认上。
            var started = DebugEventClocks.MonotonicMilliseconds;
            DebugPauseHost.ReleaseAll(DebugPauseReleaseReasons.EditorShutdown);
            var elapsed = DebugEventClocks.MonotonicMilliseconds - started;
            Assert.Less(elapsed, DebugLimits.PauseCleanupWaitMs / 2d, "a lifecycle release must not wait for pause release");
            Assert.IsFalse(operation.Completed, "running synchronous code must not be reported as completed by a lifecycle release");
            Assert.IsTrue(site.IsPaused, "the site stays paused until its own synchronous code returns");

            // 信号已经置上：用户代码一返回，现场必然终结并留下终结事实。
            UnityCliPauseProbe.Gate = true;
            Assert.IsTrue(WaitFor(() => site.IsTerminal, 30000), "the release signal must take effect once the synchronous code returns");
            Assert.AreEqual(DebugPauseReleaseReasons.EditorShutdown, site.TerminalReason);
            JoinHosts();
        }

        // ------------------------------------------------------------------
        // 夹具
        // ------------------------------------------------------------------

        private static MethodHookBreakpoint PauseTimeout(long durationMs, bool unbounded)
        {
            return new MethodHookBreakpoint
            {
                Kind = DebugPauseBreakpointKinds.Pause,
                Position = DebugPausePositions.Prefix,
                Unbounded = unbounded,
                DurationMs = unbounded ? 0L : durationMs
            };
        }

        private MethodHookBinding Binding(MethodHookBreakpoint breakpoint)
        {
            return Binding(DebugPausePositions.Prefix, breakpoint, 0);
        }

        private MethodHookBinding Binding(string position, MethodHookBreakpoint breakpoint, int stackDepth = 0)
        {
            var method = typeof(DebugPauseTests).GetMethod(nameof(ProbeTarget), BindingFlags.Public | BindingFlags.Instance);
            var definition = new MethodHookDefinition
            {
                HookId = "hook-pause-test-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Revision = 3L,
                TargetKey = "test-target",
                Resolved = method,
                StateBag = new MethodHookStateBag("test-state")
            };
            definition.State = MethodHookState.Armed;
            var effective = breakpoint.Clone();
            effective.Position = position;
            if (stackDepth > 0) effective.StackDepth = stackDepth;
            return new MethodHookBinding
            {
                Definition = definition,
                HookId = definition.HookId,
                Revision = definition.Revision,
                Resolved = method,
                Breakpoint = effective
            };
        }

        private static MethodHookInvocation Invocation(MethodHookBinding binding, int argument)
        {
            return new MethodHookInvocation(binding, null, new object[] { argument }, null, new MethodHookInterceptionStamp(), true, false);
        }

        /// <summary>在后台线程上驱动一次真实命中；命中线程会阻塞在自己的服务循环里。</summary>
        private DebugPauseSite HitInBackground(MethodHookBinding binding, bool isPrefix = true, bool mainThread = false)
        {
            var before = DebugPauseHost.ActiveSiteCount;
            var thread = new Thread(() =>
            {
                try { DebugPauseHost.Hit(binding, Invocation(binding, 5), new object[] { 5 }, isPrefix, mainThread); }
                catch { /* 命中线程的异常不得影响断言线程 */ }
            }) { IsBackground = true };
            thread.Start();
            hosts.Add(thread);
            Assert.IsTrue(WaitFor(() => DebugPauseHost.ActiveSiteCount > before, 10000), "the hit thread must publish a pause site");
            var site = DebugPauseHost.List(new DebugPauseListRequest(), UnityCliBridge.EditorSessionId()).Sites.OrderBy(item => item.PauseId, StringComparer.Ordinal).Last();
            var resolved = DebugPauseHost.FindSite(site.PauseId);
            sites.Add(resolved);
            return resolved;
        }

        /// <summary>主线程暂停现场：命中线程仍是夹具后台线程，但现场按主线程发布，控制通道闸门据此启用。</summary>
        private DebugPauseSite HitMainThreadPauseInBackground(MethodHookBinding binding)
        {
            return HitInBackground(binding, true, true);
        }

        /// <summary>
        /// 在后台线程上执行一次 off-main 服务并取回结果。
        /// 断言线程重抛捕获到的异常：主线程专属 Unity API 的失败必须表现为测试失败，而不是被吞成通过。
        /// </summary>
        private static T OffMain<T>(Func<T> action)
        {
            T value = default(T);
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { value = action(); }
                catch (Exception exception) { failure = exception; }
            }) { IsBackground = true };
            thread.Start();
            Assert.IsTrue(thread.Join(60000), "the off-main serve must return");
            if (failure != null) throw new AssertionException("the off-main serve threw " + failure.GetType().Name + ": " + failure.Message + Environment.NewLine + failure.StackTrace);
            return value;
        }

        /// <summary>调用产品真实的服务入口，并解析它返回的 envelope。</summary>
        private static Dictionary<string, object> Serve(UnityCli.Protocol.BridgeRequest request, string nestedTool, DebugMainThreadPauseView pause)
        {
            string served;
            Assert.IsTrue(DebugPauseControl.TryServe(request, nestedTool, pause, out served), nestedTool + " must be served by the pause control channel");
            var parsed = UnityCliSerializer.Parse(served) as IDictionary<string, object>;
            Assert.IsNotNull(parsed, nestedTool + " must answer with a JSON envelope");
            return new Dictionary<string, object>(parsed, StringComparer.Ordinal);
        }

        /// <summary>
        /// 成功终态里的业务 DTO：公开 CLI 把 catalog.editor.run 的成功响应投影为 data.result，
        /// 因此暂停控制通道的回复也必须从这一层取业务数据，而不是把终态包装当成业务数据。
        /// </summary>
        private static IDictionary<string, object> TerminalResult(IDictionary<string, object> envelope)
        {
            var terminal = (IDictionary<string, object>)envelope["data"];
            Assert.IsNotNull(terminal, "a served Tool must answer with terminal data");
            object result;
            Assert.IsTrue(terminal.TryGetValue("result", out result), "a successful catalog.editor.run terminal must carry the result wrapper the CLI projects");
            return (IDictionary<string, object>)result;
        }

        /// <summary>构造 CLI 实际提交的 wire payload：外层 args 是 { name, input }，内层 Tool 名来自外层 name 字段。</summary>
        private static UnityCli.Protocol.BridgeRequest WrappedRequest(string toolName, string innerArgsJson)
        {
            var outerArgs = UnityCliSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "name", toolName },
                { "input", UnityCliSerializer.Parse(innerArgsJson) }
            });
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "protocolVersion", 2d },
                { "instanceId", "instance-under-test" },
                { "command", "catalog.editor.run" },
                { "requestId", "req-under-test" },
                { "input", UnityCliSerializer.Parse(outerArgs) },
                { "execution", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "absoluteDeadlineUtc", "2999-01-01T00:00:00.0000000Z" },
                        { "completionContractVersion", 1d },
                        { "requestDigestVersion", 1d }
                    }
                }
            };
            return UnityCli.Protocol.BridgeRequest.DeserializeStrict(UnityCliSerializer.Serialize(payload));
        }

        /// <summary>同上，但显式声明 immutable deadline；用于区分"请求自己到期"与"被控制通道接管"。</summary>
        private static UnityCli.Protocol.BridgeRequest WrappedRequestWithDeadline(string toolName, string innerArgsJson, DateTimeOffset absoluteDeadlineUtc)
        {
            var request = WrappedRequest(toolName, innerArgsJson);
            request.Execution.AbsoluteDeadlineUtc = absoluteDeadlineUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            return request;
        }

        /// <summary>按产品入队形状构造一个"已进入主线程队列、尚未开始"的请求项。</summary>
        private static UnityCliBridge.BridgeWorkItem QueuedRequestItem(UnityCli.Protocol.BridgeRequest request)
        {
            var raw = Encoding.UTF8.GetBytes(UnityCliSerializer.Serialize(request));
            return new UnityCliBridge.BridgeWorkItem(
                null, request, raw, UnityCliRequestDigest.Compute(raw, request.InstanceId),
                "listener-under-test", 1L, null, UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead, false, false);
        }

        /// <summary>一次“已入队请求”等待的结果；等待必须在非主线程上驱动，因为产品服务线程也不是主线程。</summary>
        private sealed class QueueWaitOutcome
        {
            internal bool Served;
            internal int Signal;
            internal string Envelope;
        }

        /// <summary>在产品等待入口上驱动一次已入队请求的等待：返回它是否被暂停控制通道接管。</summary>
        private static QueueWaitOutcome WaitQueuedRequest(UnityCliBridge.BridgeWorkItem item)
        {
            var outcome = new QueueWaitOutcome();
            OffMain(() =>
            {
                int signal;
                string served;
                outcome.Served = UnityCliBridge.WaitForQueuedStart(item, out signal, out served);
                outcome.Signal = signal;
                outcome.Envelope = served;
                return true;
            });
            return outcome;
        }

        /// <summary>读取 envelope 字段的文本；缺失时返回空串，以便断言消息本身不会抛出。</summary>
        private static string FieldText(IDictionary<string, object> envelope, string name)
        {
            object value;
            return envelope != null && envelope.TryGetValue(name, out value) && value != null
                ? UnityCliSerializer.Serialize(value)
                : string.Empty;
        }

        private void JoinHosts()
        {
            foreach (var thread in hosts) thread.Join(15000);
            hosts.Clear();
        }

        private static DebugPauseSite SyntheticSite(string pauseId)
        {
            return new DebugPauseSite { PauseId = pauseId, ThreadId = Thread.CurrentThread.ManagedThreadId };
        }

        private static bool WaitFor(Func<bool> predicate, int timeoutMs)
        {
            var deadline = DebugEventClocks.MonotonicMilliseconds + timeoutMs;
            while (DebugEventClocks.MonotonicMilliseconds < deadline)
            {
                if (predicate()) return true;
                Thread.Sleep(5);
            }
            return predicate();
        }

        /// <summary>
        /// 一次现场求值是否"只有现场编译没跑完"而没有进入用户代码。
        /// 真实编译失败与新引入的编译预算耗尽都表示用户代码从未开始：前者是编译器诊断，
        /// 后者只是共享编译没在暂停操作预算内产出结果，两者都不声称用户代码没返回。
        /// </summary>
        private static bool NeverEnteredUserCode(DebugPauseOperation operation)
        {
            if (operation == null || operation.Failure == null) return false;
            return string.Equals(operation.Failure.Code, "E_CODE_COMPILE_FAILED", StringComparison.Ordinal)
                || string.Equals(operation.Failure.Code, "E_COMPILER_BUDGET_EXHAUSTED", StringComparison.Ordinal);
        }

        /// <summary>
        /// 用户代码从未开始时的现场语义：现场仍安装、仍可 inspect、仍可 resume，
        /// 且失败原因不得声称用户代码没返回（不得冒充执行超时或抢占）。
        /// 该断言与冷编译是否恰好落在 10 s 预算内无关，因此不放宽真正的行为断言。
        /// </summary>
        private void AssertSiteSurvivesCompileThatNeverRan(DebugPauseSite site, DebugPauseOperation operation, string label)
        {
            Assert.IsNotNull(operation.Failure, label + ": an evaluation that never starts must report a stable failure");
            Assert.IsTrue(NeverEnteredUserCode(operation), label + ": got " + operation.Failure.Code + ": " + operation.Failure.Message);
            Assert.AreNotEqual("E_EXECUTION_TIMEOUT", operation.Failure.Code, label + ": a compile that did not finish must never be reported as user code that did not return");
            Assert.IsFalse(UnityCliPauseProbe.Entered, label + ": the probe must not be entered when the on-site compile produced no code");
            Assert.IsTrue(site.IsPaused, label + ": the site must stay installed when only the on-site compile could not finish");
            var inspect = DebugPauseHost.EnqueueInspect(site, new DebugPauseInspectRequest { PauseId = site.PauseId }, "test:inspect-after-" + label);
            Assert.IsTrue(WaitFor(() => inspect.Completed, 10000), label + ": the site must stay inspectable");
            Assert.IsNull(inspect.Failure, label + ": an inspect after an on-site compile that did not finish must succeed");
            DebugPauseHost.RequestResume(site.PauseId, DebugPauseReleaseReasons.Resume);
            Assert.IsTrue(WaitFor(() => site.IsTerminal, 10000), label + ": the site must stay resumable");
            Assert.AreEqual(DebugPauseReleaseReasons.Resume, site.TerminalReason, label);
            JoinHosts();
        }

        private static void AssertSchema(string label, TestDelegate action, string expectedPath)
        {
            var failure = Assert.Throws<UnityCliBridge.BridgeCommandException>(action, label);
            Assert.AreEqual("E_SCHEMA_INVALID", failure.Code, label);
            var details = failure.Details as IDictionary<string, object>;
            Assert.IsNotNull(details, label);
            Assert.AreEqual("$." + expectedPath, Convert.ToString(details["path"]), label);
        }

        private static List<Dictionary<string, object>> PauseEvents(string name)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (var entry in DebugEventStore.Snapshot())
            {
                object sourceId;
                object eventName;
                if (!entry.TryGetValue("sourceId", out sourceId) || !string.Equals(Convert.ToString(sourceId), UnityCliDebugSources.Pause, StringComparison.Ordinal)) continue;
                if (!entry.TryGetValue("name", out eventName) || !string.Equals(Convert.ToString(eventName), name, StringComparison.Ordinal)) continue;
                result.Add(entry);
            }
            return result;
        }
    }
}
#endif
