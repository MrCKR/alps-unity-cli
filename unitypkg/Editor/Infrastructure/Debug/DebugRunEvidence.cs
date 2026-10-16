#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityCli
{
    /// <summary>一条必需检查的运行时声明；由请求校验后冻结，判定期只读。</summary>
    internal sealed class DebugRunCheckPlan
    {
        internal string Name = string.Empty;
        internal string Source = string.Empty;
        internal string Position = string.Empty;
        internal string Quantifier = string.Empty;
        internal string HookId = string.Empty;
        internal string Kind = string.Empty;
        internal List<string> RequiredSources = new List<string>();
    }

    /// <summary>一条窗口级源覆盖要求。</summary>
    internal sealed class DebugRunSourcePlan
    {
        internal string Source = string.Empty;

        /// <summary>coverage 表示要求存储层覆盖证明；declared 只记录申报。</summary>
        internal string Proof = string.Empty;

        internal bool ProofRequired
        {
            get { return string.Equals(Proof, UnityCliDebugRunCoverageProofs.Coverage, StringComparison.Ordinal); }
        }
    }

    /// <summary>一条窗口结束条件。</summary>
    internal sealed class DebugRunConditionPlan
    {
        internal string Kind = string.Empty;
        internal string Name = string.Empty;
    }

    /// <summary>
    /// begin 的 effect 前提炼结果：全部声明、捕获起点与配置指纹已冻结。
    /// 任何校验失败都在这里发生，因此跨过 effect barrier 之后不再有可见的前置错误。
    /// </summary>
    internal sealed class DebugRunBeginPlan
    {
        internal string SessionId = string.Empty;
        internal List<DebugRunCheckPlan> Checks = new List<DebugRunCheckPlan>();
        internal List<DebugRunSourcePlan> SourceRequirements = new List<DebugRunSourcePlan>();
        internal List<DebugRunConditionPlan> Conditions = new List<DebugRunConditionPlan>();
        internal bool AllowSegments;
        internal long? MaxWindowMs;
        internal DebugRunCaptureView Capture;
        internal string RuntimeId = string.Empty;
        internal string DomainGeneration = string.Empty;
        internal string EditorSessionId = string.Empty;
        internal Dictionary<string, object> Environment = new Dictionary<string, object>(StringComparer.Ordinal);
        internal string DefinitionHash = string.Empty;
        internal List<string> Configuration = new List<string>();
        internal string ConfigurationFingerprint = string.Empty;
        internal HashSet<string> SessionPrograms = new HashSet<string>(StringComparer.Ordinal);
        internal string StartedAtUtc = string.Empty;
        internal double StartedAtMonotonicMs;
    }

    /// <summary>
    /// 一个证据窗口的全部事实。窗口只持有声明、边界、判定与有界证据摘要；
    /// 事件真值仍在唯一 DebugEventStore 里，本类不复制事件流。
    /// </summary>
    internal sealed class DebugRunWindow
    {
        internal string RunId = string.Empty;
        internal string SessionId = string.Empty;
        internal string State = UnityCliDebugRunStates.Open;
        internal string RuntimeId = string.Empty;
        internal string DomainGeneration = string.Empty;
        internal string StartedAtUtc = string.Empty;
        internal double StartedAtMonotonicMs;
        internal string SealedAtUtc = string.Empty;
        internal double SealedAtMonotonicMs;
        internal string TerminationReason = string.Empty;
        internal long StartSequence;
        internal long EndSequence;
        internal long? MaxWindowMs;
        internal bool AllowSegments;
        internal string DefinitionHash = string.Empty;
        internal Dictionary<string, object> Environment = new Dictionary<string, object>(StringComparer.Ordinal);
        internal List<DebugRunCheckPlan> Checks = new List<DebugRunCheckPlan>();
        internal List<DebugRunSourcePlan> SourceRequirements = new List<DebugRunSourcePlan>();
        internal List<DebugRunConditionPlan> Conditions = new List<DebugRunConditionPlan>();

        /// <summary>begin 时的配置指纹与逐条配置回读；封印时用当时的指纹比较。</summary>
        internal string ConfigurationFingerprint = string.Empty;
        internal string ConfigurationFingerprintAfter = string.Empty;
        internal List<string> ConfigurationBefore = new List<string>();
        internal List<string> ConfigurationAfter = new List<string>();

        /// <summary>该窗口引用的 hook 身份；由检查声明与窗口内事件共同得出，用于配置变化归因。</summary>
        internal HashSet<string> ReferencedHooks = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>该窗口所属 session 的 program 身份集合；用于把配置变化归因到本窗口。</summary>
        internal HashSet<string> SessionPrograms = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>保留详细证据；详细证据被预算淘汰时为 false，只剩摘要。</summary>
        /// <summary>本窗口详细证据是否仍保留在窗口记录里；被预算淘汰后只剩摘要与冻结的判定。</summary>
        internal bool DetailAvailable = true;

        /// <summary>
        /// 封闭时冻结的报告：判定、原因、覆盖事实与有界证据载荷在窗口终止的那一刻固定下来，
        /// 之后只按 runId 读取，不再依赖共享事件环是否仍保留同一区间。
        /// 开窗期间为空，读取时按当前保留事件实时推导。
        /// </summary>
        internal DebugRunReportView FrozenReport;

        /// <summary>该窗口详细证据占用的全局字节账本份额。</summary>
        internal long DetailBytes;

        /// <summary>同一时刻仍保留的最早 sequence；判定用它确认窗口自己的区间是否真的丢过事件。</summary>
        internal long OldestAvailableAtEnd;

        /// <summary>begin 与边界时刻未能入库的追加计数；判定用它们的增量描述区间内不可读事实。</summary>
        internal long AppendFailuresAtStart;
        internal long AppendFailuresAtEnd;

        /// <summary>终端窗口冻结的捕获事实与 queue 水位；开放窗口每次读取重新捕获。</summary>
        internal DebugRunCaptureView Capture;

        /// <summary>淘汰引起的额外判定原因（详细证据被预算回收）。</summary>
        internal readonly List<string> EvictedReasons = new List<string>();

        internal bool IsTerminal
        {
            get { return UnityCliDebugRunStates.IsTerminal(State); }
        }
    }

    /// <summary>一个必需检查在一个区间内的判定结果。</summary>
    internal sealed class DebugRunCheckEvaluation
    {
        internal DebugRunCheckPlan Plan;
        internal string Status = DebugRunEvidence.StatusInsufficient;
        internal long MatchCount;
        internal long PassedCount;
        internal long FailedCount;

        /// <summary>该量词的证明是否消费调用方声明的覆盖要求；只有 whole-window 与 absent 为 true。</summary>
        internal bool CoverageRequired;

        /// <summary>该检查所需来源在本次区间内是否可读（无缺口、无采样、无截断）；与是否申报无关。</summary>
        internal bool CoverageProven;
        internal bool SamplePointOnly;
        internal bool Truncated;
        internal List<UnityCliDebugEvent> Matches = new List<UnityCliDebugEvent>();
        internal readonly List<string> Reasons = new List<string>();
        internal readonly List<DebugRunCoverageGapView> Gaps = new List<DebugRunCoverageGapView>();
    }

    /// <summary>一个来源在一个区间内的覆盖事实。</summary>
    internal sealed class DebugRunSourceEvaluation
    {
        internal string Source = string.Empty;

        /// <summary>调用方是否为本来源申报过覆盖要求（coverage 或 declared）；从未申报时报告为 none。</summary>
        internal bool Declared;

        /// <summary>申报中是否含 proof coverage；只有它为 true 时该来源支撑覆盖量词的证明。</summary>
        internal bool ProofRequired;
        internal bool CoverageProven;
        internal long MatchCount;
        internal bool Sampled;
        internal bool Truncated;
        internal readonly List<string> Reasons = new List<string>();
        internal readonly List<DebugRunCoverageGapView> Gaps = new List<DebugRunCoverageGapView>();
    }

    /// <summary>一个区间的评估结果；分段报告与整窗口判定共用它。</summary>
    internal sealed class DebugRunIntervalEvaluation
    {
        internal long FromSequence;
        internal long ToSequence;
        internal readonly List<DebugRunCheckEvaluation> Checks = new List<DebugRunCheckEvaluation>();
        internal readonly List<DebugRunSourceEvaluation> Sources = new List<DebugRunSourceEvaluation>();
        internal readonly List<DebugRunCounterexampleView> Counterexamples = new List<DebugRunCounterexampleView>();
        internal readonly List<string> Reasons = new List<string>();
        internal readonly List<DebugRunCoverageGapView> Gaps = new List<DebugRunCoverageGapView>();
    }

    /// <summary>一段配置区间的判定摘要；报告只用它表达逐段结论。</summary>
    internal sealed class DebugRunSegmentEvaluation
    {
        internal long Index;
        internal long FromSequence;
        internal long ToSequence;
        internal string Verdict = UnityCliDebugRunVerdicts.Passed;
        internal readonly List<string> Reasons = new List<string>();
        internal readonly List<DebugRunCheckStatusView> CheckStatuses = new List<DebugRunCheckStatusView>();
        internal readonly List<DebugRunCounterexampleView> Counterexamples = new List<DebugRunCounterexampleView>();
        internal string BoundaryFact = string.Empty;
    }

    /// <summary>窗口级判定结果；报告装配只读它，不再重新解释证据。</summary>
    internal sealed class DebugRunVerdict
    {
        internal string Verdict = UnityCliDebugRunVerdicts.Indeterminate;
        internal readonly List<string> Reasons = new List<string>();
        internal readonly List<DebugRunCheckEvaluation> Checks = new List<DebugRunCheckEvaluation>();
        internal readonly List<DebugRunSourceEvaluation> Sources = new List<DebugRunSourceEvaluation>();
        internal DebugRunCounterexampleView FirstCounterexample;
        internal readonly List<DebugRunCoverageGapView> Gaps = new List<DebugRunCoverageGapView>();
        internal readonly List<DebugRunSegmentEvaluation> Segments = new List<DebugRunSegmentEvaluation>();

        /// <summary>每一条件声明是否已在窗口内观察到；顺序与窗口声明的条件一致。</summary>
        internal readonly List<bool> ConditionObserved = new List<bool>();

        /// <summary>检测到的相关配置变化；严格窗口按它降为 indeterminate。</summary>
        internal bool ConfigurationChanged;
        internal bool ConfigurationBoundaryMissing;
        internal bool TooManySegments;
        internal int ReferencedHookCount;

        /// <summary>是否至少观察到一条结束条件；没有声明的窗口在封闭时按显式 run.end 视为已满足。</summary>
        internal bool ConditionsObserved
        {
            get
            {
                foreach (var observed in ConditionObserved)
                {
                    if (observed) return true;
                }
                return ConditionObserved.Count == 0;
            }
        }
    }

    /// <summary>
    /// 证据窗口的判定实现：把统一事件存储里真实存在的事件归约为 per-check 证明、反例与覆盖缺口。
    ///
    /// 规则：
    /// - 判定只用保留窗口内的真实事件：一次匹配是否算证明由量词决定，
    ///   而“没有匹配”只有在覆盖可证时才成立；缺口、采样与截断都会使证明不足。
    /// - 反例是完整的：只要存在一个可证明失败（或 absent 量词下的任何匹配），判定就是 failed，
    ///   后续无关缺口不会把它改回 passed。
    /// - 任意代码申报的 coverage 文本只作为 declaredCoverage 记录，永不当作覆盖证明。
    /// </summary>
    internal static class DebugRunEvidence
    {
        internal const string StatusSatisfied = "satisfied";
        internal const string StatusCounterexample = "counterexample";
        internal const string StatusInsufficient = "insufficient";

        internal const string TerminationRunEnd = "run-end";
        internal const string TerminationRuntimeChanged = "runtime-changed";
        internal const string TerminationSessionStopped = "session-stopped";

        /// <summary>判断一条事件是否命中检查声明的位置与筛选。</summary>
        internal static bool Matches(DebugRunCheckPlan check, UnityCliDebugEvent entry)
        {
            if (check == null || entry == null) return false;
            if (!string.Equals(entry.SourceId, check.Source, StringComparison.Ordinal)) return false;
            if (!string.Equals(check.Position, "*", StringComparison.Ordinal)
                && !string.Equals(entry.ExecutionPoint ?? string.Empty, check.Position, StringComparison.Ordinal)) return false;
            if (check.HookId.Length != 0 && !string.Equals(entry.HookId ?? string.Empty, check.HookId, StringComparison.Ordinal)) return false;
            if (check.Kind.Length != 0 && !string.Equals(entry.Kind ?? string.Empty, check.Kind, StringComparison.Ordinal)) return false;
            return string.Equals(entry.Name ?? string.Empty, check.Name, StringComparison.Ordinal);
        }

        /// <summary>程序检查事件自带 passed；其它来源的一次匹配只表示事件确实发生。</summary>
        internal static bool PassedOf(UnityCliDebugEvent entry)
        {
            var value = entry == null ? null : entry.Value as Dictionary<string, object>;
            object passed;
            if (value != null && value.TryGetValue("passed", out passed) && passed is bool) return (bool)passed;
            return true;
        }

        /// <summary>
        /// 窗口级判定。封闭且覆盖充分的窗口才可能 passed；严格窗口内的配置变化降为 indeterminate，
        /// 允许分段时逐段报告并取最差段结论。
        /// </summary>
        internal static DebugRunVerdict Evaluate(
            DebugRunWindow window,
            IList<UnityCliDebugEvent> events,
            IReadOnlyList<UnityCliDebugGap> gaps,
            long appendFailureDelta,
            long oldestAvailableAtBoundary,
            long boundarySequence,
            bool queueDrained)
        {
            var verdict = new DebugRunVerdict();
            var interval = EvaluateInterval(window, events, gaps, appendFailureDelta, oldestAvailableAtBoundary, window.StartSequence, boundarySequence);
            verdict.Checks.AddRange(interval.Checks);
            verdict.Sources.AddRange(interval.Sources);
            foreach (var gap in interval.Gaps)
            {
                if (!ContainsGap(verdict.Gaps, gap)) verdict.Gaps.Add(gap);
            }
            EvaluateConditions(window, events, verdict);
            var referencedHooks = ReferencedHooks(window, events);
            verdict.ReferencedHookCount = referencedHooks.Count;
            var boundaries = ConfigurationBoundaries(window, events, referencedHooks, boundarySequence, verdict);

            var failed = interval.Counterexamples.Count > 0;
            if (failed)
            {
                verdict.Verdict = UnityCliDebugRunVerdicts.Failed;
                verdict.FirstCounterexample = Earliest(interval.Counterexamples);
                verdict.Reasons.Add(UnityCliDebugRunVerdictReasons.Counterexample);
                return verdict;
            }

            var insufficient = false;
            foreach (var check in interval.Checks)
            {
                if (string.Equals(check.Status, StatusInsufficient, StringComparison.Ordinal)) insufficient = true;
            }
            CollectReasons(interval, verdict.Reasons, ref insufficient);

            if (verdict.ConfigurationChanged && window.AllowSegments)
            {
                if (verdict.ConfigurationBoundaryMissing)
                {
                    AddReason(verdict.Reasons, UnityCliDebugRunVerdictReasons.ConfigurationBoundaryMissing);
                    insufficient = true;
                }
                else if (boundaries.Count - 1 > DebugLimits.MaxRunSegments)
                {
                    verdict.TooManySegments = true;
                    AddReason(verdict.Reasons, UnityCliDebugRunVerdictReasons.TooManySegments);
                    insufficient = true;
                }
                else
                {
                    EvaluateSegments(window, events, gaps, appendFailureDelta, oldestAvailableAtBoundary, boundaries, verdict);
                    var segmentInsufficient = false;
                    foreach (var segment in verdict.Segments)
                    {
                        if (!string.Equals(segment.Verdict, UnityCliDebugRunVerdicts.Passed, StringComparison.Ordinal)) segmentInsufficient = true;
                        foreach (var reason in segment.Reasons) AddReason(verdict.Reasons, reason);
                    }
                    if (segmentInsufficient) insufficient = true;
                }
            }
            else if (verdict.ConfigurationChanged)
            {
                AddReason(verdict.Reasons, UnityCliDebugRunVerdictReasons.ConfigurationChanged);
                insufficient = true;
            }

            if (!string.Equals(window.State, UnityCliDebugRunStates.Sealed, StringComparison.Ordinal))
            {
                AddReason(verdict.Reasons, string.Equals(window.State, UnityCliDebugRunStates.Interrupted, StringComparison.Ordinal)
                    ? UnityCliDebugRunVerdictReasons.WindowInterrupted
                    : UnityCliDebugRunVerdictReasons.WindowNotComplete);
                insufficient = true;
            }
            else if (!verdict.ConditionsObserved)
            {
                AddReason(verdict.Reasons, UnityCliDebugRunVerdictReasons.WindowNotComplete);
                insufficient = true;
            }
            if (!queueDrained)
            {
                AddReason(verdict.Reasons, UnityCliDebugRunVerdictReasons.QueueNotDrained);
                insufficient = true;
            }
            verdict.Verdict = insufficient ? UnityCliDebugRunVerdicts.Indeterminate : UnityCliDebugRunVerdicts.Passed;
            return verdict;
        }

        /// <summary>
        /// 评估一个区间：逐检查求证明与反例，并给出该区间的源覆盖事实。
        /// gaps 是保留窗口已记录的不可读区间，appendFailureDelta 是未能入库的追加次数，
        /// oldestAvailableAtBoundary 是同一边界时刻仍保留的最早 sequence，用来把环淘汰定位到它真正发生的区间。
        /// </summary>
        internal static DebugRunIntervalEvaluation EvaluateInterval(
            DebugRunWindow window,
            IList<UnityCliDebugEvent> events,
            IReadOnlyList<UnityCliDebugGap> gaps,
            long appendFailureDelta,
            long oldestAvailableAtBoundary,
            long fromSequence,
            long toSequence)
        {
            var result = new DebugRunIntervalEvaluation { FromSequence = fromSequence, ToSequence = toSequence };
            var intervalGaps = new List<DebugRunCoverageGapView>();
            if (appendFailureDelta > 0L)
            {
                // 事件根本没有进入存储，具体区间无法定位，只能在整段区间上如实记录不可读。
                intervalGaps.Add(new DebugRunCoverageGapView
                {
                    FromSequence = fromSequence,
                    ToSequence = toSequence,
                    Reason = "event-not-recorded"
                });
            }
            if (window != null)
            {
                // 环淘汰只有在本窗口自己的区间真的丢了 sequence 时才构成窗口内的空洞：
                // 淘汰早于窗口起点的事件让更早的证据不可读，但不影响本窗口区间的完整性。
                var lostFrom = window.StartSequence + 1L;
                var lostTo = oldestAvailableAtBoundary - 1L;
                if (oldestAvailableAtBoundary > lostFrom && lostFrom <= toSequence && lostTo >= fromSequence)
                {
                    intervalGaps.Add(new DebugRunCoverageGapView
                    {
                        FromSequence = Math.Max(lostFrom, fromSequence),
                        ToSequence = Math.Min(lostTo, toSequence),
                        Reason = "event-ring-capacity"
                    });
                }
            }
            if (gaps != null)
            {
                foreach (var gap in gaps)
                {
                    if (gap == null) continue;
                    // 区间在起点排他：结束于起点或更早、以及起始晚于终点的缺口都不在本区间内。
                    if (gap.ToSequence <= fromSequence || gap.FromSequence > toSequence) continue;
                    intervalGaps.Add(new DebugRunCoverageGapView
                    {
                        FromSequence = Math.Max(gap.FromSequence, fromSequence),
                        ToSequence = Math.Min(gap.ToSequence, toSequence),
                        Reason = gap.Reason ?? string.Empty
                    });
                }
            }
            result.Gaps.AddRange(intervalGaps);

            var requiredSources = RequiredSourcesImpl(window);
            foreach (var source in requiredSources)
            {
                var evaluation = new DebugRunSourceEvaluation { Source = source };
                // 申报事实只进来源自己的报告字段：once/every 从不消费覆盖申报，因此“没有被要求”不是判定原因。
                foreach (var requirement in window.SourceRequirements)
                {
                    if (!string.Equals(requirement.Source, source, StringComparison.Ordinal)) continue;
                    evaluation.Declared = true;
                    if (requirement.ProofRequired) evaluation.ProofRequired = true;
                }
                var matched = 0L;
                var sampled = false;
                var truncated = false;
                if (events != null)
                {
                    foreach (var entry in events)
                    {
                        if (entry == null || !string.Equals(entry.SourceId, source, StringComparison.Ordinal)) continue;
                        matched++;
                        if (entry.Sampled || entry.SampleEvery > 1) sampled = true;
                        if (entry.Truncated || entry.ProjectionTruncated) truncated = true;
                    }
                }
                evaluation.MatchCount = matched;
                evaluation.Sampled = sampled;
                evaluation.Truncated = truncated;
                evaluation.Gaps.AddRange(intervalGaps);
                var coverageProven = intervalGaps.Count == 0;
                if (intervalGaps.Count > 0) AddReason(evaluation.Reasons, UnityCliDebugRunVerdictReasons.CoverageGap);
                if (sampled)
                {
                    AddReason(evaluation.Reasons, UnityCliDebugRunVerdictReasons.SampledOnly);
                    coverageProven = false;
                }
                if (truncated)
                {
                    AddReason(evaluation.Reasons, UnityCliDebugRunVerdictReasons.EvidenceTruncated);
                    coverageProven = false;
                }
                evaluation.CoverageProven = coverageProven;
                result.Sources.Add(evaluation);
            }

            foreach (var check in window.Checks)
            {
                var evaluation = new DebugRunCheckEvaluation { Plan = check };
                if (events != null)
                {
                    foreach (var entry in events)
                    {
                        if (!Matches(check, entry)) continue;
                        evaluation.MatchCount++;
                        if (PassedOf(entry)) evaluation.PassedCount++;
                        else evaluation.FailedCount++;
                        if (entry.Sampled || entry.SampleEvery > 1) evaluation.SamplePointOnly = true;
                        if (entry.Truncated || entry.ProjectionTruncated) evaluation.Truncated = true;
                        if (evaluation.Matches.Count < DebugLimits.MaxRunEvidencePerCheck) evaluation.Matches.Add(entry);
                    }
                }
                // 量词决定证明方式：whole-window/absent 消费调用方声明的覆盖证明，once/every 由已观察到的匹配事件决定。
                // 因此“是否有覆盖申报”只作为来源自身的报告事实，绝不进入窗口级原因，也不使一次匹配失去证明力。
                var coverageDeclared = false;
                var coverageProven = true;
                foreach (var source in check.RequiredSources)
                {
                    foreach (var sourceEvaluation in result.Sources)
                    {
                        if (!string.Equals(sourceEvaluation.Source, source, StringComparison.Ordinal)) continue;
                        if (sourceEvaluation.ProofRequired) coverageDeclared = true;
                        if (!sourceEvaluation.CoverageProven) coverageProven = false;
                        foreach (var reason in sourceEvaluation.Reasons) AddReason(evaluation.Reasons, reason);
                    }
                }
                var requiresWholeWindow = string.Equals(check.Quantifier, UnityCliDebugRunQuantifiers.WholeWindow, StringComparison.Ordinal)
                    || string.Equals(check.Quantifier, UnityCliDebugRunQuantifiers.Absent, StringComparison.Ordinal);
                evaluation.CoverageRequired = requiresWholeWindow;
                if (requiresWholeWindow && !coverageDeclared)
                {
                    // 负向与全窗口检查必须有调用方声明的覆盖要求：begin 已拒绝这种声明，这里守住同一规则，
                    // 使评估期缺少申报的检查绝不被证明，宿主也绝不自行发明覆盖。
                    AddReason(evaluation.Reasons, UnityCliDebugRunVerdictReasons.CoverageNotRequired);
                    coverageProven = false;
                }
                if (evaluation.SamplePointOnly) AddReason(evaluation.Reasons, UnityCliDebugRunVerdictReasons.SampledOnly);
                if (evaluation.Truncated) AddReason(evaluation.Reasons, UnityCliDebugRunVerdictReasons.EvidenceTruncated);
                foreach (var gap in intervalGaps)
                {
                    if (!ContainsGap(evaluation.Gaps, gap)) evaluation.Gaps.Add(gap);
                }
                evaluation.CoverageProven = coverageProven;

                if (evaluation.FailedCount > 0L)
                {
                    evaluation.Status = StatusCounterexample;
                    var first = FirstFailed(evaluation.Matches);
                    if (first != null) result.Counterexamples.Add(CounterexampleOf(check, first));
                }
                else if (string.Equals(check.Quantifier, UnityCliDebugRunQuantifiers.Absent, StringComparison.Ordinal))
                {
                    if (evaluation.MatchCount > 0L)
                    {
                        evaluation.Status = StatusCounterexample;
                        result.Counterexamples.Add(CounterexampleOf(check, evaluation.Matches[0]));
                    }
                    else if (evaluation.CoverageProven)
                    {
                        evaluation.Status = StatusSatisfied;
                    }
                    else
                    {
                        evaluation.Status = StatusInsufficient;
                    }
                }
                else if (evaluation.MatchCount == 0L)
                {
                    evaluation.Status = StatusInsufficient;
                    AddReason(evaluation.Reasons, UnityCliDebugRunVerdictReasons.NoEvidence);
                }
                else if (!evaluation.CoverageProven)
                {
                    evaluation.Status = StatusInsufficient;
                }
                else
                {
                    evaluation.Status = StatusSatisfied;
                }
                result.Checks.Add(evaluation);
            }
            return result;
        }

        /// <summary>全部被要求的来源：窗口级要求与每条检查的所需源合并去重。</summary>
        private static List<string> RequiredSourcesImpl(DebugRunWindow window)
        {
            var sources = new List<string>();
            foreach (var requirement in window.SourceRequirements)
            {
                if (!sources.Contains(requirement.Source)) sources.Add(requirement.Source);
            }
            foreach (var check in window.Checks)
            {
                foreach (var source in check.RequiredSources)
                {
                    if (!sources.Contains(source)) sources.Add(source);
                }
            }
            return sources;
        }

        /// <summary>窗口引用的 hook 身份：检查声明与窗口内事件的并集，用于配置变化归因。</summary>
        private static HashSet<string> ReferencedHooks(DebugRunWindow window, IList<UnityCliDebugEvent> events)
        {
            var hooks = new HashSet<string>(window.ReferencedHooks, StringComparer.Ordinal);
            if (events != null)
            {
                foreach (var entry in events)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.HookId)) continue;
                    hooks.Add(entry.HookId);
                }
            }
            return hooks;
        }

        /// <summary>逐段评估配置区间；每段独立要求全部必需检查成立。</summary>
        private static void EvaluateSegments(
            DebugRunWindow window,
            IList<UnityCliDebugEvent> events,
            IReadOnlyList<UnityCliDebugGap> gaps,
            long appendFailureDelta,
            long oldestAvailableAtBoundary,
            List<long> boundaries,
            DebugRunVerdict verdict)
        {
            for (var index = 0; index + 1 < boundaries.Count; index++)
            {
                var from = boundaries[index];
                var to = boundaries[index + 1];
                var segmentEvents = new List<UnityCliDebugEvent>();
                if (events != null)
                {
                    foreach (var entry in events)
                    {
                        if (entry == null || entry.Sequence <= from || entry.Sequence > to) continue;
                        segmentEvents.Add(entry);
                    }
                }
                var interval = EvaluateInterval(window, segmentEvents, gaps, appendFailureDelta, oldestAvailableAtBoundary, from, to);
                var segment = new DebugRunSegmentEvaluation
                {
                    Index = index + 1L,
                    FromSequence = from,
                    ToSequence = to,
                    BoundaryFact = index == 0 ? "window-start" : BoundaryFactOf(events, from)
                };
                foreach (var check in interval.Checks)
                {
                    segment.CheckStatuses.Add(new DebugRunCheckStatusView
                    {
                        Name = check.Plan == null ? string.Empty : check.Plan.Name,
                        Status = check.Status,
                        MatchCount = check.MatchCount,
                        FailedCount = check.FailedCount
                    });
                    if (string.Equals(check.Status, StatusCounterexample, StringComparison.Ordinal))
                        segment.Verdict = UnityCliDebugRunVerdicts.Failed;
                    else if (string.Equals(check.Status, StatusInsufficient, StringComparison.Ordinal)
                        && !string.Equals(segment.Verdict, UnityCliDebugRunVerdicts.Failed, StringComparison.Ordinal))
                        segment.Verdict = UnityCliDebugRunVerdicts.Indeterminate;
                    foreach (var reason in check.Reasons) AddReason(segment.Reasons, reason);
                }
                segment.Counterexamples.AddRange(interval.Counterexamples);
                verdict.Segments.Add(segment);
            }
        }

        /// <summary>窗口内可定位的配置变化边界序列；无变化时只返回起止边界。</summary>
        private static List<long> ConfigurationBoundaries(
            DebugRunWindow window,
            IList<UnityCliDebugEvent> events,
            HashSet<string> referencedHooks,
            long boundarySequence,
            DebugRunVerdict verdict)
        {
            var boundaries = new List<long> { window.StartSequence };
            var changeEvents = 0;
            if (events != null)
            {
                foreach (var entry in events)
                {
                    if (entry == null) continue;
                    if (!IsConfigurationBoundary(window, referencedHooks, entry)) continue;
                    changeEvents++;
                    boundaries.Add(entry.Sequence);
                }
            }
            var end = boundarySequence;
            foreach (var boundary in boundaries)
            {
                if (boundary > end) end = boundary;
            }
            boundaries.Add(end);
            verdict.ConfigurationChanged = changeEvents > 0 || !string.Equals(window.ConfigurationFingerprint, window.ConfigurationFingerprintAfter, StringComparison.Ordinal);
            // 指纹变化但找不到可定位边界时无法逐段报告；这时绝不伪造单一配置结论。
            verdict.ConfigurationBoundaryMissing = verdict.ConfigurationChanged && changeEvents == 0;
            return boundaries;
        }

        /// <summary>该事件是否是本窗口相关资源的配置发布边界。</summary>
        internal static bool IsConfigurationBoundary(DebugRunWindow window, HashSet<string> referencedHooks, UnityCliDebugEvent entry)
        {
            if (entry == null) return false;
            var name = entry.Name ?? string.Empty;
            if (string.Equals(entry.SourceId, UnityCliDebugSources.Lifecycle, StringComparison.Ordinal)
                && string.Equals(entry.Kind, "lifecycle", StringComparison.Ordinal))
            {
                if (!referencedHooks.Contains(entry.HookId ?? string.Empty)) return false;
                return string.Equals(name, "installed", StringComparison.Ordinal)
                    || string.Equals(name, "replaced", StringComparison.Ordinal)
                    || string.Equals(name, "enabled", StringComparison.Ordinal)
                    || string.Equals(name, "disabled", StringComparison.Ordinal)
                    || string.Equals(name, "removed", StringComparison.Ordinal);
            }
            if (string.Equals(entry.SourceId, UnityCliDebugProgramAbi.EventSource, StringComparison.Ordinal)
                && string.Equals(entry.Kind, "lifecycle", StringComparison.Ordinal))
            {
                var identity = entry.MethodIdentity ?? string.Empty;
                var separator = identity.IndexOf(':');
                var programId = separator > 0 ? identity.Substring(0, separator) : identity;
                if (!window.SessionPrograms.Contains(programId)) return false;
                return string.Equals(name, "installed", StringComparison.Ordinal)
                    || string.Equals(name, "replaced", StringComparison.Ordinal);
            }
            return false;
        }

        /// <summary>窗口结束条件的观察事实：只认窗口内真实存在的证据。</summary>
        private static void EvaluateConditions(DebugRunWindow window, IList<UnityCliDebugEvent> events, DebugRunVerdict verdict)
        {
            foreach (var condition in window.Conditions)
            {
                var observed = false;
                if (string.Equals(condition.Kind, UnityCliDebugRunEndConditionKinds.Manual, StringComparison.Ordinal))
                {
                    observed = string.Equals(window.State, UnityCliDebugRunStates.Sealed, StringComparison.Ordinal)
                        && string.Equals(window.TerminationReason, TerminationRunEnd, StringComparison.Ordinal);
                }
                else if (string.Equals(condition.Kind, UnityCliDebugRunEndConditionKinds.Clock, StringComparison.Ordinal))
                {
                    observed = window.MaxWindowMs.HasValue
                        && string.Equals(window.State, UnityCliDebugRunStates.Sealed, StringComparison.Ordinal)
                        && window.SealedAtMonotonicMs - window.StartedAtMonotonicMs >= (double)window.MaxWindowMs.Value;
                }
                else if (events != null && condition.Name.Length != 0)
                {
                    foreach (var entry in events)
                    {
                        if (entry == null || !string.Equals(entry.Name ?? string.Empty, condition.Name, StringComparison.Ordinal)) continue;
                        if (string.Equals(condition.Kind, UnityCliDebugRunEndConditionKinds.Signal, StringComparison.Ordinal)
                            && !string.Equals(entry.SourceId, UnityCliDebugSources.Trigger, StringComparison.Ordinal)) continue;
                        observed = true;
                        break;
                    }
                }
                verdict.ConditionObserved.Add(observed);
            }
        }

        private static string BoundaryFactOf(IList<UnityCliDebugEvent> events, long sequence)
        {
            if (events == null) return "configuration-change";
            foreach (var entry in events)
            {
                if (entry == null || entry.Sequence != sequence) continue;
                return (entry.SourceId ?? string.Empty) + ":" + (entry.Name ?? string.Empty)
                    + (string.IsNullOrEmpty(entry.HookId) ? string.Empty : " hook=" + entry.HookId)
                    + (string.IsNullOrEmpty(entry.MethodIdentity) ? string.Empty : " " + entry.MethodIdentity);
            }
            return "configuration-change";
        }

        /// <summary>检查在窗口内的匹配证据；报告只读这些真实事件。</summary>
        internal static List<DebugRunEvidenceView> EvidenceOf(DebugRunCheckEvaluation check)
        {
            var views = new List<DebugRunEvidenceView>();
            if (check == null) return views;
            foreach (var entry in check.Matches)
            {
                var view = EvidenceOf(entry);
                if (view != null) views.Add(view);
            }
            return views;
        }

        internal static DebugRunEvidenceView EvidenceOf(UnityCliDebugEvent entry)
        {
            if (entry == null) return null;
            return new DebugRunEvidenceView
            {
                Sequence = entry.Sequence,
                SourceId = entry.SourceId ?? string.Empty,
                Kind = entry.Kind ?? string.Empty,
                Name = entry.Name ?? string.Empty,
                ExecutionPoint = entry.ExecutionPoint ?? string.Empty,
                Revision = entry.Revision,
                ThreadId = entry.ThreadId,
                MainThread = entry.MainThread,
                ClockKind = entry.ClockKind ?? string.Empty,
                ClockValue = entry.ClockValue,
                Frame = entry.Frame,
                HookId = entry.HookId ?? string.Empty,
                MethodIdentity = entry.MethodIdentity ?? string.Empty,
                Passed = PassedOf(entry),
                Expected = ValueField(entry, "expected"),
                Actual = ValueField(entry, "actual"),
                DeclaredCoverage = ValueText(entry, "coverage"),
                Sampled = entry.Sampled,
                Truncated = entry.Truncated || entry.ProjectionTruncated,
                // 证据引用指向统一事件存储里的真实 sequence，不是外部不可核对的字符串。
                EvidenceRefs = new List<string> { "sequence:" + entry.Sequence.ToString(CultureInfo.InvariantCulture) }
            };
        }

        internal static DebugRunCounterexampleView CounterexampleOf(DebugRunCheckPlan check, UnityCliDebugEvent entry)
        {
            if (entry == null) return null;
            return new DebugRunCounterexampleView
            {
                CheckName = check == null ? string.Empty : check.Name,
                SourceId = entry.SourceId ?? string.Empty,
                Kind = entry.Kind ?? string.Empty,
                Sequence = entry.Sequence,
                Revision = entry.Revision,
                ExecutionPoint = entry.ExecutionPoint ?? string.Empty,
                MethodIdentity = entry.MethodIdentity ?? string.Empty,
                HookId = entry.HookId ?? string.Empty,
                ThreadId = entry.ThreadId,
                MainThread = entry.MainThread,
                ClockKind = entry.ClockKind ?? string.Empty,
                ClockValue = entry.ClockValue,
                Frame = entry.Frame,
                Expected = ValueField(entry, "expected"),
                Actual = ValueField(entry, "actual"),
                Passed = PassedOf(entry),
                EvidenceRefs = new List<string> { "sequence:" + entry.Sequence.ToString(CultureInfo.InvariantCulture) }
            };
        }

        /// <summary>值投影里的一个字段；不存在时返回 null，不猜测。</summary>
        private static object ValueField(UnityCliDebugEvent entry, string key)
        {
            var value = entry == null ? null : entry.Value as Dictionary<string, object>;
            object item;
            return value != null && value.TryGetValue(key, out item) ? item : null;
        }

        private static string ValueText(UnityCliDebugEvent entry, string key)
        {
            var value = ValueField(entry, key);
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static UnityCliDebugEvent FirstFailed(List<UnityCliDebugEvent> matches)
        {
            foreach (var entry in matches)
            {
                if (!PassedOf(entry)) return entry;
            }
            return null;
        }

        private static DebugRunCounterexampleView Earliest(List<DebugRunCounterexampleView> candidates)
        {
            DebugRunCounterexampleView earliest = null;
            foreach (var candidate in candidates)
            {
                if (candidate == null) continue;
                if (earliest == null || candidate.Sequence < earliest.Sequence) earliest = candidate;
            }
            return earliest;
        }

        private static void CollectReasons(DebugRunIntervalEvaluation interval, List<string> reasons, ref bool insufficient)
        {
            foreach (var check in interval.Checks)
            {
                if (string.Equals(check.Status, StatusInsufficient, StringComparison.Ordinal)) insufficient = true;
                foreach (var reason in check.Reasons) AddReason(reasons, reason);
            }
            foreach (var source in interval.Sources)
            {
                foreach (var reason in source.Reasons) AddReason(reasons, reason);
            }
        }

        internal static void AddReason(List<string> reasons, string reason)
        {
            if (reasons == null || string.IsNullOrEmpty(reason)) return;
            if (!reasons.Contains(reason)) reasons.Add(reason);
        }

        private static bool ContainsGap(List<DebugRunCoverageGapView> gaps, DebugRunCoverageGapView candidate)
        {
            if (candidate == null) return true;
            foreach (var gap in gaps)
            {
                if (gap.FromSequence == candidate.FromSequence && gap.ToSequence == candidate.ToSequence
                    && string.Equals(gap.Reason, candidate.Reason, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
#endif
