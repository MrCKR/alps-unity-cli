#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// 监听式触发源的冻结取值与可用性判定。
    ///
    /// Editor 与 Editor Play 是唯一执行域：PlayerLoop phase 只在 Editor Play 下存在，
    /// Edit 模式必须明确拒绝，绝不允许用中央 tick 冒充某个真实 phase。
    /// </summary>
    internal static class DebugTriggerSources
    {
        /// <summary>主线程中央 scheduler 的一个 tick；Edit 与 Play 都可用。</summary>
        internal const string EditorTick = "editor-tick";

        /// <summary>Editor Play 下的真实 Unity PlayerLoop phase；Edit 模式明确不可用。</summary>
        internal const string PlayerLoopPhase = "player-loop-phase";

        /// <summary>exact 方法 prefix/postfix/exception 命中点；由原调用线程当场同步执行。</summary>
        internal const string MethodHit = "method-hit";

        /// <summary>线程化日志 callback；记录真实 callback 线程，不推断它是精确业务边界。</summary>
        internal const string Log = "log";

        /// <summary>host 信号；记录发出者与执行者，并受递归深度约束。</summary>
        internal const string HostSignal = "host-signal";

        /// <summary>phase 现场不可用（Edit 模式或环境失败）时使用的稳定错误码；program 与 listener 共用同一语义。</summary>
        internal const string PlayerLoopPhaseUnavailableCode = "E_DEBUG_PROGRAM_SITE_UNAVAILABLE";

        internal static bool IsDefined(string value)
        {
            return string.Equals(value, EditorTick, StringComparison.Ordinal)
                || string.Equals(value, PlayerLoopPhase, StringComparison.Ordinal)
                || string.Equals(value, MethodHit, StringComparison.Ordinal)
                || string.Equals(value, Log, StringComparison.Ordinal)
                || string.Equals(value, HostSignal, StringComparison.Ordinal);
        }

        /// <summary>
        /// hook listener 可以直接订阅的触发源。editor-tick 是中央 scheduler 自己的推进点，
        /// 不是 listener 的触发源：需要主线程 tick 时用 debug.program-run 的 executionSite editor-tick。
        /// </summary>
        internal static bool IsHookTrigger(string value)
        {
            return string.Equals(value, PlayerLoopPhase, StringComparison.Ordinal)
                || string.Equals(value, MethodHit, StringComparison.Ordinal)
                || string.Equals(value, Log, StringComparison.Ordinal)
                || string.Equals(value, HostSignal, StringComparison.Ordinal);
        }

        internal static string[] All()
        {
            return new[] { EditorTick, PlayerLoopPhase, MethodHit, Log, HostSignal };
        }

        /// <summary>
        /// 请求一个当前不可用的 phase 执行域时给出同一稳定错误码与同一恢复路径。
        /// 调用方必须提供 8 条以内的可执行 nextActions；本类只统一错误身份。
        /// </summary>
        internal static BridgeCommandException PlayerLoopPhaseUnavailable(Dictionary<string, object> details, string[] nextActions)
        {
            return new BridgeCommandException(
                PlayerLoopPhaseUnavailableCode,
                "A PlayerLoop phase site only exists while the Editor is playing, so this call was refused in Edit mode instead of being served by the editor tick.",
                details,
                nextActions);
        }

        /// <summary>
        /// 记录一次触发事实：触发、排队与开始时沿，触发线程与执行线程，运行 revision 与过期状态。
        /// 触发记录进入唯一调试事件存储，因此不需要第二套真值。
        /// fields 用于已经在执行点现场物化的额外观察（例如 phase 到达时可读的静态字段），未声明时整个字段不出现。
        /// </summary>
        internal static void AppendTriggerEvent(
            string sourceId,
            string kind,
            string name,
            string hookId,
            long revision,
            int triggerThreadId,
            int executionThreadId,
            double triggeredAtMonotonicMs,
            double queuedAtMonotonicMs,
            double startedAtMonotonicMs,
            bool expired,
            string detail,
            string executionPoint = null,
            Dictionary<string, object> fields = null)
        {
            var value = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "triggeredAtMonotonicMs", triggeredAtMonotonicMs },
                { "queuedAtMonotonicMs", queuedAtMonotonicMs },
                { "startedAtMonotonicMs", startedAtMonotonicMs },
                { "triggerThreadId", (long)triggerThreadId },
                { "executionThreadId", (long)executionThreadId },
                { "revision", revision },
                { "expired", expired }
            };
            if (!string.IsNullOrEmpty(detail)) value["detail"] = detail;
            if (fields != null && fields.Count != 0) value["fields"] = fields;
            try
            {
                DebugEventStore.Append(new UnityCliDebugEvent
                {
                    SourceId = sourceId ?? UnityCliDebugSources.Trigger,
                    Kind = kind ?? string.Empty,
                    ExecutionPoint = string.IsNullOrEmpty(executionPoint) ? "trigger:" + (kind ?? string.Empty) : executionPoint,
                    HookId = hookId ?? string.Empty,
                    Revision = revision,
                    ThreadId = executionThreadId,
                    MainThread = DebugEventClocks.IsCurrentThreadMainThread(),
                    ClockKind = UnityCliDebugClocks.Monotonic,
                    ClockValue = startedAtMonotonicMs,
                    Frame = DebugEventClocks.LastObservedMainFrame,
                    Name = name ?? string.Empty,
                    Value = value
                });
            }
            catch (Exception exception)
            {
                UnityCliBridge.WriteLog("debug_trigger_event_failed " + exception.GetType().Name);
            }
        }
    }

    /// <summary>
    /// 同一 session 的计划/待执行注入与输入的唯一账本。
    ///
    /// listener 注册与显式输入共用同一计数上限：两者都是“未来会被执行的条目”，
    /// 因此必须一起受 MaxPlannedInjectionsPerSession 约束，不能各留一份预算。
    /// 账本只保存稳定身份与种类，不保存定义、回调或业务对象。
    /// </summary>
    internal static class DebugTriggerBudget
    {
        /// <summary>计划条目的种类取值；计数上限对两者合并生效。</summary>
        internal const string KindListener = "listener";
        internal const string KindInjection = "injection";
        internal const string KindInput = "input";

        internal static readonly string[] Kinds = { KindListener, KindInjection, KindInput };

        private sealed class Plan
        {
            internal string SessionId;
            internal string Kind;
            internal double PlannedAtMonotonicMs;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Plan> Plans = new Dictionary<string, Plan>(StringComparer.Ordinal);

        internal static bool IsDefinedKind(string kind)
        {
            return kind != null && Kinds.Contains(kind, StringComparer.Ordinal);
        }

        /// <summary>为同一 session 保留一个计划槽位；超出上限时返回非 null 的稳定原因。</summary>
        internal static string TryPlan(string sessionId, string kind, string ownerId, out int used)
        {
            lock (Sync)
            {
                used = CountForLocked(sessionId);
                if (string.IsNullOrEmpty(sessionId)) return "session-required";
                if (string.IsNullOrEmpty(ownerId)) return "owner-required";
                if (!IsDefinedKind(kind)) return "kind-unknown";
                Plan existing;
                if (Plans.TryGetValue(ownerId, out existing))
                {
                    if (string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal)
                        && string.Equals(existing.Kind, kind, StringComparison.Ordinal)) return null;
                    return "owner-conflict";
                }
                if (used >= DebugLimits.MaxPlannedInjectionsPerSession) return "limit-exceeded";
                Plans[ownerId] = new Plan { SessionId = sessionId, Kind = kind, PlannedAtMonotonicMs = DebugEventClocks.MonotonicMilliseconds };
                used = CountForLocked(sessionId);
                return null;
            }
        }

        /// <summary>释放一个计划槽位；未登记时是幂等 no-op。</summary>
        internal static bool Release(string ownerId)
        {
            lock (Sync) return !string.IsNullOrEmpty(ownerId) && Plans.Remove(ownerId);
        }

        internal static int PlannedFor(string sessionId)
        {
            lock (Sync) return CountForLocked(sessionId);
        }

        internal static int CountOfKind(string sessionId, string kind)
        {
            lock (Sync)
            {
                if (string.IsNullOrEmpty(sessionId)) return 0;
                return Plans.Values.Count(plan => string.Equals(plan.SessionId, sessionId, StringComparison.Ordinal)
                    && string.Equals(plan.Kind, kind, StringComparison.Ordinal));
            }
        }

        /// <summary>该 session 当前是否已经登记了同一 owner 的计划条目。</summary>
        internal static bool HasPlan(string ownerId)
        {
            lock (Sync) return !string.IsNullOrEmpty(ownerId) && Plans.ContainsKey(ownerId);
        }

        /// <summary>
        /// 计划/待执行注入预算的结构化事实。待执行投递数来自唯一的中央投递队列，本账本不另存一份。
        /// </summary>
        internal static Dictionary<string, object> Describe(string sessionId)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "listenersPlanned", (long)CountOfKind(sessionId, KindListener) },
                { "injectionsPlanned", (long)(CountOfKind(sessionId, KindInjection) + CountOfKind(sessionId, KindInput)) },
                { "plannedTotal", (long)PlannedFor(sessionId) },
                { "maxPlannedPerSession", (long)DebugLimits.MaxPlannedInjectionsPerSession },
                { "pendingDeliveries", (long)DebugScheduler.PendingDeliveryCount },
                { "maxQueuedDeliveries", (long)DebugLimits.MaxProgramDeliveries }
            };
        }

        /// <summary>本次 Unity 启动的机制状态作废；域重载、Play 段结束与测试复位共用这里。</summary>
        internal static void Reset()
        {
            lock (Sync) Plans.Clear();
        }

        private static int CountForLocked(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return 0;
            return Plans.Values.Count(plan => string.Equals(plan.SessionId, sessionId, StringComparison.Ordinal));
        }
    }
}
#endif
