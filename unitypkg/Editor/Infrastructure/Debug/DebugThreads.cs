#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// debug.thread-list 的唯一观察投影 owner。
    ///
    /// 职责：
    /// - 把「debug 设施实际观察到的线程」投影为公开事实：统一事件存储中产生过事件的线程，
    ///   以及 hook 派发路径当前仍持有在途命中帧的线程；
    /// - 明确声明观察范围，不把进程线程集合当成 managed thread 集合。
    ///
    /// 规则：
    /// - 只读：不创建线程记录、不驱逐在途帧、不追加事件、不注册 update 回调、不读取 Unity 帧号。
    /// - 主线程身份取自 DebugEventClocks 已绑定的绑定值；无法证明时一律报 false。
    /// - 本文件不复制线程真值：线程记录仍由 hook 派发路径拥有，事件仍由统一事件存储拥有。
    /// </summary>
    internal static class DebugThreads
    {
        private const string ListScope = "debug.thread-list";
        private const string SourceEvents = "retained-debug-events";
        private const string SourceFrames = "active-hook-hit-frames";

        /// <summary>观察范围声明；公开文本与实现共用同一来源。</summary>
        internal const string ObservationScope =
            "Observed scope: only threads this debug facility actually observed, meaning threads that produced a retained debug event "
            + "or that currently hold an in-flight hook hit frame. This is not an enumeration of every managed thread in the Editor process: "
            + "the process thread list is never used as a managed thread set, and a thread that produced nothing is simply absent.";

        /// <summary>按稳定排序与游标分页读取观察线程事实；不改变任何状态。</summary>
        internal static DebugThreadListResult List(DebugThreadListRequest request, string editorSessionId)
        {
            var observed = Observe();
            var offset = 0;
            if (request != null && !string.IsNullOrEmpty(request.Cursor))
            {
                string position;
                if (!UnityCliContinuationToken.TryParse(request.Cursor, ListScope, editorSessionId, out position)
                    || !int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset)
                    || offset < 0 || offset > observed.Count)
                    throw new BridgeCommandException(
                        "E_CONTINUATION_STALE",
                        "The thread list continuation does not match the current Editor session or observation scope.",
                        null,
                        new[] { "Start a new debug.thread-list read without a cursor." });
            }
            var limit = request == null || request.Limit == null
                ? DebugLimits.ThreadListDefaultLimit
                : Math.Min(Math.Max(1, request.Limit.Value), DebugLimits.ThreadListMaxLimit);
            var page = new List<DebugThreadView>();
            for (var index = offset; index < observed.Count && page.Count < limit; index++) page.Add(observed[index]);
            var nextOffset = offset + page.Count;
            var boundMainThreadId = DebugEventClocks.MainThreadId;
            return new DebugThreadListResult
            {
                Threads = page,
                NextCursor = nextOffset < observed.Count
                    ? UnityCliContinuationToken.Create(ListScope, editorSessionId, nextOffset.ToString(CultureInfo.InvariantCulture))
                    : null,
                Total = observed.Count,
                NextOffset = nextOffset,
                HasMore = nextOffset < observed.Count,
                ObservationScope = ObservationScope,
                EnumeratesAllManagedThreads = false,
                MainThreadId = boundMainThreadId,
                MainThreadEvidence = MainThreadEvidence(boundMainThreadId),
                Truncated = DebugEventStore.Truncated,
                Bounds = new DebugThreadBoundsView
                {
                    ThreadRecordsGlobalMax = DebugLimits.MaxThreadRecords,
                    FramesPerThreadMax = DebugLimits.MaxFramesPerThread,
                    EventsPerSessionMax = DebugLimits.MaxEventsPerSession,
                    PageDefaultLimit = DebugLimits.ThreadListDefaultLimit,
                    PageMaxLimit = DebugLimits.ThreadListMaxLimit
                },
                EditorSessionId = editorSessionId,
                RuntimeId = DebugSessionHost.RuntimeId(),
                DomainGeneration = DebugSessionHost.DomainGeneration()
            };
        }

        /// <summary>
        /// 归并两个真实来源并按主线程优先、线程 id 升序稳定排序。
        /// 事件条数来自统一事件存储的保留窗口；在途帧数来自 hook 派发路径当前持有的记录。
        /// </summary>
        private static List<DebugThreadView> Observe()
        {
            var boundMainThreadId = DebugEventClocks.MainThreadId;
            var eventRecords = new Dictionary<int, long>();
            foreach (var data in DebugEventStore.Snapshot())
            {
                if (data == null) continue;
                object rawThreadId;
                if (!data.TryGetValue("threadId", out rawThreadId) || rawThreadId == null) continue;
                var threadId = Convert.ToInt32(rawThreadId, CultureInfo.InvariantCulture);
                long count;
                eventRecords.TryGetValue(threadId, out count);
                eventRecords[threadId] = count + 1L;
            }

            var activeFrames = new Dictionary<int, int>();
            foreach (var frame in MethodHookHost.ObservedThreadFrames())
            {
                if (frame == null) continue;
                activeFrames[frame.ThreadId] = frame.ActiveFrames;
            }

            var threadIds = new List<int>();
            var seen = new HashSet<int>();
            foreach (var threadId in eventRecords.Keys)
            {
                if (seen.Add(threadId)) threadIds.Add(threadId);
            }
            foreach (var threadId in activeFrames.Keys)
            {
                if (seen.Add(threadId)) threadIds.Add(threadId);
            }

            var result = new List<DebugThreadView>(threadIds.Count);
            foreach (var threadId in threadIds)
            {
                long records;
                eventRecords.TryGetValue(threadId, out records);
                int frames;
                activeFrames.TryGetValue(threadId, out frames);
                var mainThread = boundMainThreadId != 0 && boundMainThreadId == threadId;
                var sources = new List<string>();
                if (records > 0L) sources.Add(SourceEvents);
                if (activeFrames.ContainsKey(threadId)) sources.Add(SourceFrames);
                result.Add(new DebugThreadView
                {
                    ThreadId = threadId,
                    MainThread = mainThread,
                    MainThreadEvidence = mainThread ? "bound-main-thread-id" : "not-proven-main-thread",
                    EventRecords = records,
                    ActiveFrames = frames,
                    HasThreadRecord = activeFrames.ContainsKey(threadId),
                    ObservedFrom = sources
                });
            }
            // 主线程优先、然后按线程 id 升序：分页位置只由这两个稳定字段决定。
            result.Sort((left, right) =>
            {
                if (left.MainThread != right.MainThread) return left.MainThread ? -1 : 1;
                return left.ThreadId.CompareTo(right.ThreadId);
            });
            return result;
        }

        /// <summary>主线程绑定事实的稳定说明；0 表示本域尚未在主线程路径上绑定身份。</summary>
        private static string MainThreadEvidence(int boundMainThreadId)
        {
            return boundMainThreadId == 0
                ? "No main thread identity is bound in this domain generation yet, so no observed thread is reported as the main thread."
                : "Main thread identity is the managed thread id bound on an observed main-thread path in this domain generation.";
        }
    }
}
#endif
