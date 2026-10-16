#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;

namespace UnityCli
{
    /// <summary>
    /// 命中线程记录的只读观察面。
    ///
    /// 职责：
    /// - 把「debug 设施实际观察到的线程」投影为只读事实，供 debug.thread.list 使用；
    /// - 只读已存在的线程帧记录，不创建、不驱逐、不改写任何派发状态。
    ///
    /// 规则：
    /// - 返回的是当前仍在进行命中帧的线程记录；空栈线程记录会被派发路径立即释放，因此不会长期堆积。
    /// - 本文件不参与派发语义：不读取 Unity 主线程 API，不调用业务代码，不注册任何 update 回调。
    /// </summary>
    internal static partial class MethodHookHost
    {
        /// <summary>当前仍被派发路径持有的线程记录投影；按线程 id 升序，调用方不得修改返回实例。</summary>
        internal static List<MethodHookThreadObservation> ObservedThreadFrames()
        {
            var result = new List<MethodHookThreadObservation>();
            foreach (var pair in ThreadFrames)
            {
                var frames = pair.Value;
                if (frames == null) continue;
                result.Add(new MethodHookThreadObservation { ThreadId = frames.ThreadId, ActiveFrames = frames.Count });
            }
            result.Sort((left, right) => left.ThreadId.CompareTo(right.ThreadId));
            return result;
        }

        /// <summary>本域当前算计的线程记录数量；与观察投影同源，供预算报告使用。</summary>
        internal static int ObservedThreadRecordCount { get { return Volatile.Read(ref threadRecordCount); } }

        /// <summary>本域中央 Editor tick 计数；只在主线程推进，未推进过时为 0。</summary>
        internal static long EditorTickValue { get { return Interlocked.Read(ref tickCounter); } }
    }

    /// <summary>一个被观察线程的只读帧事实。</summary>
    internal sealed class MethodHookThreadObservation
    {
        internal int ThreadId;
        internal int ActiveFrames;
    }
}
#endif
