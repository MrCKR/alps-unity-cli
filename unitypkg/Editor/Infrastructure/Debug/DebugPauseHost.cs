#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// pause 断点与暂停现场的 session 资源持有者。
    ///
    /// 存在的唯一原因：session 清理回调注册时还不知道 hookId（hookId 在 effect 之后才生成），
    /// 而清理委托必须能反查"属于哪个 hook 的现场还没退出"。因此本对象是二者之间唯一的可变接线点。
    /// </summary>
    internal sealed class DebugPauseOwner
    {
        internal string HookId = string.Empty;
        internal string ResourceId = string.Empty;
    }

    /// <summary>breakpoint 的 effect 前计划：配置、owner 接线与已保留的 session 资源。</summary>
    internal sealed class DebugPauseBreakpointPlan
    {
        internal MethodHookBreakpoint Breakpoint;
        internal DebugPauseOwner Owner;
        internal bool ResourceReserved;
    }

    /// <summary>
    /// pause core 的唯一 owner。
    ///
    /// 职责：
    /// - 把一个 hook 定义的 breakpoint 命中转成 snapshot 事实或一个暂停现场；
    /// - 持有全局唯一的暂停现场注册表、mailbox 与 resume 信号；
    /// - 在命中线程上服务现场请求（inspect/evaluate），并保证 resume 永不排在等待请求之后；
    /// - 投影现场事实、覆盖缺口与抑制事实，供 debug.pause.* 与 events.read 读取。
    ///
    /// 规则：
    /// - 不注册任何 EditorApplication.update、timer 或 reload 回调：现场线程只在自己被停住的位置服务 mailbox。
    /// - 不复制 hook 真值：target resolver、revision、捕获配置和事件存储都由 MethodHookHost 持有。
    /// - 不跨线程读取用户对象：inspect/evaluate 只在暂停线程上执行，其他线程只读冻结的现场身份。
    /// - 不抢占用户代码：同步用户代码不返回时只能报告 pending。
    /// </summary>
    internal static class DebugPauseHost
    {
        private const string ListScope = "debug.pause-list";

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, DebugPauseSite> Sites = new Dictionary<string, DebugPauseSite>(StringComparer.Ordinal);
        private static readonly List<string> SiteOrder = new List<string>();
        private static readonly Dictionary<string, DebugPauseSite> Finished = new Dictionary<string, DebugPauseSite>(StringComparer.Ordinal);
        private static readonly LinkedList<string> FinishedOrder = new LinkedList<string>();
        private static readonly HashSet<string> PendingResourceReleases = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> SkipReasons = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> SuppressOrigins = new Dictionary<string, long>(StringComparer.Ordinal);
        private static long pauseSequence;
        private static long skippedSites;
        private static long suppressedNestedHits;
        private static long servedOperations;
        private static long refusedRequests;
        private static volatile string runtimeIdentity = string.Empty;
        private static volatile string domainIdentity = string.Empty;

        /// <summary>当前线程正在服务的现场；非 null 表示该线程不得再次进入嵌套暂停。</summary>
        [ThreadStatic]
        private static string servicingPauseId;

        [ThreadStatic]
        private static string servicingOrigin;

        internal static long ActiveSiteCount { get { lock (Sync) return Sites.Count; } }

        /// <summary>
        /// 当前仍停住的主线程暂停现场的唯一投影。
        /// health、editor.state 快照与拒绝 nextActions 都从这里读取，因此"主线程是否被故意暂停"只有一个真值。
        /// 只读内存注册表，不读文件、不读主线程缓存，也不引用任何上一次 publication。
        /// </summary>
        internal static DebugMainThreadPauseView ReadMainThreadPause()
        {
            lock (Sync)
            {
                foreach (var pauseId in SiteOrder)
                {
                    DebugPauseSite site;
                    if (!Sites.TryGetValue(pauseId, out site)) continue;
                    if (!site.MainThread) continue;
                    if (!site.IsPaused) continue;
                    return Project(pauseId, site);
                }
                return null;
            }
        }

        /// <summary>主线程暂停现场的不可变投影；锁定内调用，只读取已冻结的现场身份。</summary>
        private static DebugMainThreadPauseView Project(string pauseId, DebugPauseSite site)
        {
            var view = View(site);
            return new DebugMainThreadPauseView
            {
                PauseId = view.PauseId,
                State = view.State,
                ThreadId = view.ThreadId,
                PublishedSequence = long.Parse(pauseId.Substring("pause-".Length), CultureInfo.InvariantCulture),
                MethodIdentity = view.MethodIdentity,
                TargetKey = view.TargetKey,
                Position = view.Position,
                SessionId = view.SessionId,
                TimeoutKind = view.TimeoutKind,
                TimeoutDurationMs = view.TimeoutDurationMs,
                RemainingMs = (long)view.RemainingMs,
                PausedForMs = view.PausedForMs,
                ServedOperations = view.ServedOperations,
                RefusedRequests = view.RefusedRequests,
                EditorSessionId = UnityCliBridge.EditorSessionId()
            };
        }

        internal static bool IsServicingPauseOnCurrentThread { get { return servicingPauseId != null; } }

        // ------------------------------------------------------------------
        // breakpoint 的 effect 前校验与 session 资源保留
        // ------------------------------------------------------------------

        /// <summary>
        /// 校验 breakpoint 配置并（在声明 sessionId 时）保留一个 pause 资源槽位。
        /// 失败时抛出稳定错误且不改变任何状态；调用方必须在 effect 之前调用本方法。
        /// </summary>
        internal static DebugPauseBreakpointPlan Prepare(MethodHookBreakpointSpec spec, string path)
        {
            if (spec == null) return null;
            if (!DebugPauseBreakpointKinds.IsDefined(spec.Kind))
                throw Schema(path + ".kind", "snapshot or pause", spec.Kind);
            var position = string.IsNullOrEmpty(spec.Position) ? DebugPausePositions.Prefix : spec.Position;
            if (!DebugPausePositions.IsDefined(position))
                throw Schema(path + ".position", "prefix or postfix", spec.Position);
            var stackDepth = spec.StackDepth == null ? 0 : Math.Min(Math.Max(0, spec.StackDepth.Value), DebugLimits.MaxPauseStackFrames);
            var breakpoint = new MethodHookBreakpoint
            {
                Kind = spec.Kind,
                Position = position,
                StackDepth = stackDepth,
                SessionId = spec.SessionId ?? string.Empty
            };
            if (string.Equals(spec.Kind, DebugPauseBreakpointKinds.Snapshot, StringComparison.Ordinal))
            {
                if (spec.PauseTimeout != null)
                    throw Schema(path + ".pauseTimeout", "omitted for a snapshot breakpoint", "present");
                var snapshotPlan = new DebugPauseBreakpointPlan { Breakpoint = breakpoint };
                if (!string.IsNullOrEmpty(breakpoint.SessionId)) snapshotPlan.Owner = ReserveSessionResource(breakpoint, path);
                return snapshotPlan;
            }
            // pause 断点必须显式声明时间边界：缺失、null 或 0 都不得被解释为无限。
            if (spec.PauseTimeout == null)
                throw Schema(path + ".pauseTimeout", "{\"kind\":\"bounded\",\"durationMs\":<positive int>} or {\"kind\":\"unbounded\"}", "missing");
            var timeout = spec.PauseTimeout;
            if (!DebugPauseTimeoutKinds.IsDefined(timeout.Kind))
                throw Schema(path + ".pauseTimeout.kind", "bounded or unbounded", timeout.Kind);
            if (string.Equals(timeout.Kind, DebugPauseTimeoutKinds.Unbounded, StringComparison.Ordinal))
            {
                if (timeout.DurationMs != null)
                    throw Schema(path + ".pauseTimeout.durationMs", "omitted when kind is unbounded", timeout.DurationMs.Value.ToString(CultureInfo.InvariantCulture));
                breakpoint.Unbounded = true;
            }
            else
            {
                if (timeout.DurationMs == null)
                    throw Schema(path + ".pauseTimeout.durationMs", "a positive integer number of milliseconds", "missing");
                if (timeout.DurationMs.Value <= 0L)
                    throw Schema(path + ".pauseTimeout.durationMs", "a positive integer number of milliseconds", timeout.DurationMs.Value.ToString(CultureInfo.InvariantCulture));
                if (timeout.DurationMs.Value > DebugLimits.MaxPauseDurationMs)
                    throw Schema(path + ".pauseTimeout.durationMs", "at most " + DebugLimits.MaxPauseDurationMs.ToString(CultureInfo.InvariantCulture) + " milliseconds", timeout.DurationMs.Value.ToString(CultureInfo.InvariantCulture));
                breakpoint.DurationMs = timeout.DurationMs.Value;
            }
            // 先校验声明输入、后判定控制通道可用性：漏声明必需字段的调用方必须得到 schema 错误，
            // 不得被可用性错误掩盖。合法声明在本域没有可达解除路径时仍被拒绝，因为现场一旦停住就没有解除路径。
            if (!DebugPauseControl.Available)
                throw new BridgeCommandException(
                    "E_DEBUG_CONTROL_PLANE_UNAVAILABLE",
                    "The pause control channel is not published in this Editor domain, so a pause breakpoint cannot be installed: the site would have no reachable release path.",
                    new Dictionary<string, object> { { "path", path + ".kind" }, { "available", false } },
                    new[]
                    {
                        "Let Unity finish compiling and reloading the domain so the Bridge republishes its control channel, then submit the pause breakpoint again.",
                        "Use a snapshot breakpoint when only value capture is required; it never holds a thread."
                    });
            var plan = new DebugPauseBreakpointPlan { Breakpoint = breakpoint };
            if (!string.IsNullOrEmpty(breakpoint.SessionId)) plan.Owner = ReserveSessionResource(breakpoint, path);
            return plan;
        }

        /// <summary>
        /// session 资源保留发生在 effect 之前；清理委托在 session stop 时解除该 hook 的全部现场，
        /// 现场尚未真正退出时清理必须报告 pending，由原 owner 在线程恢复后继续完成。
        /// </summary>
        private static DebugPauseOwner ReserveSessionResource(MethodHookBreakpoint breakpoint, string path)
        {
            DebugSessionInfoResult info;
            try
            {
                info = DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = breakpoint.SessionId });
            }
            catch (BridgeCommandException)
            {
                throw new BridgeCommandException(
                    "E_DEBUG_SESSION_NOT_FOUND",
                    "The session that should own this pause breakpoint does not exist in the current Editor session.",
                    new Dictionary<string, object> { { "sessionId", breakpoint.SessionId }, { "path", path + ".sessionId" } },
                    new[]
                    {
                        "Run tools run debug.session-list to read the loaded session definitions.",
                        "Run tools run debug.session-create to establish an owner, or omit sessionId to own the pause site without a session resource."
                    });
            }
            if (!string.Equals(info.Session == null ? string.Empty : info.Session.State, UnityCliDebugSessionStates.Open, StringComparison.Ordinal))
                throw new BridgeCommandException(
                    "E_DEBUG_SESSION_NOT_OPEN",
                    "A session that is not open cannot own a new pause resource.",
                    new Dictionary<string, object>
                    {
                        { "sessionId", breakpoint.SessionId },
                        { "state", info.Session == null ? string.Empty : info.Session.State },
                        { "path", path + ".sessionId" }
                    },
                    new[]
                    {
                        "Run tools run debug.session-info to read the current state and cleanup detail.",
                        "Run tools run debug.session-create to start a new session for this investigation."
                    });
            var owner = new DebugPauseOwner();
            var reservation = DebugSessionHost.ReserveResource(
                breakpoint.SessionId,
                UnityCliDebugResourceKinds.Pause,
                "pause-breakpoint:" + breakpoint.Position,
                report =>
                {
                    // 释放本身是幂等的：解除条目已经发过信号时这里只做最终确认，不重复计数。
                    var hookId = owner.HookId;
                    if (string.IsNullOrEmpty(hookId)) return true;
                    return ReleaseSitesForHook(hookId, DebugPauseReleaseReasons.SessionStop);
                });
            if (!reservation.Success)
                throw new BridgeCommandException(
                    "E_DEBUG_LIMIT_EXCEEDED",
                    "The session cannot accept another active resource, so this pause breakpoint was refused before any effect.",
                    new Dictionary<string, object>
                    {
                        { "limit", "session-active-resources" },
                        { "sessionId", breakpoint.SessionId },
                        { "reason", reservation.Failure ?? string.Empty },
                        { "message", reservation.Message ?? string.Empty },
                        { "used", (long)reservation.ActiveResourcesUsed },
                        { "max", (long)DebugSessionHost.Capacity().MaxActiveResourcesPerSession }
                    },
                    new[]
                    {
                        "Run tools run debug.session-info to read the active resources of this session.",
                        "Run tools run debug.session-stop for a session you no longer need, then install the pause breakpoint again."
                    });
            owner.ResourceId = reservation.Resource.ResourceId;
            breakpoint.ResourceId = owner.ResourceId;
            // 解除条目必须在释放等待/句柄之前发出解除信号：暂停现场的在途 evaluate 可能还持有句柄。
            // 解除只置信号，不等待也不抢占用户同步代码，因此它可以在服务 stop 的线程上直接完成。
            RegisterCancelAndResumeStep(owner, breakpoint.SessionId, owner.ResourceId);
            return owner;
        }

        /// <summary>
        /// 为一个 pause 资源登记解除条目（冻结顺序的 cancel-and-resume）：发出解除信号并计入已解除暂停数量。
        /// 现场尚未真正退出时由资源清理在更后的步骤如实报 pending，而不是在这里兰截。
        /// </summary>
        private static void RegisterCancelAndResumeStep(DebugPauseOwner owner, string sessionId, string resourceId)
        {
            if (owner == null || string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(resourceId)) return;
            var failure = DebugSessionHost.RegisterCleanupStep(
                sessionId,
                UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.CancelAndResume),
                "pause-resume:" + resourceId,
                UnityCliDebugFacilityOwners.Pause,
                report =>
                {
                    // 资源已被 disable/remove 释放时不再计为本次 stop 的已解除暂停：那条事实已经由那一侧记录。
                    if (string.IsNullOrEmpty(owner.ResourceId)) return true;
                    var hookId = owner.HookId;
                    if (string.IsNullOrEmpty(hookId)) return true;
                    if (report != null) report.ReleasedPauses++;
                    // 返回值只描述“解除信号已发出”；现场是否已退出由资源清理在后续阶段确认。
                    ReleaseSitesForHook(hookId, DebugPauseReleaseReasons.SessionStop);
                    return true;
                });
            if (failure != null)
                UnityCliBridge.WriteLog("debug_pause_resume_step_reused session=" + sessionId + " resource=" + resourceId + " reason=" + failure);
        }

        /// <summary>安装/替换失败时归还未跨过 effect 的资源槽位；已经生效的资源由 disable/remove/session stop 释放。</summary>
        internal static void ReleaseUncommittedPlan(DebugPauseBreakpointPlan plan)
        {
            if (plan == null || plan.Owner == null || !plan.ResourceReserved) return;
            DebugSessionHost.ReleaseResource(plan.Owner.ResourceId);
            plan.Owner.ResourceId = string.Empty;
            plan.ResourceReserved = false;
        }

        /// <summary>effect 成功之后把保留槽位标记为活动；未声明 session 的计划是 no-op。</summary>
        internal static void CommitPlan(DebugPauseBreakpointPlan plan, string hookId)
        {
            if (plan == null || plan.Owner == null) return;
            plan.Owner.HookId = hookId ?? string.Empty;
            plan.ResourceReserved = true;
            DebugSessionHost.AttachResource(plan.Owner.ResourceId);
        }

        /// <summary>释放一个定义拥有的 pause 资源；不触碰其他 session 记录。</summary>
        internal static void ReleaseOwnership(MethodHookDefinition definition, string reason)
        {
            if (definition == null) return;
            var owner = definition.BreakpointOwner;
            if (owner == null) return;
            if (!string.IsNullOrEmpty(owner.HookId)) ReleaseSitesForHook(owner.HookId, reason);
            if (string.IsNullOrEmpty(owner.ResourceId)) return;
            DebugSessionHost.ReleaseResource(owner.ResourceId);
            if (definition.Breakpoint != null) definition.Breakpoint.ResourceId = string.Empty;
            owner.ResourceId = string.Empty;
        }

        // ------------------------------------------------------------------
        // 命中：snapshot 捕获或暂停现场
        // ------------------------------------------------------------------

        /// <summary>
        /// 一次 breakpoint 命中。必须在 hook 派发路径上、命中线程内调用；
        /// 只有真的建立暂停现场时本方法才会阻塞调用线程。
        /// </summary>
        internal static void Hit(MethodHookBinding binding, IUnityCliHookInvocation invocation, object[] arguments, bool isPrefix, bool mainThread)
        {
            var breakpoint = binding == null ? null : binding.Breakpoint;
            if (breakpoint == null || invocation == null) return;
            if (breakpoint.IsPrefix != isPrefix) return;
            if (servicingPauseId != null)
            {
                // 服务现场期间不再进入嵌套暂停：记录 pauseSuppressed 与请求来源，绝不声称与自然业务调用同源。
                RecordSuppressed(binding, servicingPauseId, servicingOrigin);
                return;
            }
            if (string.Equals(breakpoint.Kind, DebugPauseBreakpointKinds.Snapshot, StringComparison.Ordinal))
            {
                AppendSnapshotEvent(binding, invocation, arguments, isPrefix, mainThread);
                return;
            }
            string skipReason;
            var site = TryRegisterSite(binding, invocation, arguments, isPrefix, mainThread, out skipReason);
            if (site == null)
            {
                // 超出同时暂停上限：保持原方法语义，并记录 pauseSkipped 与覆盖缺口。
                RecordSkipped(binding, skipReason);
                return;
            }
            // 暂停即将停住命中线程：主线程命中当场释放可达的按下输入，worker 命中把释放交给中央调度。
            // 必须在 ServiceSite 之前完成，否则主线程停住后设备读取只能靠中央调度补完。
            DebugInputHost.OnPauseHit(mainThread);
            ServiceSite(site);
        }

        private static DebugPauseSite TryRegisterSite(MethodHookBinding binding, IUnityCliHookInvocation invocation, object[] arguments, bool isPrefix, bool mainThread, out string skipReason)
        {
            skipReason = string.Empty;
            var breakpoint = binding.Breakpoint;
            lock (Sync)
            {
                if (Sites.Count >= DebugLimits.MaxPauseSites)
                {
                    skipReason = "site-limit";
                    return null;
                }
                pauseSequence++;
                var site = new DebugPauseSite
                {
                    PauseId = "pause-" + pauseSequence.ToString(CultureInfo.InvariantCulture),
                    ThreadId = Thread.CurrentThread.ManagedThreadId,
                    MainThread = mainThread,
                    InvocationId = InvocationIdOf(invocation),
                    Revision = binding.Revision,
                    HookId = binding.HookId,
                    TargetKey = binding.Definition == null ? string.Empty : binding.Definition.TargetKey,
                    MethodIdentity = invocation.MethodIdentity,
                    Position = breakpoint.Position,
                    RuntimeId = runtimeIdentity,
                    DomainGeneration = domainIdentity,
                    SessionId = breakpoint.SessionId,
                    ResourceId = breakpoint.ResourceId,
                    Unbounded = breakpoint.Unbounded,
                    DurationMs = breakpoint.DurationMs,
                    StartedMonotonicMs = DebugEventClocks.MonotonicMilliseconds,
                    Invocation = invocation,
                    Instance = invocation.Instance,
                    Arguments = invocation.Arguments,
                    Binding = binding,
                    EmittedOffset = EmittedCount(invocation)
                };
                site.DeadlineMonotonicMs = breakpoint.Unbounded ? 0d : site.StartedMonotonicMs + breakpoint.DurationMs;
                Sites[site.PauseId] = site;
                SiteOrder.Add(site.PauseId);
                if (!breakpoint.Unbounded) { }
                return site;
            }
        }

        private static long InvocationIdOf(IUnityCliHookInvocation invocation)
        {
            var hookInvocation = invocation as MethodHookInvocation;
            return hookInvocation == null ? 0L : hookInvocation.InvocationId;
        }

        private static int EmittedCount(IUnityCliHookInvocation invocation)
        {
            var hookInvocation = invocation as MethodHookInvocation;
            return hookInvocation == null ? 0 : hookInvocation.Emitted.Count;
        }

        /// <summary>
        /// 现场线程上的暂停服务循环。
        ///
        /// 本线程就在自己被停住的位置服务自己的 mailbox：不注册任何 update 回调，也不把现场搬到别的线程。
        /// 每轮顺序固定为 resume 信号、有限超时、等待请求；因此 resume 永不排在等待请求之后。
        /// </summary>
        private static void ServiceSite(DebugPauseSite site)
        {
            AppendSiteEvent(site, "paused", site.PauseId, null);
            while (true)
            {
                DebugPauseOperation operation = null;
                lock (site.Gate)
                {
                    var reason = TerminalReasonLocked(site);
                    if (reason != null)
                    {
                        site.State = DebugPauseStates.Resuming;
                        site.TerminalReason = reason;
                    }
                    else if (site.Waiting.Count > 0)
                    {
                        operation = site.Waiting.Dequeue();
                        site.Executing = operation;
                    }
                }
                if (operation != null)
                {
                    ExecuteOperation(site, operation);
                    continue;
                }
                if (site.TerminalReason.Length > 0) break;
                site.Signal.Wait(DebugLimits.PauseServicePollMs);
                site.Signal.Reset();
            }
            var timedOut = string.Equals(site.TerminalReason, DebugPauseReleaseReasons.Timeout, StringComparison.Ordinal);
            DebugPauseMailbox.Terminalize(site, timedOut ? DebugPauseStates.TimedOut : DebugPauseStates.Resumed, site.TerminalReason);
            FinishSite(site);
        }

        /// <summary>现场离开 paused 的唯一判定；锁定内调用，只读取已冻结的现场字段与单调时钟。</summary>
        private static string TerminalReasonLocked(DebugPauseSite site)
        {
            if (site.ResumeRequested)
                return string.IsNullOrEmpty(site.ResumeReason) ? DebugPauseReleaseReasons.Resume : site.ResumeReason;
            if (site.TerminalReason.Length > 0) return site.TerminalReason;
            if (DebugPauseMailbox.DeadlineReached(site)) return DebugPauseReleaseReasons.Timeout;
            return null;
        }

        /// <summary>
        /// 在暂停线程上执行一个现场操作。用户同步代码不返回时现场保持 paused，
        /// 本方法不会、也无法强行抢占它，调用方只能得到 pending。
        /// </summary>
        private static void ExecuteOperation(DebugPauseSite site, DebugPauseOperation operation)
        {
            var previousId = servicingPauseId;
            var previousOrigin = servicingOrigin;
            servicingPauseId = site.PauseId;
            servicingOrigin = operation.Origin;
            operation.StartedMonotonicMs = DebugEventClocks.MonotonicMilliseconds;
            UnityCliPauseContext.Enter(site.Invocation);
            try
            {
                if (string.Equals(operation.Kind, DebugPauseOperationKinds.Inspect, StringComparison.Ordinal))
                    operation.InspectResult = BuildInspect(site, operation.InspectRequest, Thread.CurrentThread.ManagedThreadId);
                else if (string.Equals(operation.Kind, DebugPauseOperationKinds.Evaluate, StringComparison.Ordinal))
                    operation.EvaluateResult = DebugPauseEvaluation.Run(site, operation);
                else if (string.Equals(operation.Kind, DebugPauseOperationKinds.ValueRead, StringComparison.Ordinal))
                    operation.ValueReadResult = DebugPauseValue.Read(operation);
                else
                    operation.ValueWriteResult = DebugPauseValue.Write(operation);
            }
            catch (BridgeCommandException failure)
            {
                operation.Fail(failure);
            }
            catch (Exception exception)
            {
                operation.Fail(new BridgeCommandException(
                    "E_DEBUG_PAUSE_OPERATION_FAILED",
                    "The on-site operation failed on the paused thread: " + exception.GetType().Name + ".",
                    new Dictionary<string, object> { { "pauseId", site.PauseId }, { "kind", operation.Kind }, { "message", exception.Message ?? string.Empty } },
                    new[] { "Run tools run debug.pause-inspect to read the paused state before submitting another operation." }));
            }
            finally
            {
                UnityCliPauseContext.Exit();
                servicingPauseId = previousId;
                servicingOrigin = previousOrigin;
                lock (site.Gate)
                {
                    site.Executing = null;
                    site.ServedOperations++;
                    servedOperations++;
                    site.LastServedMonotonicMs = DebugEventClocks.MonotonicMilliseconds;
                }
                AppendEmittedFromSite(site);
                operation.Complete();
            }
        }

        /// <summary>把现场操作期间用户代码 Emit 的事件按 pause 来源追加；不复制 hook 事件真值。</summary>
        private static void AppendEmittedFromSite(DebugPauseSite site)
        {
            var hookInvocation = site.Invocation as MethodHookInvocation;
            if (hookInvocation == null) return;
            var emitted = hookInvocation.Emitted;
            for (var index = site.EmittedOffset; index < emitted.Count; index++)
            {
                var record = new UnityCliDebugEvent
                {
                    SourceId = UnityCliDebugSources.Pause,
                    Kind = "custom",
                    ExecutionPoint = "pause:evaluate",
                    HookId = site.HookId,
                    Revision = site.Revision,
                    InvocationId = site.InvocationId,
                    PlaySessionId = site.Invocation.PlaySessionId,
                    ClockSegment = site.Invocation.ClockSegment,
                    MethodIdentity = site.MethodIdentity,
                    Instance = site.Invocation.Instance == null ? "static" : site.Invocation.Instance.GetType().FullName,
                    Name = emitted[index].Name,
                    Value = emitted[index].Value,
                    Truncated = emitted[index].Truncated
                };
                DebugEventStore.Append(record);
            }
            site.EmittedOffset = emitted.Count;
        }

        /// <summary>现场终结：从活动注册表移出、发布终结事实并释放它占用的 session 资源槽位。</summary>
        private static void FinishSite(DebugPauseSite site)
        {
            lock (Sync)
            {
                Sites.Remove(site.PauseId);
                SiteOrder.Remove(site.PauseId);
                Finished[site.PauseId] = site;
                FinishedOrder.AddLast(site.PauseId);
                while (FinishedOrder.Count > DebugLimits.MaxRetainedPauseSites)
                {
                    var oldest = FinishedOrder.First.Value;
                    FinishedOrder.RemoveFirst();
                    Finished.Remove(oldest);
                }
            }
            AppendSiteEvent(site, site.State, site.PauseId, site.TerminalReason);
            if (!string.IsNullOrEmpty(site.ResourceId)) ReleaseResourceSlot(site);
        }

        /// <summary>
        /// 释放 session 资源槽位。session host 的观察读取主线程 API，
        /// 因此只有命中线程就是主线程时才能就地释放；其他情况交给主线程 pump 有界归位。
        /// </summary>
        private static void ReleaseResourceSlot(DebugPauseSite site)
        {
            var resourceId = site.ResourceId;
            site.ResourceId = string.Empty;
            if (site.MainThread)
            {
                DebugSessionHost.ReleaseResource(resourceId);
                return;
            }
            lock (Sync) PendingResourceReleases.Add(resourceId);
        }

        /// <summary>
        /// 主线程有界 pulse：发布本域 runtime/generation 身份并归位待释放的 session 资源槽位。
        /// 由 hook host 的既有主线程 tick 调用，不新增任何 update 订阅。
        /// </summary>
        internal static void Pump()
        {
            runtimeIdentity = DebugSessionHost.RuntimeId();
            domainIdentity = DebugSessionHost.DomainGeneration();
            List<string> releases = null;
            lock (Sync)
            {
                if (PendingResourceReleases.Count == 0) return;
                releases = new List<string>(PendingResourceReleases);
                PendingResourceReleases.Clear();
            }
            foreach (var resourceId in releases) DebugSessionHost.ReleaseResource(resourceId);
        }

        // ------------------------------------------------------------------
        // 解除：resume、timeout、session stop 与生命周期清理
        // ------------------------------------------------------------------

        /// <summary>请求解除一个指定现场。resume 信号是独立标记，绝不排在等待请求之后。</summary>
        internal static DebugPauseSite RequestResume(string pauseId, string reason)
        {
            var site = FindSite(pauseId);
            DebugPauseOperation executing;
            lock (site.Gate)
            {
                if (site.ResumeRequested) return site;
                site.ResumeRequested = true;
                site.ResumeReason = string.IsNullOrEmpty(reason) ? DebugPauseReleaseReasons.Resume : reason;
                executing = site.Executing;
            }
            // 取消在途的现场编译：这是框架自己的工作，不是用户同步代码。
            DebugPauseMailbox.Cancel(executing);
            site.Signal.Set();
            AppendSiteEvent(site, "pause-resume-requested", site.PauseId, site.ResumeReason);
            return site;
        }

        /// <summary>解除某个 hook 定义的全部现场；返回是否已无现场遗留。</summary>
        internal static bool ReleaseSitesForHook(string hookId, string reason)
        {
            if (string.IsNullOrEmpty(hookId)) return true;
            List<DebugPauseSite> matches;
            lock (Sync)
            {
                matches = Sites.Values.Where(site => string.Equals(site.HookId, hookId, StringComparison.Ordinal)).ToList();
            }
            foreach (var site in matches) RequestResume(site.PauseId, reason);
            return WaitForRelease(matches, DebugLimits.PauseCleanupWaitMs);
        }

        /// <summary>
        /// 解除全部现场；用于 domain reload、Play 退出与 Editor 关闭。
        ///
        /// 本方法只发出解除信号，绝不等待：生命周期清理发生在 Unity 自己的 reload/关闭路径上，
        /// 而现场线程可能正停在无法抢占的用户同步代码里，等待既不可能成功，又会让 Unity 的关闭流程停在这里。
        /// 现场线程在自己的边界观察到信号后终结自己并留下终结事实。
        /// 只有必须向调用方确认"现场是否真的离开 paused"的显式变更路径（hook disable/remove）才使用
        /// <see cref="WaitForRelease"/> 的有界等待。
        /// </summary>
        internal static void ReleaseAll(string reason)
        {
            List<DebugPauseSite> matches;
            lock (Sync)
            {
                matches = Sites.Values.ToList();
                PendingResourceReleases.Clear();
            }
            foreach (var site in matches) RequestResume(site.PauseId, reason);
        }

        /// <summary>
        /// 有界等待现场真正离开 paused；窗口内未退出即返回 false，由调用方报告 pending。
        /// 只允许显式变更路径（hook disable/remove）使用；reload/quit/Play 退出的清理解除永不等待。
        /// </summary>
        private static bool WaitForRelease(List<DebugPauseSite> sites, int waitMs)
        {
            if (sites == null || sites.Count == 0) return true;
            var deadline = DebugEventClocks.MonotonicMilliseconds + waitMs;
            while (true)
            {
                var released = true;
                foreach (var site in sites)
                {
                    if (site.IsTerminal) continue;
                    released = false;
                    break;
                }
                if (released) return true;
                if (DebugEventClocks.MonotonicMilliseconds >= deadline) return false;
                Thread.Sleep(2);
            }
        }

        internal static void Reset()
        {
            lock (Sync)
            {
                Sites.Clear();
                SiteOrder.Clear();
                Finished.Clear();
                FinishedOrder.Clear();
                PendingResourceReleases.Clear();
                SkipReasons.Clear();
                SuppressOrigins.Clear();
                pauseSequence = 0L;
                skippedSites = 0L;
                suppressedNestedHits = 0L;
                servedOperations = 0L;
                refusedRequests = 0L;
                runtimeIdentity = string.Empty;
                domainIdentity = string.Empty;
            }
            servicingPauseId = null;
            servicingOrigin = null;
            DebugPauseControl.Reset();
        }
        // ------------------------------------------------------------------
        // 查询与投影
        // ------------------------------------------------------------------

        /// <summary>按精确 pauseId 找到活动现场；已结束或从未发布的 pauseId 抛出稳定 E_DEBUG_PAUSE_STALE。</summary>
        internal static DebugPauseSite FindSite(string pauseId)
        {
            if (string.IsNullOrEmpty(pauseId)) throw DebugPauseMailbox.Unknown(pauseId);
            lock (Sync)
            {
                DebugPauseSite site;
                if (Sites.TryGetValue(pauseId, out site)) return site;
                DebugPauseSite finished;
                if (Finished.TryGetValue(pauseId, out finished))
                    throw DebugPauseMailbox.Stale(finished, "the site already left the paused state with reason '" + finished.TerminalReason + "'");
            }
            throw DebugPauseMailbox.Unknown(pauseId);
        }

        /// <summary>只读查找一个匹配现场；wait 用它作为框架 wait 的谓词，找不到返回 null。</summary>
        internal static DebugPauseSite FindMatch(DebugPauseWaitRequest request)
        {
            lock (Sync)
            {
                foreach (var pauseId in SiteOrder)
                {
                    DebugPauseSite site;
                    if (!Sites.TryGetValue(pauseId, out site)) continue;
                    if (request != null)
                    {
                        if (!string.IsNullOrEmpty(request.PauseId) && !string.Equals(request.PauseId, site.PauseId, StringComparison.Ordinal)) continue;
                        if (!string.IsNullOrEmpty(request.SessionId) && !string.Equals(request.SessionId, site.SessionId, StringComparison.Ordinal)) continue;
                        if (!string.IsNullOrEmpty(request.HookId) && !string.Equals(request.HookId, site.HookId, StringComparison.Ordinal)) continue;
                        if (request.ThreadId != null && request.ThreadId.Value != site.ThreadId) continue;
                    }
                    return site;
                }
                return null;
            }
        }

        internal static DebugPauseListResult List(DebugPauseListRequest request, string editorSessionId)
        {
            var state = request == null ? null : request.State;
            var invocationId = request == null ? (long?)null : request.InvocationId;
            var mainThread = request == null ? (bool?)null : request.MainThread;
            List<DebugPauseSite> snapshot;
            lock (Sync)
            {
                snapshot = SiteOrder
                    .Select(pauseId => { DebugPauseSite site; return Sites.TryGetValue(pauseId, out site) ? site : null; })
                    .Where(site => site != null)
                    .Where(site => request == null || string.IsNullOrEmpty(request.SessionId) || string.Equals(request.SessionId, site.SessionId, StringComparison.Ordinal))
                    .Where(site => request == null || string.IsNullOrEmpty(request.HookId) || string.Equals(request.HookId, site.HookId, StringComparison.Ordinal))
                    .Where(site => request == null || request.ThreadId == null || request.ThreadId.Value == site.ThreadId)
                    .Where(site => string.IsNullOrEmpty(state) || string.Equals(site.State, state, StringComparison.Ordinal))
                    .Where(site => invocationId == null || site.InvocationId == invocationId.Value)
                    .Where(site => mainThread == null || site.MainThread == mainThread.Value)
                    .ToList();
            }
            var scope = UnityCliContinuationToken.Fingerprint(
                editorSessionId + "\0" + ListScope + "\0"
                + (request == null ? string.Empty : request.SessionId ?? string.Empty) + "\0"
                + (request == null ? string.Empty : request.HookId ?? string.Empty) + "\0"
                + (request == null || request.ThreadId == null ? string.Empty : request.ThreadId.Value.ToString(CultureInfo.InvariantCulture)) + "\0"
                + (state ?? string.Empty) + "\0"
                + (invocationId.HasValue ? invocationId.Value.ToString(CultureInfo.InvariantCulture) : string.Empty) + "\0"
                + (mainThread.HasValue ? (mainThread.Value ? "true" : "false") : string.Empty));
            var offset = 0;
            if (request != null && !string.IsNullOrEmpty(request.Cursor))
            {
                string position;
                if (!UnityCliContinuationToken.TryParse(request.Cursor, scope, editorSessionId, out position)
                    || !int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset)
                    || offset < 0 || offset > snapshot.Count)
                    throw new BridgeCommandException(
                        "E_CONTINUATION_STALE",
                        "The pause list continuation does not match the current Editor session or filter scope.",
                        null,
                        new[] { "Start a new debug.pause-list read without a cursor." });
            }
            var limit = request == null || request.Limit == null
                ? DebugLimits.PauseListDefaultLimit
                : Math.Min(Math.Max(1, request.Limit.Value), DebugLimits.PauseListMaxLimit);
            var page = new List<DebugPauseSiteView>();
            for (var index = offset; index < snapshot.Count && page.Count < limit; index++) page.Add(View(snapshot[index]));
            var nextOffset = offset + page.Count;
            return new DebugPauseListResult
            {
                Sites = page,
                NextCursor = nextOffset < snapshot.Count
                    ? UnityCliContinuationToken.Create(scope, editorSessionId, nextOffset.ToString(CultureInfo.InvariantCulture))
                    : null,
                Total = snapshot.Count,
                NextOffset = nextOffset,
                HasMore = nextOffset < snapshot.Count,
                Coverage = Coverage(),
                Bounds = new DebugPauseBoundsView
                {
                    SimultaneousSitesMax = DebugLimits.MaxPauseSites,
                    StackFramesMax = DebugLimits.MaxPauseStackFrames,
                    MailboxWaitingMax = DebugLimits.MaxPauseMailboxWaiting,
                    MailboxExecutingMax = DebugLimits.MaxPauseMailboxExecuting,
                    RetainedFinishedSitesMax = DebugLimits.MaxRetainedPauseSites,
                    PageDefaultLimit = DebugLimits.PauseListDefaultLimit,
                    PageMaxLimit = DebugLimits.PauseListMaxLimit
                },
                EditorSessionId = editorSessionId,
                RuntimeId = runtimeIdentity,
                DomainGeneration = domainIdentity
            };
        }

        internal static DebugPauseSiteView View(DebugPauseSite site)
        {
            lock (site.Gate)
            {
                return new DebugPauseSiteView
                {
                    PauseId = site.PauseId,
                    State = site.State,
                    ReleaseReason = site.TerminalReason,
                    ThreadId = site.ThreadId,
                    MainThread = site.MainThread,
                    HookId = site.HookId,
                    Revision = site.Revision,
                    InvocationId = site.InvocationId,
                    RuntimeId = site.RuntimeId,
                    DomainGeneration = site.DomainGeneration,
                    MethodIdentity = site.MethodIdentity,
                    TargetKey = site.TargetKey,
                    Position = site.Position,
                    SessionId = site.SessionId,
                    OwnerRegistered = !string.IsNullOrEmpty(site.ResourceId),
                    ResourceId = string.IsNullOrEmpty(site.ResourceId) ? null : site.ResourceId,
                    TimeoutKind = site.Unbounded ? DebugPauseTimeoutKinds.Unbounded : DebugPauseTimeoutKinds.Bounded,
                    TimeoutDurationMs = site.Unbounded ? 0L : site.DurationMs,
                    EffectiveBound = site.EffectiveBound,
                    HasDeadline = !site.Unbounded,
                    RemainingMs = site.RemainingMs,
                    PausedForMs = (DebugPauseStates.IsTerminal(site.State) ? site.TerminalMonotonicMs : DebugEventClocks.MonotonicMilliseconds) - site.StartedMonotonicMs,
                    WaitingRequests = site.Waiting.Count,
                    MaxWaitingRequests = DebugLimits.MaxPauseMailboxWaiting,
                    ExecutingOperations = site.Executing == null ? 0L : 1L,
                    ExecutingOperation = site.Executing == null ? string.Empty : site.Executing.Kind,
                    ResumeRequested = site.ResumeRequested,
                    ServedOperations = site.ServedOperations,
                    RefusedRequests = site.RefusedRequests,
                    SuppressedNestedHits = site.SuppressedNestedHits
                };
            }
        }

        internal static DebugPauseCoverageView Coverage()
        {
            lock (Sync)
            {
                return new DebugPauseCoverageView
                {
                    SkippedSites = skippedSites,
                    SuppressedNestedHits = suppressedNestedHits,
                    RefusedRequests = refusedRequests,
                    ServedOperations = servedOperations,
                    SkipReasons = SkipReasons.Select(pair => new DebugPauseReasonCount { Reason = pair.Key, Count = pair.Value }).ToList(),
                    SuppressOrigins = SuppressOrigins.Select(pair => new DebugPauseReasonCount { Reason = pair.Key, Count = pair.Value }).ToList(),
                    Note = "Skipped hits kept the original method path and produced no pause site; suppressed hits were rejected only because the hit thread was already serving a pause site, so they are not the same evidence sample as natural business calls."
                };
            }
        }

        // ------------------------------------------------------------------
        // 现场操作入队
        // ------------------------------------------------------------------

        /// <summary>
        /// 同步用户代码未在等待窗口内返回时的公开结论。
        /// 现场保持 paused，操作仍未完成；这里只能报告执行状态未知/pending，绝不声称线程已被强行恢复。
        /// Tool 与定向测试共用本构造点，措辞与语义只有一份。
        /// </summary>
        internal static DebugPauseEvaluateResult PendingEvaluateResult(DebugPauseSite site, DebugPauseOperation operation, bool programEntry)
        {
            return new DebugPauseEvaluateResult
            {
                PauseId = site.PauseId,
                ExecutionState = DebugPauseExecutionStates.Pending,
                Completed = false,
                ServedOnPausedThread = true,
                ServedOnThreadId = site.ThreadId,
                DurationMs = DebugEventClocks.MonotonicMilliseconds - operation.StartedMonotonicMs,
                SourceKind = programEntry ? "program-entry" : "code",
                KnownInterventions = new List<string>(),
                CompileDiagnostics = new List<string>(),
                OutputNote = "Process stdout is not captured or redirected for an on-site evaluation; return a value instead of printing.",
                PendingNote = "The on-site code had not returned when the bounded wait ended, so its execution state is unknown and it may still be running. The pause site is still held and was not resumed; resume releases the wait and does not preempt running code.",
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DomainGeneration = domainIdentity
            };
        }

        internal static DebugPauseOperation EnqueueInspect(DebugPauseSite site, DebugPauseInspectRequest request, string origin)
        {
            var operation = new DebugPauseOperation(DebugPauseOperationKinds.Inspect, origin) { InspectRequest = request, Site = site };
            var failure = DebugPauseMailbox.TryEnqueue(site, operation, out var enqueued);
            if (failure != null) throw failure;
            if (!enqueued) throw DebugPauseMailbox.Stale(site, "the request could not be enqueued");
            return operation;
        }

        internal static DebugPauseOperation EnqueueEvaluate(DebugPauseSite site, DebugPauseEvaluateRequest request, string origin)
        {
            var operation = new DebugPauseOperation(DebugPauseOperationKinds.Evaluate, origin) { EvaluateRequest = request, Site = site };
            var failure = DebugPauseMailbox.TryEnqueue(site, operation, out var enqueued);
            if (failure != null) { lock (Sync) refusedRequests++; throw failure; }
            if (!enqueued) throw DebugPauseMailbox.Stale(site, "the request could not be enqueued");
            return operation;
        }

        /// <summary>命名了 pauseId 的值读取；与 inspect 走同一有界 mailbox 与同一终结答复规则。</summary>
        internal static DebugPauseOperation EnqueueValueRead(DebugPauseSite site, DebugValueReadRequest request, string origin)
        {
            var operation = new DebugPauseOperation(DebugPauseOperationKinds.ValueRead, origin) { ValueReadRequest = request, Site = site };
            var failure = DebugPauseMailbox.TryEnqueue(site, operation, out var enqueued);
            if (failure != null) { lock (Sync) refusedRequests++; throw failure; }
            if (!enqueued) throw DebugPauseMailbox.Stale(site, "the request could not be enqueued");
            return operation;
        }

        /// <summary>命名了 pauseId 的值写入；写入本身是 effect，但现场保留与拒绝规则和读取一致。</summary>
        internal static DebugPauseOperation EnqueueValueWrite(DebugPauseSite site, DebugValueWriteRequest request, string origin)
        {
            var operation = new DebugPauseOperation(DebugPauseOperationKinds.ValueWrite, origin) { ValueWriteRequest = request, Site = site };
            var failure = DebugPauseMailbox.TryEnqueue(site, operation, out var enqueued);
            if (failure != null) { lock (Sync) refusedRequests++; throw failure; }
            if (!enqueued) throw DebugPauseMailbox.Stale(site, "the request could not be enqueued");
            return operation;
        }

        // ------------------------------------------------------------------
        // 栈与 snapshot 捕获
        // ------------------------------------------------------------------

        /// <summary>在命中线程上捕获有界真实栈；其他帧的 locals 不可读，必须显式标记不可用。</summary>
        internal static List<Dictionary<string, object>> CaptureStack(int depth)
        {
            var frames = new List<Dictionary<string, object>>();
            var limit = Math.Min(Math.Max(0, depth), DebugLimits.MaxPauseStackFrames);
            if (limit == 0) return frames;
            StackTrace trace;
            try
            {
                // 不读取文件/调试信息：它可能触碰符号与源码加载，且对 worker 命中没有价值。
                trace = new StackTrace(false);
            }
            catch (Exception exception)
            {
                frames.Add(new Dictionary<string, object>
                {
                    { "index", 0L },
                    { "method", "unavailable" },
                    { "localsAvailable", false },
                    { "unavailableReason", "The stack of the hit thread could not be captured: " + exception.GetType().Name + "." }
                });
                return frames;
            }
            for (var index = 0; index < trace.FrameCount && frames.Count < limit; index++)
            {
                var frame = trace.GetFrame(index);
                var method = frame == null ? null : frame.GetMethod();
                frames.Add(new Dictionary<string, object>
                {
                    { "index", (long)frames.Count },
                    { "method", method == null ? "unknown" : MethodHookMethodText.Describe(method) },
                    { "declaringType", method == null || method.DeclaringType == null ? string.Empty : method.DeclaringType.FullName },
                    { "ilOffset", (long)(frame == null ? -1 : frame.GetILOffset()) },
                    { "localsAvailable", false },
                    { "unavailableReason", "Locals of a frame other than the paused invocation are not readable from a stack trace; only the paused call's arguments and instance are available." }
                });
            }
            return frames;
        }

        /// <summary>snapshot 断点：捕获配置的值与可选有界栈后继续，不等待任何客户端。</summary>
        private static void AppendSnapshotEvent(MethodHookBinding binding, IUnityCliHookInvocation invocation, object[] arguments, bool isPrefix, bool mainThread)
        {
            var truncation = new MethodHookProjection.Truncation();
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                MethodHookHost.CaptureFields(invocation, binding.Capture.InstanceFields, fields, truncation, mainThread);
                MethodHookHost.CaptureFields(invocation, binding.Capture.StaticFields, fields, truncation, mainThread);
            }
            catch (Exception exception)
            {
                fields["captureError"] = exception.GetType().Name;
                truncation.Mark();
            }
            object projectedArguments = null;
            if (binding.Capture.Arguments) projectedArguments = MethodHookProjection.Project(invocation.Arguments, truncation, mainThread);
            object projectedResult = null;
            if (!isPrefix && binding.Capture.Result) projectedResult = MethodHookProjection.Project(invocation.Result, truncation, mainThread);
            var stackDepth = binding.Breakpoint == null ? 0 : binding.Breakpoint.StackDepth;
            DebugEventStore.Append(new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugSources.Pause,
                Kind = "snapshot",
                HookId = binding.HookId,
                Revision = binding.Revision,
                InvocationId = InvocationIdOf(invocation),
                ExecutionPoint = "breakpoint:" + (isPrefix ? DebugPausePositions.Prefix : DebugPausePositions.Postfix),
                PlaySessionId = invocation.PlaySessionId,
                ClockSegment = invocation.ClockSegment,
                ElapsedMilliseconds = invocation.ElapsedMilliseconds,
                Instance = invocation.Instance == null ? "static" : invocation.Instance.GetType().FullName,
                MethodIdentity = invocation.MethodIdentity,
                Arguments = projectedArguments,
                Result = projectedResult,
                Fields = fields.Count == 0 ? null : fields,
                Name = "snapshot",
                Value = stackDepth > 0 ? new Dictionary<string, object> { { "stack", CaptureStack(stackDepth) } } : null,
                Truncated = truncation.Truncated
            });
        }

        private static void AppendSiteEvent(DebugPauseSite site, string name, string detail, string reason)
        {
            var value = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "pauseId", site.PauseId },
                { "state", site.State },
                { "reason", reason ?? string.Empty },
                { "threadId", (long)site.ThreadId },
                { "mainThread", site.MainThread },
                { "position", site.Position },
                { "runtimeId", site.RuntimeId },
                { "domainGeneration", site.DomainGeneration },
                { "sessionId", site.SessionId },
                { "timeoutKind", site.Unbounded ? DebugPauseTimeoutKinds.Unbounded : DebugPauseTimeoutKinds.Bounded },
                { "durationMs", site.Unbounded ? 0L : site.DurationMs },
                { "effectiveBound", site.EffectiveBound },
                { "servedOperations", site.ServedOperations },
                { "refusedRequests", site.RefusedRequests },
                { "suppressedNestedHits", site.SuppressedNestedHits },
                { "detail", detail ?? string.Empty }
            };
            DebugEventStore.Append(new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugSources.Pause,
                Kind = "pause",
                HookId = site.HookId,
                Revision = site.Revision,
                InvocationId = site.InvocationId,
                ExecutionPoint = "pause:" + name,
                PlaySessionId = site.Invocation == null ? string.Empty : site.Invocation.PlaySessionId,
                ClockSegment = site.Invocation == null ? string.Empty : site.Invocation.ClockSegment,
                MethodIdentity = site.MethodIdentity,
                Instance = site.Instance == null ? "static" : site.Instance.GetType().FullName,
                Name = name,
                Value = value
            });
        }

        /// <summary>超出上限未建立现场：记录 pauseSkipped 与覆盖缺口，原方法语义保持不变。</summary>
        private static void RecordSkipped(MethodHookBinding binding, string reason)
        {
            lock (Sync)
            {
                skippedSites++;
                long count;
                SkipReasons.TryGetValue(reason, out count);
                if (SkipReasons.ContainsKey(reason) || SkipReasons.Count < DebugLimits.MaxPauseCoverageReasons) SkipReasons[reason] = count + 1L;
            }
            DebugEventStore.Append(new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugSources.Pause,
                Kind = "pause-skipped",
                HookId = binding.HookId,
                Revision = binding.Revision,
                ExecutionPoint = "pause:skipped",
                MethodIdentity = binding.Resolved == null ? string.Empty : MethodHookMethodText.Describe(binding.Resolved),
                Name = "pause-skipped",
                Value = new Dictionary<string, object>
                {
                    { "reason", reason },
                    { "maxSites", (long)DebugLimits.MaxPauseSites },
                    { "coverageGap", "This hit was not paused, so the pause evidence window has a gap at this invocation." }
                }
            });
        }

        /// <summary>服务现场期间命中同一断点：记录 pauseSuppressed 与请求来源，不建立第二个现场。</summary>
        private static void RecordSuppressed(MethodHookBinding binding, string pauseId, string origin)
        {
            var key = string.IsNullOrEmpty(origin) ? "unknown-origin" : origin;
            lock (Sync)
            {
                suppressedNestedHits++;
                long count;
                SuppressOrigins.TryGetValue(key, out count);
                if (SuppressOrigins.ContainsKey(key) || SuppressOrigins.Count < DebugLimits.MaxPauseCoverageReasons) SuppressOrigins[key] = count + 1L;
                DebugPauseSite site;
                if (Sites.TryGetValue(pauseId, out site))
                {
                    lock (site.Gate) site.SuppressedNestedHits++;
                }
            }
            DebugEventStore.Append(new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugSources.Pause,
                Kind = "pause-suppressed",
                HookId = binding.HookId,
                Revision = binding.Revision,
                ExecutionPoint = "pause:suppressed",
                MethodIdentity = binding.Resolved == null ? string.Empty : MethodHookMethodText.Describe(binding.Resolved),
                Name = "pause-suppressed",
                Value = new Dictionary<string, object>
                {
                    { "pauseId", pauseId },
                    { "requestOrigin", key },
                    { "note", "The hit thread was already serving this pause site, so no nested pause was entered. This call is not the same evidence sample as a natural business call." }
                }
            });
        }

        // ------------------------------------------------------------------
        // inspect 投影（只在暂停线程上执行）
        // ------------------------------------------------------------------

        private static DebugPauseInspectResult BuildInspect(DebugPauseSite site, DebugPauseInspectRequest request, int servedThreadId)
        {
            var requirement = request ?? new DebugPauseInspectRequest();
            var breakpoint = site.Binding == null ? null : site.Binding.Breakpoint;
            var includeStack = requirement.IncludeStack ?? true;
            var requestedDepth = requirement.StackDepth ?? (breakpoint == null ? 0 : breakpoint.StackDepth);
            var depth = includeStack ? Math.Min(Math.Max(0, requestedDepth), DebugLimits.MaxPauseStackFrames) : 0;
            var unavailable = new List<string>
            {
                "Locals of frames other than the paused invocation cannot be read from a stack trace.",
                "Other threads, servers, external clocks and object mutation continue while this thread is paused."
            };
            var result = new DebugPauseInspectResult
            {
                PauseId = site.PauseId,
                Site = View(site),
                ServedOnPausedThread = true,
                ServedOnThreadId = servedThreadId,
                StackCaptured = depth > 0,
                Unavailable = unavailable,
                ExecutionModel = "Reads are executed on the paused hit thread itself; the calling request only waits for the answer."
            };
            if (depth > 0)
            {
                var raw = CaptureStack(depth);
                result.Stack = raw.Select(frame => new DebugPauseStackFrameView
                {
                    Index = Convert.ToInt64(frame["index"], CultureInfo.InvariantCulture),
                    MethodIdentity = Convert.ToString(frame["method"], CultureInfo.InvariantCulture),
                    DeclaringType = Convert.ToString(frame["declaringType"], CultureInfo.InvariantCulture),
                    IlOffset = Convert.ToInt64(frame["ilOffset"], CultureInfo.InvariantCulture),
                    LocalsAvailable = false,
                    UnavailableReason = Convert.ToString(frame["unavailableReason"], CultureInfo.InvariantCulture)
                }).ToList();
            }
            else
            {
                result.Stack = new List<DebugPauseStackFrameView>();
                unavailable.Add("No stack was captured for this read; pass includeStack or stackDepth to capture a bounded real stack.");
            }
            var truncation = new MethodHookProjection.Truncation();
            var mainThread = site.MainThread;
            if (requirement.IncludeArguments ?? true)
            {
                result.Arguments = MethodHookProjection.Project(site.Invocation.Arguments, truncation, mainThread);
            }
            if (requirement.IncludeInstance ?? true)
            {
                result.InstanceIdentity = site.Invocation.Instance == null ? "static" : site.Invocation.Instance.GetType().FullName;
                result.Instance = site.Invocation.Instance == null ? null : MethodHookProjection.Project(site.Invocation.Instance, truncation, mainThread);
            }
            if (site.Position == DebugPausePositions.Postfix)
            {
                result.Result = MethodHookProjection.Project(site.Invocation.Result, truncation, mainThread);
            }
            else
            {
                unavailable.Add("The result is not available at a prefix boundary: the original method has not returned yet.");
            }
            unavailable.Add("No exception object exists at this boundary; an exception ends the invocation through the original-exception lifecycle event instead.");
            result.Fields = new List<DebugPauseFieldView>();
            if (requirement.Fields != null)
            {
                foreach (var field in requirement.Fields)
                {
                    if (result.Fields.Count >= DebugLimits.MaxPauseInspectFields) break;
                    result.Fields.Add(ReadField(site, field, truncation, mainThread));
                }
            }
            return result;
        }

        private static DebugPauseFieldView ReadField(DebugPauseSite site, DebugPauseFieldRequest request, MethodHookProjection.Truncation truncation, bool mainThread)
        {
            var view = new DebugPauseFieldView
            {
                Requested = request == null ? string.Empty : request.FieldName,
                FieldName = request == null ? string.Empty : request.FieldName,
                DeclaringType = request == null ? string.Empty : request.DeclaringType ?? string.Empty,
                Readable = false,
                Static = request != null && request.Static == true
            };
            if (request == null || string.IsNullOrWhiteSpace(request.FieldName))
            {
                view.UnavailableReason = "The field request has no fieldName.";
                return view;
            }
            var instanceType = site.Invocation.Instance == null ? null : site.Invocation.Instance.GetType();
            var fallback = site.Binding == null || site.Binding.Resolved == null ? null : site.Binding.Resolved.DeclaringType;
            try
            {
                var field = MethodHookTargetResolver.ResolveField(instanceType ?? fallback, request.DeclaringType, request.FieldName);
                view.Static = field.IsStatic;
                var value = field.GetValue(field.IsStatic ? null : site.Invocation.Instance);
                view.Value = MethodHookProjection.Project(value, truncation, mainThread);
                view.Readable = true;
            }
            catch (BridgeCommandException failure)
            {
                view.UnavailableReason = failure.Code + ": " + failure.Message;
            }
            catch (Exception exception)
            {
                view.UnavailableReason = "The field could not be read: " + exception.GetType().Name + ".";
            }
            return view;
        }

        private static BridgeCommandException Schema(string path, string expected, string actual)
        {
            return new BridgeCommandException(
                "E_SCHEMA_INVALID",
                "Field " + path + " is invalid: expected " + expected + ".",
                new Dictionary<string, object> { { "path", "$." + path }, { "expected", expected }, { "actual", actual ?? "missing" } },
                new[] { "Run tools describe debug.hook-install to read the breakpoint contract, then submit the corrected input." });
        }
    }
}
#endif
