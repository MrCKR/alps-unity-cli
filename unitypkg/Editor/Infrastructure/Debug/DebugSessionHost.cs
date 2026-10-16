#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEditor;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>create 的 effect 前提炼结果；全部校验通过后才允许跨过 effect barrier。</summary>
    internal sealed class DebugSessionCreatePlan
    {
        internal string Label;
        internal string ActivationScope;
        internal string ClockKind;
        internal Dictionary<string, object> InitialState;
        internal long StateBytes;
        /// <summary>规范化后的日志捕获等级；off 表示不挂载日志订阅。</summary>
        internal string LogCaptureLevel = DebugLogCaptureLevels.Off;
        internal string LogFilterContains = string.Empty;
        internal string LogSignal = string.Empty;
    }

    /// <summary>stop 的 effect 前提炼结果。</summary>
    internal sealed class DebugSessionStopPlan
    {
        internal string SessionId;
    }

    /// <summary>remove 的 effect 前提炼结果。</summary>
    internal sealed class DebugSessionRemovePlan
    {
        internal string SessionId;
    }

    /// <summary>
    /// debug session 域的唯一运行时 owner。
    ///
    /// 职责：
    /// - 持有本 domain 唯一的 UnityCliDebugSessionRegistry，并在每个 domain 首次使用时从官方文件加载；
    /// - 把 engine-free 失败原因归约为稳定错误码、details 与可执行 nextActions；
    /// - 观察 Play 段边界，让 Play 段内资源在退出 Play 后失效；
    /// - 为后续设施提供唯一的资源占用与有序清理条目入口。
    ///
    /// 规则：
    /// - 定义只属于 canonical 项目路径加当前 editorSessionId；owner 永不从选择、上次调用或 CLI 进程推断。
    /// - 读取型调用不写存储文件；只有定义发生变化时才发布。
    /// - 本类不注册任何 update 回调，也不持有线程或文件句柄。
    /// </summary>
    internal static class DebugSessionHost
    {
        private const int MaxDiagnosticBytes = 512;
        private static readonly object Sync = new object();
        private static UnityCliDebugSessionRegistry registry;
        private static DebugSessionStoreObservation observation = new DebugSessionStoreObservation();
        private static bool storeUsable = true;
        private static string storeReason = string.Empty;
        private static string loggedReason = string.Empty;
        private static int playSegmentIndex;
        private static string playSegmentId = string.Empty;

        /// <summary>
        /// 主线程最近一次观察到的 Play 段身份；Edit 模式为空。
        ///
        /// 命中路径可能运行在任意托管线程上，而 Unity 的 EditorApplication.isPlaying 只能由主线程读取，
        /// 因此 runtime 段身份在主线程观察后发布到本字段，任何线程都只读取已发布的值。
        /// </summary>
        private static volatile string observedRuntimeSegment = string.Empty;

        /// <summary>
        /// 主线程最近一次观察到的 Play 状态；off-main 只读取这个已发布值。
        ///
        /// EditorApplication.isPlaying 只能由主线程读取，而同步 effect（例如首次 worker 命中后的资源释放、
        /// 清理条目注册）可能发生在任意命中线程上，因此这里与 runtime 段身份使用同一条发布-读取规则：
        /// 主线程读取后发布，任何线程都只读取已发布的值。
        /// </summary>
        private static volatile bool observedIsPlaying;

        /// <summary>本 domain 的 session 上限来源。</summary>
        internal static UnityCliDebugSessionCapacity Capacity()
        {
            return new UnityCliDebugSessionCapacity(
                DebugLimits.MaxSessions,
                DebugLimits.MaxActiveResourcesPerSession,
                DebugLimits.MaxCleanupStepsPerSession,
                DebugLimits.SessionListDefaultLimit,
                DebugLimits.SessionListMaxLimit,
                DebugLimits.MaxSessionStateBytes,
                DebugLimits.MaxSessionStoreBytes);
        }

        /// <summary>当前 domain 身份；Domain Reload 后必然变化。</summary>
        internal static string DomainGeneration()
        {
            return UnityCliContinuationToken.Fingerprint(UnityCliBridge.ReadGenerationIdentity());
        }

        /// <summary>当前观察到的 Play runtime 段；Edit 模式为空。任何线程都可安全读取，不触碰 Unity 主线程状态。</summary>
        internal static string RuntimeId()
        {
            return observedRuntimeSegment;
        }

        /// <summary>读取一个 session 定义的当前真值；不存在时返回 null。只读，不写存储。</summary>
        internal static UnityCliDebugSessionDefinition Definition(string sessionId)
        {
            SyncObservedState();
            return Registry().Find(sessionId);
        }

        // ------------------------------------------------------------------
        // create
        // ------------------------------------------------------------------

        /// <summary>create 的 effect 前校验与计划；失败时抛出稳定错误且不改变任何状态。</summary>
        internal static DebugSessionCreatePlan PrepareCreate(DebugSessionCreateRequest request)
        {
            SyncObservedState();
            var session = Registry();
            var label = request == null || request.Label == null ? string.Empty : request.Label.Trim();
            if (label.Length == 0) throw InvalidField("label", "non-empty string", "empty");
            var labelBytes = Encoding.UTF8.GetByteCount(label);
            if (labelBytes > DebugLimits.MaxSessionLabelBytes)
                throw InvalidField(
                    "label",
                    DebugLimits.MaxSessionLabelBytes.ToString(CultureInfo.InvariantCulture) + " UTF-8 bytes or fewer",
                    labelBytes.ToString(CultureInfo.InvariantCulture) + " UTF-8 bytes");
            var initialState = request.InitialState;
            var stateBytes = initialState == null ? 0L : Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(initialState));
            var isPlaying = EditorApplication.isPlaying;
            string logReason;
            string logLevel;
            var logLevelText = DebugListenerHost.Normalize(request.LogCapture, out logReason, out logLevel);
            if (logReason.Length != 0)
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "Field logCapture is invalid: " + logReason + ".",
                    new Dictionary<string, object> { { "path", "$.logCapture" }, { "reason", logReason } },
                    new[] { "Run tools describe debug.session.create to read the log capture contract, then submit the corrected input." });
            int sessionsUsed;
            var failure = session.CanCreate(label, request.ActivationScope, stateBytes, isPlaying, out sessionsUsed);
            if (failure != null) throw CreateRejection(failure, label, request.ActivationScope, stateBytes, sessionsUsed, isPlaying);
            return new DebugSessionCreatePlan
            {
                Label = label,
                ActivationScope = request.ActivationScope,
                ClockKind = request.ClockKind,
                InitialState = initialState,
                StateBytes = stateBytes,
                LogCaptureLevel = logLevelText ?? DebugLogCaptureLevels.Off,
                LogFilterContains = request.LogCapture == null ? string.Empty : request.LogCapture.MessageContains ?? string.Empty,
                LogSignal = request.LogCapture == null ? string.Empty : request.LogCapture.Signal ?? string.Empty
            };
        }

        /// <summary>执行 create 的唯一 effect：建立定义并原子发布。</summary>
        internal static DebugSessionCreateResult ApplyCreate(DebugSessionCreatePlan plan)
        {
            return ApplyCreate(plan, false);
        }

        /// <summary>
        /// 执行 create 的唯一 effect；restoredFromRecipe 只表达“该 identity 由 recipe 建立、当前不带任何活动资源”，
        /// 不改变建立顺序、不执行任何业务代码，也不重放 recipe 里的操作。
        /// </summary>
        internal static DebugSessionCreateResult ApplyCreate(DebugSessionCreatePlan plan, bool restoredFromRecipe)
        {
            SyncObservedState();
            var session = Registry();
            var outcome = session.Create(
                plan.Label,
                plan.ActivationScope,
                plan.ClockKind,
                plan.InitialState,
                plan.StateBytes,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                DebugEventClocks.MonotonicMilliseconds,
                EditorApplication.isPlaying,
                restoredFromRecipe);
            if (!outcome.Success) throw CreateRejection(outcome.Failure, plan.Label, plan.ActivationScope, plan.StateBytes, outcome.SessionsUsed, EditorApplication.isPlaying);
            var logSubscribed = false;
            string logFailure = null;
            if (!string.Equals(plan.LogCaptureLevel, DebugLogCaptureLevels.Off, StringComparison.Ordinal))
            {
                logFailure = DebugListenerHost.RegisterSession(outcome.Definition.SessionId, plan.LogCaptureLevel, plan.LogFilterContains, plan.LogSignal);
                logSubscribed = logFailure == null;
            }
            if (logSubscribed) RegisterLogCaptureCleanup(outcome.Definition.SessionId);
            var publication = Publish("create");
            return new DebugSessionCreateResult
            {
                SessionId = outcome.Definition.SessionId,
                Label = outcome.Definition.Label,
                ActivationScope = outcome.Definition.ActivationScope,
                ClockKind = outcome.Definition.ClockKind,
                State = outcome.Definition.State,
                Revision = outcome.Definition.Revision,
                CreatedAtUtc = outcome.Definition.CreatedAtUtc,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                RuntimeId = RuntimeId(),
                DomainGeneration = DomainGeneration(),
                InitialStateBytes = outcome.Definition.InitialStateBytes,
                CleanupPending = outcome.Definition.CleanupPending,
                StorePublication = publication,
                LogCaptureSubscribed = logSubscribed,
                LogCaptureLevel = string.Equals(plan.LogCaptureLevel, DebugLogCaptureLevels.Off, StringComparison.Ordinal) ? string.Empty : plan.LogCaptureLevel,
                Handles = HandleView(outcome.Definition),
                Budget = Budget(outcome.Definition.SessionId, session)
            };
        }

        /// <summary>
        /// 日志捕获清理条目：stop 与停止后的重复清理都释放同一订阅，
        /// 使“最后一个启用捕获的 session 释放”成为退订的唯一条件。
        /// </summary>
        private static void RegisterLogCaptureCleanup(string sessionId)
        {
            var order = UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.CloseObservation);
            var failure = DebugSessionHost.RegisterCleanupStep(sessionId, order, "log-capture:" + sessionId, UnityCliDebugFacilityOwners.Observation, report =>
            {
                var released = DebugListenerHost.ReleaseSession(sessionId);
                if (report != null) report.ClosedObservations++;
                if (released) UnityCliBridge.WriteLog("debug_log_capture_released session=" + sessionId);
                return true;
            });
            if (failure != null) UnityCliBridge.WriteLog("debug_log_capture_cleanup_step_reused session=" + sessionId + " reason=" + failure);
        }

        /// <summary>句柄与注入计数来自唯一触发预算账本；未注册设施时才报零。</summary>
        private static DebugSessionHandleView HandleView(UnityCliDebugSessionDefinition definition)
        {
            var report = DebugTriggerBudget.Describe(definition.SessionId);
            return new DebugSessionHandleView
            {
                Resources = definition.ActiveResourceCount,
                Waits = 0L,
                Injections = Convert.ToInt64(report["injectionsPlanned"], CultureInfo.InvariantCulture),
                Listeners = Convert.ToInt64(report["listenersPlanned"], CultureInfo.InvariantCulture),
                Detours = 0L,
                OwnerRegistered = HasRegisteredOwner(definition),
                Note = string.Empty
            };
        }

        // ------------------------------------------------------------------
        // stop
        // ------------------------------------------------------------------

        /// <summary>stop 的 effect 前校验；未知 session 在这里失败。</summary>
        internal static DebugSessionStopPlan PrepareStop(DebugSessionStopRequest request)
        {
            SyncObservedState();
            var session = Registry();
            var sessionId = request == null ? null : request.SessionId;
            if (session.Find(sessionId) == null) throw MissingSession(sessionId);
            return new DebugSessionStopPlan { SessionId = sessionId };
        }

        /// <summary>执行 stop：关闭未来派发、运行清理管线，并在状态变化时发布。
        ///
        /// offMainStop 表示本次 stop 由主线程暂停的控制通道服务：同一管线只把需要主线程的清理条目推迟为 pending，
        /// 因此不会出现第二套 stop 语义，也不会出现跨线程执行主线程专属清理的分支。
        /// </summary>
        internal static DebugSessionStopResult ApplyStop(DebugSessionStopPlan plan, bool offMainStop = false)
        {
            SyncObservedState();
            var session = Registry();
            var outcome = session.Stop(plan.SessionId, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), DebugEventClocks.MonotonicMilliseconds, Thread.CurrentThread.ManagedThreadId, offMainStop);
            if (!outcome.Success) throw MissingSession(plan.SessionId);
            var publication = outcome.Changed ? Publish("stop") : session.Publication;
            // 发布走到了 off-main 分支时，主线程专属的恢复事实还没写：如实把它作为未完成条目报告，
            // 而不是静默丢弃，也不把它计成已完成。
            if (DebugSessionStore.RecoveryFactsPending) outcome.Report.UnmanagedFacts.Add(DebugSessionStore.RecoveryFactsDeferredFact());
            // 同一条推迟事实必须进入单一待清理真值：报告 pending、定义的待清理状态与 remove 判定都读它。
            // 只有本次真的在 off-main 上发布的 stop 才拥有这条未完成条目，因此不会被别人的旧推迟误标。
            if (offMainStop && outcome.Changed && DebugSessionStore.RecoveryFactsPending)
                session.MarkDeferredCleanupEntryPending(outcome.Definition, outcome.Report);
            return new DebugSessionStopResult
            {
                SessionId = outcome.Definition.SessionId,
                Label = outcome.Definition.Label,
                State = outcome.Definition.State,
                Revision = outcome.Definition.Revision,
                Changed = outcome.Changed,
                CleanupPending = outcome.Definition.CleanupPending,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                RuntimeId = RuntimeId(),
                DomainGeneration = DomainGeneration(),
                StorePublication = publication,
                Cleanup = DebugSessionCleanupView.From(outcome.Report),
                Budget = Budget(outcome.Definition.SessionId, session)
            };
        }

        /// <summary>
        /// 主线程暂停期间由控制通道服务的 session stop。
        ///
        /// 它不新增第二套 stop：入口仍是同一 PrepareStop/ApplyStop、同一清理管线与同一报告，
        /// 只把“本次服务不在主线程上”告知 owner，使需要主线程的清理条目与主线程专属的存储恢复事实
        /// 都在 effect 之前被推迟，由线程恢复后的下一次主线程发布补完。
        /// 调用方必须先证明该 session 正是持有暂停现场的 owner。
        /// </summary>
        internal static DebugSessionStopResult StopFromPauseControl(DebugSessionStopRequest request)
        {
            var plan = PrepareStop(request);
            return ApplyStop(plan, true);
        }

        // ------------------------------------------------------------------
        // remove
        // ------------------------------------------------------------------

        /// <summary>remove 的 effect 前校验；未完成清理时在这里失败并给出待清理明细。</summary>
        internal static DebugSessionRemovePlan PrepareRemove(DebugSessionRemoveRequest request)
        {
            SyncObservedState();
            var session = Registry();
            var sessionId = request == null ? null : request.SessionId;
            string state;
            bool cleanupPending;
            int activeResourcesUsed;
            var failure = session.CanRemove(sessionId, out state, out cleanupPending, out activeResourcesUsed);
            if (failure != null) throw RemoveRejection(session, failure, sessionId, state, cleanupPending, activeResourcesUsed);
            return new DebugSessionRemovePlan { SessionId = sessionId };
        }

        /// <summary>执行 remove：删除定义并原子发布。</summary>
        internal static DebugSessionRemoveResult ApplyRemove(DebugSessionRemovePlan plan)
        {
            SyncObservedState();
            var session = Registry();
            var outcome = session.Remove(plan.SessionId);
            if (!outcome.Success) throw RemoveRejection(session, outcome.Failure, outcome.SessionId, outcome.State, outcome.CleanupPending, outcome.ActiveResourcesUsed);
            var publication = Publish("remove");
            var capacity = session.Capacity;
            return new DebugSessionRemoveResult
            {
                SessionId = outcome.SessionId,
                Removed = true,
                State = outcome.State,
                SessionsUsed = outcome.SessionsUsed,
                SessionsRemaining = Math.Max(0, capacity.MaxSessions - outcome.SessionsUsed),
                MaxSessions = capacity.MaxSessions,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DomainGeneration = DomainGeneration(),
                StorePublication = publication
            };
        }

        // ------------------------------------------------------------------
        // info / list（纯读取）
        // ------------------------------------------------------------------

        /// <summary>debug.session.info：读取一个定义的完整事实；不写存储、不改变 Unity 状态。</summary>
        internal static DebugSessionInfoResult Info(DebugSessionInfoRequest request)
        {
            SyncObservedState();
            var session = Registry();
            var sessionId = request == null ? null : request.SessionId;
            var definition = session.Find(sessionId);
            if (definition == null) throw MissingSession(sessionId);
            return new DebugSessionInfoResult
            {
                Session = DebugSessionSummaryView.From(definition, HasRegisteredOwner(definition),
                    (long)DebugTriggerBudget.CountOfKind(definition.SessionId, DebugTriggerBudget.KindListener),
                    (long)(DebugTriggerBudget.CountOfKind(definition.SessionId, DebugTriggerBudget.KindInjection) + DebugTriggerBudget.CountOfKind(definition.SessionId, DebugTriggerBudget.KindInput))),
                Cleanup = DebugSessionCleanupView.From(session.DescribeCleanup(definition.SessionId, Thread.CurrentThread.ManagedThreadId)),
                LastRun = DebugRunHost.DescribeLastRun(definition.SessionId),
                Budget = Budget(definition.SessionId, session),
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                RuntimeId = RuntimeId(),
                DomainGeneration = DomainGeneration(),
                StorePublication = session.Publication,
                StoreUsable = storeUsable,
                StoreReason = storeReason
            };
        }

        /// <summary>debug.session.list：按 session 序号分页读取定义摘要；不写存储、不改变 Unity 状态。</summary>
        internal static DebugSessionListResult List(DebugSessionListRequest request)
        {
            SyncObservedState();
            var session = Registry();
            var capacity = session.Capacity;
            var definitions = session.Sessions();
            var editorSessionId = UnityCliBridge.EditorSessionId();
            var scope = UnityCliContinuationToken.Fingerprint(editorSessionId + "\0debug.session.list");
            var offset = 0;
            if (request != null && !string.IsNullOrEmpty(request.Cursor))
            {
                string position;
                if (!UnityCliContinuationToken.TryParse(request.Cursor, scope, editorSessionId, out position)
                    || !int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset)
                    || offset < 0 || offset > definitions.Count)
                    throw new BridgeCommandException(
                        "E_CONTINUATION_STALE",
                        "The session list continuation does not match the current Editor session or the loaded store.",
                        null,
                        new[] { "Start a new debug.session.list read without a cursor." });
            }
            var limit = request == null || request.Limit == null
                ? capacity.ListDefaultLimit
                : Math.Min(Math.Max(1, request.Limit.Value), capacity.ListMaxLimit);
            var page = new List<DebugSessionSummaryView>();
            for (var index = offset; index < definitions.Count && page.Count < limit; index++)
            {
                page.Add(DebugSessionSummaryView.From(definitions[index], HasRegisteredOwner(definitions[index]),
                    (long)DebugTriggerBudget.CountOfKind(definitions[index].SessionId, DebugTriggerBudget.KindListener),
                    (long)(DebugTriggerBudget.CountOfKind(definitions[index].SessionId, DebugTriggerBudget.KindInjection) + DebugTriggerBudget.CountOfKind(definitions[index].SessionId, DebugTriggerBudget.KindInput))));
            }
            var nextOffset = offset + page.Count;
            return new DebugSessionListResult
            {
                Items = page,
                NextCursor = nextOffset < definitions.Count ? UnityCliContinuationToken.Create(scope, editorSessionId, nextOffset.ToString(CultureInfo.InvariantCulture)) : null,
                Total = definitions.Count,
                NextOffset = nextOffset,
                HasMore = nextOffset < definitions.Count,
                EditorSessionId = editorSessionId,
                RuntimeId = RuntimeId(),
                DomainGeneration = DomainGeneration(),
                StorePublication = session.Publication,
                StoreUsable = storeUsable,
                StoreReason = storeReason,
                Budget = Budget(null, session)
            };
        }

        // ------------------------------------------------------------------
        // 设施入口：保留槽位、附着、释放与有序清理条目
        // ------------------------------------------------------------------

        /// <summary>
        /// 设施执行 effect 之前必须先保留一个活动资源槽位；失败时返回失败原因，
        /// 调用方不得执行 effect。成功时 resourceId 是该资源的稳定身份。
        /// </summary>
        internal static UnityCliDebugResourceReservation ReserveResource(string sessionId, string kind, string target, Func<UnityCliDebugCleanupReport, bool> cleanup, bool requiresMainThread = false)
        {
            SyncObservedState();
            return Registry().Reserve(sessionId, kind, target, RuntimeId(), cleanup, requiresMainThread);
        }

        /// <summary>设施完成 effect 后把保留的槽位标记为活动。</summary>
        internal static bool AttachResource(string resourceId)
        {
            SyncObservedState();
            return Registry().Attach(resourceId);
        }

        /// <summary>设施正常释放资源；释放后该记录不再参与清理。</summary>
        internal static bool ReleaseResource(string resourceId)
        {
            SyncObservedState();
            return Registry().Release(resourceId);
        }

        /// <summary>
        /// 该 session 当前是否允许新的派发。
        /// 命中路径可能运行在任意托管线程上，而 stop 一开始就把定义置为关闭，
        /// 因此清理期间任何线程的命中都不会再进入用户代码。
        /// </summary>
        internal static bool IsDispatchOpen(string sessionId)
        {
            return !string.IsNullOrEmpty(sessionId) && Registry().IsDispatchOpen(sessionId);
        }

        /// <summary>设施注册显式有限清理条目；返回 null 表示注册成功，否则返回稳定失败原因。</summary>
        internal static string RegisterCleanupStep(string sessionId, int order, string name, string owner, Func<UnityCliDebugCleanupReport, bool> apply, bool requiresMainThread = false)
        {
            SyncObservedState();
            return Registry().RegisterCleanupStep(sessionId, order, name, owner, apply, requiresMainThread);
        }

        // ------------------------------------------------------------------
        // 观察、加载与发布
        // ------------------------------------------------------------------

        /// <summary>
        /// 每次公开调用前观察 Play 段边界。
        /// 只有可证明的主线程路径才读取并发布 EditorApplication.isPlaying；off-main 命中/投递路径只读取该已发布值，
        /// 绝不读取主线程专属 Unity API。
        /// 进入 Play 时建立新 runtime 段；退出 Play 时把上一段建立的活动资源置为失效并标记待清理。
        /// 这里只更新内存事实，不写存储文件。
        /// </summary>
        private static void SyncObservedState()
        {
            if (DebugEventClocks.IsCurrentThreadMainThread()) observedIsPlaying = EditorApplication.isPlaying;
            var playing = observedIsPlaying;
            lock (Sync)
            {
                if (playing)
                {
                    if (!string.IsNullOrEmpty(playSegmentId))
                    {
                        observedRuntimeSegment = playSegmentId;
                        return;
                    }
                    playSegmentIndex++;
                    playSegmentId = "runtime-" + playSegmentIndex.ToString(CultureInfo.InvariantCulture);
                    observedRuntimeSegment = playSegmentId;
                    UnityCliBridge.WriteLog("debug_session_play_segment runtimeId=" + playSegmentId);
                    return;
                }
                observedRuntimeSegment = string.Empty;
                if (string.IsNullOrEmpty(playSegmentId)) return;
                var ended = playSegmentId;
                playSegmentId = string.Empty;
                if (registry == null) return;
                var invalidated = registry.InvalidatePlayScopedResources(ended);
                // Play 退出同时请求释放该 Play 段建立的虚拟输入：无法确认释放的部分保持 pending。
                DebugInputHost.OnPlayExit(ended);
                if (invalidated > 0)
                    UnityCliBridge.WriteLog("debug_session_play_exit runtimeId=" + ended + " invalidated=" + invalidated.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// 本 domain 唯一的注册表。首次访问时无锁读取官方文件，并按 fail-closed 规则决定是否加载；
        /// 不可用时只记录一条有界日志，其他 Bridge 能力与该文件无关。
        /// </summary>
        private static UnityCliDebugSessionRegistry Registry()
        {
            lock (Sync)
            {
                if (registry != null) return registry;
                var capacity = Capacity();
                var observed = DebugSessionStore.Observe();
                observation = observed;
                if (!observed.Present)
                {
                    // 官方路径没有文件等价于空存储：没有任何受影响定义，也没有不可用文件。
                    registry = new UnityCliDebugSessionRegistry(capacity);
                    storeUsable = true;
                    storeReason = string.Empty;
                    return registry;
                }
                string reason = null;
                if (observed.Bytes == 0L) reason = "file-empty";
                else if (observed.Bytes > capacity.MaxStoreBytes) reason = "file-over-budget";
                else if (!observed.Parsable) reason = "file-unparsable";
                UnityCliDebugSessionRegistry restored = null;
                if (reason == null)
                {
                    string restoreFailure;
                    var accepted = new List<string>();
                    var earlier = DebugSessionStore.PublishedGeneration();
                    if (!string.IsNullOrEmpty(earlier)) accepted.Add(earlier);
                    if (!UnityCliDebugSessionRegistry.TryRestore(observed.Payload, capacity, UnityCliBridge.EditorSessionId(), DomainGeneration(), accepted, DebugSessionStore.PublicationFloor(), out restored, out restoreFailure))
                        reason = restoreFailure;
                }
                if (reason == null)
                {
                    registry = restored;
                    storeUsable = true;
                    storeReason = string.Empty;
                    return registry;
                }
                registry = new UnityCliDebugSessionRegistry(capacity);
                storeUsable = false;
                storeReason = reason;
                if (!string.Equals(loggedReason, reason, StringComparison.Ordinal))
                {
                    loggedReason = reason;
                    DebugSessionStore.LogUnusable(reason);
                }
                return registry;
            }
        }

        /// <summary>发布当前注册表并返回新的 publication；失败时抛出稳定 E_DEBUG_STORE_FAILED。</summary>
        private static long Publish(string reason)
        {
            lock (Sync)
            {
                var publication = Math.Max(registry.Publication, DebugSessionStore.PublicationFloor()) + 1L;
                var editorSessionId = UnityCliBridge.EditorSessionId();
                var generation = DomainGeneration();
                var payload = registry.CapturePayload(editorSessionId, generation, publication);
                var bytes = DebugSessionStore.Publish(payload, observation, editorSessionId, generation, publication);
                // 发布成功：后续语义 CAS 以刚写入的文件为期望值。
                observation = new DebugSessionStoreObservation
                {
                    Present = true,
                    Parsable = true,
                    EditorSessionId = editorSessionId,
                    DomainGeneration = generation,
                    Publication = publication,
                    Bytes = bytes
                };
                storeUsable = true;
                storeReason = string.Empty;
                loggedReason = string.Empty;
                UnityCliBridge.WriteLog("debug_session_publish reason=" + reason + " sessions=" + registry.SessionCount.ToString(CultureInfo.InvariantCulture)
                    + " publication=" + publication.ToString(CultureInfo.InvariantCulture) + " bytes=" + bytes.ToString(CultureInfo.InvariantCulture));
                return publication;
            }
        }

        // ------------------------------------------------------------------
        // 投影与错误映射
        // ------------------------------------------------------------------

        /// <summary>该定义是否注册了任何清理条目；决定句柄摘要是否声明 owner 已注册。</summary>
        private static bool HasRegisteredOwner(UnityCliDebugSessionDefinition definition)
        {
            foreach (var step in definition.CleanupSteps)
            {
                if (step.Apply != null && !string.IsNullOrEmpty(step.Owner)) return true;
            }
            return false;
        }

        private static DebugSessionBudgetView Budget(string sessionId, UnityCliDebugSessionRegistry session)
        {
            var capacity = session.Capacity;
            var used = (long)session.SessionCount;
            var active = sessionId == null ? 0L : (long)session.ActiveResourceCount(sessionId);
            var bytes = observation == null ? 0L : observation.Bytes;
            return new DebugSessionBudgetView
            {
                SessionsUsed = used,
                SessionsRemaining = Math.Max(0L, (long)capacity.MaxSessions - used),
                MaxSessions = capacity.MaxSessions,
                ActiveResourcesUsed = active,
                ActiveResourcesRemaining = Math.Max(0L, (long)capacity.MaxActiveResourcesPerSession - active),
                MaxActiveResourcesPerSession = capacity.MaxActiveResourcesPerSession,
                StoreBytesUsed = bytes,
                StoreBytesRemaining = Math.Max(0L, capacity.MaxStoreBytes - bytes),
                MaxStoreBytes = capacity.MaxStoreBytes,
                StorePublication = session.Publication
            };
        }

        private static BridgeCommandException MissingSession(string sessionId)
        {
            return new BridgeCommandException(
                "E_DEBUG_SESSION_NOT_FOUND",
                "No loaded session definition has that sessionId in the current Editor session.",
                new Dictionary<string, object>
                {
                    { "sessionId", sessionId ?? string.Empty },
                    { "storeUsable", storeUsable },
                    { "storeReason", storeReason }
                },
                new[] { "Run tools run debug.session.list to read the currently loaded session definitions." });
        }

        private static BridgeCommandException CreateRejection(string failure, string label, string activationScope, long stateBytes, int sessionsUsed, bool isPlaying)
        {
            var capacity = Capacity();
            if (string.Equals(failure, UnityCliDebugSessionFailures.StateTooLarge, StringComparison.Ordinal))
            {
                return new BridgeCommandException(
                    "E_DEBUG_LIMIT_EXCEEDED",
                    "Initial state exceeds the declared per-session byte budget, so no definition was created.",
                    new Dictionary<string, object>
                    {
                        { "limit", "session-state-bytes" }, { "used", stateBytes }, { "max", (long)capacity.MaxStateBytes }, { "label", label ?? string.Empty }
                    },
                    new[]
                    {
                        "Send a smaller initialState payload; tools describe debug.session.create reports the per-session state budget.",
                        "Run tools run debug.session.list to read the current session budget usage."
                    });
            }
            if (string.Equals(failure, UnityCliDebugSessionFailures.LimitExceeded, StringComparison.Ordinal))
            {
                return new BridgeCommandException(
                    "E_DEBUG_LIMIT_EXCEEDED",
                    "The session definition limit is already reached, so no definition was created.",
                    new Dictionary<string, object>
                    {
                        { "limit", "sessions" }, { "used", (long)sessionsUsed }, { "max", (long)capacity.MaxSessions }, { "sessions", SessionDetail() }
                    },
                    new[]
                    {
                        "Run tools run debug.session.remove for a stopped session you no longer need, then retry this create.",
                        "Run tools run debug.session.list to read the current session definitions."
                    });
            }
            if (string.Equals(failure, UnityCliDebugSessionFailures.DuplicateLabel, StringComparison.Ordinal))
            {
                return new BridgeCommandException(
                    "E_DEBUG_SESSION_LABEL_CONFLICT",
                    "The label is already used by another session definition in this Editor session.",
                    new Dictionary<string, object>
                    {
                        { "label", label ?? string.Empty }, { "sessions", SessionDetail() }
                    },
                    new[]
                    {
                        "Choose a label that no current session definition uses.",
                        "Run tools run debug.session.list to read the current labels."
                    });
            }
            if (string.Equals(failure, UnityCliDebugSessionFailures.ScopeConflict, StringComparison.Ordinal))
            {
                var nextActions = isPlaying
                    ? new[]
                    {
                        "Create the session with activationScope editor-and-play, or stop Play Mode before creating an edit-only session.",
                        "Run tools run editor.play-mode.stop, wait until editor.state reports Edit Mode, then create the session again."
                    }
                    : new[]
                    {
                        "Create the session with activationScope editor-and-play, or run tools run editor.play-mode.start first.",
                        "Run tools run debug.session.list to read the current session definitions."
                    };
                return new BridgeCommandException(
                    "E_DEBUG_SESSION_SCOPE_CONFLICT",
                    "The requested activation scope cannot be activated in the current editor mode, so the definition was rejected.",
                    new Dictionary<string, object>
                    {
                        { "activationScope", activationScope ?? string.Empty }, { "editorMode", isPlaying ? "play" : "edit" }, { "sessions", SessionDetail() }
                    },
                    nextActions);
            }
            return InvalidField("label", "non-empty string", "empty");
        }

        private static BridgeCommandException RemoveRejection(UnityCliDebugSessionRegistry session, string failure, string sessionId, string state, bool cleanupPending, int activeResourcesUsed)
        {
            if (string.Equals(failure, UnityCliDebugSessionFailures.MissingSession, StringComparison.Ordinal)) return MissingSession(sessionId);
            var detail = new Dictionary<string, object>
            {
                { "sessionId", sessionId ?? string.Empty },
                { "state", state ?? string.Empty },
                { "cleanupPending", cleanupPending },
                { "activeResourcesUsed", (long)activeResourcesUsed }
            };
            if (cleanupPending)
                detail["cleanup"] = DebugSessionCleanupView.From(session.DescribeCleanup(sessionId, Thread.CurrentThread.ManagedThreadId));
            var nextActions = new[]
            {
                string.Equals(state, UnityCliDebugSessionStates.Open, StringComparison.Ordinal)
                    ? "Run tools run debug.session.stop for this session before removing it."
                    : "Run tools run debug.session.stop for this session to run or repeat the cleanup pipeline.",
                "Run tools run debug.session.info to read the pending cleanup detail."
            };
            return new BridgeCommandException(
                "E_DEBUG_CLEANUP_PENDING",
                "The session still owns cleanup that has not completed, so it cannot be removed.",
                detail,
                nextActions);
        }

        private static BridgeCommandException InvalidField(string field, string expected, string actual)
        {
            return new BridgeCommandException(
                "E_SCHEMA_INVALID",
                "Field " + field + " is invalid: expected " + expected + ".",
                new Dictionary<string, object> { { "path", "$." + field }, { "expected", expected }, { "actual", actual } },
                new[] { "Run tools describe debug.session.create to read the field contract, then submit the corrected input." });
        }

        /// <summary>有界的当前定义摘要；只用于拒绝路径的 details，不返回完整清理报告。</summary>
        private static List<object> SessionDetail()
        {
            var detail = new List<object>();
            foreach (var definition in Registry().Sessions())
            {
                if (detail.Count >= DebugLimits.MaxSessions) break;
                detail.Add(new Dictionary<string, object>
                {
                    { "sessionId", definition.SessionId },
                    { "label", UnityCliBridge.BoundedText(definition.Label, MaxDiagnosticBytes) },
                    { "state", definition.State },
                    { "cleanupPending", definition.CleanupPending }
                });
            }
            return detail;
        }
    }
}
#endif
