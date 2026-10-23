#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// debug.events-read 的公开输入契约。
    ///
    /// 所有筛选字段都为空表示读取当前尾部；cursor 是 sequence 的排他下界，与规范化筛选和 Editor Session 绑定。
    /// sourceId 与 kind 是开放字符串：新增调试来源或种类时只追加文档化取值，不需要改变 schema。
    /// </summary>
    public sealed class DebugEventsRequest
    {
        /// <summary>来源标识筛选；当前取值 hook、lifecycle、trigger、log、pause。</summary>
        public string SourceId { get; set; }

        /// <summary>来源内部种类筛选；hook 来源当前取值 prefix、postfix、custom、fault、lifecycle、supplement、coverage；pause 来源当前取值 snapshot、pause、custom、pause-skipped、pause-suppressed、pause-resume-requested。</summary>
        public string Kind { get; set; }

        /// <summary>hook 定义筛选。</summary>
        public string HookId { get; set; }

        /// <summary>运行段筛选。</summary>
        public string PlaySessionId { get; set; }

        /// <summary>产生线程筛选。</summary>
        public int? ThreadId { get; set; }

        /// <summary>配置或程序版本筛选。</summary>
        public long? Revision { get; set; }

        /// <summary>事件序列下界（含）；与 toSequence 同时给出时下界不得大于上界。</summary>
        public long? FromSequence { get; set; }

        /// <summary>事件序列上界（含）。</summary>
        public long? ToSequence { get; set; }

        /// <summary>调用身份筛选；省略表示不筛选。程序事件不携带调用身份，其 invocationId 为 0。</summary>
        public long? InvocationId { get; set; }

        /// <summary>执行点筛选；取值与事件自身的 executionPoint 完全一致才匹配。</summary>
        public string ExecutionPoint { get; set; }

        /// <summary>方法身份前缀筛选；程序事件的身份形如 programId:entry，因此该前缀可按程序收敛。</summary>
        public string MethodIdentityPrefix { get; set; }

        /// <summary>线程归属筛选；true 只读主线程事件，false 只读 worker 线程事件。</summary>
        public bool? MainThread { get; set; }

        /// <summary>事件时钟下界（含）。</summary>
        public double? FromClockValue { get; set; }

        /// <summary>事件时钟上界（含）。</summary>
        public double? ToClockValue { get; set; }

        /// <summary>页大小；缺省使用 debug 域默认页大小。</summary>
        [UnityCliRange(1, DebugLimits.EventsMaxLimit)] public int? Limit { get; set; }

        /// <summary>继续读取位置；缺省返回当前尾部。</summary>
        public string Cursor { get; set; }

        /// <summary>等待新事件的上限（毫秒）；0 表示立即返回，到期返回当前尾部而不是失败。</summary>
        [UnityCliRange(0, DebugLimits.EventsMaxWaitMs)] public int? WaitMs { get; set; }
    }

    /// <summary>
    /// 一条统一调试事件。字段名与 hook 读取路径原有字段保持一致，并补充统一身份、时钟溯源与完整性标记。
    /// </summary>
    public sealed class DebugEventItem
    {
        /// <summary>全局单调 sequence。</summary>
        public long Sequence { get; set; }

        /// <summary>来源标识，见 UnityCliDebugSources。</summary>
        public string SourceId { get; set; }

        /// <summary>来源内部业务种类。</summary>
        public string Kind { get; set; }

        /// <summary>hook 定义 id；非 hook 来源为空。</summary>
        public string HookId { get; set; }

        /// <summary>配置或程序版本。</summary>
        public long Revision { get; set; }

        /// <summary>调用身份；不适用时为 0。</summary>
        public long InvocationId { get; set; }

        /// <summary>产生该事件的托管线程 id。</summary>
        public int ThreadId { get; set; }

        /// <summary>是否在主线程产生。</summary>
        public bool MainThread { get; set; }

        /// <summary>捕获位置描述。</summary>
        public string ExecutionPoint { get; set; }

        /// <summary>时钟种类，见 UnityCliDebugClocks。</summary>
        public string ClockKind { get; set; }

        /// <summary>时钟种类对应的读数。</summary>
        public double ClockValue { get; set; }

        /// <summary>主线程事件记录的 Unity 真实帧号。</summary>
        public long Frame { get; set; }

        /// <summary>off-main 事件携带的最后一次主线程观察帧号；主线程事件为 0。</summary>
        public long LastObservedMainFrame { get; set; }

        /// <summary>Play 运行段标识。</summary>
        public string PlaySessionId { get; set; }

        /// <summary>逻辑时钟段标识。</summary>
        public string ClockSegment { get; set; }

        /// <summary>调用耗时（毫秒）。</summary>
        public double ElapsedMilliseconds { get; set; }

        /// <summary>该事件是否来自采样流；false 表示完整流。</summary>
        public bool Sampled { get; set; }

        /// <summary>来源记录该事件时的采样步长；1 表示逐步记录。</summary>
        public int SampleEvery { get; set; }

        /// <summary>回调尚未编译完成时命中只被记录。</summary>
        public bool CallbackPending { get; set; }

        /// <summary>被观察实例身份摘要。</summary>
        public string Instance { get; set; }

        /// <summary>被拦截方法的稳定身份。</summary>
        public string MethodIdentity { get; set; }

        /// <summary>参数投影。</summary>
        [UnityCliAnyJson] public object Arguments { get; set; }

        /// <summary>结果投影。</summary>
        [UnityCliAnyJson] public object Result { get; set; }

        /// <summary>字段投影。</summary>
        [UnityCliAnyJson] public object Fields { get; set; }

        /// <summary>事件名称。</summary>
        public string Name { get; set; }

        /// <summary>附加值投影。</summary>
        [UnityCliAnyJson] public object Value { get; set; }

        /// <summary>故障码；无故障时为空。</summary>
        public string FaultCode { get; set; }

        /// <summary>故障描述；无故障时为空。</summary>
        public string FaultMessage { get; set; }

        /// <summary>事件内容在入库或来源侧被截断。</summary>
        public bool Truncated { get; set; }

        /// <summary>入库投影触发了深度、条目或字符串截断。</summary>
        public bool ProjectionTruncated { get; set; }

        /// <summary>把存储条目投影为公开字段。</summary>
        internal static DebugEventItem FromEvent(UnityCliDebugEvent entry)
        {
            if (entry == null) return null;
            return new DebugEventItem
            {
                Sequence = entry.Sequence,
                SourceId = entry.SourceId ?? string.Empty,
                Kind = entry.Kind ?? string.Empty,
                HookId = entry.HookId ?? string.Empty,
                Revision = entry.Revision,
                InvocationId = entry.InvocationId,
                ThreadId = entry.ThreadId,
                MainThread = entry.MainThread,
                ExecutionPoint = entry.ExecutionPoint ?? string.Empty,
                ClockKind = entry.ClockKind ?? string.Empty,
                ClockValue = entry.ClockValue,
                Frame = entry.Frame,
                LastObservedMainFrame = entry.LastObservedMainFrame,
                PlaySessionId = entry.PlaySessionId ?? string.Empty,
                ClockSegment = entry.ClockSegment ?? string.Empty,
                ElapsedMilliseconds = entry.ElapsedMilliseconds,
                Sampled = entry.Sampled,
                SampleEvery = entry.SampleEvery,
                CallbackPending = entry.CallbackPending,
                Instance = entry.Instance ?? string.Empty,
                MethodIdentity = entry.MethodIdentity ?? string.Empty,
                Arguments = entry.Arguments,
                Result = entry.Result,
                Fields = entry.Fields,
                Name = entry.Name ?? string.Empty,
                Value = entry.Value,
                FaultCode = entry.FaultCode ?? string.Empty,
                FaultMessage = entry.FaultMessage ?? string.Empty,
                Truncated = entry.Truncated,
                ProjectionTruncated = entry.ProjectionTruncated
            };
        }
    }

    /// <summary>
    /// debug.events-read 的公开输出契约。
    /// 分页与覆盖率字段如实描述保留窗口：缺口、丢弃与截断永远不会被伪装成完整观察。
    /// </summary>
    public sealed class DebugEventsResult
    {
        /// <summary>本页事件，按 sequence 升序。</summary>
        public List<DebugEventItem> Events { get; set; }

        /// <summary>继续读取位置；原样传回即可续读同一筛选。</summary>
        public string NextCursor { get; set; }

        /// <summary>该筛选下是否还有更旧的未读事件；true 表示需要再用 cursor 读一次。</summary>
        public bool HasMore { get; set; }

        /// <summary>当前仍可续读的最早起点；早于它的 cursor 返回 E_HOOK_EVENT_CURSOR_EXPIRED。</summary>
        public long OldestAvailableCursor { get; set; }

        /// <summary>仍保留的最早事件 sequence；空保留窗口时等于 latestSequence。</summary>
        public long OldestAvailableSequence { get; set; }

        /// <summary>存储当前尾部 sequence。</summary>
        public long LatestSequence { get; set; }

        /// <summary>因容量边界淘汰的事件条数。</summary>
        public long Dropped { get; set; }

        /// <summary>
        /// 当前保留窗口内调用方报告过无法入库的追加次数；只计数，不保留来源与内容。
        /// 每个计数都同时带一条日志行与一条“未记录”的 coverage 事实，因此不存在无法归因的计数；
        /// 归因事实随保留窗口丢弃时计数一起归零。大于 0 表示某些事件根本没有进入存储，
        /// 调用方不能把本页当作完整观察。
        /// </summary>
        public long AppendFailures { get; set; }

        /// <summary>保留窗口内是否包含采样流事件。</summary>
        public bool Sampled { get; set; }

        /// <summary>保留窗口内唯一的采样步长；0 表示没有采样事件或步长混合。</summary>
        public int SampleEvery { get; set; }

        /// <summary>保留窗口是否为不完整观察。</summary>
        public bool Truncated { get; set; }

        /// <summary>覆盖率描述；机器分支请使用上面的计数字段。</summary>
        public string Coverage { get; set; }

        /// <summary>不可读的 sequence 区间；只描述保留窗口，不声称调用未发生。</summary>
        public List<UnityCliDebugGap> Gaps { get; set; }

        /// <summary>本次读取所属的 Editor Session。</summary>
        public string EditorSessionId { get; set; }

        /// <summary>本次调用等待新事件的实际耗时（毫秒）。</summary>
        public double WaitedMilliseconds { get; set; }
    }
}
#endif
