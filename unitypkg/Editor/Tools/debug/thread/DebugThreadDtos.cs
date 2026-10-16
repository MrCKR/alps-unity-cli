#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// debug.thread.list 的公开输入契约。
    ///
    /// 分页按稳定排序的观察线程列表推进；cursor 与 Editor session 绑定。
    /// </summary>
    public sealed class DebugThreadListRequest
    {
        /// <summary>页大小；缺省使用 debug 域线程观察默认页大小。</summary>
        [UnityCliRange(1, DebugLimits.ThreadListMaxLimit)] public int? Limit { get; set; }

        /// <summary>继续读取位置；缺省返回第一页。</summary>
        public string Cursor { get; set; }
    }

    /// <summary>一个被调试设施实际观察到的线程；不声称它是进程全部 managed thread 之一。</summary>
    public sealed class DebugThreadView
    {
        /// <summary>稳定线程身份：本次 Editor 进程内的托管线程 id（观察身份，不跨进程启动保持不变）。</summary>
        public int ThreadId { get; set; }

        /// <summary>该线程是否已被证明为主线程；无法证明主线程身份时恒为 false。</summary>
        public bool MainThread { get; set; }

        /// <summary>主线程身份的判定依据说明。</summary>
        public string MainThreadEvidence { get; set; }

        /// <summary>当前保留窗口中携带该线程 id 的调试事件条数。</summary>
        public long EventRecords { get; set; }

        /// <summary>该线程当前仍在进行的 hook 命中帧数量；0 表示没有在途命中帧。</summary>
        public long ActiveFrames { get; set; }

        /// <summary>该线程当前是否占用一条 hook 线程记录。</summary>
        public bool HasThreadRecord { get; set; }

        /// <summary>观察来源；只列出真正产生该线程记录的事实来源。</summary>
        public List<string> ObservedFrom { get; set; }
    }

    /// <summary>线程观察读取的有效上限；与 DebugLimits 同源，不复制数量。</summary>
    public sealed class DebugThreadBoundsView
    {
        public int ThreadRecordsGlobalMax { get; set; }
        public int FramesPerThreadMax { get; set; }
        public int EventsPerSessionMax { get; set; }
        public int PageDefaultLimit { get; set; }
        public int PageMaxLimit { get; set; }
    }

    /// <summary>
    /// debug.thread.list 的公开输出契约。
    /// 报告只描述被观察线程与保留窗口事实，并显式声明观察范围。
    /// </summary>
    public sealed class DebugThreadListResult
    {
        /// <summary>本页线程，按主线程优先、线程 id 升序稳定排序。</summary>
        public List<DebugThreadView> Threads { get; set; }

        /// <summary>继续读取位置；原样传回即可续读同一列表。</summary>
        public string NextCursor { get; set; }

        /// <summary>当前被观察线程总数。</summary>
        public long Total { get; set; }

        /// <summary>下一页起始下标。</summary>
        public long NextOffset { get; set; }

        /// <summary>是否还有更旧的未读线程。</summary>
        public bool HasMore { get; set; }

        /// <summary>观察范围声明：本列表不是进程全部 managed thread 的枚举。</summary>
        public string ObservationScope { get; set; }

        /// <summary>本工具是否枚举了进程全部 managed thread；恒为 false。</summary>
        public bool EnumeratesAllManagedThreads { get; set; }

        /// <summary>主线程身份的绑定事实说明。</summary>
        public string MainThreadEvidence { get; set; }

        /// <summary>当前已绑定的主线程 id；0 表示本域尚未在主线程路径上绑定。</summary>
        public int MainThreadId { get; set; }

        /// <summary>保留窗口是否为不完整观察；被容量边界淘汰的事件不计入本列表的线程计数。</summary>
        public bool Truncated { get; set; }

        /// <summary>当前生效的线程观察上限。</summary>
        public DebugThreadBoundsView Bounds { get; set; }

        /// <summary>本次读取所属的 Editor session。</summary>
        public string EditorSessionId { get; set; }

        /// <summary>当前观察到的 Play runtime 段；Edit 模式为空。</summary>
        public string RuntimeId { get; set; }

        /// <summary>当前 domain generation。</summary>
        public string DomainGeneration { get; set; }
    }
}
#endif
