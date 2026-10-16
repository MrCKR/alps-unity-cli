#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// debug 域统一事件存储的进程级 owner。
    ///
    /// 职责：
    /// - 持有唯一的 UnityCliDebugEventBuffer，并把 DebugLimits 的唯一预算声明传给它；
    /// - 负责 Editor Session 归属、主线程身份与时钟溯源；
    /// - 是所有托管调试事件（hook、以及后续 program/log/check/pause/input/lifecycle）唯一的追加入口；
    /// - 是 debug.events.read 唯一的读取入口，并把 cursor 状态归约为稳定错误码。
    ///
    /// 线程规则：
    /// - Append 可以被任意托管线程调用；是否主线程由已绑定的主线程身份证明，不能证明时按 off-main 处理。
    /// - 只有可证明的主线程路径才会读取 Unity 真实帧号；off-main 路径只用单调时钟与主线程缓存帧号。
    /// - 读取与 cursor 编解码只在主线程调用。
    /// - 本类不注册任何 update 回调：主线程不 tick 时追加与读取仍然可用。
    /// </summary>
    internal static class DebugEventStore
    {
        private static readonly object LifecycleGate = new object();
        private static readonly UnityCliDebugGlobalBudget GlobalBudget = new UnityCliDebugGlobalBudget(DebugLimits.MaxEventBytesGlobal);
        private static UnityCliDebugEventBuffer buffer;
        private static string sessionId = string.Empty;
        private static long appendFailures;

        internal static long Count { get { return BufferInstance().Count; } }
        internal static long LatestSequence { get { return BufferInstance().LatestSequence; } }
        internal static long OldestAvailableSequence { get { return BufferInstance().OldestAvailableSequence; } }
        internal static bool Truncated { get { return BufferInstance().Truncated; } }
        internal static IReadOnlyList<UnityCliDebugGap> Gaps { get { return BufferInstance().Gaps; } }

        /// <summary>
        /// 当前保留窗口内调用方报告过无法入库的追加次数；debug.events.read 把本值原样返回。
        /// 计数与它归因的事件同属一个保留窗口：窗口被丢弃时计数一起归零，
        /// 因此读到的每一次失败都必然能在同一窗口里被归因。
        /// </summary>
        internal static long AppendFailures { get { return Interlocked.Read(ref appendFailures); } }

        /// <summary>无法入库的事件在事件流中的种类，与 hook 覆盖事实共用同一约定。</summary>
        private const string CoverageKind = "coverage";

        /// <summary>补偿事实的固定名称；调用方按它找到“根本没有进入存储”的事件。</summary>
        internal const string CoverageEventName = "event-not-recorded";

        /// <summary>
        /// 追加失败的唯一记账与归因入口：计数、日志行与 coverage 事实在同一处一次完成。
        ///
        /// 本 owner 不提供只增加计数的 API，因此不存在“计数了却无法归因”的路径：
        /// debug.events.read 读到的每一次失败都带一条固定日志行，并尽力补发一条 coverage 事实，
        /// 说明丢失事件的来源、种类与名称；归因事实被保留窗口丢弃时计数同时归零。
        /// coverage 事实只用固定标量构造，不做投影、不读主线程专属 API，因此不会因同一原因再次失败；
        /// 它和所有事件一样受存储的条数与字节上限约束，不随失败次数无限增长。
        /// </summary>
        internal static void ReportAppendFailure(UnityCliDebugEvent lost, string reason)
        {
            Interlocked.Increment(ref appendFailures);
            var source = lost == null ? string.Empty : lost.SourceId;
            var kind = lost == null ? string.Empty : lost.Kind;
            var name = lost == null ? string.Empty : lost.Name;
            UnityCliBridge.WriteLog("debug_event_append_failed reason=" + (reason ?? string.Empty)
                + " source=" + source + " kind=" + kind + " name=" + name);
            try
            {
                Append(new UnityCliDebugEvent
                {
                    SourceId = source,
                    Kind = CoverageKind,
                    ExecutionPoint = "event-append",
                    Revision = lost == null ? 0L : lost.Revision,
                    ThreadId = lost == null ? Thread.CurrentThread.ManagedThreadId : lost.ThreadId,
                    MainThread = DebugEventClocks.IsCurrentThreadMainThread(),
                    ClockKind = UnityCliDebugClocks.Monotonic,
                    ClockValue = DebugEventClocks.MonotonicMilliseconds,
                    Frame = DebugEventClocks.LastObservedMainFrame,
                    MethodIdentity = lost == null ? string.Empty : lost.MethodIdentity,
                    Name = CoverageEventName,
                    Value = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "reason", reason ?? string.Empty },
                        { "action", "not-recorded" },
                        { "lostKind", kind },
                        { "lostName", name },
                        { "faultCode", lost == null ? string.Empty : lost.FaultCode }
                    }
                });
            }
            catch (Exception nested)
            {
                // 补偿事实也失败时不再递归：计数与日志行已经给出了这次丢失的可归因事实。
                UnityCliBridge.WriteLog("debug_event_coverage_failed " + nested.GetType().Name);
            }
        }

        /// <summary>统一的追加入口；按可证明的线程身份选择时钟捕获路径。</summary>
        internal static long Append(UnityCliDebugEvent entry)
        {
            if (entry == null) return BufferInstance().LatestSequence;
            if (DebugEventClocks.IsCurrentThreadMainThread()) CaptureMainThread(entry);
            else CaptureOffMainThread(entry);
            return BufferInstance().Append(entry);
        }

        /// <summary>记录不可读区间；缺口只描述保留窗口。</summary>
        internal static void RecordGap(long fromSequence, long toSequence, string reason)
        {
            BufferInstance().RecordGap(fromSequence, toSequence, reason);
        }

        /// <summary>当前 session 的快照，用于重载前 checkpoint。</summary>
        internal static List<Dictionary<string, object>> Snapshot()
        {
            return BufferInstance().Snapshot();
        }

        /// <summary>按 Editor Session 与 sequence floor 恢复事件；缺口由调用方给出。</summary>
        internal static void Restore(IEnumerable<Dictionary<string, object>> entries, long sequenceFloor, IEnumerable<UnityCliDebugGap> gaps)
        {
            EnsureSession();
            BufferInstance().Restore(entries, sequenceFloor, gaps);
        }

        /// <summary>清空事件与缺口；用于 quitting 的 session 收尾。</summary>
        internal static void Reset()
        {
            Interlocked.Exchange(ref appendFailures, 0L);
            BufferInstance().Reset();
        }

        /// <summary>当前序列位置的起点 cursor；与规范化筛选和 Editor Session 绑定。</summary>
        internal static string BeginCursor(UnityCliDebugEventFilter filter)
        {
            return BufferInstance().BeginCursor(filter, EnsureSession());
        }

        /// <summary>在单次临界区内捕获边界 sequence 与同一瞬间的淘汰、缺口计数。</summary>
        internal static UnityCliDebugEventBoundary CaptureBoundary()
        {
            return BufferInstance().CaptureBoundary();
        }

        /// <summary>读取一个 sequence 区间内仍保留的事件；不消费也不移动 cursor。</summary>
        internal static List<UnityCliDebugEvent> Range(long afterExclusive, long upToInclusive)
        {
            return BufferInstance().Range(afterExclusive, upToInclusive);
        }

        /// <summary>尾部是否已经超过给定 sequence；用于有界等待新事件。</summary>
        internal static bool HasEventsAfter(long sequence)
        {
            return BufferInstance().LatestSequence > sequence;
        }

        /// <summary>
        /// debug.events.read 的唯一读取入口：统一分页、筛选与覆盖率字段，并把 cursor 状态归约为稳定错误码。
        /// </summary>
        internal static DebugEventsResult ReadEvents(DebugEventsRequest request, double waitedMilliseconds = 0d)
        {
            var session = EnsureSession();
            var store = BufferInstance();
            var filter = Filter(request);
            var limit = PageSize(request == null ? (int?)null : request.Limit);
            var page = store.Read(filter, request == null ? null : request.Cursor, session, limit);
            if (string.Equals(page.CursorStatus, "stale", StringComparison.Ordinal))
            {
                throw new BridgeCommandException(
                    "E_CONTINUATION_STALE",
                    "The event continuation no longer matches the current Editor session or filters.",
                    "Re-read the current tail of debug.events.read without a cursor.");
            }
            if (string.Equals(page.CursorStatus, "expired", StringComparison.Ordinal))
            {
                throw new BridgeCommandException(
                    "E_HOOK_EVENT_CURSOR_EXPIRED",
                    "The event continuation predates the retained event window.",
                    new Dictionary<string, object> { { "oldestAvailableCursor", page.OldestAvailableCursor } },
                    new[] { "Re-read debug.events.read without a cursor; oldestAvailableCursor reports the new readable start." });
            }
            var events = new List<DebugEventItem>(page.Events.Count);
            foreach (var entry in page.Events) events.Add(DebugEventItem.FromEvent(entry));
            var failedAppends = AppendFailures;
            return new DebugEventsResult
            {
                Events = events,
                NextCursor = store.CursorFor(filter, session, page.NextScannedSequence),
                HasMore = page.HasMore,
                OldestAvailableCursor = page.OldestAvailableCursor,
                OldestAvailableSequence = page.OldestAvailableSequence,
                LatestSequence = page.LatestSequence,
                Dropped = page.Dropped,
                Sampled = page.Sampled,
                SampleEvery = page.SampleEvery,
                AppendFailures = failedAppends,
                Truncated = store.Truncated,
                Coverage = page.Coverage,
                Gaps = new List<UnityCliDebugGap>(store.Gaps),
                EditorSessionId = session,
                WaitedMilliseconds = waitedMilliseconds
            };
        }

        /// <summary>事件页的规范化上限；Tool 声明的 read budget 与 host 实际读取必须使用同一数值。</summary>
        internal static int PageSize(int? requested)
        {
            var value = requested ?? DebugLimits.EventsDefaultLimit;
            return Math.Min(Math.Max(1, value), DebugLimits.EventsMaxLimit);
        }

        /// <summary>等待上限的规范化；0 表示不等待。</summary>
        internal static int WaitMilliseconds(int? requested)
        {
            var value = requested ?? 0;
            return Math.Min(Math.Max(0, value), DebugLimits.EventsMaxWaitMs);
        }

        /// <summary>公开筛选字段到存储筛选的唯一映射。</summary>
        internal static UnityCliDebugEventFilter Filter(DebugEventsRequest request)
        {
            if (request == null) return new UnityCliDebugEventFilter();
            return new UnityCliDebugEventFilter
            {
                SourceId = request.SourceId,
                Kind = request.Kind,
                HookId = request.HookId,
                PlaySessionId = request.PlaySessionId,
                ThreadId = request.ThreadId,
                Revision = request.Revision,
                FromClockValue = request.FromClockValue,
                ToClockValue = request.ToClockValue
            };
        }

        /// <summary>
        /// 解析当前 Editor Session；只在主线程调用。
        /// 会话变化时丢弃旧 buffer，绝不把两个会话的事件混在同一个 sequence 空间里。
        /// </summary>
        private static string EnsureSession()
        {
            var current = UnityCliBridge.EditorSessionId();
            DebugEventClocks.BindOrObserveMainThread();
            lock (LifecycleGate)
            {
                if (buffer == null || !string.Equals(sessionId, current, StringComparison.Ordinal))
                {
                    // 换 session 前必须释放旧 buffer 占用的全局字节账本，不能把两个会话的占用叠加。
                    if (buffer != null) buffer.Reset();
                    sessionId = current;
                    // 计数归因的事实与事件同处一个保留窗口：窗口丢弃时计数必须一起归零，
                    // 否则新窗口会读到一条无法归因的历史计数。
                    Interlocked.Exchange(ref appendFailures, 0L);
                    buffer = CreateBuffer(current, 0L);
                }
                return sessionId;
            }
        }

        private static UnityCliDebugEventBuffer BufferInstance()
        {
            var current = Volatile.Read(ref buffer);
            if (current != null) return current;
            lock (LifecycleGate)
            {
                if (buffer == null) buffer = CreateBuffer(string.Empty, 0L);
                return buffer;
            }
        }

        private static UnityCliDebugEventBuffer CreateBuffer(string session, long sequenceFloor)
        {
            return new UnityCliDebugEventBuffer(
                session,
                sequenceFloor,
                new UnityCliDebugEventCapacity(
                    DebugLimits.MaxEventsPerSession,
                    DebugLimits.MaxEventBytesPerSession,
                    DebugLimits.MaxStoredEventBytes,
                    DebugLimits.MaxStoredProjectionDepth,
                    DebugLimits.MaxStoredProjectionItemsPerLevel,
                    DebugLimits.MaxStoredProjectionStringBytes,
                    DebugLimits.MaxHistoryGaps),
                GlobalBudget);
        }

        /// <summary>主线程捕获：记录主线程身份，并按声明的时钟种类取值，同时刷新供 off-main 引用的帧缓存。</summary>
        private static void CaptureMainThread(UnityCliDebugEvent entry)
        {
            entry.MainThread = true;
            entry.ThreadId = Thread.CurrentThread.ManagedThreadId;
            entry.LastObservedMainFrame = 0L;
            var kind = entry.ClockKind;
            if (string.Equals(kind, UnityCliDebugClocks.EditorTick, StringComparison.Ordinal))
            {
                // 编辑器 tick 值由推进中央 tick 的调用方给出；没有值时退回真实帧号。
                entry.ClockValue = entry.ClockValue != 0d ? entry.ClockValue : entry.Frame;
            }
            else if (string.Equals(kind, UnityCliDebugClocks.ProjectLogical, StringComparison.Ordinal))
            {
                // 项目逻辑时钟值完全由项目逻辑边界给出，捕获不改写它。
            }
            else if (string.Equals(kind, UnityCliDebugClocks.Monotonic, StringComparison.Ordinal))
            {
                entry.ClockValue = DebugEventClocks.MonotonicMilliseconds;
            }
            else
            {
                entry.ClockKind = UnityCliDebugClocks.UnityFrame;
                entry.Frame = DebugEventClocks.ReadUnityFrame();
                entry.ClockValue = entry.Frame;
            }
            DebugEventClocks.ObserveMainThread();
        }

        /// <summary>
        /// off-main 捕获：只允许单调时钟与主线程缓存帧号。
        /// 本方法刻意不引用帧提供者，因此从任意 worker 线程追加都不可能触发 Unity 主线程 API。
        /// </summary>
        private static void CaptureOffMainThread(UnityCliDebugEvent entry)
        {
            entry.MainThread = false;
            entry.ThreadId = Thread.CurrentThread.ManagedThreadId;
            entry.ClockKind = UnityCliDebugClocks.Monotonic;
            entry.ClockValue = DebugEventClocks.MonotonicMilliseconds;
            entry.Frame = 0L;
            entry.LastObservedMainFrame = DebugEventClocks.LastObservedMainFrame;
        }
    }
}
#endif
