#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace UnityCli
{
    /// <summary>
    /// debug 域的中央 scheduler：持续步骤 driver 与跨执行点投递的唯一 owner。
    ///
    /// 规则：
    /// - 每个 program 同时最多一个持续 step driver；driver 只在自己声明的执行点上推进：
    ///   默认是中央 editor tick，声明 phase 时是该真实 PlayerLoop phase。
    /// - step 返回的等待计划（frame / time / signal / condition）由本类解释；程序不能注册私有 update、
    ///   reload callback、timeout timer 或请求生命周期。
    /// - 投递是非阻塞、有界、单赢家的：pending→running 的转换与取消竞争只有一个赢家；
    ///   过期的投递记录为 expired，永不迟到执行，也永不换线程。
    /// - 投递地址只有两种：一个已绑定的 exact target（在命中点上取得执行权），
    ///   或中央 framework phase（在声明的 tick 或真实 PlayerLoop phase 上取得执行权）；
    ///   两者共用同一队列，不建立第二条队列。
    /// - 每个 tick/phase 段记录派发耗时、backlog 与 overrun，并用有界预算只关闭未来条目；
    ///   同步用户代码无法被抢占，只能事后测量。
    /// - 本类不注册任何 update 回调；Tick 只由唯一 Bridge 主循环调用，phase 推进只由唯一
    ///   DebugPlayerLoopHost 插入的子系统调用。
    /// - 机制状态只对本次 Unity 启动有效：域重载与 Play 段结束时一律作废。
    /// </summary>
    internal static class DebugScheduler
    {
        /// <summary>一个持续 driver 的运行事实；只描述本类拥有的调度状态。</summary>
        internal sealed class DriverState
        {
            internal string ProgramId = string.Empty;
            internal string ProgramRunId = string.Empty;
            internal string Entry = string.Empty;

            /// <summary>该 driver 取得执行权的执行点：中央 editor tick 或一个具名 PlayerLoop phase。</summary>
            internal string Phase = UnityCliDebugProgramExecutionSites.EditorTick;

            internal long StepIndex;
            internal double StartedMonotonicMs;
            internal double DeadlineMonotonicMs;
            internal double NextTickMonotonicMs;
            internal long FramesRemaining;
            internal string WaitSignal = string.Empty;
            internal string WaitCondition = string.Empty;
            internal double WaitDeadlineMonotonicMs;
            internal long LastRevision;

            internal bool Waiting { get { return WaitSignal.Length > 0 || WaitCondition.Length > 0; } }
        }

        /// <summary>
        /// 一次跨执行点投递的完整事实；提交与执行分开记录。
        /// triggeredAt/queuedAt/startedAt、触发线程与执行线程、运行 revision 与过期状态都在这里。
        /// </summary>
        internal sealed class Delivery
        {
            internal string DeliveryId = string.Empty;
            internal string ProgramId = string.Empty;
            internal string ProgramRunId = string.Empty;
            internal string Entry = string.Empty;
            internal string Signal = string.Empty;
            internal string PayloadJson = string.Empty;
            internal long Revision;
            internal int TriggeredThreadId;
            internal double TriggeredAtMonotonicMs;
            internal double QueuedAtMonotonicMs;
            internal double StartedAtMonotonicMs;
            internal double ExpiresAtMonotonicMs;
            internal int ExecutionThreadId;
            internal string State = "pending";

            /// <summary>已绑定的 exact target key；为空表示投递给中央 framework phase。</summary>
            internal string TargetKey = string.Empty;

            /// <summary>声明的执行点：exact target 为 signal，框架投递为中央 tick 或真实 PlayerLoop phase。</summary>
            internal string Phase = string.Empty;

            /// <summary>
            /// 投递目标的 hook 定义 id；为空表示这是一个投递给 program entry 的框架投递。
            /// 一个 hook listener 在 phase 上满足条件时也走同一队列与该字段规定的执行者，不建立第二条队列。
            /// </summary>
            internal string HookId = string.Empty;

            /// <summary>过期原因；未过期时为空。</summary>
            internal string ExpireReason = string.Empty;

            internal bool Expired { get { return string.Equals(State, "expired", StringComparison.Ordinal); } }
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, DriverState> Drivers = new Dictionary<string, DriverState>(StringComparer.Ordinal);
        private static readonly List<Delivery> Deliveries = new List<Delivery>();
        private static readonly Queue<double> TickCosts = new Queue<double>();
        private static long nextDeliveryIndex = 1L;
        private static long expiredDeliveries;
        private static long droppedDeliveries;
        private static long completedDeliveries;
        private static int pendingDeliveries;
        private static int queueHighWaterMark;
        private static long tickCount;
        private static double lastTickMilliseconds;
        private static double maxTickMilliseconds;
        private static long overrunTicks;
        private static long frameworkDeliveriesClaimed;
        private static long targetDeliveriesClaimed;
        private static long expiredLastTick;
        private static long signalRecursionRefusals;

        [ThreadStatic] private static int signalDepth;

        internal static int DriverCount { get { lock (Sync) return Drivers.Count; } }

        internal static long ExpiredDeliveries { get { lock (Sync) return expiredDeliveries; } }

        internal static long DroppedDeliveries { get { lock (Sync) return droppedDeliveries; } }

        internal static long CompletedDeliveries { get { lock (Sync) return completedDeliveries; } }

        /// <summary>当前仍未取得执行权的投递数量；命中热路径据此跳过投递派发。</summary>
        internal static int PendingDeliveryCount { get { return Volatile.Read(ref pendingDeliveries); } }

        internal static long SignalRecursionRefusals { get { return Interlocked.Read(ref signalRecursionRefusals); } }

        /// <summary>
        /// 提交一个 driver；同一 program 已存在 driver 时拒绝并返回稳定原因。
        /// phase 决定该 driver 在哪个执行点取得步骤执行权，缺省以外的取值必须是已声明的 PlayerLoop phase。
        /// </summary>
        internal static string PlantDriver(string programId, string programRunId, string entry, long revision, double nowMonotonicMs, double deadlineMonotonicMs, string phase)
        {
            if (string.IsNullOrEmpty(programId) || string.IsNullOrEmpty(programRunId)) return "missing-program-run";
            lock (Sync)
            {
                DriverState existing;
                if (Drivers.TryGetValue(programId, out existing)) return "driver-already-planted";
                Drivers[programId] = new DriverState
                {
                    ProgramId = programId,
                    ProgramRunId = programRunId,
                    Entry = entry ?? string.Empty,
                    Phase = string.IsNullOrEmpty(phase) ? UnityCliDebugProgramExecutionSites.EditorTick : phase,
                    StepIndex = 0L,
                    StartedMonotonicMs = nowMonotonicMs,
                    DeadlineMonotonicMs = deadlineMonotonicMs,
                    NextTickMonotonicMs = nowMonotonicMs,
                    LastRevision = revision
                };
                return null;
            }
        }

        internal static bool HasDriver(string programId)
        {
            lock (Sync) return !string.IsNullOrEmpty(programId) && Drivers.ContainsKey(programId);
        }

        /// <summary>关闭一个 driver 的调度状态；返回被关闭的 run 身份，未持有返回空串。</summary>
        internal static string CloseDriver(string programId)
        {
            lock (Sync)
            {
                DriverState driver;
                if (string.IsNullOrEmpty(programId) || !Drivers.TryGetValue(programId, out driver)) return string.Empty;
                Drivers.Remove(programId);
                return driver.ProgramRunId;
            }
        }

        /// <summary>返回当前 driver 的只读事实快照，供 info 报告而不再调度。</summary>
        internal static DriverState DescribeDriver(string programId)
        {
            lock (Sync)
            {
                DriverState driver;
                return !string.IsNullOrEmpty(programId) && Drivers.TryGetValue(programId, out driver) ? driver : null;
            }
        }

        /// <summary>
        /// 有界投递一个信号；过期即丢弃并记录事实，绝不补执行。
        /// targetKey 非空表示投递给已绑定的 exact target，为空表示投递给中央 framework phase。
        /// hookId 非空表示执行者是该 hook 定义的 listener，为空表示执行者是 program entry。
        /// </summary>
        internal static string EnqueueDelivery(string programId, string programRunId, string entry, string signal, string payloadJson, long revision, double timeoutMilliseconds, string targetKey = null, string phase = null, string hookId = null)
        {
            var now = DebugEventClocks.MonotonicMilliseconds;
            var timeout = timeoutMilliseconds > 0d ? timeoutMilliseconds : 1000d;
            lock (Sync)
            {
                // 入队前先关闭已超期的 pending：过期是终态，不允许以后命中时迟到执行。
                ExpirePendingLocked(now);
                while (Deliveries.Count >= DebugLimits.MaxProgramDeliveries)
                {
                    Deliveries.RemoveAt(0);
                    droppedDeliveries++;
                }
                var delivery = new Delivery
                {
                    DeliveryId = "sig-" + nextDeliveryIndex.ToString(CultureInfo.InvariantCulture),
                    ProgramId = programId ?? string.Empty,
                    ProgramRunId = programRunId ?? string.Empty,
                    Entry = entry ?? string.Empty,
                    Signal = signal ?? string.Empty,
                    PayloadJson = payloadJson ?? string.Empty,
                    Revision = revision,
                    TriggeredThreadId = Thread.CurrentThread.ManagedThreadId,
                    TriggeredAtMonotonicMs = now,
                    QueuedAtMonotonicMs = now,
                    ExpiresAtMonotonicMs = now + timeout,
                    TargetKey = targetKey ?? string.Empty,
                    HookId = hookId ?? string.Empty,
                    Phase = string.IsNullOrEmpty(phase) ? UnityCliDebugProgramExecutionSites.Signal : phase
                };
                nextDeliveryIndex++;
                Deliveries.Add(delivery);
                pendingDeliveries = Deliveries.Count;
                if (pendingDeliveries > queueHighWaterMark) queueHighWaterMark = pendingDeliveries;
                return delivery.DeliveryId;
            }
        }

        /// <summary>
        /// 关闭一个 hook 的所有 pending 投递：session stop 的第 1 阶段必须让未到达执行点的注入永不执行。
        /// 返回被关闭的数量；已过期的条目由常规扫描处理。
        /// </summary>
        internal static long DropPendingForHook(string hookId)
        {
            if (string.IsNullOrEmpty(hookId)) return 0L;
            lock (Sync) return DropPendingLocked(delivery => string.Equals(delivery.HookId, hookId, StringComparison.Ordinal), "session-dispatch-closed");
        }

        /// <summary>关闭一个 program 的所有 pending 投递；session stop 的第 1 阶段使用同一入口。</summary>
        internal static long DropPendingForProgram(string programId)
        {
            if (string.IsNullOrEmpty(programId)) return 0L;
            lock (Sync) return DropPendingLocked(delivery => string.Equals(delivery.ProgramId, programId, StringComparison.Ordinal), "session-dispatch-closed");
        }

        /// <summary>按谓词关闭 pending 投递；关闭是终态，并记录“未执行”的触发事实。</summary>
        private static long DropPendingLocked(Func<Delivery, bool> matches, string reason)
        {
            var dropped = 0L;
            for (var index = 0; index < Deliveries.Count; index++)
            {
                var delivery = Deliveries[index];
                if (!string.Equals(delivery.State, "pending", StringComparison.Ordinal)) continue;
                if (!matches(delivery)) continue;
                delivery.State = "cancelled";
                delivery.ExpireReason = reason;
                Deliveries.RemoveAt(index);
                index--;
                droppedDeliveries++;
                dropped++;
                DebugTriggerSources.AppendTriggerEvent(
                    UnityCliDebugSources.Trigger,
                    DebugTriggerSources.HostSignal,
                    delivery.Signal,
                    delivery.HookId,
                    delivery.Revision,
                    delivery.TriggeredThreadId,
                    0,
                    delivery.TriggeredAtMonotonicMs,
                    delivery.QueuedAtMonotonicMs,
                    0d,
                    true,
                    reason);
            }
            pendingDeliveries = Deliveries.Count;
            return dropped;
        }

        /// <summary>等待中的 driver 被同名信号或命名检查唤醒。</summary>
        internal static void ResumeWaits(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (Sync)
            {
                foreach (var driver in Drivers.Values)
                {
                    var matches = (driver.WaitSignal.Length > 0 && string.Equals(driver.WaitSignal, name, StringComparison.Ordinal))
                        || (driver.WaitCondition.Length > 0 && string.Equals(driver.WaitCondition, name, StringComparison.Ordinal));
                    if (!matches) continue;
                    driver.WaitSignal = string.Empty;
                    driver.WaitCondition = string.Empty;
                    driver.NextTickMonotonicMs = DebugEventClocks.MonotonicMilliseconds;
                }
            }
        }

        /// <summary>
        /// 原子取得一个匹配的 pending 投递。只有一个赢家；过期的投递在这里关闭并返回 null。
        /// </summary>
        internal static Delivery TryClaimDelivery(string programId, string signal, int threadId)
        {
            var now = DebugEventClocks.MonotonicMilliseconds;
            lock (Sync)
            {
                ExpirePendingLocked(now);
                for (var index = 0; index < Deliveries.Count; index++)
                {
                    var delivery = Deliveries[index];
                    if (!string.Equals(delivery.State, "pending", StringComparison.Ordinal)) continue;
                    if (!string.Equals(delivery.ProgramId, programId ?? string.Empty, StringComparison.Ordinal)) continue;
                    if (!string.Equals(delivery.Signal, signal ?? string.Empty, StringComparison.Ordinal)) continue;
                    if (now > delivery.ExpiresAtMonotonicMs)
                    {
                        ExpireLocked(delivery, index, "expired-before-claim");
                        continue;
                    }
                    return ClaimLocked(delivery, index, threadId, now);
                }
                return null;
            }
        }

        /// <summary>
        /// 在已绑定的 exact target 到达执行点时原子取得该 target 的 pending 投递。
        /// 每个投递只有一个赢家；未到达的 target 不补执行、不换线程。
        /// </summary>
        internal static List<Delivery> TryClaimTargetDeliveries(string targetKey, int threadId, int max)
        {
            var claimed = new List<Delivery>();
            if (string.IsNullOrEmpty(targetKey) || max <= 0) return claimed;
            var now = DebugEventClocks.MonotonicMilliseconds;
            lock (Sync)
            {
                ExpirePendingLocked(now);
                for (var index = 0; index < Deliveries.Count && claimed.Count < max; index++)
                {
                    var delivery = Deliveries[index];
                    if (!string.Equals(delivery.State, "pending", StringComparison.Ordinal)) continue;
                    if (!string.Equals(delivery.TargetKey, targetKey, StringComparison.Ordinal)) continue;
                    if (now > delivery.ExpiresAtMonotonicMs)
                    {
                        ExpireLocked(delivery, index, "expired-before-target-hit");
                        index--;
                        continue;
                    }
                    var winner = ClaimLocked(delivery, index, threadId, now);
                    if (winner == null) continue;
                    index--;
                    targetDeliveriesClaimed++;
                    claimed.Add(winner);
                }
            }
            return claimed;
        }

        /// <summary>中央 framework phase（该 driver 声明的 editor-tick）取得 pending 投递；每个执行点段的数量由预算约束。目标未到达的投递只会在过期后留下 expired 事实。</summary>
        internal static List<Delivery> ClaimFrameworkPhaseDeliveries(string phase, int threadId, int max)
        {
            var claimed = new List<Delivery>();
            if (max <= 0) return claimed;
            var effective = string.IsNullOrEmpty(phase) ? UnityCliDebugProgramExecutionSites.EditorTick : phase;
            var now = DebugEventClocks.MonotonicMilliseconds;
            lock (Sync)
            {
                ExpirePendingLocked(now);
                for (var index = 0; index < Deliveries.Count && claimed.Count < max; index++)
                {
                    var delivery = Deliveries[index];
                    if (!string.Equals(delivery.State, "pending", StringComparison.Ordinal)) continue;
                    if (delivery.TargetKey.Length != 0) continue;
                    if (!string.Equals(delivery.Phase, effective, StringComparison.Ordinal)) continue;
                    if (now > delivery.ExpiresAtMonotonicMs)
                    {
                        ExpireLocked(delivery, index, "expired-before-tick");
                        index--;
                        continue;
                    }
                    var winner = ClaimLocked(delivery, index, threadId, now);
                    if (winner == null) continue;
                    index--;
                    frameworkDeliveriesClaimed++;
                    claimed.Add(winner);
                }
            }
            return claimed;
        }

        /// <summary>scheduler 段的全量过期扫描；只关闭未来条目，返回本次关闭数量。</summary>
        internal static long ExpirePendingDeliveries()
        {
            var now = DebugEventClocks.MonotonicMilliseconds;
            lock (Sync) return ExpirePendingLocked(now);
        }

        /// <summary>
        /// 进入一次 host signal 同步派发。递归深度超过上限时返回 false：
        /// 调用方必须拒绝本次派发并记录稳定事实，而不是继续加深递归链。
        /// </summary>
        internal static bool TryEnterSignalScope()
        {
            if (signalDepth >= DebugLimits.MaxSignalRecursionDepth)
            {
                Interlocked.Increment(ref signalRecursionRefusals);
                return false;
            }
            signalDepth++;
            return true;
        }

        internal static void ExitSignalScope()
        {
            if (signalDepth > 0) signalDepth--;
        }

        internal static int SignalDepth { get { return signalDepth; } }

        /// <summary>
        /// 中央 scheduler 的 program 段。只做有界工作：超期运行关闭、过期投递扫描、driver 推进与等待计划
        /// 解释，再按预算执行该执行点上已经取得执行权的投递。
        /// 任何 driver 故障都在内部归约为该运行的终态，不影响其他 driver 或唯一主循环。
        /// </summary>
        internal static void Tick()
        {
            Advance(UnityCliDebugProgramExecutionSites.EditorTick);
        }

        /// <summary>
        /// 一个具名 PlayerLoop phase 的推进段；只由 DebugPlayerLoopHost 插入的子系统调用。
        /// 与 editor tick 共用同一有界工作体与同一每-tick 投递预算，不建立第二个 scheduler。
        /// </summary>
        internal static void AdvancePhase(string phase)
        {
            if (string.IsNullOrEmpty(phase)) return;
            Advance(phase);
        }

        private static void Advance(string phase)
        {
            var watch = Stopwatch.StartNew();
            DebugProgramHost.CancelExpiredRuns();
            DriverState[] drivers;
            lock (Sync) drivers = Drivers.Values.ToArray();
            var now = DebugEventClocks.MonotonicMilliseconds;
            foreach (var driver in drivers)
            {
                // driver 只在它自己声明的执行点上推进：别的 phase 既不偷走步骤，也不触发等待解释。
                if (!string.Equals(driver.Phase, phase, StringComparison.Ordinal)) continue;
                if (now > driver.DeadlineMonotonicMs)
                {
                    DebugProgramHost.StopDriverByDeadline(driver);
                    continue;
                }
                if (driver.Waiting)
                {
                    if (now > driver.WaitDeadlineMonotonicMs)
                    {
                        DebugProgramHost.StopDriverByWaitTimeout(driver);
                    }
                    continue;
                }
                if (driver.FramesRemaining > 0L)
                {
                    driver.FramesRemaining--;
                    if (driver.FramesRemaining > 0L) continue;
                }
                if (now < driver.NextTickMonotonicMs) continue;
                DebugProgramHost.AdvanceDriver(driver);
            }
            // framework phase 上的监听式触发：满足条件的 listener 在同一执行点段投递，
            // 并与 program 投递共用同一条队列与同一每段预算（phase 以外的执行点不会求值 phase listener）。
            MethodHookHost.TriggerPhaseListeners(phase);
            // framework phase 上的投递：每个执行点段只执行有界数量，剩余条目留在队列里等待下一段。
            if (PendingDeliveryCount > 0) DebugProgramHost.DispatchFrameworkDeliveries(phase, Thread.CurrentThread.ManagedThreadId, DebugLimits.MaxDeliveriesPerTick);
            // 输入域的到期边界与等待主线程的释放请求在中央 editor tick 上推进：同一 scheduler，不新增 update 注册。
            if (string.Equals(phase, UnityCliDebugProgramExecutionSites.EditorTick, StringComparison.Ordinal)) DebugInputHost.Advance();
            RecordTickCost(watch.Elapsed.TotalMilliseconds);
        }

        /// <summary>由 step 返回的等待计划写回 driver 状态；这是程序唯一的等待表达方式。</summary>
        internal static void ApplyWait(DriverState driver, UnityCliDebugStepWait wait, double nowMonotonicMs)
        {
            lock (Sync)
            {
                if (driver == null) return;
                driver.WaitSignal = string.Empty;
                driver.WaitCondition = string.Empty;
                driver.FramesRemaining = 0L;
                driver.WaitDeadlineMonotonicMs = 0d;
                driver.NextTickMonotonicMs = nowMonotonicMs;
                if (wait == null) return;
                switch (wait.Kind)
                {
                    case UnityCliDebugStepWaitKinds.Frame:
                        driver.FramesRemaining = wait.Frames;
                        return;
                    case UnityCliDebugStepWaitKinds.Time:
                        driver.NextTickMonotonicMs = nowMonotonicMs + wait.DelayMilliseconds;
                        return;
                    case UnityCliDebugStepWaitKinds.Signal:
                        driver.WaitSignal = wait.Signal;
                        driver.WaitDeadlineMonotonicMs = nowMonotonicMs + (wait.TimeoutMilliseconds > 0d ? wait.TimeoutMilliseconds : driver.DeadlineMonotonicMs - nowMonotonicMs);
                        return;
                    case UnityCliDebugStepWaitKinds.Condition:
                        driver.WaitCondition = wait.Condition;
                        driver.WaitDeadlineMonotonicMs = nowMonotonicMs + (wait.TimeoutMilliseconds > 0d ? wait.TimeoutMilliseconds : driver.DeadlineMonotonicMs - nowMonotonicMs);
                        return;
                    default:
                        return;
                }
            }
        }

        /// <summary>record driver 的 step 事实：序号与固定 revision。</summary>
        internal static void RecordStepWithRevision(DriverState driver, long revision)
        {
            lock (Sync)
            {
                if (driver == null) return;
                driver.LastRevision = revision;
            }
        }

        /// <summary>本次 Unity 启动的机制状态作废；域重载、Play 段结束与测试复位共用这里。</summary>
        internal static void Reset()
        {
            lock (Sync)
            {
                Drivers.Clear();
                Deliveries.Clear();
                expiredDeliveries = 0L;
                droppedDeliveries = 0L;
                completedDeliveries = 0L;
                pendingDeliveries = 0;
                queueHighWaterMark = 0;
                tickCount = 0L;
                lastTickMilliseconds = 0d;
                maxTickMilliseconds = 0d;
                overrunTicks = 0L;
                frameworkDeliveriesClaimed = 0L;
                targetDeliveriesClaimed = 0L;
                expiredLastTick = 0L;
                signalRecursionRefusals = 0L;
                TickCosts.Clear();
                signalDepth = 0;
            }
        }

        /// <summary>投递的结构化占用事实，供 descriptor 与拒绝路径使用。</summary>
        internal static Dictionary<string, object> Describe()
        {
            Dictionary<string, object> report;
            lock (Sync)
            {
                report = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "drivers", (long)Drivers.Count },
                    { "pendingDeliveries", (long)pendingDeliveries },
                    { "expiredDeliveries", expiredDeliveries },
                    { "droppedDeliveries", droppedDeliveries },
                    { "completedDeliveries", completedDeliveries },
                    { "maxDeliveries", (long)DebugLimits.MaxProgramDeliveries },
                    { "maxDeliveriesPerTick", (long)DebugLimits.MaxDeliveriesPerTick },
                    { "queueHighWaterMark", (long)queueHighWaterMark },
                    { "backlog", (long)pendingDeliveries },
                    { "targetDeliveriesClaimed", targetDeliveriesClaimed },
                    { "frameworkDeliveriesClaimed", frameworkDeliveriesClaimed },
                    { "expiredLastTick", expiredLastTick }
                };
                foreach (var pair in DebugSchedulerTicks.Observe(tickCount, lastTickMilliseconds, maxTickMilliseconds, overrunTicks, TickCosts, signalRecursionRefusals))
                    report[pair.Key] = pair.Value;
            }
            // PlayerLoop phase 现场事实属于同一个 scheduler 可观测块：安装计数、封闭集合与插入/移除记账。
            report["playerLoop"] = DebugPlayerLoopHost.Describe();
            // hook 派发计数来自 hook host 自己的锁；不在本类的锁内读取，避免两把锁的持有顺序互相依赖。
            foreach (var pair in MethodHookHost.DispatchObservability()) report[pair.Key] = pair.Value;
            return report;
        }

        // ------------------------------------------------------------------
        // 内部：投递状态机与每-tick 观测
        // ------------------------------------------------------------------

        private static Delivery ClaimLocked(Delivery delivery, int index, int threadId, double now)
        {
            if (!string.Equals(delivery.State, "pending", StringComparison.Ordinal)) return null;
            delivery.State = "running";
            delivery.StartedAtMonotonicMs = now;
            delivery.ExecutionThreadId = threadId;
            Deliveries.RemoveAt(index);
            pendingDeliveries = Deliveries.Count;
            completedDeliveries++;
            return delivery;
        }

        private static void ExpireLocked(Delivery delivery, int index, string reason)
        {
            delivery.State = "expired";
            delivery.ExpireReason = reason;
            Deliveries.RemoveAt(index);
            pendingDeliveries = Deliveries.Count;
            expiredDeliveries++;
            expiredLastTick++;
            // 过期是终态：触发事实必须记录它没有执行，而不是只留下一个计数。
            DebugTriggerSources.AppendTriggerEvent(
                UnityCliDebugSources.Trigger,
                DebugTriggerSources.HostSignal,
                delivery.Signal,
                string.Empty,
                delivery.Revision,
                delivery.TriggeredThreadId,
                0,
                delivery.TriggeredAtMonotonicMs,
                delivery.QueuedAtMonotonicMs,
                0d,
                true,
                reason + (delivery.TargetKey.Length == 0 ? string.Empty : " target=" + delivery.TargetKey));
        }

        private static long ExpirePendingLocked(double now)
        {
            var expired = 0L;
            for (var index = 0; index < Deliveries.Count; index++)
            {
                var delivery = Deliveries[index];
                if (!string.Equals(delivery.State, "pending", StringComparison.Ordinal)) continue;
                if (now <= delivery.ExpiresAtMonotonicMs) continue;
                ExpireLocked(delivery, index, "expired");
                index--;
                expired++;
            }
            return expired;
        }

        /// <summary>记录一个执行点段的派发成本；样本有界，overrun 只作为事后证据。editor tick 与 PlayerLoop phase 段共用这一份观测。</summary>
        private static void RecordTickCost(double milliseconds)
        {
            lock (Sync)
            {
                tickCount++;
                lastTickMilliseconds = milliseconds;
                if (milliseconds > maxTickMilliseconds) maxTickMilliseconds = milliseconds;
                if (milliseconds > DebugLimits.SchedulerTickOverrunMs) overrunTicks++;
                TickCosts.Enqueue(milliseconds);
                while (TickCosts.Count > DebugLimits.SchedulerTickSamples) TickCosts.Dequeue();
            }
        }
    }

    /// <summary>每-tick 调度观测的投影；把有界样本归约为稳定字段，不在 Describe 里复制队列。</summary>
    internal static class DebugSchedulerTicks
    {
        internal static Dictionary<string, object> Observe(long tickCount, double lastTickMilliseconds, double maxTickMilliseconds, long overrunTicks, Queue<double> costs, long signalRecursionRefusals)
        {
            var report = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tickCount", tickCount },
                { "lastTickMilliseconds", lastTickMilliseconds },
                { "maxTickMilliseconds", maxTickMilliseconds },
                { "overrunTicks", overrunTicks },
                { "overrunThresholdMs", (long)DebugLimits.SchedulerTickOverrunMs },
                { "tickSamples", (long)costs.Count },
                { "maxTickSamples", (long)DebugLimits.SchedulerTickSamples },
                { "signalRecursionRefusals", signalRecursionRefusals },
                { "maxSignalRecursionDepth", (long)DebugLimits.MaxSignalRecursionDepth }
            };
            var sum = 0d;
            foreach (var cost in costs) sum += cost;
            report["meanTickMilliseconds"] = costs.Count == 0 ? 0d : sum / costs.Count;
            return report;
        }
    }
}
#endif
