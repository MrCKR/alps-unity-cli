#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>一个声明 entry 的冻结形状；类别决定必须精确匹配的签名。</summary>
    public sealed class DebugProgramEntrySpec
    {
        [UnityCliRequired] public string Name { get; set; }

        /// <summary>step、method、listener、oneshot 或 cleanup；签名见 descriptor 的 ABI 文本。</summary>
        [UnityCliRequired, UnityCliStringEnum("step", "method", "listener", "oneshot", "cleanup")] public string Category { get; set; }

        /// <summary>编译单元内的完全限定类型名；ABI 按 metadata 精确解析该类型。</summary>
        [UnityCliRequired] public string DeclaringType { get; set; }
    }

    /// <summary>一个显式 binding：把 exact target 的一个阶段绑定到一个命名 entry。</summary>
    public sealed class DebugProgramBindingSpec
    {
        /// <summary>稳定 binding 身份；省略时按请求内顺序生成 b1、b2 等。</summary>
        public string BindingId { get; set; }

        [UnityCliRequired, UnityCliStringEnum("prefix", "postfix", "signal")] public string Phase { get; set; }

        [UnityCliRequired] public string Entry { get; set; }

        /// <summary>exact 方法目标；与 debug.hook-install 使用同一 target 形状与同一解析器。</summary>
        [UnityCliRequired] public MethodHookTargetSpec Target { get; set; }
    }

    /// <summary>单个 entry 的只读事实投影；签名来自真实 method。</summary>
    public sealed class DebugProgramEntryView
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public string DeclaringType { get; set; }
        public string Signature { get; set; }
    }

    /// <summary>单个 binding 的只读事实投影；attached 表示 detour 当前是否已经挂载。</summary>
    public sealed class DebugProgramBindingView
    {
        public string BindingId { get; set; }
        public string Phase { get; set; }
        public string Entry { get; set; }
        public string TargetSummary { get; set; }
        public string HookId { get; set; }
        public bool Attached { get; set; }
    }

    /// <summary>program 预算占用；拒绝路径与读取结果共用同一份事实。</summary>
    public sealed class DebugProgramBudgetView
    {
        public long ProgramsUsed { get; set; }
        public long ProgramsRemaining { get; set; }
        public long MaxProgramsGlobal { get; set; }
        public long SessionProgramsUsed { get; set; }
        public long SessionProgramsRemaining { get; set; }
        public long MaxProgramsPerSession { get; set; }
        public long ActiveRunsUsed { get; set; }
        public long ActiveRunsRemaining { get; set; }
        public long MaxActiveRunsGlobal { get; set; }
    }

    /// <summary>program 定义摘要；info 与 list 共用同一投影。</summary>
    public sealed class DebugProgramSummaryView
    {
        public string ProgramId { get; set; }
        public string SessionId { get; set; }
        public string Label { get; set; }
        public string EntryType { get; set; }
        public string State { get; set; }
        public long Revision { get; set; }
        public string SourceSha256 { get; set; }
        public long SourceBytes { get; set; }
        public long StateEpoch { get; set; }
        public long StateBytes { get; set; }
        public long StateSchemaBytes { get; set; }
        public long InFlightCalls { get; set; }
        public bool StepDriver { get; set; }
        public string CreatedAtUtc { get; set; }
        public string StoppedAtUtc { get; set; }
        public string AssemblyName { get; set; }
        public List<DebugProgramEntryView> Entries { get; set; }
        public List<DebugProgramBindingView> Bindings { get; set; }
    }

    /// <summary>一次运行的事实投影；它不声称业务目标已完成。</summary>
    public sealed class DebugProgramRunView
    {
        public string ProgramRunId { get; set; }
        public string Entry { get; set; }
        public string EntryCategory { get; set; }
        public string State { get; set; }
        public long Revision { get; set; }
        public string ExecutionSite { get; set; }
        public string Target { get; set; }
        public string RunId { get; set; }
        public long DurationMs { get; set; }
        public string StartedAtUtc { get; set; }
        public string CompletedAtUtc { get; set; }
        public long StepCount { get; set; }
        public long CheckCount { get; set; }
        public long DeliveryCount { get; set; }
        public bool CancellationRequested { get; set; }
        public bool InFlight { get; set; }
        public string ResultTrust { get; set; }
        public string FailureCode { get; set; }
    }

    /// <summary>持续 step driver 的只读事实；没有 driver 时以 Planted=false 与说明出现。</summary>
    public sealed class DebugProgramDriverView
    {
        public bool Planted { get; set; }
        public string ProgramRunId { get; set; }
        public string Entry { get; set; }

        /// <summary>driver 取得步骤执行权的执行点：editor-tick 或一个具名 PlayerLoop phase。</summary>
        public string Phase { get; set; }

        public long StepIndex { get; set; }
        public string WaitingOnSignal { get; set; }
        public string WaitingOnCondition { get; set; }
        public long FramesRemaining { get; set; }
        public double DeadlineMonotonicMs { get; set; }
        public long LastRevision { get; set; }
        public string Note { get; set; }
    }

    // ---- install ----

    public sealed class DebugProgramInstallRequest
    {
        [UnityCliRequired] public string SessionId { get; set; }

        /// <summary>同一 session 内唯一的 label。</summary>
        [UnityCliRequired] public string Label { get; set; }

        /// <summary>程序形态：只有一个已声明的 C# 编译单元形态 code。</summary>
        [UnityCliRequired, UnityCliStringEnum("code")] public string EntryType { get; set; }

        /// <summary>内联源码；与 file 二选一。</summary>
        public string Source { get; set; }

        /// <summary>只能在准备阶段读取一次的 .cs 文件路径；与 source 二选一。</summary>
        public string File { get; set; }

        [UnityCliRequired, UnityCliItemsRange(1, DebugLimits.MaxProgramEntries)] public List<DebugProgramEntrySpec> Entries { get; set; }

        [UnityCliItemsRange(0, DebugLimits.MaxProgramBindings)] public List<DebugProgramBindingSpec> Bindings { get; set; }

        /// <summary>revision 状态的声明 schema；与状态一起发布，供 info 读回。</summary>
        [UnityCliAnyJson] public Dictionary<string, object> StateSchema { get; set; }

        /// <summary>初始 revision 状态；省略表示空对象。</summary>
        [UnityCliAnyJson] public Dictionary<string, object> State { get; set; }
    }

    public sealed class DebugProgramInstallResult
    {
        public string ProgramId { get; set; }
        public string SessionId { get; set; }
        public string Label { get; set; }
        public long Revision { get; set; }
        public string State { get; set; }
        public string SourceSha256 { get; set; }
        public long SourceBytes { get; set; }
        public long StateEpoch { get; set; }
        public long StateBytes { get; set; }
        public string EntryType { get; set; }
        public List<DebugProgramEntryView> Entries { get; set; }
        public List<DebugProgramBindingView> Bindings { get; set; }
        public bool BindingsValidated { get; set; }
        public long BindingsAttached { get; set; }
        public bool Installed { get; set; }
        public bool Dormant { get; set; }
        public string AssemblyName { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public DebugProgramBudgetView Budget { get; set; }
        public string Note { get; set; }
    }

    // ---- replace ----

    public sealed class DebugProgramReplaceRequest
    {
        [UnityCliRequired] public string ProgramId { get; set; }

        /// <summary>当前 revision；不匹配即拒绝并保留旧配置。</summary>
        [UnityCliRequired] public long ExpectedRevision { get; set; }

        public string Label { get; set; }

        /// <summary>完整候选源码；与 file 二选一。</summary>
        public string Source { get; set; }

        public string File { get; set; }

        [UnityCliRequired, UnityCliItemsRange(1, DebugLimits.MaxProgramEntries)] public List<DebugProgramEntrySpec> Entries { get; set; }

        [UnityCliItemsRange(0, DebugLimits.MaxProgramBindings)] public List<DebugProgramBindingSpec> Bindings { get; set; }

        /// <summary>候选 revision 状态；发布前校验 state epoch。</summary>
        [UnityCliAnyJson] public Dictionary<string, object> StateSchema { get; set; }

        [UnityCliAnyJson] public Dictionary<string, object> State { get; set; }
    }

    public sealed class DebugProgramReplaceResult
    {
        public string ProgramId { get; set; }
        public string SessionId { get; set; }
        public long OldRevision { get; set; }
        public long NewRevision { get; set; }
        public long StateEpoch { get; set; }
        public string State { get; set; }
        public bool Published { get; set; }
        public long OldInFlightCalls { get; set; }
        public long StateBytes { get; set; }
        public List<DebugProgramEntryView> Entries { get; set; }
        public List<DebugProgramBindingView> Bindings { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public DebugProgramBudgetView Budget { get; set; }
        public string Note { get; set; }
    }

    // ---- run ----

    public sealed class DebugProgramRunRequest
    {
        [UnityCliRequired] public string ProgramId { get; set; }

        [UnityCliRequired] public string Entry { get; set; }

        [UnityCliAnyJson] public Dictionary<string, object> Args { get; set; }

        /// <summary>
        /// 声明的执行点。editor-tick 是主线程中央 editor tick；player-loop-phase 是 Editor Play 下真实的
        /// Unity PlayerLoop phase，必须同时声明 phase，Edit 模式下被拒绝。
        /// </summary>
        [UnityCliRequired, UnityCliStringEnum("editor-tick", "method-prefix", "method-postfix", "signal", "worker-thread", "player-loop-phase")] public string ExecutionSite { get; set; }

        /// <summary>
        /// executionSite 为 player-loop-phase 时必需的具名 phase；只在 Editor Play 下可用。
        /// before-update 在顶层 Update 之前，after-update 在其后，fixed-update 在 FixedUpdate 之后，
        /// late-update 在 LateUpdate 脚本之后。
        /// </summary>
        [UnityCliStringEnum("before-update", "after-update", "fixed-update", "late-update")] public string Phase { get; set; }

        /// <summary>必需的运行时长上限；没有默认值。</summary>
        [UnityCliRequired, UnityCliRange(1, DebugLimits.MaxProgramRunMaxDurationMs)] public long DurationMs { get; set; }

        /// <summary>可选的关联证据窗口 runId；只作为事实记录，不改变任何运行语义。</summary>
        public string RunId { get; set; }
    }

    public sealed class DebugProgramRunResult
    {
        public string ProgramRunId { get; set; }
        public string ProgramId { get; set; }
        public string SessionId { get; set; }
        public string Entry { get; set; }
        public string EntryCategory { get; set; }
        public string State { get; set; }
        public long Revision { get; set; }
        public string ExecutionSite { get; set; }
        public string Target { get; set; }
        public string RunId { get; set; }
        public long DurationMs { get; set; }
        public double DeadlineMonotonicMs { get; set; }
        public bool StepDriver { get; set; }
        public bool Scheduled { get; set; }
        public bool Running { get; set; }

        /// <summary>
        /// 该运行在返回时是否已经终态。dispatch 执行点的运行在返回时仍在等待它的执行点，
        /// 因此 terminal=false：这是一次有界非终态观察，不声称业务目标已完成。
        /// </summary>
        public bool Terminal { get; set; }
        public long BindingsAttached { get; set; }
        [UnityCliAnyJson] public Dictionary<string, object> Scheduler { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public DebugProgramBudgetView Budget { get; set; }
        public string Note { get; set; }
    }

    // ---- wait ----

    public sealed class DebugProgramWaitRequest
    {
        [UnityCliRequired] public string ProgramRunId { get; set; }

        /// <summary>可选的有界等待毫秒数；省略表示只读当前事实。</summary>
        [UnityCliRange(0, DebugLimits.ProgramWaitMaxMs)] public long? WaitMs { get; set; }
    }

    public sealed class DebugProgramWaitResult
    {
        public string ProgramRunId { get; set; }
        public string ProgramId { get; set; }
        public string SessionId { get; set; }
        public string Entry { get; set; }
        public string State { get; set; }
        public bool Terminal { get; set; }
        public long Revision { get; set; }
        public string ExecutionSite { get; set; }
        public string Target { get; set; }
        public long DurationMs { get; set; }
        public string StartedAtUtc { get; set; }
        public string CompletedAtUtc { get; set; }
        public double DeadlineMonotonicMs { get; set; }
        public long StepCount { get; set; }
        public long CheckCount { get; set; }
        public long DeliveryCount { get; set; }
        public bool CancellationRequested { get; set; }
        public bool InFlight { get; set; }
        public string ResultTrust { get; set; }
        [UnityCliAnyJson] public object Result { get; set; }
        public bool LastTrustedResultAvailable { get; set; }
        public List<long> StepRevisions { get; set; }
        public string FailureCode { get; set; }
        public string FailureMessage { get; set; }
        public bool Waited { get; set; }
        public long WaitMs { get; set; }
        [UnityCliAnyJson] public Dictionary<string, object> Scheduler { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public DebugProgramBudgetView Budget { get; set; }
        public string Note { get; set; }
    }

    // ---- stop ----

    public sealed class DebugProgramStopRequest
    {
        [UnityCliRequired] public string ProgramRunId { get; set; }
    }

    public sealed class DebugProgramStopResult
    {
        public string ProgramRunId { get; set; }
        public string ProgramId { get; set; }
        public string State { get; set; }
        public bool Terminal { get; set; }
        public bool Changed { get; set; }
        public bool CancellationRequested { get; set; }
        public bool InFlight { get; set; }
        public bool NotYetExecutedStepsCancelled { get; set; }
        public bool CleanupRan { get; set; }
        public bool DefinitionDeleted { get; set; }
        public DebugProgramDriverView Driver { get; set; }
        public long OtherRunsUntouched { get; set; }
        [UnityCliAnyJson] public Dictionary<string, object> Scheduler { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public DebugProgramBudgetView Budget { get; set; }
        public string Note { get; set; }
    }

    // ---- info ----

    public sealed class DebugProgramInfoRequest
    {
        [UnityCliRequired] public string ProgramId { get; set; }
    }

    public sealed class DebugProgramInfoResult
    {
        public DebugProgramSummaryView Program { get; set; }
        public List<DebugProgramRunView> ActiveRuns { get; set; }
        public DebugProgramDriverView Driver { get; set; }
        [UnityCliAnyJson] public Dictionary<string, object> Scheduler { get; set; }
        [UnityCliAnyJson] public Dictionary<string, object> CompiledArtifacts { get; set; }
        public string EditorSessionId { get; set; }
        public string RuntimeId { get; set; }
        public string DomainGeneration { get; set; }
        public DebugProgramBudgetView Budget { get; set; }
        public string Note { get; set; }
    }

    // ---- list ----

    public sealed class DebugProgramListRequest
    {
        public string SessionId { get; set; }
        [UnityCliRange(1, DebugLimits.ProgramListMaxLimit)] public int? Limit { get; set; }
        public string Cursor { get; set; }
    }

    public sealed class DebugProgramListResult
    {
        public List<DebugProgramSummaryView> Items { get; set; }
        public string NextCursor { get; set; }
        public long Total { get; set; }
        public long NextOffset { get; set; }
        public bool HasMore { get; set; }
        [UnityCliAnyJson] public Dictionary<string, object> Scheduler { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public DebugProgramBudgetView Budget { get; set; }
        public string Note { get; set; }
    }

    // ---- remove ----

    public sealed class DebugProgramRemoveRequest
    {
        [UnityCliRequired] public string ProgramId { get; set; }
    }

    public sealed class DebugProgramRemoveResult
    {
        public string ProgramId { get; set; }
        public string SessionId { get; set; }
        public bool Removed { get; set; }
        public long DetachedBindings { get; set; }
        public long RevocableRoutesReleased { get; set; }
        public long LoadedNonUnloadableAssemblies { get; set; }
        public long LoadedNonUnloadableBytes { get; set; }
        public bool Unloadable { get; set; }
        [UnityCliAnyJson] public Dictionary<string, object> Scheduler { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public DebugProgramBudgetView Budget { get; set; }
        public string Note { get; set; }
    }
}
#endif
