#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// program 的中央调度与执行点派发。它由唯一主循环与生成的 binding adapter 调用，
    /// 也是 program 拥有的活动资源在运行期被释放的唯一入口。
    /// </summary>
    internal static partial class DebugProgramHost
    {
        /// <summary>超期运行统一关闭执行权；中央 scheduler 每 tick 调用一次。</summary>
        internal static void CancelExpiredRuns()
        {
            var session = Registry();
            var nowUtc = UtcMilliseconds();
            // 已排定但从未取得执行权的运行：先记住它们，再让 session 扫描关闭执行权，绝不迟到执行。
            var pendingExpired = session.ActiveRuns()
                .Where(candidate => string.Equals(candidate.State, UnityCliDebugProgramRunStates.Scheduled, StringComparison.Ordinal) && nowUtc > candidate.DeadlineUtcMs)
                .ToList();
            var cancelled = session.CancelExpiredRuns(nowUtc);
            if (cancelled > 0) UnityCliBridge.WriteLog("debug_program_pending_expired count=" + cancelled.ToString(CultureInfo.InvariantCulture));
            foreach (var run in pendingExpired) CloseExpiredRun(session, run, nowUtc);
            // 已经取得执行权但超过声明时长的运行：没有同步代码在途时立即关闭，否则只请求取消。
            foreach (var run in session.ActiveRuns().Where(candidate => !string.Equals(candidate.State, UnityCliDebugProgramRunStates.Scheduled, StringComparison.Ordinal)).ToList())
            {
                if (nowUtc <= run.DeadlineUtcMs) continue;
                if (run.InFlight)
                {
                    session.RequestCancellation(run.ProgramRunId, "the run passed its declared durationMs while synchronous code was still in flight");
                    continue;
                }
                CloseExpiredRun(session, run, nowUtc);
            }
        }

        /// <summary>
        /// 关闭一个超期运行的完整事实：终态、driver、detour、资源与生命周期证据。
        /// pending 扫描先发布终态时这里只补同一份证据；无论哪种顺序，关闭恰好发生一次。
        /// </summary>
        private static void CloseExpiredRun(UnityCliDebugProgramRegistry session, UnityCliDebugProgramRun run, double nowUtc)
        {
            var definition = session.Find(run.ProgramId);
            if (definition != null) RunCleanupEntry(definition, run, "duration-expired");
            session.MarkCodeReturned(run.ProgramRunId);
            var published = session.CompleteRun(run.ProgramRunId, UnityCliDebugProgramRunStates.Cancelled, "E_DEBUG_PROGRAM_RUN_EXPIRED", "The run reached its declared durationMs, so its future steps were closed.", nowUtc);
            DebugScheduler.CloseDriver(run.ProgramId);
            session.MarkStepDriverPlanted(run.ProgramId, false);
            if (definition != null)
            {
                DetachBindings(definition);
                if (!string.Equals(definition.State, UnityCliDebugProgramStates.Stopped, StringComparison.Ordinal)) session.MarkDormant(run.ProgramId);
            }
            ReleaseRunResource(run.ProgramId);
            // 超期终态与步骤终态使用同一生命周期证据，不能因为取消来自 deadline 就没有事件。
            if (published || string.Equals(run.FailureCode, "E_DEBUG_PROGRAM_RUN_EXPIRED", StringComparison.Ordinal))
                AppendLifecycle(definition, run, UnityCliDebugProgramRunStates.Cancelled, "E_DEBUG_PROGRAM_RUN_EXPIRED");
        }

        /// <summary>
        /// 推进一个 step driver 的一个有界步骤；每个 tick 至多一步。
        /// driver 是它自己那一次运行唯一的步骤推进者，因此步骤权允许 scheduled→running 与
        /// running→running；终态、取消、超期与在途重入都在取得步骤权之前判定，绝不静默停摆。
        /// </summary>
        internal static void AdvanceDriver(DebugScheduler.DriverState driver)
        {
            if (driver == null) return;
            var session = Registry();
            var run = session.FindRun(driver.ProgramRunId);
            var definition = session.Find(driver.ProgramId);
            if (run == null || definition == null || run.IsTerminal)
            {
                CloseDriver(definition, driver.ProgramId);
                return;
            }
            // session 停止后第 1 阶段已关闭未来入口：不得再进入一步用户代码，也不得静默停摆。
            if (!DebugSessionHost.IsDispatchOpen(definition.SessionId))
            {
                session.RequestCancellation(run.ProgramRunId, "The owning session closed its dispatch, so this run was asked to cancel.");
                CloseDriver(definition, driver.ProgramId);
                return;
            }
            if (run.CancellationRequested)
            {
                // 协作取消只在步骤边界生效：这里先确定终态，再关闭 driver，不留下无推进者的活动运行。
                RunCleanupEntry(definition, run, "cancelled");
                FinishDriver(driver, definition, run, UnityCliDebugProgramRunStates.Cancelled, "E_DEBUG_PROGRAM_RUN_CANCELLED", "The driver stopped cooperatively at a step boundary after a cancellation request.");
                return;
            }
            if (UtcMilliseconds() > run.DeadlineUtcMs)
            {
                StopDriverByDeadline(driver);
                return;
            }
            if (!session.TryBeginStep(run.ProgramRunId, UtcMilliseconds(), Thread.CurrentThread.ManagedThreadId))
            {
                // 已终态或仍在途的同步代码持有执行权：本 tick 不推进，下一个 tick 重新判定。
                if (run.IsTerminal) CloseDriver(definition, driver.ProgramId);
                return;
            }
            var entry = definition.FindEntry(driver.Entry);
            if (entry == null)
            {
                FinishDriver(driver, definition, run, UnityCliDebugProgramRunStates.Failed, "E_DEBUG_PROGRAM_ENTRY_MISSING", "The driver entry is no longer part of the definition.");
                return;
            }
            driver.StepIndex++;
            try
            {
                if (string.Equals(entry.Category, UnityCliDebugProgramEntryCategories.Step, StringComparison.Ordinal))
                {
                    var result = InvokeStep(definition, run, entry, driver.StepIndex, driver.Phase);
                    session.RecordStep(run.ProgramRunId, run.Revision, false);
                    DebugScheduler.RecordStepWithRevision(driver, definition.Revision);
                    if (result.State != null)
                    {
                        var epoch = result.StateEpoch >= 0L ? result.StateEpoch : definition.StateEpoch;
                        var bytes = Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(result.State));
                        long updated;
                        var failure = session.TryUpdateState(definition.ProgramId, epoch, result.State, bytes, out updated);
                        if (failure != null) RecordFault(definition, run, failure, "A step state candidate was refused; the previous configuration and revision state were kept.");
                    }
                    if (result.Completed)
                    {
                        session.MarkCodeReturned(run.ProgramRunId);
                        FinishDriver(driver, definition, run, UnityCliDebugProgramRunStates.Completed, string.Empty, string.Empty);
                        return;
                    }
                    // 步骤调用已经返回：在两次步骤之间该运行不再有在途同步代码，只保留其活动状态。
                    session.MarkCodeReturned(run.ProgramRunId);
                    // 后续 step 在 step 返回后的安全点采用当时的最新 revision。
                    DebugScheduler.ApplyWait(driver, result.Wait, DebugEventClocks.MonotonicMilliseconds);
                    return;
                }
                if (string.Equals(entry.Category, UnityCliDebugProgramEntryCategories.OneShot, StringComparison.Ordinal))
                {
                    var value = InvokeOneShot(definition, run, entry, driver.Phase);
                    session.MarkCodeReturned(run.ProgramRunId);
                    session.RecordResult(run.ProgramRunId, value);
                    FinishDriver(driver, definition, run, UnityCliDebugProgramRunStates.Completed, string.Empty, string.Empty);
                    return;
                }
                FinishDriver(driver, definition, run, UnityCliDebugProgramRunStates.Failed, "E_DEBUG_PROGRAM_ENTRY_INVALID", "Only a step or oneshot entry can drive a main-thread run.");
            }
            catch (BridgeCommandException exception)
            {
                session.MarkCodeReturned(run.ProgramRunId);
                FinishDriver(driver, definition, run, UnityCliDebugProgramRunStates.Failed, exception.Code, exception.Message);
            }
            catch (Exception exception)
            {
                session.MarkCodeReturned(run.ProgramRunId);
                FinishDriver(driver, definition, run, UnityCliDebugProgramRunStates.Failed, "E_DEBUG_PROGRAM_ENTRY_FAILED", exception.GetType().Name + ": " + exception.Message);
            }
        }

        /// <summary>driver 超过声明时长：运行清理 entry 并发布 cancelled。</summary>
        internal static void StopDriverByDeadline(DebugScheduler.DriverState driver)
        {
            if (driver == null) return;
            var session = Registry();
            var run = session.FindRun(driver.ProgramRunId);
            var definition = session.Find(driver.ProgramId);
            if (run == null || definition == null)
            {
                CloseDriver(definition, driver.ProgramId);
                return;
            }
            RunCleanupEntry(definition, run, "duration-expired");
            FinishDriver(driver, definition, run, UnityCliDebugProgramRunStates.Cancelled, "E_DEBUG_PROGRAM_RUN_EXPIRED", "The run reached its declared durationMs and was closed as cancelled.");
        }

        /// <summary>等待计划超时：关闭 driver 并发布 cancelled，不迟到执行剩余步骤。</summary>
        internal static void StopDriverByWaitTimeout(DebugScheduler.DriverState driver)
        {
            if (driver == null) return;
            var session = Registry();
            var run = session.FindRun(driver.ProgramRunId);
            var definition = session.Find(driver.ProgramId);
            if (run == null || definition == null)
            {
                CloseDriver(definition, driver.ProgramId);
                return;
            }
            RunCleanupEntry(definition, run, "wait-timeout");
            FinishDriver(driver, definition, run, UnityCliDebugProgramRunStates.Cancelled, "E_DEBUG_PROGRAM_WAIT_TIMEOUT", "A declared wait plan was not satisfied inside its timeout, so the driver stopped.");
        }

        /// <summary>
        /// 生成 adapter 在 exact 方法命中时的入口。method callback 当场同步执行；
        /// 命中线程不是主线程时也只是同一线程上的一次调用，绝不把 continuation 搬到主线程。
        /// 本方法可能在任意受支持托管线程上运行，因此不得调用任何主线程专属 Unity API。
        /// </summary>
        internal static bool DispatchMethod(string programId, string entry, UnityCliHookContext context, bool isPrefix)
        {
            var session = Registry();
            var definition = session.Find(programId);
            if (definition == null || context == null) return true;
            if (string.Equals(definition.State, UnityCliDebugProgramStates.Stopped, StringComparison.Ordinal)) return true;
            var resolved = FindResolved(programId, entry);
            if (resolved == null) return true;
            var threadId = Thread.CurrentThread.ManagedThreadId;
            var mainThread = DebugEventClocks.IsCurrentThreadMainThread();
            var phase = isPrefix ? UnityCliDebugProgramBindingPhases.Prefix : UnityCliDebugProgramBindingPhases.Postfix;
            if (!definition.Bindings.Any(binding => binding.Attached
                && string.Equals(binding.Phase, phase, StringComparison.Ordinal)
                && string.Equals(binding.Entry, entry, StringComparison.Ordinal))) return true;
            var site = isPrefix ? UnityCliDebugProgramExecutionSites.MethodPrefix : UnityCliDebugProgramExecutionSites.MethodPostfix;
            session.EnterCallback(programId);
            UnityCliDebugProgramRun claimed = null;
            try
            {
                if (string.Equals(resolved.Entry.Category, UnityCliDebugProgramEntryCategories.Method, StringComparison.Ordinal))
                {
                    // 真正的命中使匹配的 pending 运行取得执行权；未命中任何待运行或竞争失败时仍按绑定激活窗口当场执行。
                    claimed = ClaimDispatchRun(definition, entry, site, threadId);
                    var runtime = new ProgramRuntime(definition, claimed == null ? string.Empty : claimed.ProgramRunId, entry, site, threadId, mainThread, UnityCliDebugProgramExecutionSites.EditorTick);
                    var program = new UnityCliDebugProgramContext(runtime);
                    InvokeMember(resolved, definition.ProgramId, new object[] { new UnityCliDebugInvocation(program, context) });
                    CompleteDispatchRun(session, definition, claimed, UnityCliDebugProgramRunStates.Completed, string.Empty, string.Empty);
                    return true;
                }
                if (string.Equals(resolved.Entry.Category, UnityCliDebugProgramEntryCategories.OneShot, StringComparison.Ordinal))
                {
                    // 定点运行只能由一次真实命中触发，且只执行一次同步调用。
                    ClaimPointRun(definition, resolved, entry, site, threadId, null);
                    return true;
                }
                RecordFault(definition, null, "E_DEBUG_PROGRAM_ENTRY_INVALID", "Binding " + entry + " must declare the method or oneshot entry category to run at a method execution point.");
                return true;
            }
            catch (Exception exception)
            {
                RecordFault(definition, null, "E_DEBUG_PROGRAM_ENTRY_FAILED", exception.GetType().Name + ": " + exception.Message);
                // 入口已取得执行权的运行必须给出真实终态：原代码已经返回，因此这里可以发布 failed。
                CompleteDispatchRun(session, definition, claimed, UnityCliDebugProgramRunStates.Failed, "E_DEBUG_PROGRAM_ENTRY_FAILED", exception.GetType().Name + ": " + exception.Message);
                return true;
            }
            finally
            {
                session.ExitCallback(programId);
                MarkRunsReturned(definition.ProgramId, entry);
            }
        }

        /// <summary>
        /// 一次真实执行点到达时把与该执行点匹配、尚未开始的 dispatch 运行交给本次执行。
        /// pending→running 只有一个赢家；没有匹配运行或竞争失败时返回 null，调用方仍按绑定激活窗口执行入口。
        /// </summary>
        private static UnityCliDebugProgramRun ClaimDispatchRun(UnityCliDebugProgramDefinition definition, string entry, string actualSite, int threadId)
        {
            var session = Registry();
            var pending = session.RunsFor(definition.ProgramId).FirstOrDefault(run =>
                string.Equals(run.State, UnityCliDebugProgramRunStates.Scheduled, StringComparison.Ordinal)
                && string.Equals(run.Entry, entry, StringComparison.Ordinal)
                && MatchesSite(run.ExecutionSite, actualSite, threadId));
            if (pending == null) return null;
            return session.TryBeginRun(pending.ProgramRunId, UtcMilliseconds(), threadId) ? pending : null;
        }

        /// <summary>
        /// 发布一次已取得执行权的 dispatch 运行的终态，并释放它拥有的调度与运行资源。
        /// 终态只在入口真正返回后发布；没有取得执行权的运行什么都不做。
        /// </summary>
        private static void CompleteDispatchRun(UnityCliDebugProgramRegistry session, UnityCliDebugProgramDefinition definition, UnityCliDebugProgramRun run, string terminal, string failureCode, string failureMessage)
        {
            if (run == null) return;
            session.MarkCodeReturned(run.ProgramRunId);
            session.CompleteRun(run.ProgramRunId, terminal, failureCode, failureMessage, UtcMilliseconds());
            DebugScheduler.CloseDriver(definition.ProgramId);
            session.MarkStepDriverPlanted(definition.ProgramId, false);
            ReleaseRunResource(definition.ProgramId);
            if (!string.Equals(definition.State, UnityCliDebugProgramStates.Stopped, StringComparison.Ordinal)) session.MarkDormant(definition.ProgramId);
            AppendLifecycle(definition, run, terminal, string.IsNullOrEmpty(failureCode) ? "dispatch execution point claimed at " + run.ExecutionSite : failureCode);
        }

        /// <summary>
        /// 已绑定执行点上的信号入口：只执行排队中尚未过期的投递；过期的投递永不迟到执行。
        /// host signal 同步派发受递归深度约束，超过上限时本次派发被拒绝并留下稳定事实。
        /// </summary>
        internal static void DispatchSignal(string programId, string entry, string signal, string payloadJson, int threadId)
        {
            if (!TryEnterDeliveryScope()) return;
            try
            {
                var delivery = DebugScheduler.TryClaimDelivery(programId, signal, threadId);
                if (delivery == null) return;
                ExecuteDelivery(delivery, entry);
            }
            finally
            {
                DebugScheduler.ExitSignalScope();
            }
        }

        /// <summary>
        /// 一个已绑定的 exact target 到达执行点时取得投递给它的跨线程操作。
        /// 只有到达的 target 才使 pending 变成 running；未到达的投递只会在过期后留下 expired 事实。
        /// </summary>
        internal static void DispatchDeliveriesForTarget(string targetKey, int threadId)
        {
            if (string.IsNullOrEmpty(targetKey)) return;
            if (!TryEnterDeliveryScope()) return;
            try
            {
                foreach (var delivery in DebugScheduler.TryClaimTargetDeliveries(targetKey, threadId, DebugLimits.MaxDeliveriesPerTick)) ExecuteDelivery(delivery, null);
            }
            finally
            {
                DebugScheduler.ExitSignalScope();
            }
        }

        /// <summary>
        /// 中央 framework phase 上的投递：在声明的那一刻（中央 editor tick 或真实 PlayerLoop phase）
        /// 每个执行点段只执行有界数量；每个投递只被它的声明执行点取得一次。
        /// 执行者由投递自身规定：hook listener 与 program entry 共用本取得路径，因此两者不可能各自拥有一份预算。
        /// </summary>
        internal static void DispatchFrameworkDeliveries(string phase, int threadId, int max)
        {
            if (max <= 0) return;
            if (!TryEnterDeliveryScope()) return;
            try
            {
                foreach (var delivery in DebugScheduler.ClaimFrameworkPhaseDeliveries(phase, threadId, max))
                {
                    if (delivery.HookId.Length != 0) MethodHookHost.ExecutePhaseListenerTrigger(delivery);
                    else ExecuteDelivery(delivery, null);
                }
            }
            finally
            {
                DebugScheduler.ExitSignalScope();
            }
        }

        /// <summary>进入一次 host signal 同步派发；递归链超过上限时拒绝本次派发并记录稳定事实。</summary>
        private static bool TryEnterDeliveryScope()
        {
            if (DebugScheduler.TryEnterSignalScope()) return true;
            UnityCliBridge.WriteLog("debug_program_signal_recursion_refused depth=" + DebugScheduler.SignalDepth.ToString(CultureInfo.InvariantCulture));
            RecordFault(null, null, "E_DEBUG_SIGNAL_RECURSION", "The host signal dispatch chain reached the declared recursion depth, so this delivery was refused instead of executing.");
            return false;
        }

        /// <summary>
        /// 一次已取得执行权的投递的真正执行：当场同步调用声明的 entry，并记录触发事实。
        /// 入口优先采用投递自带的 entry；仅在它不能在本定义解析时回退到调用方声明的 entry。
        /// </summary>
        private static void ExecuteDelivery(DebugScheduler.Delivery delivery, string fallbackEntry)
        {
            var session = Registry();
            var definition = session.Find(delivery.ProgramId);
            if (definition == null) return;
            var entry = definition.FindEntry(delivery.Entry) != null ? delivery.Entry : (fallbackEntry ?? delivery.Entry);
            var resolved = FindResolved(delivery.ProgramId, entry);
            if (resolved == null)
            {
                RecordFault(definition, null, "E_DEBUG_PROGRAM_ENTRY_MISSING", "The resolved entry for signal " + delivery.Signal + " is unavailable in this domain.");
                return;
            }
            var payload = ParseJsonText(delivery.PayloadJson);
            // 投递的执行点是它自己的声明：exact target 命中点，或中央 editor tick / 真实 PlayerLoop phase。
            var frameworkPhase = delivery.TargetKey.Length == 0 ? EffectiveFrameworkPhase(delivery.Phase) : string.Empty;
            var playerLoopPhase = DebugPlayerLoopPhases.IsDefined(frameworkPhase);
            var site = delivery.TargetKey.Length != 0
                ? UnityCliDebugProgramExecutionSites.Signal
                : (playerLoopPhase ? UnityCliDebugProgramExecutionSites.PlayerLoopPhase : UnityCliDebugProgramExecutionSites.EditorTick);
            var threadId = delivery.ExecutionThreadId == 0 ? Thread.CurrentThread.ManagedThreadId : delivery.ExecutionThreadId;
            session.EnterCallback(delivery.ProgramId);
            UnityCliDebugProgramRun claimed = null;
            try
            {
                if (string.Equals(resolved.Entry.Category, UnityCliDebugProgramEntryCategories.Listener, StringComparison.Ordinal))
                {
                    // 投递真正到达才使匹配的 pending 运行取得执行权；未匹配时只按投递自身执行，不声称为哪个运行执行。
                    claimed = ClaimDispatchRun(definition, entry, UnityCliDebugProgramExecutionSites.Signal, threadId);
                    var runtime = new ProgramRuntime(
                        definition,
                        claimed != null ? claimed.ProgramRunId : delivery.ProgramRunId,
                        entry,
                        site,
                        threadId,
                        DebugEventClocks.IsCurrentThreadMainThread(),
                        frameworkPhase);
                    var eventContext = new UnityCliDebugEventContext(runtime, delivery.Signal, payload, delivery.TriggeredThreadId, delivery.TriggeredAtMonotonicMs);
                    InvokeMember(resolved, delivery.ProgramId, new object[] { eventContext });
                    CompleteDispatchRun(session, definition, claimed, UnityCliDebugProgramRunStates.Completed, string.Empty, string.Empty);
                }
                else if (string.Equals(resolved.Entry.Category, UnityCliDebugProgramEntryCategories.OneShot, StringComparison.Ordinal))
                {
                    ClaimPointRun(definition, resolved, entry, UnityCliDebugProgramExecutionSites.Signal, threadId, payload);
                }
                else
                {
                    RecordFault(definition, null, "E_DEBUG_PROGRAM_ENTRY_INVALID", "Signal bindings must declare the listener or oneshot entry category.");
                    return;
                }
                AppendProgramEvent(definition, null, "delivery", "signal:" + delivery.Signal, payload, null, threadId);
            }
            catch (Exception exception)
            {
                RecordFault(definition, null, "E_DEBUG_PROGRAM_ENTRY_FAILED", exception.GetType().Name + ": " + exception.Message);
                CompleteDispatchRun(session, definition, claimed, UnityCliDebugProgramRunStates.Failed, "E_DEBUG_PROGRAM_ENTRY_FAILED", exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                session.ExitCallback(delivery.ProgramId);
                MarkRunsReturned(delivery.ProgramId, entry);
            }
            // 触发事实与投递事实分开记录：这里只描述本次触发实际用了哪个触发源、哪个线程与哪个 revision。
            // phase 投递必须能被证明来自真实 PlayerLoop phase：executionPoint 直接命名那个 phase，
            // 与中央 editor tick 的 executionPoint 可区分。
            DebugTriggerSources.AppendTriggerEvent(
                UnityCliDebugSources.Trigger,
                delivery.TargetKey.Length != 0 ? DebugTriggerSources.MethodHit : (playerLoopPhase ? DebugTriggerSources.PlayerLoopPhase : DebugTriggerSources.EditorTick),
                delivery.Signal,
                string.Empty,
                delivery.Revision,
                delivery.TriggeredThreadId,
                delivery.ExecutionThreadId,
                delivery.TriggeredAtMonotonicMs,
                delivery.QueuedAtMonotonicMs,
                delivery.StartedAtMonotonicMs,
                false,
                delivery.TargetKey.Length != 0 ? "exact-target" : (playerLoopPhase ? "player-loop-phase:" + frameworkPhase : "framework-phase"),
                playerLoopPhase ? "trigger:" + DebugTriggerSources.PlayerLoopPhase + ":" + frameworkPhase : null);
        }

        /// <summary>框架投递的执行点归一化：未声明 phase 时就是中央 editor tick，绝不把未知值冒充成 phase。</summary>
        private static string EffectiveFrameworkPhase(string phase)
        {
            if (DebugPlayerLoopPhases.IsDefined(phase)) return phase;
            return UnityCliDebugProgramExecutionSites.EditorTick;
        }

        /// <summary>session 清理管线：关闭该 program 的全部未终态运行并释放资源。</summary>
        private static bool CloseProgramOnCleanup(string programId, UnityCliDebugCleanupReport report)
        {
            CloseProgramRuns(programId, "The owning session stopped, so this run was interrupted.");
            if (report != null) report.ClosedObservations++;
            return true;
        }

        /// <summary>
        /// 关闭一个 program 的全部非终态运行、执行它的 cleanup entry 并释放运行资源。
        /// session 停止与资源清理共用这一处，因此不会因为某个清理回调未被调用而留下永远非终态的 run。
        /// </summary>
        private static void CloseProgramRuns(string programId, string reason)
        {
            var session = Registry();
            var definition = session.Find(programId);
            foreach (var run in session.RunsFor(programId).Where(candidate => !candidate.IsTerminal).ToList())
            {
                if (definition != null) RunCleanupEntry(definition, run, "session-stop");
                session.MarkCodeReturned(run.ProgramRunId);
                session.CompleteRun(run.ProgramRunId, UnityCliDebugProgramRunStates.Interrupted, "E_DEBUG_PROGRAM_RUN_INTERRUPTED", reason, UtcMilliseconds());
            }
            session.MarkStepDriverPlanted(programId, false);
            DebugScheduler.CloseDriver(programId);
            ReleaseRunResource(programId);
        }

        /// <summary>关闭一个 driver 并释放它占用的会话资源。</summary>
        private static void CloseDriver(UnityCliDebugProgramDefinition definition, string programId)
        {
            DebugScheduler.CloseDriver(programId);
            Registry().MarkStepDriverPlanted(programId, false);
            ReleaseRunResource(programId);
            if (definition != null) AppendLifecycle(definition, null, "driver-closed", "no scheduler state remains for this program");
        }

        /// <summary>发布一个终态并撤除调度、detour 与会话资源；终态只在原代码返回后调用。
        /// 运行已被另一个 owner 发布为终态时，这里只负责释放 driver 与调度状态，不再重复发布终态。</summary>
        private static void FinishDriver(DebugScheduler.DriverState driver, UnityCliDebugProgramDefinition definition, UnityCliDebugProgramRun run, string terminal, string failureCode, string failureMessage)
        {
            var session = Registry();
            session.MarkCodeReturned(run.ProgramRunId);
            var published = session.CompleteRun(run.ProgramRunId, terminal, failureCode, failureMessage, UtcMilliseconds());
            if (!published && !run.IsTerminal)
            {
                // 未完成的同步代码不能被声明为终态：保持当前事实并继续报告在途。
                RecordFault(definition, run, "E_DEBUG_PROGRAM_TERMINAL_PENDING", "The terminal state could not be published because the original code is still in flight.");
                return;
            }
            DebugScheduler.CloseDriver(definition.ProgramId);
            session.MarkStepDriverPlanted(definition.ProgramId, false);
            DetachBindings(definition);
            ReleaseRunResource(definition.ProgramId);
            // 运行结束后定义回到 dormant（installed），因此可以再次 run；只有 stop 才把定义置为 stopped。
            if (!string.Equals(definition.State, UnityCliDebugProgramStates.Stopped, StringComparison.Ordinal)) session.MarkDormant(definition.ProgramId);
            if (published) AppendLifecycle(definition, run, terminal, failureCode);
        }

        /// <summary>执行程序声明的 cleanup entry；没有 cleanup entry 时如实报告未注册。</summary>
        private static bool RunCleanupEntry(UnityCliDebugProgramDefinition definition, UnityCliDebugProgramRun run, string reason)
        {
            if (definition == null) return false;
            var cleanupEntries = definition.Entries.Where(entry => string.Equals(entry.Category, UnityCliDebugProgramEntryCategories.Cleanup, StringComparison.Ordinal)).ToList();
            if (cleanupEntries.Count == 0) return false;
            var executed = false;
            foreach (var entry in cleanupEntries)
            {
                var resolved = FindResolved(definition.ProgramId, entry.Name);
                if (resolved == null) continue;
                try
                {
                    var runtime = new ProgramRuntime(definition, run == null ? string.Empty : run.ProgramRunId, entry.Name, UnityCliDebugProgramExecutionSites.EditorTick, Thread.CurrentThread.ManagedThreadId, DebugEventClocks.IsCurrentThreadMainThread(), UnityCliDebugProgramExecutionSites.EditorTick);
                    var context = new UnityCliDebugCleanupContext(runtime, reason, run != null && run.CancellationRequested);
                    InvokeMember(resolved, definition.ProgramId, new object[] { context });
                    executed = true;
                }
                catch (Exception exception)
                {
                    RecordFault(definition, run, "E_DEBUG_PROGRAM_CLEANUP_FAILED", exception.GetType().Name + ": " + exception.Message);
                }
            }
            if (run != null) AppendProgramEvent(definition, run, "lifecycle", "cleanup:" + reason, null, null, Thread.CurrentThread.ManagedThreadId);
            return executed;
        }

        /// <summary>
        /// 若存在与该执行点匹配的 pending 定点运行则取得执行权，并当场执行一次同步调用。
        /// 取得执行权与取消竞争只有一个赢家；未命中时什么都不执行。
        /// </summary>
        private static void ClaimPointRun(UnityCliDebugProgramDefinition definition, ResolvedProgramEntry resolved, string entry, string site, int threadId, object payload)
        {
            var session = Registry();
            var pending = session.RunsFor(definition.ProgramId).FirstOrDefault(run =>
                string.Equals(run.State, UnityCliDebugProgramRunStates.Scheduled, StringComparison.Ordinal)
                && string.Equals(run.Entry, entry, StringComparison.Ordinal)
                && MatchesSite(run.ExecutionSite, site, threadId));
            if (pending == null) return;
            if (!session.TryBeginRun(pending.ProgramRunId, UtcMilliseconds(), threadId)) return;
            try
            {
                var runtime = new ProgramRuntime(definition, pending.ProgramRunId, entry, site, threadId, DebugEventClocks.IsCurrentThreadMainThread(), UnityCliDebugProgramExecutionSites.EditorTick);
                var context = new UnityCliDebugOneShotContext(runtime, payload is IDictionary<string, object> map ? map : new Dictionary<string, object>(StringComparer.Ordinal));
                var value = InvokeMember(resolved, definition.ProgramId, new object[] { context });
                session.MarkCodeReturned(pending.ProgramRunId);
                session.RecordResult(pending.ProgramRunId, value);
                session.CompleteRun(pending.ProgramRunId, UnityCliDebugProgramRunStates.Completed, string.Empty, string.Empty, UtcMilliseconds());
            }
            catch (Exception exception)
            {
                session.MarkCodeReturned(pending.ProgramRunId);
                session.CompleteRun(pending.ProgramRunId, UnityCliDebugProgramRunStates.Failed, "E_DEBUG_PROGRAM_ENTRY_FAILED", exception.GetType().Name + ": " + exception.Message, UtcMilliseconds());
            }
            DebugScheduler.CloseDriver(definition.ProgramId);
            session.MarkStepDriverPlanted(definition.ProgramId, false);
            ReleaseRunResource(definition.ProgramId);
            AppendLifecycle(definition, pending, "completed", "point run claimed at " + site);
        }

        /// <summary>
        /// 一个 entry 在信号阶段声明的投递地址。targetKey 为空表示投递给中央 framework phase；
        /// 非空表示投递必须在该 exact target 到达执行点时被取得，否则只会过期。
        /// </summary>
        private static string SignalTargetKey(UnityCliDebugProgramDefinition definition, string entry)
        {
            if (definition == null || string.IsNullOrEmpty(entry)) return string.Empty;
            foreach (var binding in definition.Bindings)
            {
                if (!string.Equals(binding.Phase, UnityCliDebugProgramBindingPhases.Signal, StringComparison.Ordinal)) continue;
                if (!string.Equals(binding.Entry, entry, StringComparison.Ordinal)) continue;
                return binding.TargetKey ?? string.Empty;
            }
            return string.Empty;
        }

        /// <summary>同步调用返回后，把该 program 匹配 entry 的运行标记为不再在途。</summary>
        private static void MarkRunsReturned(string programId, string entry)
        {
            var session = Registry();
            foreach (var run in session.RunsFor(programId))
            {
                if (run.IsTerminal) continue;
                if (!string.Equals(run.Entry, entry, StringComparison.Ordinal)) continue;
                session.MarkCodeReturned(run.ProgramRunId);
            }
        }

        private static bool MatchesSite(string declaredSite, string actualSite, int threadId)
        {
            if (string.Equals(declaredSite, actualSite, StringComparison.Ordinal)) return true;
            if (!string.Equals(declaredSite, UnityCliDebugProgramExecutionSites.WorkerThread, StringComparison.Ordinal)) return false;
            return !DebugEventClocks.IsCurrentThreadMainThread();
        }

        private static UnityCliDebugStepResult InvokeStep(UnityCliDebugProgramDefinition definition, UnityCliDebugProgramRun run, UnityCliDebugProgramEntry entry, long stepIndex, string phase)
        {
            var resolved = RequireResolved(definition, entry.Name);
            var playerLoopPhase = DebugPlayerLoopPhases.IsDefined(phase);
            var runtime = new ProgramRuntime(
                definition,
                run.ProgramRunId,
                entry.Name,
                playerLoopPhase ? UnityCliDebugProgramExecutionSites.PlayerLoopPhase : UnityCliDebugProgramExecutionSites.EditorTick,
                Thread.CurrentThread.ManagedThreadId,
                true,
                playerLoopPhase ? phase : UnityCliDebugProgramExecutionSites.EditorTick);
            var context = new UnityCliDebugStepContext(runtime) { StepIndex = stepIndex };
            var value = InvokeMember(resolved, definition.ProgramId, new object[] { context });
            var result = value as UnityCliDebugStepResult;
            if (result == null)
                throw new BridgeCommandException(
                    "E_DEBUG_PROGRAM_ENTRY_INVALID",
                    "A step entry did not return a UnityCliDebugStepResult.",
                    new Dictionary<string, object> { { "entry", entry.Name }, { "signature", UnityCliDebugProgramAbi.SignatureOf(UnityCliDebugProgramEntryCategories.Step) } },
                    new[] { "Return UnityCliDebugStepResult.Continue(wait) or UnityCliDebugStepResult.Finish() from the step entry." });
            if (!string.IsNullOrEmpty(result.NextEntry) && definition.FindEntry(result.NextEntry) == null)
                throw new BridgeCommandException(
                    "E_DEBUG_PROGRAM_ENTRY_INVALID",
                    "A step declared a next entry that the definition does not contain.",
                    new Dictionary<string, object> { { "nextEntry", result.NextEntry } },
                    new[] { "Declare every next entry in the installed entries list before running the driver." });
            return result;
        }

        private static object InvokeOneShot(UnityCliDebugProgramDefinition definition, UnityCliDebugProgramRun run, UnityCliDebugProgramEntry entry, string phase)
        {
            var resolved = RequireResolved(definition, entry.Name);
            var playerLoopPhase = DebugPlayerLoopPhases.IsDefined(phase);
            var runtime = new ProgramRuntime(
                definition,
                run.ProgramRunId,
                entry.Name,
                playerLoopPhase ? UnityCliDebugProgramExecutionSites.PlayerLoopPhase : run.ExecutionSite,
                Thread.CurrentThread.ManagedThreadId,
                true,
                playerLoopPhase ? phase : UnityCliDebugProgramExecutionSites.EditorTick);
            var context = new UnityCliDebugOneShotContext(runtime, AsDictionary(run.Args));
            return InvokeMember(resolved, definition.ProgramId, new object[] { context });
        }

        /// <summary>
        /// 一次同步调用。wrapper entry 使用安装时建立的唯一实例；实例构造只在挂载时发生一次。
        /// </summary>
        private static object InvokeMember(ResolvedProgramEntry resolved, string programId, object[] arguments)
        {
            object instance = null;
            if (resolved.Instance)
            {
                lock (Sync)
                {
                    Dictionary<Type, object> wrappers;
                    if (WrapperSets.TryGetValue(programId, out wrappers)) wrappers.TryGetValue(resolved.Type, out instance);
                }
                if (instance == null) throw new InvalidOperationException("The wrapper instance for " + resolved.Type.FullName + " is unavailable.");
            }
            return resolved.Method.Invoke(instance, arguments);
        }

        /// <summary>entry 可见能力的唯一实现；程序无法绕过它接触 host 内部状态。</summary>
        private sealed class ProgramRuntime : IUnityCliDebugProgramRuntime
        {
            private readonly UnityCliDebugProgramDefinition definition;
            private readonly List<UnityCliDebugCheck> checks = new List<UnityCliDebugCheck>();
            private readonly List<string> evidence = new List<string>();
            private readonly string frameworkPhase;

            /// <summary>
            /// frameworkPhase 是该入口所在运行声明的框架执行点（中央 editor tick 或真实 PlayerLoop phase）：
            /// 它决定本入口排定的框架投递在哪个执行点取得执行权。exact target 入口为空。
            /// </summary>
            internal ProgramRuntime(UnityCliDebugProgramDefinition definition, string programRunId, string entry, string executionSite, int threadId, bool mainThread, string frameworkPhase)
            {
                this.definition = definition;
                ProgramRunId = programRunId ?? string.Empty;
                Entry = entry ?? string.Empty;
                ExecutionSite = executionSite ?? string.Empty;
                ThreadId = threadId;
                MainThread = mainThread;
                this.frameworkPhase = string.IsNullOrEmpty(frameworkPhase) ? UnityCliDebugProgramExecutionSites.EditorTick : frameworkPhase;
            }

            public string SessionId { get { return definition.SessionId; } }
            public string ProgramId { get { return definition.ProgramId; } }
            public string ProgramRunId { get; private set; }
            public string Entry { get; private set; }
            public long Revision { get { return definition.Revision; } }
            public string ExecutionSite { get; private set; }
            public int ThreadId { get; private set; }
            public bool MainThread { get; private set; }
            public string RuntimeId { get { return DebugSessionHost.RuntimeId(); } }
            public string DomainGeneration { get { return DebugProgramHost.DomainGeneration(); } }
            public object StateSchema { get { return definition.StateSchema; } }
            public object StateSnapshot() { return definition.RevisionState; }
            public long StateEpoch { get { return definition.StateEpoch; } }

            public string TryUpdateState(long expectedEpoch, object candidate, long candidateBytes)
            {
                long epoch;
                var failure = Registry().TryUpdateState(definition.ProgramId, expectedEpoch, candidate, candidateBytes, out epoch);
                return failure;
            }

            public void Emit(string name, object value)
            {
                AppendProgramEvent(definition, CurrentRun(), "emit", name, value, null, ThreadId);
            }

            public void AddCheck(UnityCliDebugCheck check)
            {
                if (check == null) return;
                lock (checks)
                {
                    if (checks.Count >= DebugLimits.MaxProgramChecks) return;
                    checks.Add(check);
                }
                var run = CurrentRun();
                if (run != null) run.CheckCount++;
                AppendProgramEvent(definition, run, "check", check.Name, new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "expected", DebugValueProjection.ProjectValueOnly(check.Expected) },
                    { "actual", DebugValueProjection.ProjectValueOnly(check.Actual) },
                    { "passed", check.Passed },
                    { "coverage", check.Coverage }
                }, null, ThreadId);
                // 命名检查是等待计划中 condition 信号的唯一来源；通过一次即唤醒同名等待。
                if (check.Passed) DebugScheduler.ResumeWaits(check.Name);
            }

            public void AddEvidence(string reference)
            {
                if (string.IsNullOrEmpty(reference)) return;
                lock (evidence)
                {
                    if (evidence.Count >= DebugLimits.MaxProgramChecks) return;
                    evidence.Add(reference);
                }
            }

            public string AcquireHandle(object target, bool strong)
            {
                DebugHandleView view;
                int sessionUsed;
                int globalUsed;
                var failure = DebugHandleRegistry.Create(definition.SessionId, target, strong, DebugSessionHost.RuntimeId(), DomainGeneration, out view, out sessionUsed, out globalUsed);
                return failure != null ? string.Empty : view.HandleId;
            }

            public bool ReleaseHandle(string handleId)
            {
                return DebugHandleRegistry.Release(handleId);
            }

            public void PublishClock(string name, double value, string rawText)
            {
                DebugLogicalClocks.Publish(name, value, rawText, "program:" + definition.ProgramId + ":" + Entry, DomainGeneration);
            }

            public void ScheduleSignal(string signal, object payload)
            {
                if (string.IsNullOrEmpty(signal)) return;
                var json = payload == null ? string.Empty : UnityCliSerializer.Serialize(DebugValueProjection.ProjectValueOnly(payload));
                var targetKey = SignalTargetKey(definition, Entry);
                // 无 target 的投递地址就是本入口的框架执行点：声明了 PlayerLoop phase 的运行在这里交付，
                // 未声明的运行仍交给中央 editor tick，两者共用同一队列。
                var phase = targetKey.Length == 0 ? frameworkPhase : UnityCliDebugProgramExecutionSites.Signal;
                var deliveryId = DebugScheduler.EnqueueDelivery(definition.ProgramId, ProgramRunId, Entry, signal, json, Revision, DebugLimits.ProgramSignalTimeoutMs, targetKey, phase);
                if (string.IsNullOrEmpty(deliveryId)) return;
                var run = CurrentRun();
                if (run != null) run.DeliveryCount++;
                // 投递只排队；当场执行由目标执行点的 DispatchSignal 记录，两者不混为一件事。
                DebugScheduler.ResumeWaits(signal);
            }

            public void RegisterCleanup(string name, Action cleanup)
            {
                if (cleanup == null || string.IsNullOrEmpty(name)) return;
                var order = UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.ExplicitCleanup);
                DebugSessionHost.RegisterCleanupStep(definition.SessionId, order, "program-entry-cleanup:" + definition.ProgramId + ":" + name, UnityCliDebugFacilityOwners.Program, report =>
                {
                    cleanup();
                    if (report != null) report.ExplicitCleanupEntries++;
                    return true;
                },
                    // 显式清理条目运行用户注册的回调代码，因此属于必须在主线程上执行的清理。
                    true);
            }

            private UnityCliDebugProgramRun CurrentRun()
            {
                return string.IsNullOrEmpty(ProgramRunId) ? null : Registry().FindRun(ProgramRunId);
            }
        }
    }
}
#endif
