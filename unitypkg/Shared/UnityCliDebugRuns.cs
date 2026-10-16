using System;

namespace UnityCli
{
    /// <summary>证据窗口的状态机：open → sealed，或在 runtime/session 中断时进入 interrupted。</summary>
    public static class UnityCliDebugRunStates
    {
        /// <summary>窗口仍在收集证据。</summary>
        public const string Open = "open";

        /// <summary>窗口已由 run.end 封闭；封闭不可撤销，也不终止任何程序运行。</summary>
        public const string Sealed = "sealed";

        /// <summary>窗口被 runtime 段变化或 session 停止中断；已收集证据保留，判定按缺口处理。</summary>
        public const string Interrupted = "interrupted";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Open, StringComparison.Ordinal)
                || string.Equals(value, Sealed, StringComparison.Ordinal)
                || string.Equals(value, Interrupted, StringComparison.Ordinal);
        }

        public static bool IsTerminal(string value)
        {
            return string.Equals(value, Sealed, StringComparison.Ordinal)
                || string.Equals(value, Interrupted, StringComparison.Ordinal);
        }
    }

    /// <summary>三态判定。断言失败是业务结论，不是错误码：failed 与 indeterminate 都是合法调试结果。</summary>
    public static class UnityCliDebugRunVerdicts
    {
        /// <summary>全部必需检查满足，且适用窗口完整、结束条件已观察到。</summary>
        public const string Passed = "passed";

        /// <summary>存在足够反例；报告保留首个可证明反例。</summary>
        public const string Failed = "failed";

        /// <summary>检查缺失、源失效、证据缺口、截断、采样、配置变化或窗口未完成。</summary>
        public const string Indeterminate = "indeterminate";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Passed, StringComparison.Ordinal)
                || string.Equals(value, Failed, StringComparison.Ordinal)
                || string.Equals(value, Indeterminate, StringComparison.Ordinal);
        }

        public static string[] All()
        {
            return new[] { Passed, Failed, Indeterminate };
        }
    }

    /// <summary>必需检查的量词；它决定什么算证明、什么算反例。</summary>
    public static class UnityCliDebugRunQuantifiers
    {
        /// <summary>存在一次：窗口内至少出现一次匹配且 passed=true 的检查事件。</summary>
        public const string Once = "once";

        /// <summary>每次：窗口内每一次匹配的检查事件都必须 passed=true，且至少出现一次。</summary>
        public const string Every = "every";

        /// <summary>整个窗口：除覆盖必须可证外，窗口结束条件也必须已观察到。</summary>
        public const string WholeWindow = "whole-window";

        /// <summary>缺席：窗口内不得出现任何匹配事件，且覆盖必须可证。</summary>
        public const string Absent = "absent";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Once, StringComparison.Ordinal)
                || string.Equals(value, Every, StringComparison.Ordinal)
                || string.Equals(value, WholeWindow, StringComparison.Ordinal)
                || string.Equals(value, Absent, StringComparison.Ordinal);
        }

        public static string[] All()
        {
            return new[] { Once, Every, WholeWindow, Absent };
        }
    }

    /// <summary>
    /// 检查的观察来源。取值必须与统一事件存储真实的 sourceId 一致：程序检查事件是 program，
    /// hook 捕获是 hook，线程化日志是 log，触发事实是 trigger，暂停事实是 pause。
    /// </summary>
    public static class UnityCliDebugRunCheckSources
    {
        public const string Program = "program";
        public const string Hook = "hook";
        public const string Log = "log";
        public const string Trigger = "trigger";
        public const string Pause = "pause";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Program, StringComparison.Ordinal)
                || string.Equals(value, Hook, StringComparison.Ordinal)
                || string.Equals(value, Log, StringComparison.Ordinal)
                || string.Equals(value, Trigger, StringComparison.Ordinal)
                || string.Equals(value, Pause, StringComparison.Ordinal);
        }

        public static string[] All()
        {
            return new[] { Program, Hook, Log, Trigger, Pause };
        }
    }

    /// <summary>
    /// 窗口结束条件的种类。manual 由显式 run.end 满足；check 由同名检查事件满足；
    /// signal 由同名触发事实满足；clock 由声明的窗口时长到期满足。
    /// </summary>
    public static class UnityCliDebugRunEndConditionKinds
    {
        public const string Manual = "manual";
        public const string Check = "check";
        public const string Signal = "signal";
        public const string Clock = "clock";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Manual, StringComparison.Ordinal)
                || string.Equals(value, Check, StringComparison.Ordinal)
                || string.Equals(value, Signal, StringComparison.Ordinal)
                || string.Equals(value, Clock, StringComparison.Ordinal);
        }

        public static string[] All()
        {
            return new[] { Manual, Check, Signal, Clock };
        }
    }

    /// <summary>源覆盖要求的种类：coverage 要求存储层覆盖证明，declared 只记录调用方申报。</summary>
    public static class UnityCliDebugRunCoverageProofs
    {
        public const string Coverage = "coverage";
        public const string Declared = "declared";

        /// <summary>
        /// 报告专用取值：该来源没有任何覆盖申报。它不是可申报的取值，因此不出现在 IsDefined 与 All 里，
        /// 调用方提交 none 会被 sourceRequirements[].proof 的校验拒绝。
        /// </summary>
        public const string None = "none";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Coverage, StringComparison.Ordinal)
                || string.Equals(value, Declared, StringComparison.Ordinal);
        }
    }

    /// <summary>判定原因的稳定取值；每个原因都对应一条可复核的事实，不用自然语言表达结论。</summary>
    public static class UnityCliDebugRunVerdictReasons
    {
        /// <summary>存在完整反例：判定为 failed 的原因码，反例细节在 firstCounterexample 里。</summary>
        public const string Counterexample = "counterexample";

        /// <summary>必需检查在窗口内没有任何匹配证据。</summary>
        public const string NoEvidence = "no-evidence";

        /// <summary>必需来源在窗口区间内存在不可读缺口（淘汰、未入库或保存失败）。</summary>
        public const string CoverageGap = "coverage-gap";

        /// <summary>匹配事件来自采样流：只能证明采样点，不能证明整窗口。</summary>
        public const string SampledOnly = "sampled-only";

        /// <summary>匹配证据被边界截断，不能作为完整覆盖证明。</summary>
        public const string EvidenceTruncated = "evidence-truncated";

        /// <summary>窗口未封闭或结束条件未观察到。</summary>
        public const string WindowNotComplete = "window-not-complete";

        /// <summary>窗口在 runtime 段变化或 session 停止时被中断。</summary>
        public const string WindowInterrupted = "window-interrupted";

        /// <summary>严格窗口内出现了相关配置变化；修订前后不得拼成一次通过。</summary>
        public const string ConfigurationChanged = "configuration-changed";

        /// <summary>配置已变化但缺少可定位的阶段边界，因此无法逐段报告。</summary>
        public const string ConfigurationBoundaryMissing = "configuration-boundary-missing";

        /// <summary>配置段数量超过声明上限，无法给出可信的分段结论。</summary>
        public const string TooManySegments = "too-many-segments";

        /// <summary>封闭边界处 scheduler 仍有未取得执行权的投递或活动 driver，事件可能尚未进入存储。</summary>
        public const string QueueNotDrained = "queue-not-drained";

        /// <summary>该窗口的详细证据已被预算淘汰，只剩摘要。</summary>
        public const string DetailEvicted = "detail-evicted";

        /// <summary>
        /// 消费覆盖证明的量词（whole-window 与 absent）所需来源没有调用方声明的覆盖要求。
        /// 只有这类量词会产生本原因；once 与 every 由已观察到的匹配事件决定，从不产生它。
        /// </summary>
        public const string CoverageNotRequired = "coverage-not-required";

        public static string[] All()
        {
            return new[]
            {
                Counterexample, NoEvidence, CoverageGap, SampledOnly, EvidenceTruncated, WindowNotComplete, WindowInterrupted,
                ConfigurationChanged, ConfigurationBoundaryMissing, TooManySegments, QueueNotDrained, DetailEvicted,
                CoverageNotRequired
            };
        }
    }

    /// <summary>run.compare 的三态比较结论；它不是业务正确与否，只描述两条证据是否可比与是否相同。</summary>
    public static class UnityCliDebugRunCompareResults
    {
        /// <summary>两侧完整、配对完整且比较值全部相同。</summary>
        public const string Equal = "equal";

        /// <summary>存在由两侧真实证据证明的首个差异。</summary>
        public const string Diverged = "diverged";

        /// <summary>缺少可比证据、窗口未封闭、时钟不可用或差异只能靠一侧缺席成立。</summary>
        public const string Indeterminate = "indeterminate";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Equal, StringComparison.Ordinal)
                || string.Equals(value, Diverged, StringComparison.Ordinal)
                || string.Equals(value, Indeterminate, StringComparison.Ordinal);
        }

        public static string[] All()
        {
            return new[] { Equal, Diverged, Indeterminate };
        }
    }

    /// <summary>run.compare 的对齐键种类：单来源调用序号或调用方声明的业务键。</summary>
    public static class UnityCliDebugRunCompareAlignments
    {
        /// <summary>按该来源内匹配事件的调用序号对齐；两侧序号都必须可证明是完整调用序号。</summary>
        public const string Ordinal = "ordinal";

        /// <summary>按业务键的值路径对齐；缺失键值的事件不可配对，必须如实报告。</summary>
        public const string Key = "key";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Ordinal, StringComparison.Ordinal)
                || string.Equals(value, Key, StringComparison.Ordinal);
        }

        public static string[] All()
        {
            return new[] { Ordinal, Key };
        }
    }

    /// <summary>run.compare 的差异种类；只有 value 与 value-missing 由两侧同一事件证明。</summary>
    public static class UnityCliDebugRunCompareDifferenceKinds
    {
        /// <summary>两侧同一对齐位置的值不同。</summary>
        public const string Value = "value";

        /// <summary>两侧同一对齐位置都存在，但值路径在其中一侧不存在。</summary>
        public const string ValueMissing = "value-missing";

        /// <summary>右侧缺少左侧存在的一个事件；需要两侧区间完整才成立。</summary>
        public const string EventMissingRight = "event-missing-right";

        /// <summary>左侧缺少右侧存在的一个事件；需要两侧区间完整才成立。</summary>
        public const string EventMissingLeft = "event-missing-left";

        /// <summary>两侧事件数量不同；需要两侧区间完整才成立。</summary>
        public const string Count = "count";
    }

    /// <summary>run.compare 的条件差异种类；只描述两次运行的可比较条件，不描述业务结论。</summary>
    public static class UnityCliDebugRunCompareConditionKinds
    {
        /// <summary>调用方申报的准备、输入或种子定义 hash 不同。</summary>
        public const string Definition = "definition";

        /// <summary>窗口起点的程序与 hook 配置指纹不同。</summary>
        public const string Configuration = "configuration";

        /// <summary>场景、Play 状态或来源声明等环境读回不同。</summary>
        public const string Environment = "environment";

        /// <summary>两侧在声明时钟上的事件分布不同，或被声明的时钟排除了匹配事件。</summary>
        public const string Clock = "clock";

        /// <summary>两侧区间的可读性与保留状态不同。</summary>
        public const string Coverage = "coverage";

        /// <summary>窗口终态或终止原因不同（sealed 与 interrupted）。</summary>
        public const string WindowState = "window-state";
    }

    /// <summary>run.compare 结论原因的稳定取值；每个原因都对应一条可复核事实。</summary>
    public static class UnityCliDebugRunCompareReasons
    {
        /// <summary>至少一侧窗口仍未封闭，证据集尚未固定，因此比较只能是 indeterminate。</summary>
        public const string WindowOpen = "window-open";

        /// <summary>声明时钟上没有任何可比较事件，而该来源在窗口内确实有过事件。</summary>
        public const string ClockUnavailable = "clock-unavailable";

        /// <summary>该来源在窗口内的事件有一部分带有别的时钟种类，因此没有全部进入比较。</summary>
        public const string ClockExcluded = "clock-excluded";

        /// <summary>窗口区间内存在不可读缺口。</summary>
        public const string CoverageGap = "coverage-gap";

        /// <summary>窗口区间内有事件根本没有进入存储。</summary>
        public const string EventNotRecorded = "event-not-recorded";

        /// <summary>窗口区间的保留事件已被事件环淘汰。</summary>
        public const string EvidenceEvicted = "evidence-evicted";

        /// <summary>匹配事件来自采样流，只证明采样点。</summary>
        public const string SampledOnly = "sampled-only";

        /// <summary>匹配事件的投影被截断，不能用作比较值。</summary>
        public const string EvidenceTruncated = "evidence-truncated";

        /// <summary>对齐键值在部分匹配事件上不存在，这些事件无法配对。</summary>
        public const string AlignmentKeyMissing = "alignment-key-missing";

        /// <summary>对齐键在同一侧重复出现，无法确定唯一配对。</summary>
        public const string AlignmentAmbiguous = "alignment-ambiguous";

        /// <summary>事件数量差异只能靠一侧缺席成立，而该侧区间不完整。</summary>
        public const string CountMismatchUnproven = "count-mismatch-unproven";

        /// <summary>值差异出现在被截断的事件上，因此不是可证明的差异。</summary>
        public const string ValueDifferenceUnproven = "value-difference-unproven";

        /// <summary>结果里的上下文或条件差异被声明上限截断。</summary>
        public const string ResultTruncated = "result-truncated";

        public static string[] All()
        {
            return new[]
            {
                WindowOpen, ClockUnavailable, ClockExcluded, CoverageGap, EventNotRecorded, EvidenceEvicted, SampledOnly,
                EvidenceTruncated, AlignmentKeyMissing, AlignmentAmbiguous, CountMismatchUnproven, ValueDifferenceUnproven,
                ResultTruncated
            };
        }
    }

    /// <summary>窗口证据的保留状态：open 窗口实时推导、封闭窗口冻结保留、被预算淘汰后只剩摘要。</summary>
    public static class UnityCliDebugRunEvidenceStates
    {
        /// <summary>窗口未封闭：报告由读取时刻事件环里真实保留的事件推导。</summary>
        public const string Live = "live";

        /// <summary>窗口已封闭：判定与有界证据在封闭时冻结，之后只按 runId 读取。</summary>
        public const string Frozen = "frozen";

        /// <summary>详细证据载荷已被预算释放，只剩摘要事实与冻结的原因。</summary>
        public const string Evicted = "evicted";
    }
}
