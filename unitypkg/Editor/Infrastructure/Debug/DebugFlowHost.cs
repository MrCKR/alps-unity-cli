#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// 瞬态流的唯一 owner：持有活跃流、解释等待、结算三只时钟与终态，并把机制状态限制在本次域内。
    ///
    /// 规则：
    /// - 推进只有一个入口：中央 scheduler 的 editor tick；本类不注册任何 update 回调。
    /// - 用户代码只在本类置位 executing 期间运行，借此拒绝嵌套提交，避免"流等 CLI、CLI 等流"。
    /// - 三只时钟分开：请求等待由调用方的 waiter 负责，流预算与单次等待超时由本类记账。
    /// - debug.pause 停住主线程时段不推进，因此预算按"未暂停的时长"累计；暂停不会白送预算。
    /// - 终态是闭集且不可变：completed / failed / timed-out / cancelled / aborted。
    /// </summary>
    internal static class DebugFlowHost
    {
        internal const string StateRunning = "running";
        internal const string StateCompleted = "completed";
        internal const string StateFailed = "failed";
        internal const string StateTimedOut = "timed-out";
        internal const string StateCancelled = "cancelled";
        internal const string StateAborted = "aborted";

        /// <summary>一条流从提交到终态的全部事实；终态之后仍保留在有界退役表里供重复查询。</summary>
        internal sealed class FlowRecord
        {
            internal string FlowId = string.Empty;
            internal string SessionId = string.Empty;
            internal IEnumerator Body;
            internal string State = StateRunning;
            internal string FailureTag = string.Empty;
            internal string ErrorCode = string.Empty;
            internal string ErrorMessage = string.Empty;
            internal object Result;
            internal bool ResultTruncated;
            internal double CreatedMonotonicMs;
            internal double BudgetMs;
            internal double ConsumedMs;
            internal double LastAdvanceMonotonicMs;
            internal long Steps;
            internal long Samples;
            internal object LastObserved;
            internal bool CancelRequested;
            internal bool StartedInPlayMode;
            internal bool Busy;
            internal DebugFlowWait Wait;
            internal double WaitElapsedMs;
            internal long WaitFramesRemaining;

            /// <summary>Play 模式下等帧的计数器基线；0 表示尚未采样。</summary>
            internal long WaitFrameBaseline;
            internal bool WaitSignalSeen;

            /// <summary>本流注册的日志信号名；等于 "flow-log-<id>" 时本条流拥有一个日志捕获。</summary>
            internal string WaitLogSignal = string.Empty;

            /// <summary>本流是否已在 listener owner 上登记日志捕获；嵌套在 All/Any 里的日志等待也要计数。</summary>
            internal bool WaitLogRegistered;

            internal bool Terminal { get { return !string.Equals(State, StateRunning, StringComparison.Ordinal); } }
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, FlowRecord> Active = new Dictionary<string, FlowRecord>(StringComparer.Ordinal);
        private static readonly List<FlowRecord> Retired = new List<FlowRecord>();
        private static long nextFlowIndex = 1L;
        private static bool executingUserCode;

        /// <summary>当前是否正在运行某条流的用户代码；提交入口据此拒绝嵌套。</summary>
        internal static bool IsExecutingUserCode { get { lock (Sync) return executingUserCode; } }

        internal static int ActiveCount { get { lock (Sync) return Active.Count; } }

        internal static int ActiveForSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return 0;
            var count = 0;
            lock (Sync)
            {
                foreach (var pair in Active)
                {
                    if (string.Equals(pair.Value.SessionId, sessionId, StringComparison.Ordinal)) count++;
                }
            }
            return count;
        }

        /// <summary>
        /// 登记一条新流。并发额度按 session 与 Unity 实例两级同时生效，超出立即拒绝而不是排队：
        /// 排队会把不可预测的延迟变成调用方无法归因的失败。
        /// </summary>
        internal static FlowRecord Create(IEnumerator body, string sessionId, double budgetMs)
        {
            if (body == null) throw Invalid("Flow body is required.");
            if (budgetMs <= 0d || budgetMs > DebugLimits.FlowBudgetMaxMs)
                throw Invalid("flowBudgetMs must be inside the declared flow budget.");
            lock (Sync)
            {
                // 嵌套提交在这里统一拒绝：任何提交路径都不能让两条流互相等待同一条请求。
                if (executingUserCode)
                    throw new BridgeCommandException(
                        "E_DEBUG_FLOW_NESTED",
                        "A flow cannot be submitted from inside another flow.",
                        null,
                        new[] { "Return from the running flow first, then submit the next one." });
                if (!string.IsNullOrEmpty(sessionId) && CountLocked(sessionId) >= DebugLimits.MaxFlowsPerSession)
                    throw LimitExceeded("session", CountLocked(sessionId), DebugLimits.MaxFlowsPerSession);
                if (Active.Count >= DebugLimits.MaxFlowsPerInstance)
                    throw LimitExceeded("instance", Active.Count, DebugLimits.MaxFlowsPerInstance);
                var record = new FlowRecord
                {
                    FlowId = "flow-" + (nextFlowIndex++).ToString(CultureInfo.InvariantCulture),
                    SessionId = sessionId ?? string.Empty,
                    Body = body,
                    CreatedMonotonicMs = DebugEventClocks.MonotonicMilliseconds,
                    BudgetMs = budgetMs,
                    StartedInPlayMode = EditorApplication.isPlaying
                };
                Active[record.FlowId] = record;
                return record;
            }
        }

        /// <summary>按 id 查找；活跃表优先，其次是有界退役表，保证终态可重复读取。</summary>
        internal static FlowRecord Find(string flowId)
        {
            if (string.IsNullOrEmpty(flowId)) return null;
            lock (Sync)
            {
                FlowRecord record;
                if (Active.TryGetValue(flowId, out record)) return record;
                for (var index = 0; index < Retired.Count; index++)
                {
                    if (string.Equals(Retired[index].FlowId, flowId, StringComparison.Ordinal)) return Retired[index];
                }
                return null;
            }
        }

        /// <summary>取消一条流；已是终态时保持原终态，取消不会覆盖已发生的事实。</summary>
        internal static void Cancel(FlowRecord record)
        {
            if (record == null || record.Terminal) return;
            lock (Sync)
            {
                if (record.Terminal) return;
                record.CancelRequested = true;
                FinishLocked(record, StateCancelled, "cancelled", null);
            }
        }

        /// <summary>命名信号到达；命中等待该信号的流在下一个 tick 结算。</summary>
        internal static void NotifySignal(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (Sync)
            {
                foreach (var pair in Active)
                {
                    var record = pair.Value;
                    if (record.Terminal || record.Wait == null) continue;
                    var isLogSignal = record.WaitLogSignal.Length > 0 && string.Equals(record.WaitLogSignal, name, StringComparison.Ordinal);
                    if (isLogSignal || MentionsSignal(record.Wait, name)) record.WaitSignalSeen = true;
                }
            }
        }

        /// <summary>
        /// 中央 tick 的流推进段。只做有界工作：预算与等待结算，然后最多推进一次 iterator。
        /// 迭代器异常归约为该流的 failed 终态，不影响其它流或唯一主循环。
        /// </summary>
        internal static void Advance()
        {
            FlowRecord[] snapshot;
            lock (Sync) snapshot = new List<FlowRecord>(Active.Values).ToArray();
            var now = DebugEventClocks.MonotonicMilliseconds;
            var paused = DebugPauseHost.ActiveSiteCount > 0;
            foreach (var record in snapshot)
            {
                lock (Sync)
                {
                    if (record.Terminal || record.Busy) continue;
                    record.Busy = true;
                }
                try
                {
                    Step(record, now, paused);
                }
                finally
                {
                    lock (Sync) record.Busy = false;
                }
            }
        }

        /// <summary>域重载、Play 段结束与测试复位共用：机制状态一律作废，不跨启动保留。</summary>
        internal static void Reset()
        {
            lock (Sync)
            {
                Active.Clear();
                Retired.Clear();
                nextFlowIndex = 1L;
                executingUserCode = false;
            }
            // 日志捕获由 DebugListenerHost 统一持有：复位时释放所有 flow 键。
            DebugListenerHost.Reset();
        }

        private static void Step(FlowRecord record, double now, bool paused)
        {
            if (record.LastAdvanceMonotonicMs > 0d && !paused)
            {
                var delta = now - record.LastAdvanceMonotonicMs;
                if (delta > 0d)
                {
                    record.ConsumedMs += delta;
                    record.WaitElapsedMs += delta;
                }
            }
            record.LastAdvanceMonotonicMs = now;

            lock (Sync)
            {
                if (record.Terminal) return;
                if (record.CancelRequested)
                {
                    FinishLocked(record, StateCancelled, "cancelled", null);
                    return;
                }
                if (record.ConsumedMs > record.BudgetMs)
                {
                    FinishLocked(record, StateTimedOut, "budget", null);
                    return;
                }
                if (record.StartedInPlayMode != EditorApplication.isPlaying)
                {
                    FinishLocked(record, StateAborted, "play-mode-exit", null);
                    return;
                }
            }

            if (record.Wait != null)
            {
                bool settled;
                try
                {
                    settled = TrySettleWait(record);
                }
                catch (Exception exception)
                {
                    // 条件求值抛异常必须归约为该流终态：否则一次错误的条件会跑到超时才暴露。
                    lock (Sync) FinishLocked(record, StateFailed, "predicate-exception", exception);
                    return;
                }
                if (!settled) return;
            }

            AdvanceBody(record, now);
        }

        private static void AdvanceBody(FlowRecord record, double now)
        {
            bool moved;
            lock (Sync) executingUserCode = true;
            try
            {
                moved = record.Body.MoveNext();
            }
            catch (Exception exception)
            {
                lock (Sync)
                {
                    FinishLocked(record, StateFailed, "user-code-exception", exception);
                }
                return;
            }
            finally
            {
                lock (Sync) executingUserCode = false;
            }

            if (!moved)
            {
                lock (Sync) FinishLocked(record, StateCompleted, string.Empty, null);
                return;
            }

            var yielded = record.Body.Current;
            var wait = yielded as DebugFlowWait;
            lock (Sync)
            {
                record.Steps++;
                if (wait == null)
                {
                    // 无等待值等价于"下一帧再看"，不做隐式无界循环保护以外的解释。
                    record.Wait = null;
                    return;
                }
                if (string.Equals(wait.Kind, DebugFlowWaitKinds.Return, StringComparison.Ordinal))
                {
                    var bounded = Bounded(wait.ReturnValue);
                    record.Result = bounded.Value;
                    record.ResultTruncated = bounded.Truncated;
                    FinishLocked(record, StateCompleted, string.Empty, null);
                    return;
                }
                record.Wait = wait;
                record.WaitElapsedMs = 0d;
                record.WaitFramesRemaining = string.Equals(wait.Kind, DebugFlowWaitKinds.Frame, StringComparison.Ordinal) ? wait.Frames : 0L;
                record.WaitFrameBaseline = 0L;
                record.WaitSignalSeen = false;
                var logWait = FirstLogWait(wait);
                if (logWait != null && !record.WaitLogRegistered)
                {
                    // 键是 flow 而不是 session：同一 session 的两条流各自持有自己的日志捕获。
                    record.WaitLogSignal = "flow-log-" + record.FlowId;
                    // 空等级必须归一到 log，否则 owner 会以 level-unknown 拒绝登记。
                    var level = string.IsNullOrEmpty(logWait.LogLevel) ? "log" : logWait.LogLevel;
                    var refused = DebugListenerHost.RegisterSession("flow-" + record.FlowId, level, logWait.LogPattern, record.WaitLogSignal);
                    if (refused != null)
                    {
                        record.Wait = null;
                        FinishLocked(record, StateFailed, "log-capture-rejected", null);
                        record.ErrorCode = "E_DEBUG_FLOW_LOG_CAPTURE_REJECTED";
                        record.ErrorMessage = refused;
                        return;
                    }
                    record.WaitLogRegistered = true;
                }
                record.LastAdvanceMonotonicMs = now;
            }
        }

        /// <summary>结算当前等待；返回 true 表示等待已结束，可以推进 iterator。</summary>
        private static bool TrySettleWait(FlowRecord record)
        {
            DebugFlowWait wait;
            double elapsed;
            long frames;
            bool signalSeen;
            lock (Sync)
            {
                wait = record.Wait;
                elapsed = record.WaitElapsedMs;
                frames = record.WaitFramesRemaining;
                signalSeen = record.WaitSignalSeen;
            }
            if (wait == null) return true;

            object observed = null;
            if (string.Equals(wait.Kind, DebugFlowWaitKinds.Frame, StringComparison.Ordinal))
            {
                // Play 模式下必须等到真实帧计数增长：tick 数量不等于帧数。
                if (EditorApplication.isPlaying)
                {
                    var current = DebugEventClocks.ReadUnityFrame();
                    long baseline;
                    lock (Sync)
                    {
                        if (record.WaitFrameBaseline == 0L) record.WaitFrameBaseline = current;
                        baseline = record.WaitFrameBaseline;
                        record.Samples++;
                    }
                    if (current - baseline >= wait.Frames) return SettleWait(record, wait, true, false, current);
                    if (elapsed > DebugLimits.FlowFrameWaitStallMs) return SettleWait(record, wait, false, true, current);
                    return false;
                }
                // Edit 模式没有推进的帧计数，退化为 editor tick 计数，并在 describe 里声明这一点。
                frames--;
                lock (Sync)
                {
                    record.WaitFramesRemaining = frames;
                    record.Samples++;
                }
                if (frames > 0L) return false;
                return SettleWait(record, wait, true, false, observed);
            }

            bool satisfied;
            bool childTimeout;
            var ok = Evaluate(wait, elapsed, signalSeen, record.WaitLogSignal, out satisfied, out childTimeout, out observed);
            lock (Sync)
            {
                record.Samples++;
                if (observed != null) record.LastObserved = observed;
            }
            if (!ok) return false;
            if (satisfied) return SettleWait(record, wait, true, false, observed);
            if (childTimeout) return SettleWait(record, wait, false, true, observed);
            if (wait.TimeoutMilliseconds > 0d && elapsed > wait.TimeoutMilliseconds)
                return SettleWait(record, wait, false, true, observed);
            return false;
        }

        private static bool SettleWait(FlowRecord record, DebugFlowWait wait, bool satisfied, bool timedOut, object observed)
        {
            var result = new DebugFlowWaitResult
            {
                Kind = wait.Kind,
                Satisfied = satisfied,
                TimedOut = timedOut,
                ElapsedMs = record.WaitElapsedMs,
                Samples = record.Samples,
                LastObserved = observed ?? record.LastObserved
            };
            lock (Sync)
            {
                record.Wait = null;
                record.WaitElapsedMs = 0d;
                record.WaitFramesRemaining = 0L;
                record.WaitFrameBaseline = 0L;
                record.WaitSignalSeen = false;
            }
            if (record.WaitLogRegistered)
            {
                record.WaitLogRegistered = false;
                DebugListenerHost.ReleaseSession("flow-" + record.FlowId);
            }
            DebugFlow.ApplyLast(result);
            return true;
        }

        // 递归求值组合等待：返回 false 表示尚未结束（例如 All 中还有子项未满足）。
        private static bool Evaluate(DebugFlowWait wait, double elapsed, bool signalSeen, string logSignal, out bool satisfied, out bool timedOut, out object observed)
        {
            satisfied = false;
            timedOut = false;
            observed = null;
            switch (wait.Kind)
            {
                case DebugFlowWaitKinds.Time:
                    satisfied = elapsed >= wait.DelayMilliseconds;
                    return true;
                case DebugFlowWaitKinds.Signal:
                    satisfied = signalSeen;
                    return true;
                case DebugFlowWaitKinds.Log:
                    // 命中经 DebugListenerHost 发信号，再由 ResumeWaits 置 WaitSignalSeen。
                    satisfied = signalSeen;
                    if (satisfied) observed = logSignal;
                    return true;
                case DebugFlowWaitKinds.Condition:
                    satisfied = wait.Condition != null && wait.Condition();
                    observed = satisfied;
                    return true;
                case DebugFlowWaitKinds.All:
                case DebugFlowWaitKinds.Any:
                {
                    var isAll = string.Equals(wait.Kind, DebugFlowWaitKinds.All, StringComparison.Ordinal);
                    var anySatisfied = false;
                    var allSatisfied = true;
                    foreach (var child in wait.Waits)
                    {
                        bool childSatisfied;
                        bool childTimedOut;
                        object childObserved;
                        var done = Evaluate(child, elapsed, signalSeen, logSignal, out childSatisfied, out childTimedOut, out childObserved);
                        if (childObserved != null) observed = childObserved;
                        if (childTimedOut) timedOut = true;
                        if (done && childSatisfied) anySatisfied = true;
                        if (!(done && childSatisfied)) allSatisfied = false;
                    }
                    satisfied = isAll ? allSatisfied : anySatisfied;
                    if (isAll && timedOut) return true;
                    if (!isAll && anySatisfied) return true;
                    if (!isAll && timedOut) return true;
                    return true;
                }
                default:
                    return true;
            }
        }

        private static void FinishLocked(FlowRecord record, string state, string failureTag, Exception exception)
        {
            record.State = state;
            record.FailureTag = failureTag ?? string.Empty;
            if (exception != null)
            {
                record.ErrorCode = string.Equals(failureTag, "predicate-exception", StringComparison.Ordinal)
                    ? "E_DEBUG_FLOW_PREDICATE_FAILED"
                    : "E_DEBUG_FLOW_USER_CODE_FAILED";
                record.ErrorMessage = UnityCliBridge.BoundedText(exception.GetType().Name + ": " + exception.Message, 2048);
            }
            record.Wait = null;
            record.Body = null;
            // 终态可能与日志等待同时发生：未结算的捕获必须在这里释放。
            if (record.WaitLogRegistered)
            {
                record.WaitLogRegistered = false;
                DebugListenerHost.ReleaseSession("flow-" + record.FlowId);
            }
            if (Active.Remove(record.FlowId))
            {
                Retired.Add(record);
                while (Retired.Count > DebugLimits.MaxRetiredFlows) Retired.RemoveAt(0);
            }
        }

        // 组合等待里的日志子项同样需要捕获：返回第一个日志子等待，供登记用的 pattern 与 level。
        private static DebugFlowWait FirstLogWait(DebugFlowWait wait)
        {
            if (wait == null) return null;
            if (string.Equals(wait.Kind, DebugFlowWaitKinds.Log, StringComparison.Ordinal)) return wait;
            if (wait.Waits == null) return null;
            foreach (var child in wait.Waits)
            {
                var found = FirstLogWait(child);
                if (found != null) return found;
            }
            return null;
        }

        private static bool MentionsSignal(DebugFlowWait wait, string name)
        {
            if (wait == null) return false;
            if (string.Equals(wait.Kind, DebugFlowWaitKinds.Signal, StringComparison.Ordinal))
                return string.Equals(wait.SignalName, name, StringComparison.Ordinal);
            if (wait.Waits == null) return false;
            foreach (var child in wait.Waits)
            {
                if (MentionsSignal(child, name)) return true;
            }
            return false;
        }

        private static int CountLocked(string sessionId)
        {
            var count = 0;
            foreach (var pair in Active)
            {
                if (string.Equals(pair.Value.SessionId, sessionId, StringComparison.Ordinal)) count++;
            }
            return count;
        }

        /// <summary>结果值有界投影：超出声明上限时改为说明字符串并置截断标记。</summary>
        private static BoundedValueResult Bounded(object value)
        {
            if (value == null) return new BoundedValueResult(null, false);
            string serialized;
            try
            {
                serialized = UnityCliSerializer.Serialize(value);
            }
            catch (Exception exception)
            {
                return new BoundedValueResult("<unserializable: " + exception.GetType().Name + ">", true);
            }
            var bytes = System.Text.Encoding.UTF8.GetByteCount(serialized);
            if (bytes <= DebugLimits.MaxFlowResultBytes) return new BoundedValueResult(value, false);
            return new BoundedValueResult("<result exceeds " + DebugLimits.MaxFlowResultBytes.ToString(CultureInfo.InvariantCulture) + " UTF-8 bytes>", true);
        }

        private sealed class BoundedValueResult
        {
            internal BoundedValueResult(object value, bool truncated)
            {
                Value = value;
                Truncated = truncated;
            }

            internal object Value { get; private set; }
            internal bool Truncated { get; private set; }
        }

        private static BridgeCommandException LimitExceeded(string scope, int usage, int limit)
        {
            return new BridgeCommandException(
                "E_DEBUG_FLOW_LIMIT_EXCEEDED",
                "Concurrent flow limit reached for " + scope + ".",
                new Dictionary<string, object>
                {
                    { "scope", scope },
                    { "usage", (long)usage },
                    { "limit", (long)limit }
                },
                new[]
                {
                    "Wait for an active flow to reach a terminal state, or cancel one with debug.code.flow.cancel.",
                    "Reduce how many flows run at the same time: each flow samples on the main thread, so extra flows make the observed timing less trustworthy."
                });
        }

        private static BridgeCommandException Invalid(string message)
        {
            return new BridgeCommandException("E_SCHEMA_INVALID", message, null, new[] { "Pass a positive flowBudgetMs inside the declared budget." });
        }
    }
}
#endif
