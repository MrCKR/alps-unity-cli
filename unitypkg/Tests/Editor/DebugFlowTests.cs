#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli.Tests
{
    /// <summary>
    /// ISSUE-109：跨帧流的推进、等待结算、终态闭集与并发治理。
    ///
    /// 推进只由中央 tick 触发，因此用例显式调用 DebugFlowHost.Advance 代替等帧：
    /// 这样既能断言"第几次推进结算"，也能断言终态是否可重复读取。
    /// </summary>
    public sealed class DebugFlowTests
    {
        [SetUp]
        public void ResetBefore()
        {
            DebugFlowHost.Reset();
        }

        [TearDown]
        public void ResetAfter()
        {
            DebugFlowHost.Reset();
        }

        [Test]
        public void NextFrameThenReturnCompletesWithTheReturnedValue()
        {
            var record = DebugFlowHost.Create(Loop(), string.Empty, 5000d);

            DebugFlowHost.Advance();
            DebugFlowHost.Advance();
            Assert.AreEqual(DebugFlowHost.StateRunning, record.State, "two frames are not enough for NextFrame(2) plus the return yield");

            DebugFlowHost.Advance();
            Assert.AreEqual(DebugFlowHost.StateCompleted, record.State);
            Assert.AreEqual(7, Convert.ToInt32(record.Result));
            Assert.AreEqual(2L, record.Steps, "each yielded wait counts as one step");
        }

        [Test]
        public void NextFrameBelowOneIsClampedToOneFrame()
        {
            var record = DebugFlowHost.Create(ZeroFrames(), string.Empty, 5000d);

            DebugFlowHost.Advance();
            DebugFlowHost.Advance();
            Assert.AreEqual(DebugFlowHost.StateCompleted, record.State);
        }

        [Test]
        public void ConditionWaitSettlesAndExposesTheSettledResult()
        {
            var satisfied = false;
            var record = DebugFlowHost.Create(ConditionProbe(() => satisfied, false), string.Empty, 5000d);

            DebugFlowHost.Advance();
            Assert.AreEqual(DebugFlowHost.StateRunning, record.State, "an unsatisfied condition keeps the flow waiting");

            satisfied = true;
            DebugFlowHost.Advance();
            DebugFlowHost.Advance();
            Assert.AreEqual(DebugFlowHost.StateCompleted, record.State);
            Assert.AreEqual(true, record.Result);
        }

        [Test]
        public void ConditionWaitTimesOutAndReportsTheObservedFacts()
        {
            var record = DebugFlowHost.Create(ConditionProbe(() => false, true), string.Empty, 5000d);

            DebugFlowHost.Advance();
            Thread.Sleep(40);
            DebugFlowHost.Advance();
            DebugFlowHost.Advance();

            Assert.AreEqual(DebugFlowHost.StateCompleted, record.State, "a timed-out wait is a value the flow decides on, not a flow failure");
            var result = record.Result as DebugFlowWaitResult;
            Assert.IsNotNull(result);
            Assert.IsTrue(result.TimedOut);
            Assert.IsFalse(result.Satisfied);
            Assert.Greater(result.Samples, 0L);
        }

        [Test]
        public void AThrowingPredicateFailsTheFlowInsteadOfRetryingAsFalse()
        {
            var record = DebugFlowHost.Create(ThrowingPredicate(), string.Empty, 5000d);

            DebugFlowHost.Advance();
            DebugFlowHost.Advance();
            Assert.AreEqual(DebugFlowHost.StateFailed, record.State);
            Assert.AreEqual("predicate-exception", record.FailureTag);
            Assert.AreEqual("E_DEBUG_FLOW_PREDICATE_FAILED", record.ErrorCode);
            Assert.IsNull(record.Result);
        }

        [Test]
        public void AnExceptionInTheBodyFailsTheFlowWithTheUserCodeTag()
        {
            var record = DebugFlowHost.Create(ThrowingBody(), string.Empty, 5000d);

            DebugFlowHost.Advance();
            DebugFlowHost.Advance();
            Assert.AreEqual(DebugFlowHost.StateFailed, record.State);
            Assert.AreEqual("user-code-exception", record.FailureTag);
            Assert.AreEqual("E_DEBUG_FLOW_USER_CODE_FAILED", record.ErrorCode);
        }

        [Test]
        public void TheFlowBudgetClosesTheFlowAsTimedOut()
        {
            var record = DebugFlowHost.Create(Endless(), string.Empty, 20d);

            DebugFlowHost.Advance();
            Thread.Sleep(50);
            DebugFlowHost.Advance();

            Assert.AreEqual(DebugFlowHost.StateTimedOut, record.State);
            Assert.AreEqual("budget", record.FailureTag);
            Assert.Greater(record.ConsumedMs, record.BudgetMs);
        }

        [Test]
        public void CancelIsTerminalAndRepeatingItKeepsTheSameState()
        {
            var record = DebugFlowHost.Create(Endless(), string.Empty, 5000d);

            DebugFlowHost.Advance();
            DebugFlowHost.Cancel(record);
            Assert.AreEqual(DebugFlowHost.StateCancelled, record.State);

            DebugFlowHost.Cancel(record);
            DebugFlowHost.Advance();
            Assert.AreEqual(DebugFlowHost.StateCancelled, record.State, "a repeat cancel never rewrites the published terminal state");
            Assert.AreEqual(0, DebugFlowHost.ActiveCount);
        }

        [Test]
        public void ACompletedFlowStaysReadableAfterItLeavesTheActiveTable()
        {
            var record = DebugFlowHost.Create(ReturnOnly(42), string.Empty, 5000d);

            DebugFlowHost.Advance();
            Assert.AreEqual(DebugFlowHost.StateCompleted, record.State);
            Assert.AreEqual(0, DebugFlowHost.ActiveCount);

            var again = DebugFlowHost.Find(record.FlowId);
            Assert.IsNotNull(again);
            Assert.AreEqual(42, Convert.ToInt32(again.Result), "the terminal value must not change on a later read");
        }

        [Test]
        public void TheSessionLimitRefusesTheFlowAfterThePerSessionCap()
        {
            for (var index = 0; index < DebugLimits.MaxFlowsPerSession; index++)
                DebugFlowHost.Create(Endless(), "s1", 5000d);

            BridgeCommandException captured = null;
            try
            {
                DebugFlowHost.Create(Endless(), "s1", 5000d);
            }
            catch (BridgeCommandException exception)
            {
                captured = exception;
            }

            Assert.IsNotNull(captured, "a flow beyond the per-session cap must be refused");
            Assert.AreEqual("E_DEBUG_FLOW_LIMIT_EXCEEDED", captured.Code);
            var details = captured.Details as Dictionary<string, object>;
            Assert.IsNotNull(details);
            Assert.AreEqual("session", Convert.ToString(details["scope"]));
        }

        [Test]
        public void TheInstanceLimitRefusesTheFlowAfterThePerInstanceCap()
        {
            for (var index = 0; index < DebugLimits.MaxFlowsPerInstance; index++)
                DebugFlowHost.Create(Endless(), "s" + index, 5000d);

            BridgeCommandException captured = null;
            try
            {
                DebugFlowHost.Create(Endless(), "overflow", 5000d);
            }
            catch (BridgeCommandException exception)
            {
                captured = exception;
            }

            Assert.IsNotNull(captured, "a flow beyond the per-instance cap must be refused");
            Assert.AreEqual("E_DEBUG_FLOW_LIMIT_EXCEEDED", captured.Code);
            var details = captured.Details as Dictionary<string, object>;
            Assert.IsNotNull(details);
            Assert.AreEqual("instance", Convert.ToString(details["scope"]));
        }

        [Test]
        public void ASessionScopedFlowDoesNotConsumeTheBudgetOfAnotherSession()
        {
            DebugFlowHost.Create(Endless(), "a", 5000d);
            DebugFlowHost.Create(Endless(), "a", 5000d);

            Assert.AreEqual(2, DebugFlowHost.ActiveForSession("a"));
            Assert.AreEqual(0, DebugFlowHost.ActiveForSession("b"));
            DebugFlowHost.Create(Endless(), "b", 5000d);
            Assert.AreEqual(1, DebugFlowHost.ActiveForSession("b"));
        }

        [Test]
        public void SubmittingFromInsideAFlowIsRefusedAndFailsThatFlow()
        {
            var record = DebugFlowHost.Create(NestedSubmission(), string.Empty, 5000d);

            DebugFlowHost.Advance();
            Assert.AreEqual(DebugFlowHost.StateFailed, record.State);
            Assert.AreEqual("user-code-exception", record.FailureTag);
            Assert.AreEqual(0, DebugFlowHost.ActiveCount, "the refused nested flow must not be registered");
        }

        [Test]
        public void ResetDropsActiveAndRetiredFlows()
        {
            var record = DebugFlowHost.Create(ReturnOnly(1), string.Empty, 5000d);
            DebugFlowHost.Advance();
            Assert.IsNotNull(DebugFlowHost.Find(record.FlowId));

            DebugFlowHost.Reset();
            Assert.IsNull(DebugFlowHost.Find(record.FlowId));
            Assert.AreEqual(0, DebugFlowHost.ActiveCount);
        }

        [Test]
        public void AnOutOfRangeBudgetIsRefusedAsSchemaInvalid()
        {
            BridgeCommandException captured = null;
            try
            {
                DebugFlowHost.Create(ReturnOnly(1), string.Empty, 0d);
            }
            catch (BridgeCommandException exception)
            {
                captured = exception;
            }

            Assert.IsNotNull(captured);
            Assert.AreEqual("E_SCHEMA_INVALID", captured.Code);
        }

        [Test]
        public void LogWaitMatchesAMessageEmittedAfterTheWaitStarts()
        {
            var marker = "issue109-marker-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var record = DebugFlowHost.Create(WaitForLog(marker), string.Empty, 5000d);

            DebugFlowHost.Advance();
            Assert.AreEqual(DebugFlowHost.StateRunning, record.State);
            UnityEngine.Debug.Log(marker);
            DebugFlowHost.Advance();

            Assert.AreEqual(DebugFlowHost.StateCompleted, record.State);
            Assert.AreEqual(true, record.Result, "the wait must report itself satisfied");
            Assert.AreEqual(false, DebugListenerHost.IsSubscribed, "the subscription is released when the last log wait settles");
        }

        [Test]
        public void LogWaitTimesOutWhenNoMatchingMessageArrives()
        {
            var marker = "issue109-absent-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var record = DebugFlowHost.Create(WaitForLog(marker), string.Empty, 5000d);

            DebugFlowHost.Advance();
            Thread.Sleep(450);
            DebugFlowHost.Advance();
            DebugFlowHost.Advance();

            Assert.AreEqual(DebugFlowHost.StateCompleted, record.State);
            Assert.AreEqual(true, record.Result, "a log wait that never matches must settle as timedOut, not as a failure");
        }

        [Test]
        public void ResetReleasesTheLogSubscription()
        {
            DebugFlowHost.Create(WaitForLog("issue109-reset-probe"), string.Empty, 5000d);
            DebugFlowHost.Advance();
            Assert.IsTrue(DebugListenerHost.IsSubscribed, "an active log wait must own the single subscription");

            DebugFlowHost.Reset();
            Assert.IsFalse(DebugListenerHost.IsSubscribed, "Reset must remove the log callback so nothing keeps listening after a domain reset");
        }

        // 每个用例使用独立的迭代器工厂：流体会被推进与丢弃，不能复用同一个 IEnumerator 实例。
        private static IEnumerator Loop()
        {
            yield return DebugFlow.NextFrame(2);
            yield return DebugFlow.Return(7);
        }

        private static IEnumerator ZeroFrames()
        {
            yield return DebugFlow.NextFrame(0);
            yield return DebugFlow.Return(true);
        }

        private static IEnumerator ConditionProbe(Func<bool> condition, bool returnWaitResult)
        {
            yield return DebugFlow.Until(condition, 20d);
            yield return DebugFlow.Return(returnWaitResult ? (object)DebugFlow.Last : DebugFlow.Last.Satisfied);
        }

        private static IEnumerator ThrowingPredicate()
        {
            yield return DebugFlow.Until(() => throw new InvalidOperationException("predicate"), 1000d);
            yield return DebugFlow.Return("unreachable");
        }

        private static IEnumerator ThrowingBody()
        {
            yield return DebugFlow.NextFrame(1);
            throw new InvalidOperationException("body");
        }

        private static IEnumerator Endless()
        {
            while (true) yield return DebugFlow.NextFrame(1);
        }

        private static IEnumerator ReturnOnly(object value)
        {
            yield return DebugFlow.Return(value);
        }

        private static IEnumerator NestedSubmission()
        {
            DebugFlowHost.Create(Endless(), string.Empty, 5000d);
            yield return DebugFlow.Return("unreachable");
        }

        [Test]
        public void LogWaitInsideACombinedWaitSubscribesAndSettles()
        {
            var marker = "issue109-nested-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var record = DebugFlowHost.Create(CombinedLogWait(marker), string.Empty, 5000d);

            DebugFlowHost.Advance();
            Assert.IsTrue(DebugListenerHost.IsSubscribed, "a log wait nested in All/Any must own the subscription too");
            UnityEngine.Debug.Log(marker);
            DebugFlowHost.Advance();

            Assert.AreEqual(DebugFlowHost.StateCompleted, record.State);
            Assert.AreEqual(true, record.Result);
            Assert.IsFalse(DebugListenerHost.IsSubscribed, "the nested subscription is released when the combined wait settles");
        }

        private static IEnumerator CombinedLogWait(string marker)
        {
            yield return DebugFlow.All(
                DebugFlow.UntilLog(marker, 2000),
                DebugFlow.Until(() => true, 2000));
            yield return DebugFlow.Return(DebugFlow.Last.Satisfied);
        }
        private static IEnumerator WaitForLog(string marker)
        {
            yield return DebugFlow.UntilLog(marker, 400);
            yield return DebugFlow.Return(DebugFlow.Last.Satisfied || DebugFlow.Last.TimedOut);
        }
    }
}
#endif
