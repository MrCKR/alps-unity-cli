using System;
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// 证据窗口（debug.run-begin/end/wait/info/list）的定向逻辑测试。
    ///
    /// 覆盖：声明回读与 capture 边界、开放窗口不声称完成、未知 runId 的硬失败、
    /// 三态 verdict（不把断言失败当错误码）、首个反例、负向与全窗口检查的覆盖证明要求、
    /// 覆盖缺口与采样降级、配置变化与逐段报告、interrupted 窗口、run 预算（16 摘要 / 4 详细窗口）与 list 分页。
    /// 断言失败绝不以错误码表达（E_DEBUG_ASSERTION_FAILED 不存在）由 tests/debug-run-source.test.ts 静态核对。
    /// </summary>
    public sealed class DebugRunTests
    {
        private readonly List<string> sessions = new List<string>();

        [SetUp]
        public void SetUp()
        {
            DebugScheduler.Reset();
            DebugTriggerBudget.Reset();
            DebugRunHost.Reset();
            // 事件窗口是跨用例的共享状态：先清空，用例的窗口区间与缺口才不受前一个用例影响。
            DebugEventStore.Reset();
            sessions.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var sessionId in sessions) ReleaseSession(sessionId);
            DebugRunHost.Reset();
            // 摘要/详细窗口账本与事件窗口都必须归还，否则下一个用例读到的不是自己的状态。
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
                Label = "run-window-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                ActivationScope = UnityCliDebugActivationScopes.EditorAndPlay,
                ClockKind = UnityCliDebugClocks.Monotonic
            })).SessionId;
            sessions.Add(sessionId);
            return sessionId;
        }

        /// <summary>追加一条真实进入统一事件存储的程序检查事件；hookId 非空时同时满足检查声明的 hook 筛选。</summary>
        private static long AppendProgramCheck(string name, bool passed, object expected, object actual, string executionPoint, string hookId = null)
        {
            return DebugEventStore.Append(new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugProgramAbi.EventSource,
                Kind = "check",
                ExecutionPoint = executionPoint,
                HookId = hookId ?? string.Empty,
                Name = name,
                Value = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "expected", expected },
                    { "actual", actual },
                    { "passed", passed },
                    { "coverage", "declared by the program itself" }
                },
                ClockKind = UnityCliDebugClocks.Monotonic
            });
        }

        private static DebugRunBeginRequest BeginRequest(string sessionId, string name, string quantifier, string source = "program", string position = "*")
        {
            return new DebugRunBeginRequest
            {
                SessionId = sessionId,
                RequiredChecks = new List<DebugRunCheckSpec>
                {
                    new DebugRunCheckSpec { Name = name, Source = source, Position = position, Quantifier = quantifier }
                }
            };
        }

        [Test]
        public void BeginDeclaresChecksConditionsAndCaptureBoundary()
        {
            var sessionId = CreateSession();
            var plan = DebugRunHost.PrepareBegin(new DebugRunBeginRequest
            {
                SessionId = sessionId,
                RequiredChecks = new List<DebugRunCheckSpec>
                {
                    new DebugRunCheckSpec { Name = "damage-applied", Source = "program", Position = "editor-tick", Quantifier = "once" }
                },
                EndConditions = new List<DebugRunEndConditionSpec>
                {
                    new DebugRunEndConditionSpec { Kind = "signal", Name = "attack-finished" }
                },
                Preparation = new Dictionary<string, object>(StringComparer.Ordinal) { { "step", "reset" } },
                Inputs = new Dictionary<string, object>(StringComparer.Ordinal) { { "key", "space" } },
                SeedControl = "fixed-seed-1234"
            });
            var result = DebugRunHost.ApplyBegin(plan);

            Assert.AreEqual("run-1", result.RunId, "the first window of a session is run-1");
            Assert.AreEqual(UnityCliDebugRunStates.Open, result.State);
            Assert.IsFalse(result.Terminal, "begin never claims the window is terminal");
            Assert.AreEqual(result.StartSequence, result.Capture.BoundarySequence, "the start sequence is the captured boundary");
            Assert.IsTrue(result.Capture.QueueDrained, "no driver or delivery exists in this unit test");
            Assert.AreEqual(1, result.RequiredChecks.Count);
            Assert.AreEqual("editor-tick", result.RequiredChecks[0].Position);
            Assert.IsTrue(result.RequiredChecks[0].Verified, "a consistent check declaration is verified at begin");
            Assert.AreEqual(1, result.EndConditions.Count);
            Assert.AreEqual("signal", result.EndConditions[0].Kind);
            Assert.IsTrue(result.EndConditions[0].DeclaredOnly, "a future signal delivery cannot be proven at begin");
            Assert.IsFalse(result.EndConditions[0].Observed, "begin has not observed any end condition");
            Assert.AreEqual("fixed-seed-1234", Convert.ToString(result.Environment["seedControl"], CultureInfo.InvariantCulture));
            Assert.IsFalse(string.IsNullOrEmpty(result.DefinitionHash), "the declared preparation and inputs are hashed");
            Assert.IsTrue(result.Budget.DetailedWindowsUsed >= 1L, "the new window holds one detailed evidence share");
            Assert.AreEqual((long)DebugLimits.MaxRunSummariesPerSession, result.Budget.MaxSummariesPerSession);
        }

        [Test]
        public void OpenWindowInfoReportsIndeterminateWithoutClaimingCompletion()
        {
            var sessionId = CreateSession();
            var begun = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, "never-observed", "once")));

            var report = DebugRunHost.Info(new DebugRunInfoRequest { RunId = begun.RunId }).Report;

            Assert.AreEqual(UnityCliDebugRunStates.Open, report.State);
            Assert.IsFalse(report.Terminal, "an open window is not terminal");
            Assert.AreEqual(UnityCliDebugRunVerdicts.Indeterminate, report.Verdict);
            Assert.Contains(UnityCliDebugRunVerdictReasons.WindowNotComplete, report.VerdictReasons);
            Assert.Contains(UnityCliDebugRunVerdictReasons.NoEvidence, report.VerdictReasons);
            Assert.AreEqual(DebugRunEvidence.StatusInsufficient, report.Checks[0].Status);
            Assert.IsNull(report.FirstCounterexample, "no counterexample exists yet");
        }

        [Test]
        public void UnknownRunIdFailsWithRunNotFound()
        {
            var failure = Assert.Throws<UnityCli.UnityCliBridge.BridgeCommandException>(
                () => DebugRunHost.Info(new DebugRunInfoRequest { RunId = "run-404" }));
            Assert.AreEqual("E_DEBUG_RUN_NOT_FOUND", failure.Code);
            Assert.IsTrue(failure.Details is Dictionary<string, object>, "the failure carries machine-readable details");
            var details = (Dictionary<string, object>)failure.Details;
            Assert.IsTrue(details.ContainsKey("domainGeneration"), "the failure names the generation whose windows are live");
            Assert.IsTrue(failure.NextActions.Count > 0, "a failure must route the caller to debug.run-list or debug.run-begin");
        }

        [Test]
        public void FailedVerdictReturnsFirstCounterexampleAndNoAssertionError()
        {
            var sessionId = CreateSession();
            var begun = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, "damage-applied", "once")));
            Assert.AreEqual("run-1", begun.RunId);
            var counterexampleSequence = AppendProgramCheck("damage-applied", false, 42, 0, "editor-tick");
            AppendProgramCheck("damage-applied", true, 42, 42, "editor-tick");
            DebugEventStore.RecordGap(counterexampleSequence + 1L, counterexampleSequence + 1L, "unrelated-later-gap");

            var ended = DebugRunHost.End(new DebugRunEndRequest { RunId = begun.RunId });

            Assert.IsTrue(ended.Sealed);
            Assert.AreEqual(UnityCliDebugRunStates.Sealed, ended.Report.State);
            Assert.IsTrue(ended.Report.Terminal);
            Assert.AreEqual(UnityCliDebugRunVerdicts.Failed, ended.Report.Verdict, "an assertion failure is a business verdict, not an error");
            Assert.IsNotNull(ended.Report.FirstCounterexample, "the first provable counterexample is retained");
            Assert.AreEqual(counterexampleSequence, ended.Report.FirstCounterexample.Sequence);
            Assert.AreEqual("damage-applied", ended.Report.FirstCounterexample.CheckName);
            Assert.AreEqual(42, Convert.ToInt32(ended.Report.FirstCounterexample.Expected, CultureInfo.InvariantCulture));
            Assert.AreEqual(0, Convert.ToInt32(ended.Report.FirstCounterexample.Actual, CultureInfo.InvariantCulture));
            Assert.AreEqual("editor-tick", ended.Report.FirstCounterexample.ExecutionPoint);
            Assert.IsTrue(ended.Report.FirstCounterexample.EvidenceRefs[0].StartsWith("sequence:", StringComparison.Ordinal));
            Assert.AreEqual(DebugRunEvidence.StatusCounterexample, ended.Report.Checks[0].Status, "the check itself is reported as a counterexample");
            Assert.Contains("counterexample", ended.Report.VerdictReasons);
            Assert.IsFalse(ended.Report.VerdictReasons.Contains(UnityCliDebugRunVerdictReasons.CoverageGap),
                "an unrelated later gap never downgrades a complete counterexample");
        }

        [Test]
        public void PassedVerdictRequiresProvenCoverageAndObservedEndCondition()
        {
            var sessionId = CreateSession();
            var begun = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, "damage-applied", "once")));
            AppendProgramCheck("damage-applied", true, 42, 42, "editor-tick");

            var ended = DebugRunHost.End(new DebugRunEndRequest { RunId = begun.RunId });

            Assert.AreEqual(UnityCliDebugRunVerdicts.Passed, ended.Report.Verdict);
            Assert.AreEqual(DebugRunEvidence.StatusSatisfied, ended.Report.Checks[0].Status);
            Assert.IsTrue(ended.Report.Checks[0].CoverageProven);
            Assert.AreEqual(0, ended.Report.Checks[0].FailedCount);
            Assert.Contains("manual", Conditions(ended.Report));
            Assert.IsTrue(ended.Report.Conditions[0].Observed, "sealing by debug.run-end satisfies the implicit manual condition");
            Assert.IsFalse(ended.Report.Conditions[0].DeclaredOnly);
            Assert.IsTrue(ended.Report.DetailAvailable);
            Assert.IsTrue(ended.Report.Capture.QueueDrained);
        }

        private static List<string> Conditions(DebugRunReportView report)
        {
            var kinds = new List<string>();
            foreach (var condition in report.Conditions) kinds.Add(condition.Kind);
            return kinds;
        }

        /// <summary>
        /// 冻结规则：只有 whole-window 与 absent 消费调用方声明的覆盖证明，once 与 every 由已观察到的匹配决定。
        /// 本用例锁定该规则在报告里可区分：不需要申报的量词不得被拒绝、不得被降级，也不得报告“缺少覆盖申报”。
        /// </summary>
        [Test]
        public void OnceAndEveryProveFromAnObservedMatchWithoutADeclaredCoverageRequirement()
        {
            var sessionId = CreateSession();
            foreach (var quantifier in new[] { UnityCliDebugRunQuantifiers.Once, UnityCliDebugRunQuantifiers.Every })
            {
                var name = "observed-" + quantifier;
                var begun = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, name, quantifier)));
                Assert.AreEqual(0, begun.SourceRequirements.Count, quantifier + " must not need a declared coverage requirement");
                AppendProgramCheck(name, true, 4, 4, "editor-tick");

                var report = DebugRunHost.End(new DebugRunEndRequest { RunId = begun.RunId }).Report;

                Assert.AreEqual(UnityCliDebugRunVerdicts.Passed, report.Verdict,
                    quantifier + " must pass from an observed match on a readable interval without any sourceRequirements");
                Assert.AreEqual(DebugRunEvidence.StatusSatisfied, report.Checks[0].Status);
                Assert.AreEqual(1, report.Checks[0].PassedCount);
                Assert.IsFalse(report.Checks[0].CoverageRequired, quantifier + " consumes no caller-declared coverage requirement");
                Assert.IsTrue(report.Checks[0].CoverageProven, "the readable interval of its required sources is still reported as proven coverage");
                Assert.IsFalse(report.VerdictReasons.Contains(UnityCliDebugRunVerdictReasons.CoverageNotRequired),
                    "a quantifier that consumes no declaration must never be reported as missing coverage at the window level");
                Assert.IsFalse(report.Checks[0].Reasons.Contains(UnityCliDebugRunVerdictReasons.CoverageNotRequired));
                Assert.IsFalse(report.Sources[0].ProofRequired);
                Assert.AreEqual(UnityCliDebugRunCoverageProofs.None, report.Sources[0].Proof,
                    "a source with no declaration reports none, never declared");
                Assert.IsTrue(report.Sources[0].CoverageProven);
            }
        }

        /// <summary>量词消费覆盖证明时，两级报告必须同时给出“要求了”与“已证明”两个独立事实。</summary>
        [Test]
        public void CoverageRequiredIsReportedOnlyForTheQuantifiersThatConsumeIt()
        {
            var sessionId = CreateSession();
            var begun = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(new DebugRunBeginRequest
            {
                SessionId = sessionId,
                RequiredChecks = new List<DebugRunCheckSpec>
                {
                    new DebugRunCheckSpec { Name = "still-clean", Source = "log", Position = "*", Quantifier = "whole-window" }
                },
                SourceRequirements = new List<DebugRunSourceRequirementSpec>
                {
                    new DebugRunSourceRequirementSpec { Source = "log", Proof = "coverage" }
                }
            }));
            DebugEventStore.Append(new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugSources.Log,
                Kind = "log",
                ExecutionPoint = "editor-import",
                Name = "still-clean",
                Value = new Dictionary<string, object>(StringComparer.Ordinal) { { "passed", true } }
            });

            var report = DebugRunHost.End(new DebugRunEndRequest { RunId = begun.RunId }).Report;

            Assert.AreEqual(UnityCliDebugRunVerdicts.Passed, report.Verdict);
            Assert.AreEqual("whole-window", report.Checks[0].Quantifier);
            Assert.IsTrue(report.Checks[0].CoverageRequired, "a whole-window claim consumes the caller-declared coverage requirement");
            Assert.IsTrue(report.Checks[0].CoverageProven);
            Assert.AreEqual(DebugRunEvidence.StatusSatisfied, report.Checks[0].Status);
            Assert.IsTrue(report.Sources[0].ProofRequired);
            Assert.AreEqual(UnityCliDebugRunCoverageProofs.Coverage, report.Sources[0].Proof);
            Assert.IsTrue(report.Sources[0].CoverageProven);
            Assert.IsFalse(report.VerdictReasons.Contains(UnityCliDebugRunVerdictReasons.CoverageNotRequired),
                "a declaration that exists is never reported as missing coverage");
        }

        /// <summary>
        /// 冻结规则的评估期一半：消费覆盖的量词缺少申报时绝不被证明。
        /// 公开路径上 begin 会在 effect 之前直接拒绝这种声明，因此本用例直接用内核验证同一规则，
        /// 使“缺少申报 ⇒ 不可证明”不依赖调用方的善意。
        /// </summary>
        [Test]
        public void AWholeWindowCheckWithoutItsDeclaredProofIsNeverSatisfiedAtEvaluationTime()
        {
            var sessionId = CreateSession();
            var window = new DebugRunWindow
            {
                RunId = "run-direct",
                SessionId = sessionId,
                State = UnityCliDebugRunStates.Sealed,
                TerminationReason = DebugRunEvidence.TerminationRunEnd,
                Checks = new List<DebugRunCheckPlan>
                {
                    new DebugRunCheckPlan
                    {
                        Name = "still-clean",
                        Source = "log",
                        Position = "*",
                        Quantifier = UnityCliDebugRunQuantifiers.WholeWindow,
                        RequiredSources = new List<string> { "log" }
                    }
                },
                Conditions = new List<DebugRunConditionPlan>
                {
                    new DebugRunConditionPlan { Kind = UnityCliDebugRunEndConditionKinds.Manual, Name = string.Empty }
                }
            };
            var verdict = DebugRunEvidence.Evaluate(
                window,
                new List<UnityCliDebugEvent>
                {
                    new UnityCliDebugEvent { SourceId = UnityCliDebugSources.Log, Kind = "log", ExecutionPoint = "editor-import", Name = "still-clean" }
                },
                new List<UnityCliDebugGap>(),
                0L,
                1L,
                0L,
                true);

            Assert.AreEqual(0, verdict.Checks[0].Gaps.Count);
            Assert.IsTrue(verdict.Checks[0].CoverageRequired, "a whole-window claim consumes a caller-declared coverage requirement");
            Assert.IsFalse(verdict.Checks[0].CoverageProven, "an interval with no declaration is not a proven coverage proof");
            Assert.AreEqual(DebugRunEvidence.StatusInsufficient, verdict.Checks[0].Status,
                "a matching event alone must never satisfy a whole-window check that lacks its declared coverage proof");
            Assert.Contains(UnityCliDebugRunVerdictReasons.CoverageNotRequired, verdict.Reasons);
            Assert.AreEqual(UnityCliDebugRunVerdicts.Indeterminate, verdict.Verdict);
        }

        /// <summary>来源级词汇必须把“申报过什么”与“需要什么证明”分开，且报告专用取值不可被申报。</summary>
        [Test]
        public void SourceProofVocabularySeparatesDeclarationFromRequirement()
        {
            var sessionId = CreateSession();
            var declaredOnly = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(new DebugRunBeginRequest
            {
                SessionId = sessionId,
                RequiredChecks = new List<DebugRunCheckSpec>
                {
                    new DebugRunCheckSpec { Name = "observed-once", Source = "program", Position = "*", Quantifier = "once" }
                },
                SourceRequirements = new List<DebugRunSourceRequirementSpec>
                {
                    new DebugRunSourceRequirementSpec { Source = "program", Proof = "declared" }
                }
            }));
            AppendProgramCheck("observed-once", true, 1, 1, "editor-tick");

            var declaredReport = DebugRunHost.End(new DebugRunEndRequest { RunId = declaredOnly.RunId }).Report;

            Assert.AreEqual(UnityCliDebugRunCoverageProofs.Declared, declaredReport.Sources[0].Proof);
            Assert.IsFalse(declaredReport.Sources[0].ProofRequired, "a declared statement is recorded and never counted as proof");
            Assert.IsFalse(declaredReport.Checks[0].CoverageRequired);
            Assert.AreEqual(UnityCliDebugRunVerdicts.Passed, declaredReport.Verdict, "a declared statement neither proves nor blocks a once check");

            var rejected = Assert.Throws<UnityCli.UnityCliBridge.BridgeCommandException>(() => DebugRunHost.PrepareBegin(new DebugRunBeginRequest
            {
                SessionId = sessionId,
                RequiredChecks = new List<DebugRunCheckSpec>
                {
                    new DebugRunCheckSpec { Name = "observed-once", Source = "program", Position = "*", Quantifier = "once" }
                },
                SourceRequirements = new List<DebugRunSourceRequirementSpec>
                {
                    new DebugRunSourceRequirementSpec { Source = "program", Proof = UnityCliDebugRunCoverageProofs.None }
                }
            }));
            Assert.AreEqual("E_SCHEMA_INVALID", rejected.Code, "the report-only none value is not a declareable requirement");
        }

        [Test]
        public void WholeWindowAndAbsentChecksRequireDeclaredCoverageProof()
        {
            var sessionId = CreateSession();
            foreach (var quantifier in new[] { "whole-window", "absent" })
            {
                var failure = Assert.Throws<UnityCli.UnityCliBridge.BridgeCommandException>(
                    () => DebugRunHost.PrepareBegin(BeginRequest(sessionId, "never-error", quantifier, "log")));
                Assert.AreEqual("E_SCHEMA_INVALID", failure.Code, quantifier + " must require a declared coverage requirement");
                Assert.AreEqual("$.sourceRequirements", Convert.ToString(((Dictionary<string, object>)failure.Details)["path"], CultureInfo.InvariantCulture));
            }
            // 声明覆盖要求后同一检查被接受。
            var accepted = DebugRunHost.PrepareBegin(new DebugRunBeginRequest
            {
                SessionId = sessionId,
                RequiredChecks = new List<DebugRunCheckSpec>
                {
                    new DebugRunCheckSpec { Name = "never-error", Source = "log", Position = "*", Quantifier = "absent" }
                },
                SourceRequirements = new List<DebugRunSourceRequirementSpec>
                {
                    new DebugRunSourceRequirementSpec { Source = "log", Proof = "coverage" }
                }
            });
            Assert.IsTrue(accepted.SourceRequirements[0].ProofRequired);
        }

        [Test]
        public void AbsentCheckPassesWithProvenCoverageAndReportsMatchAsCounterexample()
        {
            var sessionId = CreateSession();
            var clear = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(new DebugRunBeginRequest
            {
                SessionId = sessionId,
                RequiredChecks = new List<DebugRunCheckSpec>
                {
                    new DebugRunCheckSpec { Name = "never-error", Source = "log", Position = "*", Quantifier = "absent" }
                },
                SourceRequirements = new List<DebugRunSourceRequirementSpec>
                {
                    new DebugRunSourceRequirementSpec { Source = "log", Proof = "coverage" }
                }
            }));
            var clearReport = DebugRunHost.End(new DebugRunEndRequest { RunId = clear.RunId }).Report;
            Assert.AreEqual(UnityCliDebugRunVerdicts.Passed, clearReport.Verdict, "an absent check with proven coverage and no match passes");
            Assert.AreEqual(DebugRunEvidence.StatusSatisfied, clearReport.Checks[0].Status);
            Assert.IsTrue(clearReport.Sources[0].ProofRequired, "the negative check declares that it needs proven coverage");

            var violated = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(new DebugRunBeginRequest
            {
                SessionId = sessionId,
                RequiredChecks = new List<DebugRunCheckSpec>
                {
                    new DebugRunCheckSpec { Name = "never-error", Source = "log", Position = "*", Quantifier = "absent" }
                },
                SourceRequirements = new List<DebugRunSourceRequirementSpec>
                {
                    new DebugRunSourceRequirementSpec { Source = "log", Proof = "coverage" }
                }
            }));
            DebugEventStore.Append(new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugSources.Log,
                Kind = "log",
                ExecutionPoint = "editor-import",
                Name = "never-error"
            });
            var violatedReport = DebugRunHost.End(new DebugRunEndRequest { RunId = violated.RunId }).Report;
            Assert.AreEqual(UnityCliDebugRunVerdicts.Failed, violatedReport.Verdict, "any match is the counterexample of an absent check");
            Assert.IsNotNull(violatedReport.FirstCounterexample);
            Assert.AreEqual("log", violatedReport.FirstCounterexample.SourceId);
        }

        [Test]
        public void CoverageGapAndSampledStreamDowngradeProvenChecks()
        {
            var sessionId = CreateSession();
            var gap = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, "damage-applied", "once")));
            var first = AppendProgramCheck("damage-applied", true, 1, 1, "editor-tick");
            // 缺口必须落在窗口自己的序列区间内：先把窗口推到缺口之后，再声明这段区间不可读。
            var tail = DebugEventStore.Append(new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugSources.Lifecycle,
                Kind = "window-tail",
                ExecutionPoint = "editor-tick",
                Name = "window-tail"
            });
            DebugEventStore.RecordGap(first + 1L, tail, "unreadable-range");
            var gapReport = DebugRunHost.End(new DebugRunEndRequest { RunId = gap.RunId }).Report;
            Assert.AreEqual(UnityCliDebugRunVerdicts.Indeterminate, gapReport.Verdict, "a coverage gap prevents proving a check");
            Assert.Contains(UnityCliDebugRunVerdictReasons.CoverageGap, gapReport.VerdictReasons);
            Assert.AreEqual(1, gapReport.Checks[0].MatchCount, "the check's own passing match is still retained inside the window");
            Assert.AreEqual(DebugRunEvidence.StatusInsufficient, gapReport.Checks[0].Status,
                "a retained passing match cannot prove a check whose window range has a coverage gap");

            var sampled = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, "sampled-check", "once")));
            DebugEventStore.Append(new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugProgramAbi.EventSource,
                Kind = "check",
                ExecutionPoint = "editor-tick",
                Name = "sampled-check",
                Value = new Dictionary<string, object>(StringComparer.Ordinal) { { "passed", true } },
                Sampled = true,
                SampleEvery = 10
            });
            var sampledReport = DebugRunHost.End(new DebugRunEndRequest { RunId = sampled.RunId }).Report;
            Assert.AreEqual(UnityCliDebugRunVerdicts.Indeterminate, sampledReport.Verdict, "a sampled stream only proves sample points");
            Assert.Contains(UnityCliDebugRunVerdictReasons.SampledOnly, sampledReport.VerdictReasons);
            Assert.IsTrue(sampledReport.Checks[0].SamplePointOnly);
        }

        [Test]
        public void ConfigurationChangeIsIndeterminateAndReportedPerSegmentWhenAllowed()
        {
            var sessionId = CreateSession();
            var strict = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(new DebugRunBeginRequest
            {
                SessionId = sessionId,
                RequiredChecks = new List<DebugRunCheckSpec>
                {
                    new DebugRunCheckSpec { Name = "damage-applied", Source = "program", Position = "*", Quantifier = "once", HookId = "hook-1" }
                }
            }));
            AppendProgramCheck("damage-applied", true, 1, 1, "editor-tick", "hook-1");
            DebugEventStore.Append(new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugSources.Lifecycle,
                Kind = "lifecycle",
                Name = "replaced",
                HookId = "hook-1",
                Revision = 2L
            });
            var strictReport = DebugRunHost.End(new DebugRunEndRequest { RunId = strict.RunId }).Report;
            Assert.AreEqual(UnityCliDebugRunVerdicts.Indeterminate, strictReport.Verdict, "a strict window is indeterminate when its configuration changed");
            Assert.Contains(UnityCliDebugRunVerdictReasons.ConfigurationChanged, strictReport.VerdictReasons);
            Assert.AreEqual(0, strictReport.Segments.Count, "a strict window does not publish fake segments");

            var segmented = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(new DebugRunBeginRequest
            {
                SessionId = sessionId,
                AllowSegments = true,
                RequiredChecks = new List<DebugRunCheckSpec>
                {
                    new DebugRunCheckSpec { Name = "damage-applied", Source = "program", Position = "*", Quantifier = "once", HookId = "hook-1" }
                }
            }));
            AppendProgramCheck("damage-applied", true, 1, 1, "editor-tick", "hook-1");
            var boundary = DebugEventStore.Append(new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugSources.Lifecycle,
                Kind = "lifecycle",
                Name = "replaced",
                HookId = "hook-1",
                Revision = 3L
            });
            var segmentedReport = DebugRunHost.End(new DebugRunEndRequest { RunId = segmented.RunId }).Report;
            Assert.AreEqual(2, segmentedReport.Segments.Count, "the boundary splits the window into two reported segments");
            Assert.AreEqual(1, segmentedReport.Segments[0].CheckStatuses.Count);
            Assert.AreEqual(DebugRunEvidence.StatusSatisfied, segmentedReport.Segments[0].CheckStatuses[0].Status);
            Assert.AreEqual(UnityCliDebugRunVerdicts.Indeterminate, segmentedReport.Segments[1].Verdict,
                "the second configuration segment has no evidence and is reported honestly");
            Assert.AreEqual(UnityCliDebugRunVerdicts.Indeterminate, segmentedReport.Verdict);
            Assert.AreEqual(boundary, segmentedReport.Segments[1].FromSequence);
        }

        [Test]
        public void SessionStopInterruptsAnOpenWindow()
        {
            var sessionId = CreateSession();
            var begun = DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, "damage-applied", "once")));
            DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));

            var report = DebugRunHost.Info(new DebugRunInfoRequest { RunId = begun.RunId }).Report;

            Assert.AreEqual(UnityCliDebugRunStates.Interrupted, report.State);
            Assert.IsTrue(report.Terminal, "an interrupted window is terminal");
            Assert.AreEqual(DebugRunEvidence.TerminationSessionStopped, report.TerminationReason);
            Assert.Contains(UnityCliDebugRunVerdictReasons.WindowInterrupted, report.VerdictReasons);
        }

        [Test]
        public void RunBudgetsKeepSixteenSummariesAndFourDetailedWindows()
        {
            var sessionId = CreateSession();
            var begun = new List<string>();
            for (var index = 0; index < DebugLimits.MaxDetailedRunWindowsPerSession + 1; index++)
            {
                begun.Add(DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, "check-" + index.ToString(CultureInfo.InvariantCulture), "once"))).RunId);
            }
            var oldest = DebugRunHost.Info(new DebugRunInfoRequest { RunId = begun[0] }).Report;
            Assert.IsFalse(oldest.DetailAvailable, "only the newest windows keep detailed evidence");
            Assert.Contains(UnityCliDebugRunVerdictReasons.DetailEvicted, oldest.VerdictReasons);
            Assert.AreEqual(DebugRunEvidence.StatusInsufficient, oldest.Checks[0].Status, "an evicted detail reports insufficient evidence instead of inventing a verdict");

            for (var index = 0; index < DebugLimits.MaxRunSummariesPerSession + 4; index++)
            {
                DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, "later-" + index.ToString(CultureInfo.InvariantCulture), "once")));
            }
            var total = DebugRunHost.List(new DebugRunListRequest { SessionId = sessionId }).Total;
            Assert.AreEqual((long)DebugLimits.MaxRunSummariesPerSession, total, "the summary list stays bounded to the declared budget");
            Assert.AreEqual(DebugLimits.MaxRunSummariesPerSession, DebugRunHost.WindowCount);
        }

        [Test]
        public void ListPagesWindowsOfTheCurrentDomainAndRejectsAStaleCursor()
        {
            var sessionId = CreateSession();
            DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, "check-a", "once")));
            DebugRunHost.ApplyBegin(DebugRunHost.PrepareBegin(BeginRequest(sessionId, "check-b", "once")));

            var page = DebugRunHost.List(new DebugRunListRequest { SessionId = sessionId, Limit = 1 });
            Assert.AreEqual(1, page.Items.Count);
            Assert.IsTrue(page.HasMore);
            Assert.IsNotNull(page.NextCursor);
            Assert.AreEqual("run-1", page.Items[0].RunId, "windows are listed in creation order");
            Assert.AreEqual(UnityCliDebugRunVerdicts.Indeterminate, page.Items[0].Verdict, "an open window is indeterminate until it is sealed");

            var next = DebugRunHost.List(new DebugRunListRequest { SessionId = sessionId, Limit = 1, Cursor = page.NextCursor });
            Assert.AreEqual("run-2", next.Items[0].RunId);
            Assert.IsFalse(next.HasMore);

            var failure = Assert.Throws<UnityCli.UnityCliBridge.BridgeCommandException>(
                () => DebugRunHost.List(new DebugRunListRequest { SessionId = "dbg-other", Cursor = page.NextCursor }));
            Assert.AreEqual("E_CONTINUATION_STALE", failure.Code, "a cursor is bound to its session filter");
        }
    }
}
