#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// debug.run.compare 的判定内核：把两个保留窗口里同一来源的事件按调用方声明的对齐键配对，
    /// 在调用方声明的值路径与时钟上定位首个可证明差异。
    ///
    /// 规则：
    /// - 事件真值只有 DebugEventStore 一份；本类只读取窗口区间内真实保留的事件，不建立第二份事件副本。
    /// - 事件视图复用存储的公开事件投影（UnityCliEventBuffer.ToData），不实现第二套投影。
    /// - 对齐键、值路径与适用时钟都必须由调用方声明：接口不默认使用 global sequence、对象地址或 wall time 对齐。
    /// - 只凭两侧真实存在的证据判差异：需要一侧“缺席”才能成立的差异，在任一区间不完整时只报 indeterminate。
    /// - 被截断或被采样的证据不能支撑完整结论：截断的事件不提供可信比较值，采样流只证明采样点。
    ///
    /// 本类不注册任何回调、不写文件、不封闭窗口，也不改变任何判定。
    /// </summary>
    internal static class DebugRunCompare
    {
        private const int MaxFieldBytes = 128;

        // ------------------------------------------------------------------
        // 计划与校验
        // ------------------------------------------------------------------

        /// <summary>一次比较的规范化输入；校验通过后各字段已是可用取值。</summary>
        internal sealed class Plan
        {
            internal string LeftRunId = string.Empty;
            internal string RightRunId = string.Empty;
            internal string Source = string.Empty;
            internal string Position = string.Empty;
            internal string Kind = string.Empty;
            internal string ValuePath = string.Empty;
            internal int ValuePathSegments;
            internal string Clock = string.Empty;
            internal string AlignmentKind = string.Empty;
            internal string AlignmentKeyPath = string.Empty;
            internal int AlignmentKeyPathSegments;
            internal int Context;
        }

        /// <summary>校验比较输入；任何缺失或非法取值都在读取任何窗口之前拒绝。</summary>
        internal static Plan Validate(DebugRunCompareRequest request)
        {
            if (request == null) throw SchemaInvalid("request", "a debug.run.compare body", "missing");
            var plan = new Plan
            {
                LeftRunId = RequireField(request.LeftRunId, "leftRunId"),
                RightRunId = RequireField(request.RightRunId, "rightRunId"),
                Position = RequireField(request.Position, "position"),
                Kind = request.Kind ?? string.Empty
            };
            if (!UnityCliDebugRunCheckSources.IsDefined(request.Source))
                throw SchemaInvalid("source", "one of " + string.Join(", ", UnityCliDebugRunCheckSources.All()), request.Source);
            plan.Source = request.Source;
            if (plan.Kind.Length > MaxFieldBytes)
                throw SchemaInvalid("kind", MaxFieldBytes.ToString(CultureInfo.InvariantCulture) + " characters or fewer", plan.Kind);
            if (!UnityCliDebugClocks.IsDefined(request.Clock))
                throw SchemaInvalid("clock", "one of " + string.Join(", ", UnityCliDebugClocks.All()), request.Clock);
            plan.Clock = request.Clock;
            plan.ValuePath = RequirePath(request.ValuePath, "valuePath", out plan.ValuePathSegments);

            var alignment = request.Alignment;
            if (alignment == null)
                throw SchemaInvalid("alignment", "an explicit alignment object, because this Tool never assumes a default alignment", "missing");
            if (!UnityCliDebugRunCompareAlignments.IsDefined(alignment.Kind))
                throw SchemaInvalid(
                    "alignment.kind",
                    "one of " + string.Join(", ", UnityCliDebugRunCompareAlignments.All()),
                    alignment.Kind);
            plan.AlignmentKind = alignment.Kind;
            if (string.Equals(plan.AlignmentKind, UnityCliDebugRunCompareAlignments.Key, StringComparison.Ordinal))
            {
                plan.AlignmentKeyPath = RequirePath(alignment.KeyPath, "alignment.keyPath", out plan.AlignmentKeyPathSegments);
            }
            else if (!string.IsNullOrEmpty(alignment.KeyPath))
            {
                throw SchemaInvalid("alignment.keyPath", "no key path for alignment kind " + plan.AlignmentKind, alignment.KeyPath);
            }

            var context = request.Context ?? DebugLimits.RunCompareDefaultContext;
            if (context < 0 || context > DebugLimits.MaxRunCompareContext)
            {
                throw SchemaInvalid(
                    "context",
                    "0.." + DebugLimits.MaxRunCompareContext.ToString(CultureInfo.InvariantCulture),
                    context.ToString(CultureInfo.InvariantCulture));
            }
            plan.Context = context;
            return plan;
        }

        // ------------------------------------------------------------------
        // 单侧选择
        // ------------------------------------------------------------------

        /// <summary>一侧窗口在该来源与时钟上的可比较事实；complete 为 false 时不得支撑 equal 或凭缺席得出的差异。</summary>
        internal sealed class Side
        {
            internal DebugRunWindow Window;
            internal string RunId = string.Empty;
            internal string State = string.Empty;
            internal bool Terminal;
            internal string Verdict = string.Empty;
            internal string TerminationReason = string.Empty;
            internal long StartSequence;
            internal long EndSequence;
            internal string EvidenceState = UnityCliDebugRunEvidenceStates.Live;
            internal bool DetailAvailable;

            /// <summary>窗口区间内该来源的事件总数（未经位置、种类与时钟筛选）。</summary>
            internal long SourceMatchCount;

            /// <summary>通过位置、种类与时钟筛选后参与比较的事件，按该侧自己的顺序排列。</summary>
            internal readonly List<UnityCliDebugEvent> Selected = new List<UnityCliDebugEvent>();

            /// <summary>与 Selected 一一对应的公开事件投影；值路径与对齐键都在它上面解析。</summary>
            internal readonly List<Dictionary<string, object>> Members = new List<Dictionary<string, object>>();

            /// <summary>key 对齐时每个选中事件的规范化键文本；缺失或不可规范化为 null。</summary>
            internal readonly List<string> Keys = new List<string>();

            internal readonly List<DebugRunCoverageGapView> Gaps = new List<DebugRunCoverageGapView>();
            internal readonly List<string> Reasons = new List<string>();
            internal long ExcludedByClockCount;
            internal long AlignmentKeyMissingCount;
            internal bool AlignmentAmbiguous;
            internal bool IntervalReadable;
            internal bool Sampled;
            internal bool Truncated;
            internal bool HasClockRange;
            internal double ClockFrom;
            internal double ClockTo;
            internal bool Complete;
            internal string Coverage = string.Empty;
            internal string Note = string.Empty;

            /// <summary>key 对齐的配对前提：没有缺失键值、也没有重复键。</summary>
            internal bool AlignmentSound
            {
                get { return !AlignmentAmbiguous && AlignmentKeyMissingCount == 0L; }
            }
        }

        /// <summary>选择一侧窗口中真正参与比较的事件，并如实记录缺口、采样、截断与时钟排除事实。</summary>
        private static Side Select(
            Plan plan,
            DebugRunWindow window,
            DebugRunReportView report,
            IList<UnityCliDebugEvent> events,
            IReadOnlyList<UnityCliDebugGap> gaps,
            long oldestAvailableSequence,
            long appendFailuresAtBoundary,
            long readBoundarySequence)
        {
            var side = new Side
            {
                Window = window,
                RunId = window.RunId,
                State = window.State,
                Terminal = report != null && report.Terminal,
                Verdict = report == null ? UnityCliDebugRunVerdicts.Indeterminate : report.Verdict,
                TerminationReason = window.TerminationReason ?? string.Empty,
                StartSequence = window.StartSequence,
                // 开窗窗口读到哪里就报到哪里：报告里的边界是它自己那一次捕获，不是本次读取的上界。
                EndSequence = window.IsTerminal ? window.EndSequence : readBoundarySequence,
                DetailAvailable = window.DetailAvailable,
                EvidenceState = !window.IsTerminal
                    ? UnityCliDebugRunEvidenceStates.Live
                    : (window.DetailAvailable ? UnityCliDebugRunEvidenceStates.Frozen : UnityCliDebugRunEvidenceStates.Evicted)
            };
            if (!side.Terminal) DebugRunEvidence.AddReason(side.Reasons, UnityCliDebugRunCompareReasons.WindowOpen);

            if (events != null)
            {
                foreach (var entry in events)
                {
                    if (entry == null) continue;
                    // 只有落在本窗口自己的区间内的事件参与比较：区间边界以窗口记录为准，不依赖调用方传入的范围。
                    if (entry.Sequence <= side.StartSequence || entry.Sequence > side.EndSequence) continue;
                    if (!string.Equals(entry.SourceId, plan.Source, StringComparison.Ordinal)) continue;
                    side.SourceMatchCount++;
                    if (!string.Equals(plan.Position, "*", StringComparison.Ordinal)
                        && !string.Equals(entry.ExecutionPoint ?? string.Empty, plan.Position, StringComparison.Ordinal)) continue;
                    if (plan.Kind.Length != 0 && !string.Equals(entry.Kind ?? string.Empty, plan.Kind, StringComparison.Ordinal)) continue;
                    if (!string.Equals(entry.ClockKind ?? string.Empty, plan.Clock, StringComparison.Ordinal))
                    {
                        side.ExcludedByClockCount++;
                        continue;
                    }
                    side.Selected.Add(entry);
                    side.Members.Add(UnityCliDebugEventBuffer.ToData(entry));
                    if (!side.HasClockRange)
                    {
                        side.HasClockRange = true;
                        side.ClockFrom = entry.ClockValue;
                        side.ClockTo = entry.ClockValue;
                    }
                    else
                    {
                        if (entry.ClockValue < side.ClockFrom) side.ClockFrom = entry.ClockValue;
                        if (entry.ClockValue > side.ClockTo) side.ClockTo = entry.ClockValue;
                    }
                    if (entry.Sampled || entry.SampleEvery > 1) side.Sampled = true;
                    if (entry.Truncated || entry.ProjectionTruncated) side.Truncated = true;
                    if (string.Equals(plan.AlignmentKind, UnityCliDebugRunCompareAlignments.Key, StringComparison.Ordinal))
                    {
                        var key = ResolvePath(side.Members[side.Members.Count - 1], plan.AlignmentKeyPath);
                        string text;
                        if (!key.Present || !TryCanonical(key.Value, out text))
                        {
                            side.Keys.Add(null);
                            side.AlignmentKeyMissingCount++;
                        }
                        else
                        {
                            side.Keys.Add(text);
                        }
                    }
                }
            }

            // 区间可读性：本窗口自己的区间内不得有不可读缺口、未入库事件或已被环淘汰的 sequence。
            var intervalGaps = CollectIntervalGaps(window, side.StartSequence, side.EndSequence, gaps, oldestAvailableSequence, appendFailuresAtBoundary - window.AppendFailuresAtStart);
            side.Gaps.AddRange(intervalGaps);
            side.IntervalReadable = intervalGaps.Count == 0;
            foreach (var gap in intervalGaps)
            {
                DebugRunEvidence.AddReason(side.Reasons, string.Equals(gap.Reason, "event-not-recorded", StringComparison.Ordinal)
                    ? UnityCliDebugRunCompareReasons.EventNotRecorded
                    : string.Equals(gap.Reason, "event-ring-capacity", StringComparison.Ordinal)
                        ? UnityCliDebugRunCompareReasons.EvidenceEvicted
                        : UnityCliDebugRunCompareReasons.CoverageGap);
            }
            // 重复键在同一侧就使配对不再唯一：必须在计算本侧完整性之前定性，否则该侧会被误判为完整。
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in side.Keys)
            {
                if (key == null) continue;
                if (!seenKeys.Add(key)) side.AlignmentAmbiguous = true;
            }
            if (side.Sampled) DebugRunEvidence.AddReason(side.Reasons, UnityCliDebugRunCompareReasons.SampledOnly);
            if (side.Truncated) DebugRunEvidence.AddReason(side.Reasons, UnityCliDebugRunCompareReasons.EvidenceTruncated);
            if (side.ExcludedByClockCount > 0L) DebugRunEvidence.AddReason(side.Reasons, UnityCliDebugRunCompareReasons.ClockExcluded);
            if (side.Selected.Count == 0 && side.SourceMatchCount > 0L)
                DebugRunEvidence.AddReason(side.Reasons, UnityCliDebugRunCompareReasons.ClockUnavailable);
            if (side.AlignmentKeyMissingCount > 0L)
                DebugRunEvidence.AddReason(side.Reasons, UnityCliDebugRunCompareReasons.AlignmentKeyMissing);
            if (side.AlignmentAmbiguous)
                DebugRunEvidence.AddReason(side.Reasons, UnityCliDebugRunCompareReasons.AlignmentAmbiguous);

            side.Complete = side.Terminal
                && side.IntervalReadable
                && !side.Sampled
                && !side.Truncated
                && side.ExcludedByClockCount == 0L
                && side.AlignmentSound;
            side.Coverage = CoverageText(side, plan);
            side.Note = SideNote(side);
            return side;
        }

        /// <summary>复制判定内核的区间缺口规则：缺口、未入库计数与环淘汰都必须落在本窗口自己的区间里。</summary>
        private static List<DebugRunCoverageGapView> CollectIntervalGaps(
            DebugRunWindow window,
            long fromSequence,
            long toSequence,
            IReadOnlyList<UnityCliDebugGap> gaps,
            long oldestAvailableSequence,
            long appendFailureDelta)
        {
            var result = new List<DebugRunCoverageGapView>();
            if (toSequence < fromSequence) toSequence = fromSequence;
            if (appendFailureDelta > 0L)
            {
                // 事件根本没有进入存储，具体区间无法定位，只能在整段区间上如实记录不可读。
                result.Add(new DebugRunCoverageGapView { FromSequence = fromSequence, ToSequence = toSequence, Reason = "event-not-recorded" });
            }
            var lostFrom = window.StartSequence + 1L;
            var lostTo = oldestAvailableSequence - 1L;
            if (oldestAvailableSequence > lostFrom && lostFrom <= toSequence && lostTo >= fromSequence)
            {
                result.Add(new DebugRunCoverageGapView
                {
                    FromSequence = Math.Max(lostFrom, fromSequence),
                    ToSequence = Math.Min(lostTo, toSequence),
                    Reason = "event-ring-capacity"
                });
            }
            if (gaps != null)
            {
                foreach (var gap in gaps)
                {
                    if (gap == null) continue;
                    if (gap.ToSequence <= fromSequence || gap.FromSequence > toSequence) continue;
                    result.Add(new DebugRunCoverageGapView
                    {
                        FromSequence = Math.Max(gap.FromSequence, fromSequence),
                        ToSequence = Math.Min(gap.ToSequence, toSequence),
                        Reason = gap.Reason ?? string.Empty
                    });
                }
            }
            return result;
        }

        // ------------------------------------------------------------------
        // 对齐
        // ------------------------------------------------------------------

        /// <summary>一个已建立的对齐位置；两侧下标都指向各自的选择集合。</summary>
        private sealed class Pair
        {
            internal int LeftIndex = -1;
            internal int RightIndex = -1;
            internal string KeyText = string.Empty;
        }

        /// <summary>全部对齐结果：配对与单侧多出的事件。</summary>
        private sealed class Alignment
        {
            internal readonly List<Pair> Pairs = new List<Pair>();
            internal readonly List<int> LeftOnly = new List<int>();
            internal readonly List<int> RightOnly = new List<int>();
        }

        /// <summary>按声明种类建立对齐：单来源调用序号按位置配对，业务键按规范化键文本配对。</summary>
        private static Alignment Align(Plan plan, Side left, Side right)
        {
            var alignment = new Alignment();
            if (!string.Equals(plan.AlignmentKind, UnityCliDebugRunCompareAlignments.Key, StringComparison.Ordinal))
            {
                var count = Math.Min(left.Selected.Count, right.Selected.Count);
                for (var index = 0; index < count; index++)
                {
                    alignment.Pairs.Add(new Pair { LeftIndex = index, RightIndex = index });
                }
                for (var index = count; index < left.Selected.Count; index++) alignment.LeftOnly.Add(index);
                for (var index = count; index < right.Selected.Count; index++) alignment.RightOnly.Add(index);
                return alignment;
            }

            var rightByKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var index = 0; index < right.Keys.Count; index++)
            {
                var key = right.Keys[index];
                if (key == null) continue;
                List<int> indexes;
                if (!rightByKey.TryGetValue(key, out indexes))
                {
                    indexes = new List<int>();
                    rightByKey[key] = indexes;
                }
                indexes.Add(index);
            }
            var consumed = new HashSet<int>();
            for (var leftIndex = 0; leftIndex < left.Keys.Count; leftIndex++)
            {
                var key = left.Keys[leftIndex];
                List<int> candidates;
                if (key == null || !rightByKey.TryGetValue(key, out candidates))
                {
                    alignment.LeftOnly.Add(leftIndex);
                    continue;
                }
                var paired = -1;
                foreach (var candidate in candidates)
                {
                    if (consumed.Contains(candidate)) continue;
                    paired = candidate;
                    break;
                }
                if (paired < 0)
                {
                    alignment.LeftOnly.Add(leftIndex);
                    continue;
                }
                consumed.Add(paired);
                alignment.Pairs.Add(new Pair { LeftIndex = leftIndex, RightIndex = paired, KeyText = key });
            }
            for (var rightIndex = 0; rightIndex < right.Keys.Count; rightIndex++)
            {
                if (!consumed.Contains(rightIndex)) alignment.RightOnly.Add(rightIndex);
            }
            return alignment;
        }

        // ------------------------------------------------------------------
        // 比较
        // ------------------------------------------------------------------

        internal static DebugRunCompareResult Run(
            Plan plan,
            DebugRunWindow leftWindow,
            DebugRunReportView leftReport,
            IList<UnityCliDebugEvent> leftEvents,
            DebugRunWindow rightWindow,
            DebugRunReportView rightReport,
            IList<UnityCliDebugEvent> rightEvents,
            IReadOnlyList<UnityCliDebugGap> gaps,
            long oldestAvailableSequence,
            long appendFailuresAtBoundary,
            long readBoundarySequence)
        {
            var left = Select(plan, leftWindow, leftReport, leftEvents, gaps, oldestAvailableSequence, appendFailuresAtBoundary, readBoundarySequence);
            var right = Select(plan, rightWindow, rightReport, rightEvents, gaps, oldestAvailableSequence, appendFailuresAtBoundary, readBoundarySequence);
            var alignment = Align(plan, left, right);
            var budget = new ViewBudget();
            var reasons = new List<string>();

            var first = (Candidate)null;
            var unproven = (Candidate)null;
            foreach (var pair in alignment.Pairs)
            {
                var leaf = CompareAtPath(plan, left, pair.LeftIndex, right, pair.RightIndex);
                if (leaf == null) continue;
                var candidate = new Candidate
                {
                    LeftIndex = pair.LeftIndex,
                    RightIndex = pair.RightIndex,
                    LeftOwns = true,
                    RightOwns = true,
                    KeyText = pair.KeyText,
                    // 同一对齐位置上的值差异由两侧真实事件直接证明：截断事件不提供可信比较值，
                    // 序号对齐还要求两侧区间可证明是完整调用序号。
                    Provable = leaf.Trustworthy && ProvablePair(plan, left, right, left.Selected[pair.LeftIndex], right.Selected[pair.RightIndex])
                };
                candidate.View = DifferenceView(candidate, pair.LeftIndex + 1, leaf);
                if (candidate.Provable)
                {
                    first = candidate;
                    break;
                }
                if (unproven == null)
                {
                    unproven = candidate;
                    DebugRunEvidence.AddReason(reasons, UnityCliDebugRunCompareReasons.ValueDifferenceUnproven);
                }
            }

            if (first == null && (alignment.LeftOnly.Count > 0 || alignment.RightOnly.Count > 0))
            {
                var leftHasExtra = alignment.LeftOnly.Count > 0;
                var rightHasExtra = alignment.RightOnly.Count > 0;
                var candidate = new Candidate
                {
                    // 单侧多出的事件：多出的一侧以首个多出的事件为锚点，
                    // 另一侧以配对区之后的插入点为锚点，绝不借另一侧的事件充当本侧证据。
                    LeftIndex = leftHasExtra ? alignment.LeftOnly[0] : alignment.Pairs.Count,
                    RightIndex = rightHasExtra ? alignment.RightOnly[0] : alignment.Pairs.Count,
                    LeftOwns = leftHasExtra,
                    RightOwns = rightHasExtra,
                    KeyText = ExtraKeyText(left, right, leftHasExtra, rightHasExtra, alignment),
                    // 事件数量差异只能靠一侧“没有这个事件”成立：两侧区间都完整时才算证明。
                    Provable = left.Complete && right.Complete
                };
                var kind = leftHasExtra && rightHasExtra
                    ? UnityCliDebugRunCompareDifferenceKinds.Count
                    : leftHasExtra
                        ? UnityCliDebugRunCompareDifferenceKinds.EventMissingRight
                        : UnityCliDebugRunCompareDifferenceKinds.EventMissingLeft;
                candidate.View = CountingDifferenceView(plan, candidate, alignment.Pairs.Count + 1, kind);
                if (candidate.Provable) first = candidate;
                else if (unproven == null) unproven = candidate;
            }

            var result = new DebugRunCompareResult
            {
                LeftRunId = plan.LeftRunId,
                RightRunId = plan.RightRunId,
                Source = plan.Source,
                Position = plan.Position,
                Kind = plan.Kind,
                ValuePath = plan.ValuePath,
                Clock = plan.Clock,
                AlignmentKind = plan.AlignmentKind,
                AlignmentKeyPath = plan.AlignmentKeyPath,
                Context = plan.Context,
                AlignedPairCount = alignment.Pairs.Count,
                ComparedValueCount = alignment.Pairs.Count,
                LeftOnlyCount = alignment.LeftOnly.Count,
                RightOnlyCount = alignment.RightOnly.Count,
                Reasons = new List<string>(),
                Contexts = new List<DebugRunCompareContextView>(),
                ConditionDifferences = new List<DebugRunCompareConditionView>(),
                Left = SideView(plan, left),
                Right = SideView(plan, right)
            };
            Merge(result.Reasons, left.Reasons);
            Merge(result.Reasons, right.Reasons);
            Merge(result.Reasons, reasons);

            var bothTerminal = left.Terminal && right.Terminal;
            if (!bothTerminal)
            {
                // 开窗期间证据集仍在增长：即使已经看到差异也不宣布结论，只如实报告未证明差异。
                result.Result = UnityCliDebugRunCompareResults.Indeterminate;
                DebugRunEvidence.AddReason(result.Reasons, UnityCliDebugRunCompareReasons.WindowOpen);
                if (unproven == null) unproven = first;
            }
            else if (first != null)
            {
                result.Result = UnityCliDebugRunCompareResults.Diverged;
                result.FirstDifference = first.View;
            }
            else if (left.Complete && right.Complete && unproven == null)
            {
                result.Result = UnityCliDebugRunCompareResults.Equal;
            }
            else
            {
                result.Result = UnityCliDebugRunCompareResults.Indeterminate;
                if (unproven != null)
                {
                    DebugRunEvidence.AddReason(
                        result.Reasons,
                        string.Equals(unproven.View.Kind, UnityCliDebugRunCompareDifferenceKinds.Value, StringComparison.Ordinal)
                            || string.Equals(unproven.View.Kind, UnityCliDebugRunCompareDifferenceKinds.ValueMissing, StringComparison.Ordinal)
                            ? UnityCliDebugRunCompareReasons.ValueDifferenceUnproven
                            : UnityCliDebugRunCompareReasons.CountMismatchUnproven);
                }
            }
            if (unproven != null) result.UnprovenDifference = unproven.View;

            var anchor = first ?? unproven;
            if (anchor != null)
            {
                result.Contexts.Add(ContextOf(plan, left, "left", anchor.LeftIndex, anchor.LeftOwns, budget));
                result.Contexts.Add(ContextOf(plan, right, "right", anchor.RightIndex, anchor.RightOwns, budget));
            }
            var conditionsTruncated = false;
            result.ConditionDifferences = Conditions(plan, left, right, out conditionsTruncated);
            if (conditionsTruncated || budget.Truncated)
                DebugRunEvidence.AddReason(result.Reasons, UnityCliDebugRunCompareReasons.ResultTruncated);

            result.Note = ResultNote(result, left, right);
            return result;
        }

        /// <summary>同一对齐位置上的一对差异候选；Provable 决定它能否支撑结论。</summary>
        private sealed class Candidate
        {
            internal DebugRunCompareDifferenceView View;
            internal int LeftIndex = -1;
            internal int RightIndex = -1;
            internal bool LeftOwns;
            internal bool RightOwns;
            internal string KeyText = string.Empty;
            internal bool Provable;
        }

        /// <summary>单侧多出事件的键文本：优先用真正多出那一侧的键，两侧都多出时用左侧的键。</summary>
        private static string ExtraKeyText(Side left, Side right, bool leftHasExtra, bool rightHasExtra, Alignment alignment)
        {
            if (leftHasExtra && alignment.LeftOnly[0] < left.Keys.Count) return left.Keys[alignment.LeftOnly[0]] ?? string.Empty;
            if (rightHasExtra && alignment.RightOnly[0] < right.Keys.Count) return right.Keys[alignment.RightOnly[0]] ?? string.Empty;
            return string.Empty;
        }

        /// <summary>序号对齐要求两侧区间都是可证明完整的调用序号；key 对齐只要求配对本身可信。</summary>
        private static bool ProvablePair(Plan plan, Side left, Side right, UnityCliDebugEvent leftEntry, UnityCliDebugEvent rightEntry)
        {
            if (string.Equals(plan.AlignmentKind, UnityCliDebugRunCompareAlignments.Ordinal, StringComparison.Ordinal))
                return left.Complete && right.Complete;
            if (!left.AlignmentSound || !right.AlignmentSound) return false;
            return !Truncated(leftEntry) && !Truncated(rightEntry);
        }

        private static bool Truncated(UnityCliDebugEvent entry)
        {
            return entry != null && (entry.Truncated || entry.ProjectionTruncated);
        }

        // ------------------------------------------------------------------
        // 值路径解析与差异定位
        // ------------------------------------------------------------------

        /// <summary>一次路径解析结果；Present 为 false 表示该路径在这个值上不存在。</summary>
        private struct Resolved
        {
            internal bool Present;
            internal object Value;
        }

        /// <summary>嵌套比较的结果叶子：给出实际出现差异的路径与两侧取值。</summary>
        private sealed class Leaf
        {
            internal string Path = string.Empty;
            internal bool LeftPresent;
            internal bool RightPresent;
            internal object LeftValue;
            internal object RightValue;
            internal bool Trustworthy = true;
        }

        /// <summary>按点分路径解析事件字段；数字段在序列上表示下标，在字典上仍表示键。</summary>
        private static Resolved ResolvePath(Dictionary<string, object> members, string path)
        {
            object current = members;
            var present = true;
            foreach (var segment in Split(path))
            {
                if (!Step(ref current, segment))
                {
                    present = false;
                    break;
                }
            }
            return new Resolved { Present = present, Value = present ? current : null };
        }

        private static bool Step(ref object current, string segment)
        {
            var map = current as IDictionary;
            if (map != null)
            {
                if (!map.Contains(segment)) return false;
                current = map[segment];
                return true;
            }
            var list = current as IList;
            if (list != null)
            {
                int index;
                if (!int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)) return false;
                if (index < 0 || index >= list.Count) return false;
                current = list[index];
                return true;
            }
            return false;
        }

        /// <summary>读取一个对齐位置上的比较值，并把差异下降到具体叶子路径；两侧相同返回 null。</summary>
        private static Leaf CompareAtPath(Plan plan, Side left, int leftIndex, Side right, int rightIndex)
        {
            var leftResolved = ResolvePath(left.Members[leftIndex], plan.ValuePath);
            var rightResolved = ResolvePath(right.Members[rightIndex], plan.ValuePath);
            return CompareValues(leftResolved, rightResolved, plan.ValuePath, plan.ValuePathSegments);
        }

        /// <summary>
        /// 比较两个已解析的值：容器按排序键或下标逐个下降，定位到第一个真正不同的叶子；
        /// 两侧都不存在该值时不构成差异。
        /// </summary>
        private static Leaf CompareValues(Resolved left, Resolved right, string path, int depth)
        {
            if (!left.Present && !right.Present) return null;
            if (left.Present != right.Present)
            {
                return new Leaf
                {
                    Path = path,
                    LeftPresent = left.Present,
                    RightPresent = right.Present,
                    LeftValue = left.Value,
                    RightValue = right.Value
                };
            }
            var leftMap = left.Value as IDictionary;
            var rightMap = right.Value as IDictionary;
            if (leftMap != null && rightMap != null && depth < DebugLimits.MaxRunComparePathSegments)
            {
                var keys = new SortedSet<string>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in leftMap) keys.Add(Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                foreach (DictionaryEntry entry in rightMap) keys.Add(Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                foreach (var key in keys)
                {
                    var child = CompareValues(
                        new Resolved { Present = leftMap.Contains(key), Value = leftMap.Contains(key) ? leftMap[key] : null },
                        new Resolved { Present = rightMap.Contains(key), Value = rightMap.Contains(key) ? rightMap[key] : null },
                        path + "." + key,
                        depth + 1);
                    if (child != null) return child;
                }
                return null;
            }
            var leftList = left.Value as IList;
            var rightList = right.Value as IList;
            if (leftList != null && rightList != null && depth < DebugLimits.MaxRunComparePathSegments)
            {
                var count = Math.Max(leftList.Count, rightList.Count);
                for (var index = 0; index < count; index++)
                {
                    var child = CompareValues(
                        new Resolved { Present = index < leftList.Count, Value = index < leftList.Count ? leftList[index] : null },
                        new Resolved { Present = index < rightList.Count, Value = index < rightList.Count ? rightList[index] : null },
                        path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]",
                        depth + 1);
                    if (child != null) return child;
                }
                return null;
            }
            string leftText;
            string rightText;
            var leftOk = TryCanonical(left.Value, out leftText);
            var rightOk = TryCanonical(right.Value, out rightText);
            if (!leftOk || !rightOk)
            {
                // 无法规范化的值不提供可比证据：如实报告而不用它判差异。
                return new Leaf
                {
                    Path = path,
                    LeftPresent = true,
                    RightPresent = true,
                    LeftValue = left.Value,
                    RightValue = right.Value,
                    Trustworthy = false
                };
            }
            if (string.Equals(leftText, rightText, StringComparison.Ordinal)) return null;
            return new Leaf
            {
                Path = path,
                LeftPresent = true,
                RightPresent = true,
                LeftValue = left.Value,
                RightValue = right.Value
            };
        }

        /// <summary>把值规范化为稳定文本；非有限数字等不可规范化的值返回 false，绝不抛错。</summary>
        private static bool TryCanonical(object value, out string text)
        {
            try
            {
                text = UnityCliSerializer.Canonicalize(value);
                return true;
            }
            catch (Exception)
            {
                text = string.Empty;
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 结果装配
        // ------------------------------------------------------------------

        private sealed class ViewBudget
        {
            internal int Used;
            internal bool Truncated;

            /// <summary>按声明上限分配一个事件视图位置；超出后不再增加视图。</summary>
            internal bool TryTake()
            {
                if (Used >= DebugLimits.MaxRunCompareEvents)
                {
                    Truncated = true;
                    return false;
                }
                Used++;
                return true;
            }
        }

        private static DebugRunCompareDifferenceView DifferenceView(Candidate candidate, long alignmentIndex, Leaf leaf)
        {
            var kind = leaf.LeftPresent && leaf.RightPresent
                ? UnityCliDebugRunCompareDifferenceKinds.Value
                : UnityCliDebugRunCompareDifferenceKinds.ValueMissing;
            var view = new DebugRunCompareDifferenceView
            {
                Kind = kind,
                AlignmentIndex = alignmentIndex,
                KeyText = candidate.KeyText ?? string.Empty,
                Path = leaf.Path,
                LeftValue = ValueView(new Resolved { Present = leaf.LeftPresent, Value = leaf.LeftValue }),
                RightValue = ValueView(new Resolved { Present = leaf.RightPresent, Value = leaf.RightValue }),
                Detail = candidate.Provable
                    ? "Both sides retained an event at this aligned position, so the difference is proven by evidence on each side."
                    : "A difference exists at this aligned position, but the retained evidence cannot prove it: the value projection is not comparable, or the alignment is not sound on both sides."
            };
            return view;
        }

        private static DebugRunCompareDifferenceView CountingDifferenceView(Plan plan, Candidate candidate, long alignmentIndex, string kind)
        {
            return new DebugRunCompareDifferenceView
            {
                Kind = kind,
                AlignmentIndex = alignmentIndex,
                KeyText = candidate.KeyText ?? string.Empty,
                Path = plan.ValuePath,
                LeftValue = ValueView(new Resolved { Present = false, Value = null }),
                RightValue = ValueView(new Resolved { Present = false, Value = null }),
                Detail = candidate.Provable
                    ? "Both windows are complete for the declared selection, so an event present on one side and absent on the other is a proven difference in the aligned sets."
                    : "One side has fewer aligned events, but a window interval is not complete for the declared selection, so absence is not used as evidence of divergence."
            };
        }

        private static DebugRunCompareValueView ValueView(Resolved resolved)
        {
            if (!resolved.Present)
            {
                return new DebugRunCompareValueView { Present = false, Value = null, Text = string.Empty };
            }
            var bounded = UnityCliDebugValueBounds.Bound(
                resolved.Value,
                DebugLimits.MaxStoredProjectionDepth,
                DebugLimits.MaxStoredProjectionItemsPerLevel,
                DebugLimits.MaxRunCompareValueBytes,
                out _);
            string text;
            TryCanonical(bounded, out text);
            return new DebugRunCompareValueView
            {
                Present = true,
                Value = bounded,
                Text = UnityCliDebugValueBounds.BoundText(text ?? string.Empty, DebugLimits.MaxRunCompareValueBytes)
            };
        }

        private static DebugRunCompareEventView EventView(Plan plan, Side side, int index)
        {
            var entry = side.Selected[index];
            var view = new DebugRunCompareEventView
            {
                Sequence = entry.Sequence,
                SourceId = entry.SourceId ?? string.Empty,
                Kind = entry.Kind ?? string.Empty,
                ExecutionPoint = entry.ExecutionPoint ?? string.Empty,
                Name = entry.Name ?? string.Empty,
                Revision = entry.Revision,
                InvocationId = entry.InvocationId,
                ThreadId = entry.ThreadId,
                MainThread = entry.MainThread,
                ClockKind = entry.ClockKind ?? string.Empty,
                ClockValue = entry.ClockValue,
                Frame = entry.Frame,
                HookId = entry.HookId ?? string.Empty,
                MethodIdentity = entry.MethodIdentity ?? string.Empty,
                Sampled = entry.Sampled,
                Truncated = Truncated(entry),
                EvidenceRef = "sequence:" + entry.Sequence.ToString(CultureInfo.InvariantCulture),
                Compared = ValueView(ResolvePath(side.Members[index], plan.ValuePath))
            };
            if (string.Equals(plan.AlignmentKind, UnityCliDebugRunCompareAlignments.Key, StringComparison.Ordinal))
            {
                var key = side.Keys.Count > index ? side.Keys[index] : null;
                view.Key = key == null
                    ? new DebugRunCompareValueView { Present = false, Value = null, Text = string.Empty }
                    : new DebugRunCompareValueView { Present = true, Value = key, Text = key };
            }
            else
            {
                view.Key = new DebugRunCompareValueView { Present = false, Value = null, Text = string.Empty };
            }
            return view;
        }

        /// <summary>
        /// 差异位置的一侧有限上下文：该侧自己的事件顺序上取前后各 context 条。
        /// 该侧没有差异事件时锚点按同一对齐序号作为插入点，不借用另一侧的事件充当本侧证据。
        /// </summary>
        private static DebugRunCompareContextView ContextOf(Plan plan, Side side, string sideName, int anchorIndex, bool ownsDifference, ViewBudget budget)
        {
            var view = new DebugRunCompareContextView
            {
                Side = sideName,
                AnchoredIndex = anchorIndex + 1,
                DifferencePresent = ownsDifference,
                Difference = null,
                Before = new List<DebugRunCompareEventView>(),
                After = new List<DebugRunCompareEventView>()
            };
            if (side.Selected.Count == 0) return view;
            var position = anchorIndex < 0 ? 0 : Math.Min(anchorIndex, side.Selected.Count);
            var start = Math.Max(0, position - plan.Context);
            for (var index = start; index < position; index++) AddContext(view.Before, plan, side, index, budget);
            if (ownsDifference && position < side.Selected.Count)
            {
                view.Difference = budget.TryTake() ? EventView(plan, side, position) : null;
            }
            var afterStart = ownsDifference ? position + 1 : position;
            for (var index = afterStart; index < Math.Min(side.Selected.Count, afterStart + plan.Context); index++) AddContext(view.After, plan, side, index, budget);
            return view;
        }

        private static void AddContext(List<DebugRunCompareEventView> target, Plan plan, Side side, int index, ViewBudget budget)
        {
            if (!budget.TryTake()) return;
            target.Add(EventView(plan, side, index));
        }

        private static DebugRunCompareSideView SideView(Plan plan, Side side)
        {
            return new DebugRunCompareSideView
            {
                RunId = side.RunId,
                State = side.State,
                Terminal = side.Terminal,
                Verdict = side.Verdict,
                TerminationReason = side.TerminationReason,
                StartSequence = side.StartSequence,
                EndSequence = side.EndSequence,
                EvidenceState = side.EvidenceState,
                DetailAvailable = side.DetailAvailable,
                SourceMatchCount = side.SourceMatchCount,
                SelectedCount = side.Selected.Count,
                ExcludedByClockCount = side.ExcludedByClockCount,
                IntervalReadable = side.IntervalReadable,
                Sampled = side.Sampled,
                Truncated = side.Truncated,
                AlignmentKeyMissingCount = side.AlignmentKeyMissingCount,
                AlignmentAmbiguous = side.AlignmentAmbiguous,
                HasClockRange = side.HasClockRange,
                ClockFrom = side.ClockFrom,
                ClockTo = side.ClockTo,
                Complete = side.Complete,
                CoverageGaps = new List<DebugRunCoverageGapView>(side.Gaps),
                Reasons = new List<string>(side.Reasons),
                Coverage = side.Coverage,
                Note = side.Note
            };
        }

        /// <summary>两侧运行条件差异：只列真正不同的条件事实，不描述业务结论。</summary>
        private static List<DebugRunCompareConditionView> Conditions(Plan plan, Side left, Side right, out bool truncated)
        {
            var views = new List<DebugRunCompareConditionView>();
            truncated = false;
            AddCondition(views, UnityCliDebugRunCompareConditionKinds.Definition,
                left.Window.DefinitionHash ?? string.Empty,
                right.Window.DefinitionHash ?? string.Empty,
                "The declared preparation, inputs and seed control hash the same only when both runs were driven by the same declared definition.",
                ref truncated);
            AddCondition(views, UnityCliDebugRunCompareConditionKinds.Configuration,
                left.Window.ConfigurationFingerprint ?? string.Empty,
                right.Window.ConfigurationFingerprint ?? string.Empty,
                "The program and hook configuration at the window start must match for a replay comparison.",
                ref truncated);
            foreach (var field in new[] { "sceneName", "scenePath", "isPlaying", "seedControl" })
            {
                AddCondition(views, UnityCliDebugRunCompareConditionKinds.Environment,
                    EnvironmentField(left.Window, field),
                    EnvironmentField(right.Window, field),
                    "Environment field " + field + " at the window start.",
                    ref truncated);
            }
            AddCondition(views, UnityCliDebugRunCompareConditionKinds.Clock,
                "clock=" + plan.Clock + ", excluded=" + left.ExcludedByClockCount.ToString(CultureInfo.InvariantCulture),
                "clock=" + plan.Clock + ", excluded=" + right.ExcludedByClockCount.ToString(CultureInfo.InvariantCulture),
                "Only events carrying the declared clock are compared, so the number of events excluded by that clock must be read as a coverage fact per side.",
                ref truncated);
            AddCondition(views, UnityCliDebugRunCompareConditionKinds.Coverage,
                CoverageFact(left),
                CoverageFact(right),
                "Interval readability, evidence state and coverage gaps describe how much of each window is really comparable.",
                ref truncated);
            AddCondition(views, UnityCliDebugRunCompareConditionKinds.WindowState,
                (left.State ?? string.Empty) + "/" + (left.TerminationReason ?? string.Empty),
                (right.State ?? string.Empty) + "/" + (right.TerminationReason ?? string.Empty),
                "A sealed window is compared with its frozen report; an interrupted window is a different termination fact.",
                ref truncated);
            return views;
        }

        private static void AddCondition(List<DebugRunCompareConditionView> views, string kind, string left, string right, string detail, ref bool truncated)
        {
            if (string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal)) return;
            if (views.Count >= DebugLimits.MaxRunCompareConditionDifferences)
            {
                truncated = true;
                return;
            }
            views.Add(new DebugRunCompareConditionView
            {
                Kind = kind,
                Left = UnityCliDebugValueBounds.BoundText(left ?? string.Empty, DebugLimits.MaxDiagnosticTextBytes),
                Right = UnityCliDebugValueBounds.BoundText(right ?? string.Empty, DebugLimits.MaxDiagnosticTextBytes),
                Detail = detail
            });
        }

        private static string EnvironmentField(DebugRunWindow window, string field)
        {
            if (window == null || window.Environment == null) return string.Empty;
            object value;
            if (!window.Environment.TryGetValue(field, out value) || value == null) return string.Empty;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string CoverageFact(Side side)
        {
            return side.EvidenceState + ", intervalReadable=" + (side.IntervalReadable ? "true" : "false")
                + ", gaps=" + side.Gaps.Count.ToString(CultureInfo.InvariantCulture)
                + ", selected=" + side.Selected.Count.ToString(CultureInfo.InvariantCulture)
                + ", clockExcluded=" + side.ExcludedByClockCount.ToString(CultureInfo.InvariantCulture);
        }

        private static string CoverageText(Side side, Plan plan)
        {
            var builder = new StringBuilder();
            builder.Append(side.RunId).Append(" window [").Append(side.StartSequence.ToString(CultureInfo.InvariantCulture))
                .Append("..").Append(side.EndSequence.ToString(CultureInfo.InvariantCulture)).Append("] source ").Append(plan.Source)
                .Append(" at ").Append(plan.Position);
            if (plan.Kind.Length != 0) builder.Append(" kind ").Append(plan.Kind);
            builder.Append(": ").Append(side.SourceMatchCount.ToString(CultureInfo.InvariantCulture)).Append(" retained, ")
                .Append(side.Selected.Count.ToString(CultureInfo.InvariantCulture)).Append(" selected on clock ").Append(plan.Clock);
            if (side.ExcludedByClockCount > 0L)
                builder.Append(", ").Append(side.ExcludedByClockCount.ToString(CultureInfo.InvariantCulture)).Append(" on another clock");
            if (side.Gaps.Count > 0) builder.Append(", ").Append(side.Gaps.Count.ToString(CultureInfo.InvariantCulture)).Append(" unreadable range(s)");
            builder.Append(side.IntervalReadable ? ", interval readable" : ", interval not fully readable");
            return builder.ToString();
        }

        private static string SideNote(Side side)
        {
            if (!side.Terminal) return "This window is still open, so its evidence set is not final and cannot support a conclusion.";
            if (string.Equals(side.EvidenceState, UnityCliDebugRunEvidenceStates.Evicted, StringComparison.Ordinal))
                return "This window's detailed evidence payload was released by the declared budget; the frozen verdict and reasons remain, and the retained event range is still read for comparison.";
            if (side.Complete) return "This window is terminal and its interval, clock and alignment facts are complete for the declared selection.";
            return "This window is terminal but not complete for the declared selection: see reasons and coverage gaps before treating any absence as evidence.";
        }

        private static string ResultNote(DebugRunCompareResult result, Side left, Side right)
        {
            if (string.Equals(result.Result, UnityCliDebugRunCompareResults.Equal, StringComparison.Ordinal))
                return "Both windows are terminal and complete for the declared source, position, clock and alignment, and every aligned compared value is identical. Equal means the retained traces agree, not that the business outcome was correct.";
            if (string.Equals(result.Result, UnityCliDebugRunCompareResults.Diverged, StringComparison.Ordinal))
                return "The first proven difference is reported with the value path where it appeared and a bounded context of " + result.Context.ToString(CultureInfo.InvariantCulture) + " event(s) per side. Diverged means the retained traces differ, not that either run was wrong.";
            return "The comparison is indeterminate: the retained evidence cannot prove equality or a difference for the declared selection. Absence of an event on one side is never used as evidence of divergence. Read left/right reasons, coverage gaps, unprovenDifference and conditionDifferences to see exactly what is missing.";
        }

        // ------------------------------------------------------------------
        // 字段与路径
        // ------------------------------------------------------------------

        private static string[] Split(string path)
        {
            return (path ?? string.Empty).Split(new[] { '.' }, StringSplitOptions.None);
        }

        private static string RequireField(string value, string field)
        {
            var text = value == null ? string.Empty : value.Trim();
            if (text.Length == 0) throw SchemaInvalid(field, "a non-empty string", "empty");
            if (Encoding.UTF8.GetByteCount(text) > MaxFieldBytes)
                throw SchemaInvalid(field, MaxFieldBytes.ToString(CultureInfo.InvariantCulture) + " UTF-8 bytes or fewer", text);
            return text;
        }

        /// <summary>校验点分路径：段数、空段与字符集都在读取任何窗口之前判定。</summary>
        private static string RequirePath(string value, string field, out int segments)
        {
            var text = RequireField(value, field);
            var parts = Split(text);
            segments = parts.Length;
            if (segments > DebugLimits.MaxRunComparePathSegments)
            {
                throw SchemaInvalid(
                    field,
                    DebugLimits.MaxRunComparePathSegments.ToString(CultureInfo.InvariantCulture) + " segments or fewer",
                    segments.ToString(CultureInfo.InvariantCulture));
            }
            foreach (var part in parts)
            {
                if (part.Length == 0)
                    throw SchemaInvalid(field, "path segments separated by single dots, with no empty segment", text);
                foreach (var ch in part)
                {
                    var allowed = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
                        || ch == '_' || ch == '-';
                    if (!allowed)
                        throw SchemaInvalid(field, "path segments of letters, digits, underscore or hyphen separated by dots", text);
                }
            }
            return text;
        }

        private static void Merge(List<string> target, List<string> source)
        {
            if (source == null) return;
            foreach (var reason in source) DebugRunEvidence.AddReason(target, reason);
        }

        private static BridgeCommandException SchemaInvalid(string field, string expected, string actual)
        {
            return new BridgeCommandException(
                "E_SCHEMA_INVALID",
                "Field " + field + " is invalid: expected " + expected + ".",
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "path", "$." + field },
                    { "expected", expected },
                    { "actual", actual ?? string.Empty }
                },
                new[] { "Run tools describe debug.run.compare to read the field contract, then submit the corrected input." });
        }
    }
}
#endif
