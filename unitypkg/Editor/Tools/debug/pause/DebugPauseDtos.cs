#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// pause 断点的配置形状。pauseTimeout 没有默认值：bounded 必须给出正整数 durationMs，
    /// unbounded 禁止 durationMs；缺失、0 或 null 一律在 effect 之前被拒绝，绝不解释为无限。
    /// </summary>
    public sealed class MethodHookPauseTimeoutSpec
    {
        /// <summary>时间边界种类：bounded 或 unbounded。</summary>
        [UnityCliRequired, UnityCliStringEnum("bounded", "unbounded")] public string Kind { get; set; }

        /// <summary>bounded 时的正整数毫秒数；unbounded 时禁止出现。没有默认值。</summary>
        public long? DurationMs { get; set; }
    }

    /// <summary>
    /// 一个 hook 定义的断点命中行为。断点不是平行引擎：它复用同一 target resolver、同一 revision 与同一事件真值。
    /// </summary>
    public sealed class MethodHookBreakpointSpec
    {
        /// <summary>snapshot 捕获配置的值后继续；pause 发布 pauseId 并停住命中线程。</summary>
        [UnityCliRequired, UnityCliStringEnum("snapshot", "pause")] public string Kind { get; set; }

        /// <summary>命中位置：prefix 在原方法前，postfix 在原方法返回后；省略等价于 prefix。</summary>
        [UnityCliStringEnum("prefix", "postfix")] public string Position { get; set; }

        /// <summary>pause 必需、snapshot 必须省略的时间边界。</summary>
        public MethodHookPauseTimeoutSpec PauseTimeout { get; set; }

        /// <summary>捕获栈的帧数上界；省略或 0 表示不捕获栈。上限由 debug 域统一声明。</summary>
        [UnityCliRange(0, DebugLimits.MaxPauseStackFrames)] public int? StackDepth { get; set; }

        /// <summary>拥有该暂停资源的 session；省略表示现场只由该定义拥有，不占用 session 资源槽位。</summary>
        public string SessionId { get; set; }
    }

    /// <summary>断点配置的公开投影。</summary>
    public sealed class MethodHookBreakpointSummary
    {
        public bool Present { get; set; }
        public string Kind { get; set; }
        public string Position { get; set; }
        public string TimeoutKind { get; set; }
        public long TimeoutDurationMs { get; set; }
        public int StackDepth { get; set; }
        public string SessionId { get; set; }
        public bool ResourceRegistered { get; set; }
        public string ResourceId { get; set; }
    }

    // ---- debug.pause-list ----

    public sealed class DebugPauseListRequest
    {
        /// <summary>只读取属于该 session 的现场。</summary>
        public string SessionId { get; set; }

        /// <summary>只读取该 hook 定义的现场。</summary>
        public string HookId { get; set; }

        /// <summary>只读取该托管线程上的现场。</summary>
        [UnityCliRange(1, 65535)] public int? ThreadId { get; set; }

        /// <summary>只读取该状态的现场；paused 表示仍停住，resuming 表示已请求解除。</summary>
        [UnityCliStringEnum("paused", "resuming")] public string State { get; set; }

        /// <summary>只读取该调用身份的现场；省略表示不筛选。</summary>
        public long? InvocationId { get; set; }

        /// <summary>只读取指定线程归属的现场；true 只读主线程现场，false 只读 worker 现场。</summary>
        public bool? MainThread { get; set; }

        [UnityCliRange(1, DebugLimits.PauseListMaxLimit)] public int? Limit { get; set; }

        public string Cursor { get; set; }
    }

    /// <summary>一个有界栈帧事实；其他帧的 locals 不可读，必须显式列出不可用原因。</summary>
    public sealed class DebugPauseStackFrameView
    {
        public long Index { get; set; }
        public string MethodIdentity { get; set; }
        public string DeclaringType { get; set; }
        public long IlOffset { get; set; }
        public bool LocalsAvailable { get; set; }
        public string UnavailableReason { get; set; }
    }

    /// <summary>现场事实的公开投影。</summary>
    public sealed class DebugPauseSiteView
    {
        public string PauseId { get; set; }
        public string State { get; set; }
        public string ReleaseReason { get; set; }
        public int ThreadId { get; set; }
        public bool MainThread { get; set; }
        public string HookId { get; set; }
        public long Revision { get; set; }
        public long InvocationId { get; set; }
        public string RuntimeId { get; set; }
        public string DomainGeneration { get; set; }
        public string MethodIdentity { get; set; }
        public string TargetKey { get; set; }
        public string Position { get; set; }
        public string SessionId { get; set; }
        public bool OwnerRegistered { get; set; }
        public string ResourceId { get; set; }
        public string TimeoutKind { get; set; }
        public long TimeoutDurationMs { get; set; }
        public string EffectiveBound { get; set; }
        public bool HasDeadline { get; set; }
        public double RemainingMs { get; set; }
        public double PausedForMs { get; set; }
        public long WaitingRequests { get; set; }
        public long MaxWaitingRequests { get; set; }
        public long ExecutingOperations { get; set; }
        public string ExecutingOperation { get; set; }
        public bool ResumeRequested { get; set; }
        public long ServedOperations { get; set; }
        public long RefusedRequests { get; set; }
        public long SuppressedNestedHits { get; set; }
    }

    /// <summary>覆盖缺口事实：超限未暂停的命中与因服务现场而抑制的嵌套命中都必须如实报告。</summary>
    public sealed class DebugPauseCoverageView
    {
        public long SkippedSites { get; set; }
        public long SuppressedNestedHits { get; set; }
        public long RefusedRequests { get; set; }
        public long ServedOperations { get; set; }
        public List<DebugPauseReasonCount> SkipReasons { get; set; }
        public List<DebugPauseReasonCount> SuppressOrigins { get; set; }
        public string Note { get; set; }
    }

    public sealed class DebugPauseReasonCount
    {
        public string Reason { get; set; }
        public long Count { get; set; }
    }

    public sealed class DebugPauseBoundsView
    {
        public long SimultaneousSitesMax { get; set; }
        public long StackFramesMax { get; set; }
        public long MailboxWaitingMax { get; set; }
        public long MailboxExecutingMax { get; set; }
        public long RetainedFinishedSitesMax { get; set; }
        public long PageDefaultLimit { get; set; }
        public long PageMaxLimit { get; set; }
    }

    public sealed class DebugPauseListResult
    {
        public List<DebugPauseSiteView> Sites { get; set; }
        public string NextCursor { get; set; }
        public long Total { get; set; }
        public long NextOffset { get; set; }
        public bool HasMore { get; set; }
        public DebugPauseCoverageView Coverage { get; set; }
        public DebugPauseBoundsView Bounds { get; set; }
        public string EditorSessionId { get; set; }
        public string RuntimeId { get; set; }
        public string DomainGeneration { get; set; }
    }

    // ---- debug.pause-wait ----

    public sealed class DebugPauseWaitRequest
    {
        /// <summary>等待精确 pauseId；省略时按下面的筛选等待任意匹配现场。</summary>
        public string PauseId { get; set; }

        public string SessionId { get; set; }
        public string HookId { get; set; }
        [UnityCliRange(1, 65535)] public int? ThreadId { get; set; }

        /// <summary>有界等待上限（毫秒）；省略表示只读取当前现场，不等待。</summary>
        [UnityCliRange(1, DebugLimits.PauseWaitMaxMs)] public int? WaitMs { get; set; }
    }

    public sealed class DebugPauseWaitResult
    {
        /// <summary>是否观察到匹配现场；false 表示等待窗口内没有出现匹配现场，不是失败。</summary>
        public bool Matched { get; set; }
        public DebugPauseSiteView Site { get; set; }
        public double WaitedMilliseconds { get; set; }
        /// <summary>固定为 false：等待结束绝不解除现场暂停。</summary>
        public bool ResumedByWait { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
    }

    // ---- debug.pause-inspect ----

    /// <summary>一次字段读取请求；在暂停线程上按声明类型解析，读取失败按字段给出原因。</summary>
    public sealed class DebugPauseFieldRequest
    {
        /// <summary>字段名。</summary>
        [UnityCliRequired] public string FieldName { get; set; }

        /// <summary>声明类型；省略时从命中实例类型向上解析。</summary>
        public string DeclaringType { get; set; }

        /// <summary>true 表示读取静态字段。</summary>
        public bool? Static { get; set; }
    }

    public sealed class DebugPauseFieldView
    {
        public string Requested { get; set; }
        public string DeclaringType { get; set; }
        public string FieldName { get; set; }
        public bool Static { get; set; }
        public bool Readable { get; set; }
        public string UnavailableReason { get; set; }
        [UnityCliAnyJson] public object Value { get; set; }
    }

    public sealed class DebugPauseInspectRequest
    {
        [UnityCliRequired] public string PauseId { get; set; }

        /// <summary>不捕获栈时传 false；省略表示按断点声明的 stackDepth 捕获。</summary>
        public bool? IncludeStack { get; set; }

        /// <summary>本次读取的栈帧上界；绝不大于 debug 域声明的上限。</summary>
        [UnityCliRange(0, DebugLimits.MaxPauseStackFrames)] public int? StackDepth { get; set; }

        /// <summary>是否读取本次调用的参数投影。</summary>
        public bool? IncludeArguments { get; set; }

        /// <summary>是否读取命中实例投影。</summary>
        public bool? IncludeInstance { get; set; }

        /// <summary>要读取的字段；都在暂停线程上解析与读取。</summary>
        [UnityCliItemsRange(0, DebugLimits.MaxPauseInspectFields)] public List<DebugPauseFieldRequest> Fields { get; set; }

        /// <summary>等待现场服务该请求的上限（毫秒）。</summary>
        [UnityCliRange(1, DebugLimits.PauseOperationTimeoutMs)] public int? WaitMs { get; set; }
    }

    public sealed class DebugPauseInspectResult
    {
        public string PauseId { get; set; }
        public DebugPauseSiteView Site { get; set; }
        public bool ServedOnPausedThread { get; set; }
        public int ServedOnThreadId { get; set; }
        public List<DebugPauseStackFrameView> Stack { get; set; }
        public bool StackCaptured { get; set; }
        [UnityCliAnyJson] public object Arguments { get; set; }
        [UnityCliAnyJson] public object Instance { get; set; }
        public string InstanceIdentity { get; set; }
        [UnityCliAnyJson] public object Result { get; set; }
        [UnityCliAnyJson] public object Exception { get; set; }
        public List<DebugPauseFieldView> Fields { get; set; }
        /// <summary>本次读取不可得的信息；只列出真实不可读项，绝不猜测。</summary>
        public List<string> Unavailable { get; set; }
        public string ExecutionModel { get; set; }
    }

    // ---- debug.pause-evaluate ----

    /// <summary>已安装 program 的命名入口；与 code/file 互斥。</summary>
    public sealed class DebugPauseProgramEntrySpec
    {
        [UnityCliRequired] public string ProgramId { get; set; }
        [UnityCliRequired] public string Entry { get; set; }
    }

    public sealed class DebugPauseEvaluateRequest
    {
        [UnityCliRequired] public string PauseId { get; set; }

        /// <summary>显式调试代码；与 file 和 programEntry 互斥。</summary>
        public string Code { get; set; }

        /// <summary>由 CLI 按入口 deadline 读取并内联的源码文件正文。</summary>
        public string SourceText { get; set; }

        /// <summary>已安装 program 的命名入口。</summary>
        public DebugPauseProgramEntrySpec ProgramEntry { get; set; }

        /// <summary>交给 Run(string argsJson) 的参数 JSON。</summary>
        [UnityCliAnyJson] public object Args { get; set; }

        /// <summary>等待现场服务该请求的上限（毫秒）；到期报告 pending 而不是声称已恢复线程。</summary>
        [UnityCliRange(1, DebugLimits.PauseOperationTimeoutMs)] public int? WaitMs { get; set; }
    }

    public sealed class DebugPauseEvaluateResult
    {
        public string PauseId { get; set; }
        public string ExecutionState { get; set; }
        public bool Completed { get; set; }
        public bool ServedOnPausedThread { get; set; }
        public int ServedOnThreadId { get; set; }
        [UnityCliAnyJson] public object ReturnValue { get; set; }

        /// <summary>返回值投影是否命中深度、条目或字符串上限；true 时 ReturnValue 只是有界残片。</summary>
        public bool ReturnValueTruncated { get; set; }
        public double DurationMs { get; set; }
        public string SourceKind { get; set; }
        public string CompileKey { get; set; }
        public List<string> KnownInterventions { get; set; }
        public List<string> CompileDiagnostics { get; set; }
        /// <summary>用户代码的同步调用可能永不返回；不捕获进程 stdout，因此该字段固定说明未捕获。</summary>
        public string OutputNote { get; set; }
        public string PendingNote { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
    }

    // ---- debug.pause-resume ----

    public sealed class DebugPauseResumeRequest
    {
        [UnityCliRequired] public string PauseId { get; set; }

        /// <summary>调用方给出的解除原因；用于审计与事件。</summary>
        public string Reason { get; set; }

        /// <summary>等待现场真正离开 paused 的上限（毫秒）；到期如实报告未释放。</summary>
        [UnityCliRange(1, DebugLimits.PauseMaxWaitForReleaseMs)] public int? WaitMs { get; set; }
    }

    public sealed class DebugPauseResumeResult
    {
        public string PauseId { get; set; }
        public string State { get; set; }
        public bool Released { get; set; }
        public string ReleaseReason { get; set; }
        public long ActiveSitesRemaining { get; set; }
        public string ExecutingOperation { get; set; }
        /// <summary>固定为 false：resume 只继续当前调用，不重放之前操作。</summary>
        public bool ReplayedPriorOperations { get; set; }
        public bool Preemption { get; set; }
        public string Note { get; set; }
        public string EditorSessionId { get; set; }
    }
}
#endif
