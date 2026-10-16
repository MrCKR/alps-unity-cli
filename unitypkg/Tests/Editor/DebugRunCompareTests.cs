using System;
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// 证据窗口比较（debug.run.compare）与封闭证据保留的定向逻辑测试。
    ///
    /// 覆盖：调用方必须声明的对齐键、值路径与时钟；equal/diverged/indeterminate 三态；
    /// 首个可证明差异与两侧有限上下文；按单来源调用序号与业务键对齐；
    /// 缺一侧完整事件时只报 indeterminate 而不凭缺席判差异；条件差异与覆盖范围；
    /// 以及封闭窗口的有界证据保留与预算淘汰后只释放载荷。
    /// </summary>
    public sealed class DebugRunCompareTests
    {
        private readonly List<string> sessions = new List<string>();

        [SetUp]
        public void SetUp()
        {
            DebugScheduler.Reset();
            DebugTriggerBudget.Reset();
            DebugRunHost.Reset();
            DebugEventStore.Reset();
            sessions.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var sessionId in sessions) ReleaseSession(sessionId);
            DebugRunHost.Reset();
            DebugEventStore.Reset();
            DebugScheduler.Reset();
            DebugTriggerBudget.Reset();
            sessions.Clear();
        }

        /// <summary>
        /// 释放用例建立的 session 定义：先 stop 关闭未来派发，再 remove 归还定义预算。
        /// 定义预算只在 remove 后归还（stop 只改状态），因此只 stop 会让后续用例撞上定义上限。
        /// 清理失败不掩盖用例断言。
        /// </summary>
        private static void ReleaseSession(string sessionId)
        {
            try
            {
                DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
            }
            catch
            {
                // 已停止或已删除都无需再停止。
            }
            try
            {
                DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = sessionId }));
            }
            catch
            {
                // 定义已被删除，或仍有未完成清理时保持现状。
            }
        }

        /// <summary>建立一个 open session，作为窗口声明与证据的 owner。</summary>
        private string CreateSession()
        {
            var sessionId = DebugSessionHost.ApplyCreate(DebugSessionHost.PrepareCreate(new DebugSessionCreateRequest
            {
                Label = "run-compare-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                ActivationScope = UnityCliDebugActivationScopes.EditorAndPlay,
                ClockKind = UnityCliDebugClocks.Monotonic
            })).SessionId;
            sessions.Add(sessionId);
            return sessionId;
        }

        /// <summary>一个只要求一次命中的窗口声明，够用来建立证据窗口。</summary>
        private static DebugRunBeginRequest BeginRequest(string sessionId, string checkName, string seedControl = null)
        {
            return new DebugRunBeginRequest
            {
                SessionId = sessionId,
                SeedControl = seedControl,
                RequiredChecks = new List<DebugRunCheckSpec>
                {
                    new DebugRunCheckSpec { Name = checkName, Source = "program", Position = "*", Quantifier = "once" }
                }
            };
        }

        /// <summary>追加一条真实进入统一事件存储的程序检查事件。</summary>
        private static long AppendCheck(string name, object actual, string executionPoint = "editor-tick", bool truncated = false, bool passed = true)
        {
            return DebugEventStore.Append(new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugProgramAbi.EventSource,
                Kind = "check",
                ExecutionPoint = executionPoint,
                Name = name ?? string.Empty,
                Value = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "actual", actual },
                    { "passed", passed }
                },
                ClockKind = UnityCliDebugClocks.Monotonic,
                Truncated = truncated
            });
        }

        /// <summary>追加一条带可选业务键的程序事件；keyId 为 null 时该事件没有这个键字段。</summary>
        private static long AppendKeyed(string keyId, object actual)
        {
            var value = new Dictionary<string, object>(StringComparer.Ordinal) { { "actual", actual }, { "passed", true } };
            if (keyId != null) value["keyId"] = keyId;
            return DebugEventStore.Append(new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugProgramAbi.EventSource,
                Kind = "check",
                ExecutionPoint = "editor-tick",
                Name = "keyed",
                Value = value,
                ClockKind = UnityCliDebugClocks.Monotonic
            });
        }

        /// <summary>开窗、追加事件并封闭，返回该窗口的 runId。</summary>
        private string SealWindow(string sessionId, string checkName, Action append, string seedControl = null)
        {
            var begun = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, checkName, seedControl)));
            if (append != null) append();
            DebugRunHost.End(new DebugRunEndRequest { RunId = begun.RunId });
            return begun.RunId;
        }

        private static DebugRunCompareRequest CompareRequest(
            string leftRunId,
            string rightRunId,
            string alignmentKind = "ordinal",
            string keyPath = null,
            string valuePath = "value.actual",
            string clock = "monotonic",
            string position = "*",
            int? context = null)
        {
            return new DebugRunCompareRequest
            {
                LeftRunId = leftRunId,
                RightRunId = rightRunId,
                Source = "program",
                Position = position,
                Alignment = new DebugRunCompareAlignmentSpec { Kind = alignmentKind, KeyPath = keyPath },
                ValuePath = valuePath,
                Clock = clock,
                Context = context
            };
        }

        [Test]
        public void CompareWithoutADifferenceIsEqualAndEchoesTheDeclaredDimensions()
        {
            var sessionId = CreateSession();
            var left = SealWindow(sessionId, "value-ok", () => AppendCheck("value-ok", 3));
            var right = SealWindow(sessionId, "value-ok", () => AppendCheck("value-ok", 3));

            var result = DebugRunHost.Compare(CompareRequest(left, right, "ordinal", null, "value.actual", "monotonic", "*", 1));

            Assert.AreEqual(UnityCliDebugRunCompareResults.Equal, result.Result, "two complete windows with identical aligned values are equal");
            Assert.AreEqual(0, result.Reasons.Count, "a complete equality needs no reason code");
            Assert.AreEqual(1, result.AlignedPairCount);
            Assert.IsNull(result.FirstDifference, "equality publishes no difference");
            Assert.IsNull(result.UnprovenDifference);
            Assert.AreEqual(0, result.Contexts.Count, "context is reported for a difference, not for equality");
            Assert.AreEqual(0, result.ConditionDifferences.Count, "identical runs must not invent a condition difference");
            Assert.AreEqual("ordinal", result.AlignmentKind);
            Assert.AreEqual("value.actual", result.ValuePath);
            Assert.AreEqual("monotonic", result.Clock);
            Assert.AreEqual(1, result.Left.SelectedCount);
            Assert.AreEqual(1, result.Right.SelectedCount);
            Assert.IsTrue(result.Left.Complete, "a sealed window with a readable interval and full clock coverage is complete");
            Assert.AreEqual(UnityCliDebugRunEvidenceStates.Frozen, result.Left.EvidenceState, "a sealed window reports its frozen retained evidence");
            Assert.IsTrue(result.Left.IntervalReadable);
            Assert.IsTrue(result.Left.HasClockRange);
            Assert.IsNotNull(result.Left.Coverage);
            Assert.IsTrue(result.Left.Coverage.Contains("interval readable", StringComparison.Ordinal));
        }

        [Test]
        public void CompareReportsTheFirstProvableDifferenceWithBoundedContext()
        {
            var sessionId = CreateSession();
            var left = SealWindow(sessionId, "step", () =>
            {
                AppendCheck("step-1", 1);
                AppendCheck("step-2", 2);
                AppendCheck("step-3", 3);
            });
            var secondSequence = 0L;
            var right = SealWindow(sessionId, "step", () =>
            {
                AppendCheck("step-1", 1);
                secondSequence = AppendCheck("step-2", 99);
                AppendCheck("step-3", 3);
            });

            var result = DebugRunHost.Compare(CompareRequest(left, right, "ordinal", null, "value.actual", "monotonic", "*", 1));

            Assert.AreEqual(UnityCliDebugRunCompareResults.Diverged, result.Result);
            Assert.IsNotNull(result.FirstDifference, "the first provable difference is published");
            Assert.AreEqual(UnityCliDebugRunCompareDifferenceKinds.Value, result.FirstDifference.Kind);
            Assert.AreEqual(2, result.FirstDifference.AlignmentIndex, "the second aligned position is the first difference");
            Assert.AreEqual("value.actual", result.FirstDifference.Path);
            Assert.IsTrue(result.FirstDifference.LeftValue.Present);
            Assert.IsTrue(result.FirstDifference.RightValue.Present);
            Assert.AreEqual(2L, Convert.ToInt64(result.FirstDifference.LeftValue.Value, CultureInfo.InvariantCulture));
            Assert.AreEqual(99L, Convert.ToInt64(result.FirstDifference.RightValue.Value, CultureInfo.InvariantCulture));
            Assert.AreEqual(3, result.Left.SelectedCount);
            Assert.AreEqual(3, result.Right.SelectedCount);
            Assert.IsNull(result.UnprovenDifference, "a proven difference is not also reported as unproven");

            Assert.AreEqual(2, result.Contexts.Count, "the first difference carries one context per side");
            foreach (var context in result.Contexts)
            {
                Assert.IsTrue(context.DifferencePresent);
                Assert.IsNotNull(context.Difference);
                Assert.AreEqual(1, context.Before.Count, "one declared context event before the difference");
                Assert.AreEqual(1, context.After.Count, "one declared context event after the difference");
                Assert.IsTrue(context.Difference.EvidenceRef.StartsWith("sequence:", StringComparison.Ordinal));
            }
            var rightContext = result.Contexts[1];
            Assert.AreEqual("right", rightContext.Side);
            Assert.AreEqual(secondSequence, rightContext.Difference.Sequence, "the context names the retained event of that side");
        }

        [Test]
        public void CompareRejectsAnUnspecifiedAlignmentAndEveryMalformedDimension()
        {
            var valid = CompareRequest("run-404", "run-405");
            valid.Alignment = null;
            var missingAlignment = Assert.Throws<UnityCli.UnityCliBridge.BridgeCommandException>(() => DebugRunHost.Compare(valid));
            Assert.AreEqual("E_SCHEMA_INVALID", missingAlignment.Code, "the alignment is never defaulted");
            Assert.AreEqual("$.alignment", Path(missingAlignment));

            ExpectSchemaInvalid(CompareRequest("run-404", "run-405", "sequence"), "$.alignment.kind");
            ExpectSchemaInvalid(CompareRequest("run-404", "run-405", "key", null), "$.alignment.keyPath");
            ExpectSchemaInvalid(CompareRequest("run-404", "run-405", "ordinal", "name"), "$.alignment.keyPath");
            ExpectSchemaInvalid(CompareRequest("run-404", "run-405", "ordinal", null, "value.actual", "wall-time"), "$.clock");
            ExpectSchemaInvalid(CompareRequest("run-404", "run-405", "ordinal", null, "value..actual"), "$.valuePath");
            ExpectSchemaInvalid(CompareRequest("run-404", "run-405", "ordinal", null, "value.actual", "monotonic", "*", 5), "$.context");
            ExpectSchemaInvalid(CompareRequest("run-404", "run-405", "ordinal", null, "value.a.b.c.d.e.f.g.h.i"), "$.valuePath");
            var badSource = CompareRequest("run-404", "run-405");
            badSource.Source = "console";
            ExpectSchemaInvalid(badSource, "$.source");
            var badPosition = CompareRequest("run-404", "run-405");
            badPosition.Position = string.Empty;
            ExpectSchemaInvalid(badPosition, "$.position");
        }

        [Test]
        public void CompareIsIndeterminateWhileAWindowIsOpen()
        {
            var sessionId = CreateSession();
            var left = SealWindow(sessionId, "value-ok", () => AppendCheck("value-ok", 1));
            var begun = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, "value-ok")));
            AppendCheck("value-ok", 2);

            var result = DebugRunHost.Compare(CompareRequest(left, begun.RunId, "ordinal", null, "value.actual"));

            Assert.AreEqual(UnityCliDebugRunCompareResults.Indeterminate, result.Result, "a growing evidence set cannot support a conclusion");
            Assert.Contains(UnityCliDebugRunCompareReasons.WindowOpen, result.Reasons);
            Assert.IsNull(result.FirstDifference, "an open window never yields a published difference");
            Assert.IsNotNull(result.UnprovenDifference, "the difference seen so far is still reported as unproven");
            Assert.IsTrue(result.Left.Terminal);
            Assert.IsFalse(result.Right.Terminal);
            Assert.AreEqual(UnityCliDebugRunEvidenceStates.Live, result.Right.EvidenceState, "an open window is read live");
            Assert.IsFalse(result.Right.Complete);
        }

        [Test]
        public void CompareIsIndeterminateWhenTheDeclaredClockCarriesNoEvidence()
        {
            var sessionId = CreateSession();
            var left = SealWindow(sessionId, "value-ok", () => AppendCheck("value-ok", 1));
            var right = SealWindow(sessionId, "value-ok", () => AppendCheck("value-ok", 1));

            var result = DebugRunHost.Compare(CompareRequest(left, right, "ordinal", null, "value.actual", "unity-frame"));

            Assert.AreEqual(UnityCliDebugRunCompareResults.Indeterminate, result.Result);
            Assert.Contains(UnityCliDebugRunCompareReasons.ClockExcluded, result.Reasons);
            Assert.Contains(UnityCliDebugRunCompareReasons.ClockUnavailable, result.Reasons);
            Assert.AreEqual(1, result.Left.SourceMatchCount, "the source really produced an event in this window");
            Assert.AreEqual(0, result.Left.SelectedCount, "no event carries the declared clock");
            Assert.AreEqual(1, result.Left.ExcludedByClockCount);
            Assert.IsFalse(result.Left.HasClockRange);
            Assert.IsFalse(result.Left.Complete);
        }

        [Test]
        public void CompareIsIndeterminateWhenOneSideIntervalWasLost()
        {
            var sessionId = CreateSession();
            var leftStart = 0L;
            var leftEnd = 0L;
            var left = SealWindow(sessionId, "step", () =>
            {
                AppendCheck("step-1", 1);
                leftEnd = AppendCheck("step-2", 2);
            });
            leftStart = DebugRunHost.Info(new DebugRunInfoRequest { RunId = left }).Report.StartSequence;
            var right = SealWindow(sessionId, "step", () =>
            {
                AppendCheck("step-1", 1);
                AppendCheck("step-2", 2);
                AppendCheck("step-3", 3);
            });
            // 左侧区间在这里变成不可读：右侧多出的事件只能靠“左侧没有它”成立。
            DebugEventStore.RecordGap(leftStart + 1L, leftEnd, "unreadable-range");

            var result = DebugRunHost.Compare(CompareRequest(left, right, "ordinal", null, "value.actual"));

            Assert.AreEqual(UnityCliDebugRunCompareResults.Indeterminate, result.Result, "absence is never used as evidence of divergence");
            Assert.IsNull(result.FirstDifference);
            Assert.IsNotNull(result.UnprovenDifference, "the count difference is reported as unproven");
            Assert.Contains(UnityCliDebugRunCompareReasons.CountMismatchUnproven, result.Reasons);
            Assert.Contains(UnityCliDebugRunCompareReasons.CoverageGap, result.Reasons);
            Assert.IsFalse(result.Left.IntervalReadable);
            Assert.IsFalse(result.Left.Complete);
            Assert.IsTrue(result.Right.Complete);
            Assert.AreEqual(1, result.RightOnlyCount);
            Assert.AreEqual(0, result.LeftOnlyCount);
        }

        [Test]
        public void CompareByBusinessKeyAlignsAcrossExtraEventsAndReportsOneSidedEvents()
        {
            var sessionId = CreateSession();
            var left = SealWindow(sessionId, "keyed", () =>
            {
                AppendCheck("alpha", 1);
                AppendCheck("beta", 2);
                AppendCheck("gamma", 3);
            });
            var right = SealWindow(sessionId, "keyed", () =>
            {
                AppendCheck("alpha", 1);
                AppendCheck("beta", 2);
            });

            var oneSided = DebugRunHost.Compare(CompareRequest(left, right, "key", "name", "value.actual"));

            Assert.AreEqual(UnityCliDebugRunCompareResults.Diverged, oneSided.Result);
            Assert.AreEqual(2, oneSided.AlignedPairCount, "only the shared business keys are paired");
            Assert.AreEqual(1, oneSided.LeftOnlyCount);
            Assert.AreEqual(0, oneSided.RightOnlyCount);
            Assert.IsNotNull(oneSided.FirstDifference);
            Assert.AreEqual(UnityCliDebugRunCompareDifferenceKinds.EventMissingRight, oneSided.FirstDifference.Kind);
            Assert.AreEqual(3, oneSided.FirstDifference.AlignmentIndex);
            Assert.AreEqual("\"gamma\"", oneSided.FirstDifference.KeyText, "the difference names the business key as its canonical JSON text");
            Assert.IsFalse(oneSided.FirstDifference.RightValue.Present);
            Assert.AreEqual(2, oneSided.Contexts.Count);
            Assert.IsFalse(oneSided.Contexts[1].DifferencePresent, "the side without that event reports an explicit absence, not a borrowed event");

            var changedRight = SealWindow(sessionId, "keyed", () =>
            {
                AppendCheck("alpha", 9);
                AppendCheck("beta", 2);
            });
            var valueDifference = DebugRunHost.Compare(CompareRequest(left, changedRight, "key", "name", "value.actual"));
            Assert.AreEqual(UnityCliDebugRunCompareResults.Diverged, valueDifference.Result);
            Assert.AreEqual(UnityCliDebugRunCompareDifferenceKinds.Value, valueDifference.FirstDifference.Kind);
            Assert.AreEqual(1, valueDifference.FirstDifference.AlignmentIndex);
            Assert.AreEqual("\"alpha\"", valueDifference.FirstDifference.KeyText);
        }

        [Test]
        public void CompareRequiresAKeyOnEverySelectedEventAndRejectsAmbiguousKeys()
        {
            var sessionId = CreateSession();
            var left = SealWindow(sessionId, "keyed", () =>
            {
                AppendKeyed("alpha", 1);
                AppendKeyed(null, 2);
            });
            var right = SealWindow(sessionId, "keyed", () =>
            {
                AppendKeyed("alpha", 1);
                AppendKeyed("beta", 2);
            });

            var missing = DebugRunHost.Compare(CompareRequest(left, right, "key", "value.keyId", "value.actual"));

            Assert.AreEqual(UnityCliDebugRunCompareResults.Indeterminate, missing.Result, "an event without a key cannot be paired");
            Assert.Contains(UnityCliDebugRunCompareReasons.AlignmentKeyMissing, missing.Reasons);
            Assert.AreEqual(1, missing.Left.AlignmentKeyMissingCount);
            Assert.IsFalse(missing.Left.Complete, "a missing key makes the whole alignment unsound on that side");
            Assert.IsNull(missing.FirstDifference, "an unsound alignment proves nothing");

            var ambiguousRight = SealWindow(sessionId, "keyed", () =>
            {
                AppendKeyed("alpha", 1);
                AppendKeyed("alpha", 5);
            });
            var ambiguous = DebugRunHost.Compare(CompareRequest(left, ambiguousRight, "key", "value.keyId", "value.actual"));
            Assert.AreEqual(UnityCliDebugRunCompareResults.Indeterminate, ambiguous.Result);
            Assert.Contains(UnityCliDebugRunCompareReasons.AlignmentAmbiguous, ambiguous.Reasons);
            Assert.IsTrue(ambiguous.Right.AlignmentAmbiguous, "a repeated business key makes the pairing ambiguous");
        }

        [Test]
        public void CompareReportsConditionDifferencesAndCoverageRange()
        {
            var sessionId = CreateSession();
            var left = SealWindow(sessionId, "value-ok", () => AppendCheck("value-ok", 1), "seed-a");
            var right = SealWindow(sessionId, "value-ok", () => AppendCheck("value-ok", 1), "seed-b");

            var result = DebugRunHost.Compare(CompareRequest(left, right, "ordinal", null, "value.actual", "monotonic", "*", 0));

            Assert.AreEqual(UnityCliDebugRunCompareResults.Equal, result.Result, "the same trace under a different declared seed is still equal");
            Assert.IsTrue(result.ConditionDifferences.Count >= 1, "a different declared definition must be reported as a condition difference");
            var definition = result.ConditionDifferences.Find((entry) => string.Equals(entry.Kind, UnityCliDebugRunCompareConditionKinds.Definition, StringComparison.Ordinal));
            Assert.IsNotNull(definition, "the declared preparation, inputs and seed are one compared condition");
            Assert.IsTrue(definition.Left != definition.Right, "the two definition hashes really differ");
            Assert.AreEqual(0, result.Context, "context 0 is a legal declaration");
            Assert.IsTrue(result.Left.Coverage.Contains("selected on clock monotonic", StringComparison.Ordinal));
            Assert.IsTrue(result.Right.Coverage.Contains("selected on clock monotonic", StringComparison.Ordinal));
            Assert.AreEqual(1, result.Left.EndSequence - result.Left.StartSequence, "the coverage text describes the real retained range");
        }

        [Test]
        public void CompareRejectsAnUnknownRunIdWithTheRunOwnerError()
        {
            var sessionId = CreateSession();
            var left = SealWindow(sessionId, "value-ok", () => AppendCheck("value-ok", 1));

            var missingLeft = Assert.Throws<UnityCli.UnityCliBridge.BridgeCommandException>(
                () => DebugRunHost.Compare(CompareRequest("run-404", left)));
            Assert.AreEqual("E_DEBUG_RUN_NOT_FOUND", missingLeft.Code);
            Assert.IsTrue(missingLeft.Details is Dictionary<string, object>);
            Assert.IsTrue(((Dictionary<string, object>)missingLeft.Details).ContainsKey("domainGeneration"));
            Assert.IsTrue(missingLeft.NextActions.Count > 0);

            var missingRight = Assert.Throws<UnityCli.UnityCliBridge.BridgeCommandException>(
                () => DebugRunHost.Compare(CompareRequest(left, "run-405")));
            Assert.AreEqual("E_DEBUG_RUN_NOT_FOUND", missingRight.Code);
        }

        [Test]
        public void CompareKeepsATruncatedPairAsAnUnprovenDifference()
        {
            var sessionId = CreateSession();
            var left = SealWindow(sessionId, "value-ok", () => AppendCheck("value-ok", 1, "editor-tick", true));
            var right = SealWindow(sessionId, "value-ok", () => AppendCheck("value-ok", 2, "editor-tick", true));

            var result = DebugRunHost.Compare(CompareRequest(left, right, "ordinal", null, "value.actual"));

            Assert.AreEqual(UnityCliDebugRunCompareResults.Indeterminate, result.Result, "a truncated projection cannot prove a difference");
            Assert.IsNull(result.FirstDifference);
            Assert.IsNotNull(result.UnprovenDifference);
            Assert.AreEqual(UnityCliDebugRunCompareDifferenceKinds.Value, result.UnprovenDifference.Kind);
            Assert.Contains(UnityCliDebugRunCompareReasons.ValueDifferenceUnproven, result.Reasons);
            Assert.Contains(UnityCliDebugRunCompareReasons.EvidenceTruncated, result.Reasons);
            Assert.IsTrue(result.Left.Truncated);
            Assert.IsFalse(result.Left.Complete);
        }

        [Test]
        public void SealedWindowKeepsItsEvidenceAfterItsRangeBecomesUnreadable()
        {
            var sessionId = CreateSession();
            var counterexample = 0L;
            var failed = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, "value-ok")));
            counterexample = AppendCheck("value-ok", 7, "editor-tick", false, false);
            var sealedRun = DebugRunHost.End(new DebugRunEndRequest { RunId = failed.RunId }).Report;
            Assert.AreEqual(UnityCliDebugRunVerdicts.Failed, sealedRun.Verdict);
            var start = sealedRun.StartSequence;
            var end = sealedRun.EndSequence;
            Assert.IsTrue(sealedRun.DetailAvailable);
            Assert.IsTrue(sealedRun.Checks[0].Evidence.Count > 0, "the sealed window retains its bounded evidence payload");

            // 事件区间在封闭之后变为不可读：封闭窗口的判定与证据必须仍然按 runId 读回同一份事实。
            DebugEventStore.RecordGap(start + 1L, end, "unreadable-range");
            var again = DebugRunHost.Info(new DebugRunInfoRequest { RunId = failed.RunId }).Report;

            Assert.AreEqual(UnityCliDebugRunVerdicts.Failed, again.Verdict, "the frozen verdict does not change with the event ring");
            Assert.IsNotNull(again.FirstCounterexample, "the retained counterexample stays readable by runId");
            Assert.AreEqual(counterexample, again.FirstCounterexample.Sequence);
            Assert.IsTrue(again.DetailAvailable);
            Assert.IsTrue(again.Checks[0].Evidence.Count > 0);
        }

        [Test]
        public void EvictedDetailReleasesTheFrozenPayloadAndKeepsTheVerdict()
        {
            var sessionId = CreateSession();
            var first = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, "value-ok")));
            AppendCheck("value-ok", 7, "editor-tick", false, false);
            DebugRunHost.End(new DebugRunEndRequest { RunId = first.RunId });
            for (var index = 0; index < DebugLimits.MaxDetailedRunWindowsPerSession; index++)
            {
                var extra = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, "value-ok")));
                AppendCheck("value-ok", 1);
                DebugRunHost.End(new DebugRunEndRequest { RunId = extra.RunId });
            }

            var report = DebugRunHost.Info(new DebugRunInfoRequest { RunId = first.RunId }).Report;

            Assert.IsFalse(report.DetailAvailable, "the declared budget keeps detailed evidence for the newest windows only");
            Assert.Contains(UnityCliDebugRunVerdictReasons.DetailEvicted, report.VerdictReasons);
            Assert.AreEqual(UnityCliDebugRunVerdicts.Failed, report.Verdict, "releasing the payload never rewrites the frozen verdict");
            Assert.AreEqual(DebugRunEvidence.StatusCounterexample, report.Checks[0].Status, "the frozen check conclusion stays readable");
            Assert.IsNull(report.FirstCounterexample, "the released payload no longer carries the counterexample values");
            Assert.AreEqual(0, report.Checks[0].Evidence.Count, "the released payload no longer carries the evidence rows");
            Assert.AreEqual(UnityCliDebugRunEvidenceStates.Evicted, DebugRunHost.Compare(CompareRequest(first.RunId, first.RunId)).Left.EvidenceState,
                "a window whose payload was released reports the evicted evidence state");
        }

        private static void ExpectSchemaInvalid(DebugRunCompareRequest request, string expectedPath)
        {
            var failure = Assert.Throws<UnityCli.UnityCliBridge.BridgeCommandException>(() => DebugRunHost.Compare(request));
            Assert.AreEqual("E_SCHEMA_INVALID", failure.Code);
            Assert.AreEqual(expectedPath, Path(failure));
        }

        private static string Path(UnityCli.UnityCliBridge.BridgeCommandException failure)
        {
            var details = failure.Details as Dictionary<string, object>;
            Assert.IsNotNull(details, "a schema failure carries machine-readable details");
            return Convert.ToString(details["path"], CultureInfo.InvariantCulture);
        }
    }
}
