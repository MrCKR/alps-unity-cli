#if UNITY_EDITOR
using System.Diagnostics;
using System.Threading;

namespace UnityCli
{
    /// <summary>
    /// debug 域编辑器侧时钟与主线程身份来源。
    ///
    /// 规则：
    /// - Unity 真实帧号只能在主线程读取；本文件中的 ReadUnityFrame 是 debug 域唯一读取点。
    /// - off-main 追加路径只能引用主线程缓存下来的 LastObservedMainFrame，因此它拿不到帧提供者。
    /// - 主线程身份未绑定（值为 0）时一律按 off-main 处理：无法证明主线程身份就不得读取主线程 API。
    /// </summary>
    internal static class DebugEventClocks
    {
        private static readonly Stopwatch MonotonicWatch = Stopwatch.StartNew();
        private static long observedMainFrame;
        private static int mainThreadId;

        /// <summary>进程单调耗时（毫秒）；任何线程都可用。</summary>
        internal static double MonotonicMilliseconds { get { return MonotonicWatch.Elapsed.TotalMilliseconds; } }

        /// <summary>已绑定的主线程 id；0 表示尚未在已知主线程路径上绑定。</summary>
        internal static int MainThreadId { get { return Volatile.Read(ref mainThreadId); } }

        /// <summary>
        /// 最后一次在主线程观察到的 Unity 帧号；off-main 事件只能引用这个值。
        ///
        /// 硬约束：off-main 代码绝不读取 Unity 帧号。worker 侧只能引用本值（主线程发布的缓存帧号）
        /// 与它自己的单调采集时间；不得把该缓存值当作 worker 命中时刻的现场帧号。
        /// </summary>
        internal static long LastObservedMainFrame { get { return Interlocked.Read(ref observedMainFrame); } }

        /// <summary>绑定当前线程为主线程并记录一次帧观察；只允许在已知主线程路径调用。</summary>
        internal static void BindMainThread()
        {
            Volatile.Write(ref mainThreadId, Thread.CurrentThread.ManagedThreadId);
            ObserveMainThread();
        }

        /// <summary>当前线程是否已可证明为主线程。</summary>
        internal static bool IsCurrentThreadMainThread()
        {
            var bound = MainThreadId;
            return bound != 0 && bound == Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>
        /// 已知主线程路径的绑定与观察：未绑定时绑定当前线程；已绑定时刷新帧缓存。
        /// 已绑定到其它线程时返回 false，调用方不得再调用主线程 API。
        /// </summary>
        internal static bool BindOrObserveMainThread()
        {
            if (!IsCurrentThreadMainThread())
            {
                if (MainThreadId != 0) return false;
                BindMainThread();
                return true;
            }
            ObserveMainThread();
            return true;
        }

        /// <summary>主线程观察点：缓存 Unity 真实帧号供 off-main 事件引用。</summary>
        internal static void ObserveMainThread()
        {
            Interlocked.Exchange(ref observedMainFrame, ReadUnityFrame());
        }

        /// <summary>读取 Unity 真实帧号；只允许主线程调用，off-main 路径必须改用 LastObservedMainFrame 与自己的单调采集时间。</summary>
        internal static long ReadUnityFrame()
        {
            return UnityEngine.Time.frameCount;
        }
    }
}
#endif
