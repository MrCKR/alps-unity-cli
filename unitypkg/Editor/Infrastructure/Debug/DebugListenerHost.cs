#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// 线程化日志触发源的唯一 owner。
    ///
    /// 职责：
    /// - 持有 Unity 进程内唯一一次 Application.logMessageReceivedThreaded 订阅；按启用日志捕获的 session 引用计数；
    /// - 把接受的日志记录作为有界事件写入唯一调试事件存储，并如实记录真实 callback 线程；
    /// - 当记录匹配声明的文本条件时发布 host 信号，使等待该信号的 driver 在中央 tick 上被唤醒。
    ///
    /// 规则：
    /// - callback 线程可能是任意托管线程，因此这里绝不读取 Unity 主线程 API、对象名或帧号；
    /// - 日志是事件的捕获点，不推断它恰好等于原日志调用的精确业务边界；
    /// - 订阅生命周期只与当前 Editor 启动的 session 定义绑定：域重载、关停与测试复位一律退订。
    /// </summary>
    internal static class DebugListenerHost
    {
        private const int MaxDiagnosticBytes = 512;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, LogCaptureSettings> Sessions = new Dictionary<string, LogCaptureSettings>(StringComparer.Ordinal);
        private static bool subscribed;
        private static long capturedRecords;
        private static long filteredRecords;
        private static long signalTriggers;
        private static int lastCallbackThreadId;

        private sealed class LogCaptureSettings
        {
            internal string SessionId;
            internal int MinimumSeverity;
            internal string MessageContains;
            internal string Signal;
        }

        /// <summary>捕获配置的规范化结果；不可用时返回 null 并把稳定原因写入 reason。</summary>
        internal static string Normalize(DebugLogCaptureSpec spec, out string reason, out string level)
        {
            reason = string.Empty;
            level = DebugLogCaptureLevels.Off;
            if (spec == null || spec.Enabled != true) return null;
            var requested = string.IsNullOrEmpty(spec.Level) ? DebugLogCaptureLevels.All : spec.Level;
            if (!DebugLogCaptureLevels.IsDefined(requested))
            {
                reason = "level-unknown";
                return null;
            }
            if (spec.MessageContains != null && spec.MessageContains.Length > DebugLimits.MaxSessionLabelBytes)
            {
                reason = "message-contains-too-long";
                return null;
            }
            level = requested;
            return requested;
        }

        /// <summary>登记一个启用日志捕获的 session；返回非 null 的稳定失败原因时不改变订阅状态。</summary>
        internal static string RegisterSession(string sessionId, string level, string messageContains, string signal)
        {
            if (string.IsNullOrEmpty(sessionId)) return "session-required";
            var severity = DebugLogCaptureLevels.SeverityOf(level);
            if (severity < 0) return "level-unknown";
            lock (Sync)
            {
                Sessions[sessionId] = new LogCaptureSettings
                {
                    SessionId = sessionId,
                    MinimumSeverity = severity,
                    MessageContains = messageContains ?? string.Empty,
                    Signal = signal ?? string.Empty
                };
                if (subscribed) return null;
                Application.logMessageReceivedThreaded += OnLogMessage;
                subscribed = true;
                UnityCliBridge.WriteLog("debug_log_capture_subscribed sessions=" + Sessions.Count.ToString(CultureInfo.InvariantCulture));
                return null;
            }
        }

        /// <summary>释放一个 session 的日志捕获；最后一个 session 释放时退订。</summary>
        internal static bool ReleaseSession(string sessionId)
        {
            lock (Sync)
            {
                if (string.IsNullOrEmpty(sessionId) || !Sessions.Remove(sessionId)) return false;
                if (Sessions.Count > 0 || !subscribed) return true;
                Application.logMessageReceivedThreaded -= OnLogMessage;
                subscribed = false;
                UnityCliBridge.WriteLog("debug_log_capture_unsubscribed");
                return true;
            }
        }

        internal static bool IsSubscribed { get { lock (Sync) return subscribed; } }

        /// <summary>日志捕获与触发事实的结构化投影；无订阅时计数保持当前真实值。</summary>
        internal static Dictionary<string, object> Describe()
        {
            lock (Sync)
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "subscribed", subscribed },
                    { "sessionsWithCapture", (long)Sessions.Count },
                    { "capturedRecords", capturedRecords },
                    { "filteredRecords", filteredRecords },
                    { "signalTriggers", signalTriggers },
                    { "lastCallbackThreadId", (long)lastCallbackThreadId },
                    { "maxPlannedPerSession", (long)DebugLimits.MaxPlannedInjectionsPerSession }
                };
            }
        }

        /// <summary>本次 Unity 启动的机制状态作废；域重载、关停与测试复位共用这里。</summary>
        internal static void Reset()
        {
            lock (Sync)
            {
                if (subscribed)
                {
                    Application.logMessageReceivedThreaded -= OnLogMessage;
                    subscribed = false;
                }
                Sessions.Clear();
                capturedRecords = 0L;
                filteredRecords = 0L;
                signalTriggers = 0L;
                lastCallbackThreadId = 0;
            }
        }

        /// <summary>
        /// 线程化日志 callback。它在真实 callback 线程上运行，因此只做托管字符串比较与有界事件写入；
        /// 记录本身不是业务边界，触发事实另行记录为 host-signal 触发。
        /// </summary>
        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            try
            {
                var threadId = Thread.CurrentThread.ManagedThreadId;
                var severity = DebugLogCaptureLevels.SeverityOfLogType(type);
                var text = condition ?? string.Empty;
                var accepted = new List<LogCaptureSettings>();
                List<LogCaptureSettings> matchedSignals = null;
                lock (Sync)
                {
                    lastCallbackThreadId = threadId;
                    foreach (var settings in Sessions.Values)
                    {
                        if (severity < settings.MinimumSeverity) continue;
                        accepted.Add(settings);
                        if (settings.Signal.Length != 0 && (settings.MessageContains.Length == 0 || text.IndexOf(settings.MessageContains, StringComparison.Ordinal) >= 0))
                        {
                            matchedSignals = matchedSignals ?? new List<LogCaptureSettings>();
                            matchedSignals.Add(settings);
                        }
                    }
                    if (accepted.Count == 0) filteredRecords++;
                    else capturedRecords++;
                }
                if (accepted.Count == 0) return;
                var now = DebugEventClocks.MonotonicMilliseconds;
                DebugEventStore.Append(new UnityCliDebugEvent
                {
                    SourceId = UnityCliDebugSources.Log,
                    Kind = type.ToString().ToLowerInvariant(),
                    ExecutionPoint = "log:threaded-callback",
                    ThreadId = threadId,
                    MainThread = false,
                    ClockKind = UnityCliDebugClocks.Monotonic,
                    ClockValue = now,
                    LastObservedMainFrame = DebugEventClocks.LastObservedMainFrame,
                    Name = type.ToString(),
                    Value = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "message", UnityCliBridge.BoundedText(text, DebugLimits.MaxStoredProjectionStringBytes) },
                        { "severity", (long)severity },
                        { "capturedForSessions", (long)accepted.Count },
                        { "identityCapturedAt", "threaded-log-callback" }
                    },
                    Truncated = text.Length > DebugLimits.MaxStoredProjectionStringBytes
                });
                if (matchedSignals == null) return;
                foreach (var settings in matchedSignals)
                {
                    // host 信号只唤醒等待者并记录触发事实：日志捕获绝不解释成业务调用边界。
                    DebugScheduler.ResumeWaits(settings.Signal);
                    DebugTriggerSources.AppendTriggerEvent(
                        UnityCliDebugSources.Trigger,
                        DebugTriggerSources.HostSignal,
                        settings.Signal,
                        string.Empty,
                        0L,
                        threadId,
                        threadId,
                        now,
                        now,
                        now,
                        false,
                        "threaded-log-condition");
                    lock (Sync) signalTriggers++;
                }
            }
            catch (Exception exception)
            {
                UnityCliBridge.WriteLog("debug_log_capture_failed " + exception.GetType().Name + ": " + UnityCliBridge.BoundedText(exception.Message, MaxDiagnosticBytes));
            }
        }
    }

    /// <summary>日志捕获等级取值与严重度排序的唯一声明点。</summary>
    internal static class DebugLogCaptureLevels
    {
        internal const string Off = "off";
        internal const string Log = "log";
        internal const string Warning = "warning";
        internal const string Error = "error";
        internal const string Exception = "exception";
        internal const string All = "all";

        internal static bool IsDefined(string value)
        {
            return string.Equals(value, Off, StringComparison.Ordinal)
                || string.Equals(value, Log, StringComparison.Ordinal)
                || string.Equals(value, Warning, StringComparison.Ordinal)
                || string.Equals(value, Error, StringComparison.Ordinal)
                || string.Equals(value, Exception, StringComparison.Ordinal)
                || string.Equals(value, All, StringComparison.Ordinal);
        }

        /// <summary>等级对应的最低严重度；off 返回 -1，表示不订阅。</summary>
        internal static int SeverityOf(string level)
        {
            if (string.Equals(level, Log, StringComparison.Ordinal) || string.Equals(level, All, StringComparison.Ordinal)) return 0;
            if (string.Equals(level, Warning, StringComparison.Ordinal)) return 1;
            if (string.Equals(level, Error, StringComparison.Ordinal)) return 2;
            if (string.Equals(level, Exception, StringComparison.Ordinal)) return 3;
            return -1;
        }

        /// <summary>Unity LogType 的严重度；assert 与 error 同级，exception 最高。</summary>
        internal static int SeverityOfLogType(LogType type)
        {
            switch (type)
            {
                case LogType.Warning: return 1;
                case LogType.Error:
                case LogType.Assert: return 2;
                case LogType.Exception: return 3;
                default: return 0;
            }
        }
    }
}
#endif
