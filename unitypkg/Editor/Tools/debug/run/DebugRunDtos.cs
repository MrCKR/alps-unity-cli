#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>一条必需检查的声明：位置（来源 + 捕获位置）、量词与所需源都在这里冻结。</summary>
    public sealed class DebugRunCheckSpec
    {
        /// <summary>检查名；与 program entry 里 context.Check(name, ...) 的名称完全一致。</summary>
        [UnityCliRequired] public string Name { get; set; }

        /// <summary>观察来源：program、hook、log、trigger 或 pause，必须与统一事件存储的 sourceId 一致。</summary>
        [UnityCliRequired, UnityCliStringEnum("program", "hook", "log", "trigger", "pause")] public string Source { get; set; }

        /// <summary>
        /// 检查位置：该来源内必须命中捕获位置 ExecutionPoint 的精确取值；"*" 表示该来源的任意位置。
        /// 捕获位置的实际取值用 debug.events.read 读取（例如 editor-tick、method-prefix、run-scheduled）。
        /// </summary>
        [UnityCliRequired] public string Position { get; set; }

        /// <summary>量词：once、every、whole-window 或 absent。</summary>
        [UnityCliRequired, UnityCliStringEnum("once", "every", "whole-window", "absent")] public string Quantifier { get; set; }

        /// <summary>可选的 hook 身份筛选；只匹配该 hook 定义产生的事件。</summary>
        public string HookId { get; set; }

        /// <summary>可选的事件种类筛选（例如 check、postfix、log）。留空表示不筛选种类。</summary>
        public string Kind { get; set; }

        /// <summary>该检查要求覆盖证明的来源集合；省略时只要求本检查自己的 Source。</summary>
        [UnityCliItemsRange(0, DebugLimits.MaxRunCheckSources)] public List<string> RequiredSources { get; set; }
    }

    /// <summary>一条窗口级源覆盖要求：coverage 要求可证覆盖，declared 只记录调用方申报。</summary>
    public sealed class DebugRunSourceRequirementSpec
    {
        [UnityCliRequired, UnityCliStringEnum("program", "hook", "log", "trigger", "pause")] public string Source { get; set; }

        /// <summary>coverage 要求存储层证明覆盖；declared 只记录申报，不参与判定。</summary>
        [UnityCliRequired, UnityCliStringEnum("coverage", "declared")] public string Proof { get; set; }
    }

    /// <summary>一条窗口结束条件声明。</summary>
    public sealed class DebugRunEndConditionSpec
    {
        [UnityCliRequired, UnityCliStringEnum("manual", "check", "signal", "clock")] public string Kind { get; set; }

        /// <summary>check 与 signal 条件的名称；manual 与 clock 不需要名称。</summary>
        public string Name { get; set; }
    }

    // ---- 声明回读 ----

    /// <summary>一条必需检查的声明回读；Verified 表示宿主已证明该声明可执行。</summary>
    public sealed class DebugRunCheckDeclarationView
    {
        public string Name { get; set; }
        public string Source { get; set; }
        public string Position { get; set; }
        public string Quantifier { get; set; }
        public string HookId { get; set; }
        public string Kind { get; set; }
        public List<string> RequiredSources { get; set; }
        public bool Verified { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>一条源覆盖要求的声明回读。</summary>
    public sealed class DebugRunSourceRequirementView
    {
        public string Source { get; set; }
        public string Proof { get; set; }
        public bool ProofRequired { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>一条结束条件的声明回读：Verified 与 DeclaredOnly 分开报告，绝不用申报冒充证明。</summary>
    public sealed class DebugRunConditionView
    {
        public string Kind { get; set; }
        public string Name { get; set; }
        public bool Verified { get; set; }
        public bool DeclaredOnly { get; set; }

        /// <summary>该条件是否已在窗口内真正观察到；未观察到时窗口不算完整。</summary>
        public bool Observed { get; set; }

        public string Detail { get; set; }
    }

    // ---- 证据与判定 ----

    /// <summary>一条被保留的匹配证据；只写存储里真实存在的字段。</summary>
    public sealed class DebugRunEvidenceView
    {
        public long Sequence { get; set; }
        public string SourceId { get; set; }
        public string Kind { get; set; }
        public string Name { get; set; }
        public string ExecutionPoint { get; set; }
        public long Revision { get; set; }
        public int ThreadId { get; set; }
        public bool MainThread { get; set; }
        public string ClockKind { get; set; }
        public double ClockValue { get; set; }
        public long Frame { get; set; }
        public string HookId { get; set; }
        public string MethodIdentity { get; set; }
        public bool Passed { get; set; }
        [UnityCliAnyJson] public object Expected { get; set; }
        [UnityCliAnyJson] public object Actual { get; set; }
        public string DeclaredCoverage { get; set; }
        public bool Sampled { get; set; }
        public bool Truncated { get; set; }
        public List<string> EvidenceRefs { get; set; }
    }

    /// <summary>不可读的覆盖缺口；只描述保留窗口缺口，不声称检查未发生。</summary>
    public sealed class DebugRunCoverageGapView
    {
        public long FromSequence { get; set; }
        public long ToSequence { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>首个可证明反例：时钟、线程、期望、实值与证据引用一并保留。</summary>
    public sealed class DebugRunCounterexampleView
    {
        public string CheckName { get; set; }
        public string SourceId { get; set; }
        public string Kind { get; set; }
        public long Sequence { get; set; }
        public long Revision { get; set; }
        public string ExecutionPoint { get; set; }
        public string MethodIdentity { get; set; }
        public string HookId { get; set; }
        public int ThreadId { get; set; }
        public bool MainThread { get; set; }
        public string ClockKind { get; set; }
        public double ClockValue { get; set; }
        public long Frame { get; set; }
        [UnityCliAnyJson] public object Expected { get; set; }
        [UnityCliAnyJson] public object Actual { get; set; }
        public bool Passed { get; set; }
        public List<string> EvidenceRefs { get; set; }
    }

    /// <summary>一个必需检查的判定；Status 取 satisfied、counterexample 或 insufficient。</summary>
    public sealed class DebugRunCheckResultView
    {
        public string Name { get; set; }
        public string Source { get; set; }
        public string Position { get; set; }
        public string Quantifier { get; set; }
        public string Status { get; set; }
        public long MatchCount { get; set; }
        public long PassedCount { get; set; }
        public long FailedCount { get; set; }

        /// <summary>该量词的证明是否消费调用方声明的覆盖要求；只有 whole-window 与 absent 为 true。</summary>
        public bool CoverageRequired { get; set; }

        /// <summary>该检查所需来源在本次区间内是否可读（无缺口、无采样、无截断）；与是否申报无关。</summary>
        public bool CoverageProven { get; set; }
        public bool SamplePointOnly { get; set; }
        public bool Truncated { get; set; }
        public List<DebugRunCoverageGapView> CoverageGaps { get; set; }
        public List<DebugRunEvidenceView> Evidence { get; set; }
        public List<string> Reasons { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>一个来源的覆盖事实；Proof 取 coverage、declared 或 none。</summary>
    public sealed class DebugRunSourceCoverageView
    {
        public string Source { get; set; }
        public string Proof { get; set; }
        public bool ProofRequired { get; set; }
        public bool CoverageProven { get; set; }
        public long MatchCount { get; set; }
        public bool Sampled { get; set; }
        public int SampleEvery { get; set; }
        public bool Truncated { get; set; }
        public List<DebugRunCoverageGapView> CoverageGaps { get; set; }
        public List<string> Reasons { get; set; }
    }

    /// <summary>一个配置段内单条检查的结论摘要；详细证据仍以整窗口投影出现。</summary>
    public sealed class DebugRunCheckStatusView
    {
        public string Name { get; set; }
        public string Status { get; set; }
        public long MatchCount { get; set; }
        public long FailedCount { get; set; }
    }

    /// <summary>一个配置段的判定；配置变化时逐段报告，不生成虚假的单一配置结论。</summary>
    public sealed class DebugRunSegmentView
    {
        public long Index { get; set; }
        public long FromSequence { get; set; }
        public long ToSequence { get; set; }
        public string Verdict { get; set; }
        public List<string> Reasons { get; set; }
        public List<DebugRunCheckStatusView> CheckStatuses { get; set; }
        public string BoundaryFact { get; set; }
    }

    /// <summary>封闭边界的原子捕获事实与 queue 水位；用于确认事件是否已经进入存储。</summary>
    public sealed class DebugRunCaptureView
    {
        public long BoundarySequence { get; set; }
        public long DroppedAtBoundary { get; set; }
        public long AppendFailuresAtBoundary { get; set; }
        public long PendingDeliveries { get; set; }
        public long QueueHighWaterMark { get; set; }
        public long ExpiredDeliveries { get; set; }
        public long DroppedDeliveries { get; set; }
        public long Drivers { get; set; }
        public bool QueueDrained { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>run 域的预算占用；拒绝路径与读取结果共用同一份事实。</summary>
    public sealed class DebugRunBudgetView
    {
        public long SummariesUsed { get; set; }
        public long SummariesRemaining { get; set; }
        public long MaxSummariesPerSession { get; set; }
        public long DetailedWindowsUsed { get; set; }
        public long DetailedWindowsRemaining { get; set; }
        public long MaxDetailedWindowsPerSession { get; set; }
        public long DetailedBytesUsed { get; set; }
        public long DetailedBytesRemaining { get; set; }
        public long MaxDetailedBytesGlobal { get; set; }
    }

    /// <summary>窗口的完整报告；verdict 是业务结论，随 ok:true 一起返回。</summary>
    public sealed class DebugRunReportView
    {
        public string RunId { get; set; }
        public string SessionId { get; set; }
        public string State { get; set; }
        public bool Terminal { get; set; }
        public string Verdict { get; set; }
        public List<string> VerdictReasons { get; set; }
        public string TerminationReason { get; set; }
        public string StartedAtUtc { get; set; }
        public double StartedAtMonotonicMs { get; set; }
        public string SealedAtUtc { get; set; }
        public double SealedAtMonotonicMs { get; set; }
        public long StartSequence { get; set; }
        public long EndSequence { get; set; }
        public long? MaxWindowMs { get; set; }
        public bool AllowSegments { get; set; }
        public bool DetailAvailable { get; set; }
        public List<DebugRunCheckResultView> Checks { get; set; }
        public DebugRunCounterexampleView FirstCounterexample { get; set; }
        public List<DebugRunCoverageGapView> CoverageGaps { get; set; }
        public List<DebugRunSourceCoverageView> Sources { get; set; }
        public List<DebugRunConditionView> Conditions { get; set; }
        public List<DebugRunSegmentView> Segments { get; set; }
        public DebugRunCaptureView Capture { get; set; }
        [UnityCliAnyJson] public Dictionary<string, object> Environment { get; set; }
        [UnityCliAnyJson] public Dictionary<string, object> Configuration { get; set; }
        public DebugRunBudgetView Budget { get; set; }
        public string EditorSessionId { get; set; }
        public string RuntimeId { get; set; }
        public string DomainGeneration { get; set; }
        public string Note { get; set; }
    }

    /// <summary>窗口摘要；list 与详细窗口被淘汰后的读取共用同一投影。</summary>
    public sealed class DebugRunSummaryView
    {
        public string RunId { get; set; }
        public string SessionId { get; set; }
        public string State { get; set; }
        public bool Terminal { get; set; }
        public string Verdict { get; set; }
        public List<string> VerdictReasons { get; set; }
        public string StartedAtUtc { get; set; }
        public string SealedAtUtc { get; set; }
        public long StartSequence { get; set; }
        public long EndSequence { get; set; }
        public long RequiredCheckCount { get; set; }
        public long FailedCheckCount { get; set; }
        public long CounterexampleCount { get; set; }
        public bool DetailAvailable { get; set; }
        public bool AllowSegments { get; set; }
        public long SegmentCount { get; set; }
    }

    // ---- compare ----

    /// <summary>一次比较的完整声明：两侧 runId、来源与位置、对齐键、值路径、适用时钟与上下文条数。</summary>
    public sealed class DebugRunCompareRequest
    {
        [UnityCliRequired] public string LeftRunId { get; set; }

        [UnityCliRequired] public string RightRunId { get; set; }

        /// <summary>比较所用的来源：program、hook、log、trigger 或 pause，必须与统一事件存储的 sourceId 一致。</summary>
        [UnityCliRequired, UnityCliStringEnum("program", "hook", "log", "trigger", "pause")] public string Source { get; set; }

        /// <summary>来源内的捕获位置：该来源必须命中的 ExecutionPoint，或 "*" 表示任意位置。</summary>
        [UnityCliRequired] public string Position { get; set; }

        /// <summary>可选的事件种类筛选（例如 check、postfix、log）；留空表示不筛选种类。</summary>
        public string Kind { get; set; }

        /// <summary>对齐键：必须显式声明，接口不提供默认对齐。</summary>
        [UnityCliRequired] public DebugRunCompareAlignmentSpec Alignment { get; set; }

        /// <summary>比较值路径：在该事件公开字段上取值的点分路径。</summary>
        [UnityCliRequired] public string ValuePath { get; set; }

        /// <summary>适用时钟：monotonic、editor-tick、unity-frame 或 project-logical；只有该时钟的事件参与比较。</summary>
        [UnityCliRequired, UnityCliStringEnum("monotonic", "editor-tick", "unity-frame", "project-logical")] public string Clock { get; set; }

        /// <summary>首个差异两侧保留的上下文事件条数；省略时使用声明默认值。</summary>
        [UnityCliRange(0, DebugLimits.MaxRunCompareContext)] public int? Context { get; set; }
    }

    /// <summary>一次比较的对齐键声明：种类必须由调用方选择，接口不提供默认对齐。</summary>
    public sealed class DebugRunCompareAlignmentSpec
    {
        [UnityCliRequired, UnityCliStringEnum("ordinal", "key")] public string Kind { get; set; }

        /// <summary>kind 为 key 时必填：业务键的取值路径；kind 为 ordinal 时必须省略。</summary>
        public string KeyPath { get; set; }
    }

    /// <summary>一个值位置的回读：Present 为 false 表示该路径在这个事件上不存在，不表示值为 null；Text 是该值的规范化 JSON 文本。</summary>
    public sealed class DebugRunCompareValueView
    {
        public bool Present { get; set; }
        [UnityCliAnyJson] public object Value { get; set; }

        /// <summary>该值的规范化 JSON 文本；字符串值带 JSON 引号，缺失路径时为空串。</summary>
        public string Text { get; set; }
    }

    /// <summary>一条参与比较的事件视图；只写存储公开的真实字段，证据引用指向事件存储的 sequence。</summary>
    public sealed class DebugRunCompareEventView
    {
        public long Sequence { get; set; }
        public string SourceId { get; set; }
        public string Kind { get; set; }
        public string ExecutionPoint { get; set; }
        public string Name { get; set; }
        public long Revision { get; set; }
        public long InvocationId { get; set; }
        public int ThreadId { get; set; }
        public bool MainThread { get; set; }
        public string ClockKind { get; set; }
        public double ClockValue { get; set; }
        public long Frame { get; set; }
        public string HookId { get; set; }
        public string MethodIdentity { get; set; }
        public bool Sampled { get; set; }
        public bool Truncated { get; set; }
        public string EvidenceRef { get; set; }
        public DebugRunCompareValueView Key { get; set; }
        public DebugRunCompareValueView Compared { get; set; }
    }

    /// <summary>首个可证明差异；两侧的值、值路径与对齐位置一同给出。</summary>
    public sealed class DebugRunCompareDifferenceView
    {
        /// <summary>差异种类，见 UnityCliDebugRunCompareDifferenceKinds。</summary>
        public string Kind { get; set; }

        /// <summary>该差异在左侧选择集合中的对齐序号（从 1 开始）；ordinal 对齐时两侧同号。</summary>
        public long AlignmentIndex { get; set; }

        /// <summary>key 对齐时的业务键规范化文本（canonical JSON，字符串键带引号）；ordinal 对齐时为空。</summary>
        public string KeyText { get; set; }

        /// <summary>实际出现差异的值路径；嵌套值会下降到具体叶子路径。</summary>
        public string Path { get; set; }

        public DebugRunCompareValueView LeftValue { get; set; }
        public DebugRunCompareValueView RightValue { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>差异位置的一侧有限上下文；Difference 为该侧在差异位置的事件，缺席时为 null。</summary>
    public sealed class DebugRunCompareContextView
    {
        /// <summary>取值 left 或 right。</summary>
        public string Side { get; set; }
        public long AnchoredIndex { get; set; }
        public bool DifferencePresent { get; set; }
        public DebugRunCompareEventView Difference { get; set; }
        public List<DebugRunCompareEventView> Before { get; set; }
        public List<DebugRunCompareEventView> After { get; set; }
    }

    /// <summary>一次比较中一侧窗口的可比较事实；complete 为 false 时该侧不得支撑 equal 或凭缺席得出的 diverged。</summary>
    public sealed class DebugRunCompareSideView
    {
        public string RunId { get; set; }
        public string State { get; set; }
        public bool Terminal { get; set; }
        public string Verdict { get; set; }
        public string TerminationReason { get; set; }
        public long StartSequence { get; set; }
        public long EndSequence { get; set; }

        /// <summary>证据状态：live（开窗实时推导）、frozen（封闭时冻结保留）或 evicted（载荷已被预算释放）。</summary>
        public string EvidenceState { get; set; }

        public bool DetailAvailable { get; set; }
        public long SourceMatchCount { get; set; }
        public long SelectedCount { get; set; }
        public long ExcludedByClockCount { get; set; }
        public bool IntervalReadable { get; set; }
        public bool Sampled { get; set; }
        public bool Truncated { get; set; }
        public long AlignmentKeyMissingCount { get; set; }
        public bool AlignmentAmbiguous { get; set; }
        public bool HasClockRange { get; set; }
        public double ClockFrom { get; set; }
        public double ClockTo { get; set; }
        public bool Complete { get; set; }
        public List<DebugRunCoverageGapView> CoverageGaps { get; set; }
        public List<string> Reasons { get; set; }
        public string Coverage { get; set; }
        public string Note { get; set; }
    }

    /// <summary>一条条件差异：两次运行在同一条件上不同，只陈述可复核的字段事实。</summary>
    public sealed class DebugRunCompareConditionView
    {
        /// <summary>条件种类，见 UnityCliDebugRunCompareConditionKinds。</summary>
        public string Kind { get; set; }
        public string Left { get; set; }
        public string Right { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>run.compare 的完整结果：三态结论、首个可证明差异、两侧有限上下文、条件差异与覆盖范围。</summary>
    public sealed class DebugRunCompareResult
    {
        /// <summary>比较结论：equal、diverged 或 indeterminate；它不是业务正确与否。</summary>
        public string Result { get; set; }
        public List<string> Reasons { get; set; }
        public string LeftRunId { get; set; }
        public string RightRunId { get; set; }
        public string Source { get; set; }
        public string Position { get; set; }
        public string Kind { get; set; }
        public string ValuePath { get; set; }
        public string Clock { get; set; }

        /// <summary>实际使用的对齐键种类：ordinal 或 key。</summary>
        public string AlignmentKind { get; set; }

        /// <summary>key 对齐时实际使用的业务键路径；ordinal 对齐时为空。</summary>
        public string AlignmentKeyPath { get; set; }

        public long Context { get; set; }
        public long AlignedPairCount { get; set; }
        public long ComparedValueCount { get; set; }
        public long LeftOnlyCount { get; set; }
        public long RightOnlyCount { get; set; }

        /// <summary>首个可证明差异；只有在两侧真实证据下能证明时才出现，否则为 null。</summary>
        public DebugRunCompareDifferenceView FirstDifference { get; set; }

        /// <summary>发现了但按当前证据不可证明的差异（例如事件被截断或一侧区间不完整）；entry 不参与结论。</summary>
        public DebugRunCompareDifferenceView UnprovenDifference { get; set; }

        /// <summary>首个差异两侧的有限上下文；结论为 diverged 时按顺序给出 left 与 right 两条。</summary>
        public List<DebugRunCompareContextView> Contexts { get; set; }

        /// <summary>两次运行在同一条件上的差异（定义、配置、环境、时钟、覆盖范围、终态）。</summary>
        public List<DebugRunCompareConditionView> ConditionDifferences { get; set; }

        public DebugRunCompareSideView Left { get; set; }
        public DebugRunCompareSideView Right { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public string Note { get; set; }
    }

    // ---- begin ----

    public sealed class DebugRunBeginRequest
    {
        [UnityCliRequired] public string SessionId { get; set; }

        [UnityCliRequired, UnityCliItemsRange(1, DebugLimits.MaxRunRequiredChecks)] public List<DebugRunCheckSpec> RequiredChecks { get; set; }

        [UnityCliItemsRange(0, DebugLimits.MaxRunSourceRequirements)] public List<DebugRunSourceRequirementSpec> SourceRequirements { get; set; }

        [UnityCliItemsRange(0, DebugLimits.MaxRunEndConditions)] public List<DebugRunEndConditionSpec> EndConditions { get; set; }

        /// <summary>窗口时长上界；clock 结束条件与到期判定使用它，没有默认值。</summary>
        [UnityCliRange(1, DebugLimits.MaxRunWindowMs)] public long? MaxWindowMs { get; set; }

        /// <summary>
        /// 默认 false 表示严格窗口：窗口内出现相关配置变化即判定 indeterminate。
        /// true 表示允许分段：窗口按可定位的配置边界分段，并逐段报告。
        /// </summary>
        public bool? AllowSegments { get; set; }

        /// <summary>调用方申报的准备定义；宿主只记录并计算定义 hash，不执行任何准备动作。</summary>
        [UnityCliAnyJson] public Dictionary<string, object> Preparation { get; set; }

        /// <summary>调用方申报的输入或命令定义；宿主只记录并计算定义 hash，不注入任何输入。</summary>
        [UnityCliAnyJson] public Dictionary<string, object> Inputs { get; set; }

        /// <summary>调用方申报的种子控制情况；宿主无法读取项目 RNG 真值，因此按申报记录。</summary>
        public string SeedControl { get; set; }
    }

    public sealed class DebugRunBeginResult
    {
        public string RunId { get; set; }
        public string SessionId { get; set; }
        public string State { get; set; }
        public bool Terminal { get; set; }
        public string StartedAtUtc { get; set; }
        public double StartedAtMonotonicMs { get; set; }
        public long StartSequence { get; set; }
        public long? MaxWindowMs { get; set; }
        public bool AllowSegments { get; set; }
        public string DefinitionHash { get; set; }
        public DebugRunCaptureView Capture { get; set; }
        [UnityCliAnyJson] public Dictionary<string, object> Environment { get; set; }
        [UnityCliAnyJson] public Dictionary<string, object> Configuration { get; set; }
        public List<DebugRunCheckDeclarationView> RequiredChecks { get; set; }
        public List<DebugRunSourceRequirementView> SourceRequirements { get; set; }
        public List<DebugRunConditionView> EndConditions { get; set; }
        public DebugRunBudgetView Budget { get; set; }
        public string EditorSessionId { get; set; }
        public string RuntimeId { get; set; }
        public string DomainGeneration { get; set; }
        public string Note { get; set; }
    }

    // ---- end ----

    public sealed class DebugRunEndRequest
    {
        [UnityCliRequired] public string RunId { get; set; }
    }

    public sealed class DebugRunEndResult
    {
        public bool Sealed { get; set; }
        public bool AlreadySealed { get; set; }
        public DebugRunReportView Report { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public string Note { get; set; }
    }

    // ---- wait ----

    public sealed class DebugRunWaitRequest
    {
        [UnityCliRequired] public string RunId { get; set; }

        /// <summary>可选的有界等待毫秒数；省略表示只读当前事实。</summary>
        [UnityCliRange(0, DebugLimits.RunWaitMaxMs)] public long? WaitMs { get; set; }
    }

    public sealed class DebugRunWaitResult
    {
        public bool Waited { get; set; }
        public long WaitMs { get; set; }
        public bool Terminal { get; set; }
        public DebugRunReportView Report { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public string Note { get; set; }
    }

    // ---- info ----

    public sealed class DebugRunInfoRequest
    {
        [UnityCliRequired] public string RunId { get; set; }
    }

    public sealed class DebugRunInfoResult
    {
        public DebugRunReportView Report { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public string Note { get; set; }
    }

    // ---- list ----

    public sealed class DebugRunListRequest
    {
        public string SessionId { get; set; }

        [UnityCliRange(1, DebugLimits.RunListMaxLimit)] public int? Limit { get; set; }

        public string Cursor { get; set; }
    }

    public sealed class DebugRunListResult
    {
        public List<DebugRunSummaryView> Items { get; set; }
        public string NextCursor { get; set; }
        public long Total { get; set; }
        public long NextOffset { get; set; }
        public bool HasMore { get; set; }
        public DebugRunBudgetView Budget { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public string Note { get; set; }
    }
}
#endif
