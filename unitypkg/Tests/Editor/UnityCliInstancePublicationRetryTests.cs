#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace UnityCli.Tests
{
    public sealed class UnityCliInstancePublicationRetryTests
    {
        private double now;
        private readonly Queue<InstancePublicationAttempt> attempts = new Queue<InstancePublicationAttempt>();
        private readonly List<string> publishedStates = new List<string>();
        private readonly List<string> logs = new List<string>();

        [SetUp]
        public void SetUp()
        {
            now = 0;
            attempts.Clear();
            publishedStates.Clear();
            logs.Clear();
        }

        [Test]
        public void InitialRetryKeepsListenerClosedUntilTransitioningPublishesThenOpensOnce()
        {
            attempts.Enqueue(Failure(true, "io"));
            attempts.Enqueue(Success());
            var retry = CreateRetry();
            var gate = new BridgeInitialPublicationGate();
            var startup = new BridgeStartupStateMachine();
            startup.BeginRegistryPublication();
            var listenerRetained = true;
            var queueOpen = false;
            var listenLoopStarts = 0;
            gate.Start();

            Assert.That(Apply(gate, retry.Request("transitioning"), ref listenerRetained, ref queueOpen, ref listenLoopStarts),
                Is.EqualTo(BridgeInitialPublicationAction.Wait));
            Assert.That(listenerRetained, Is.True);
            Assert.That(queueOpen, Is.False);
            Assert.That(listenLoopStarts, Is.EqualTo(0));
            Assert.That(retry.Pending, Is.True);
            Assert.That(retry.AttemptCount, Is.EqualTo(1));
            Assert.That(startup.Phase, Is.EqualTo(BridgeStartupPhase.RegistryPublication));
            Assert.That(startup.Tick(false,
                () => throw new InvalidOperationException("prime must not replay"),
                () => throw new InvalidOperationException("handoff must not replay")),
                Is.EqualTo(BridgeStartupTransition.RegistryPublicationPending));

            now = 0.24;
            Assert.That(retry.Tick(), Is.EqualTo(InstancePublicationRetryOutcome.None));
            now = 0.25;
            Assert.That(Apply(gate, retry.Tick(), ref listenerRetained, ref queueOpen, ref listenLoopStarts),
                Is.EqualTo(BridgeInitialPublicationAction.OpenQueueAndListen));
            startup.CompleteRegistryPublication();
            Assert.That(publishedStates, Is.EqualTo(new[] { "transitioning", "transitioning" }));
            Assert.That(listenerRetained, Is.True);
            Assert.That(queueOpen, Is.True);
            Assert.That(listenLoopStarts, Is.EqualTo(1));
            Assert.That(gate.Published, Is.True);
            Assert.That(startup.Phase, Is.EqualTo(BridgeStartupPhase.None));
            Assert.That(retry.Pending, Is.False);
            Assert.That(retry.AttemptCount, Is.EqualTo(0));

            Assert.That(gate.Apply(InstancePublicationRetryOutcome.Written), Is.EqualTo(BridgeInitialPublicationAction.None));
            Assert.That(listenLoopStarts, Is.EqualTo(1));
            Assert.That(logs.Exists(value => value.Contains("instance_publication_retry_succeeded attempts=2")), Is.True);
        }

        [Test]
        public void PendingPeriodicPublicationKeepsOneChainAndUsesLatestRequestedState()
        {
            attempts.Enqueue(Failure(true, "io"));
            attempts.Enqueue(Success());
            var retry = CreateRetry();

            Assert.That(retry.Request("transitioning"), Is.EqualTo(InstancePublicationRetryOutcome.Pending));
            Assert.That(retry.Request("ready"), Is.EqualTo(InstancePublicationRetryOutcome.Pending));
            Assert.That(retry.AttemptCount, Is.EqualTo(1));
            Assert.That(publishedStates, Is.EqualTo(new[] { "transitioning" }));
            now = 0.25;

            Assert.That(retry.Tick(), Is.EqualTo(InstancePublicationRetryOutcome.Written));
            Assert.That(publishedStates, Is.EqualTo(new[] { "transitioning", "ready" }));
            Assert.That(retry.AttemptCount, Is.EqualTo(0));
        }

        [TestCase(2L, 1L, 1L, 2L)]
        [TestCase(1L, 2L, 1L, 2L)]
        public void InitialStaleGenerationOrSequenceStopsOldListenerWithoutOpeningQueue(
            long previousGeneration,
            long previousSequence,
            long generation,
            long sequence)
        {
            var result = UnityCliBridge.RegistryPublicationResultForTests(
                previousGeneration, previousSequence, generation, sequence);
            Assert.That(result, Is.EqualTo(RegistryPublicationResult.StaleSkipped));
            attempts.Enqueue(new InstancePublicationAttempt(result, false, "stale"));
            var retry = CreateRetry();
            var gate = new BridgeInitialPublicationGate();
            var listenerRetained = true;
            var queueOpen = false;
            var listenLoopStarts = 0;
            gate.Start();

            Assert.That(Apply(gate, retry.Request("transitioning"), ref listenerRetained, ref queueOpen, ref listenLoopStarts),
                Is.EqualTo(BridgeInitialPublicationAction.StopListener));

            Assert.That(listenerRetained, Is.False);
            Assert.That(queueOpen, Is.False);
            Assert.That(listenLoopStarts, Is.EqualTo(0));
            Assert.That(gate.Published, Is.False);
            Assert.That(retry.Pending, Is.False);
            Assert.That(retry.Exhausted, Is.False);
            Assert.That(retry.AttemptCount, Is.EqualTo(0));
        }

        [Test]
        public void RetryableFailureStopsListenerOnlyAfterFourthAttempt()
        {
            for (var index = 0; index < 4; index++) attempts.Enqueue(Failure(true, "lock-timeout"));
            var retry = CreateRetry();
            var gate = new BridgeInitialPublicationGate();
            var listenerRetained = true;
            var queueOpen = false;
            var listenLoopStarts = 0;
            gate.Start();

            Assert.That(Apply(gate, retry.Request("transitioning"), ref listenerRetained, ref queueOpen, ref listenLoopStarts),
                Is.EqualTo(BridgeInitialPublicationAction.Wait));
            now = 0.25;
            Assert.That(Apply(gate, retry.Tick(), ref listenerRetained, ref queueOpen, ref listenLoopStarts),
                Is.EqualTo(BridgeInitialPublicationAction.Wait));
            now = 0.75;
            Assert.That(Apply(gate, retry.Tick(), ref listenerRetained, ref queueOpen, ref listenLoopStarts),
                Is.EqualTo(BridgeInitialPublicationAction.Wait));
            now = 1.75;
            Assert.That(Apply(gate, retry.Tick(), ref listenerRetained, ref queueOpen, ref listenLoopStarts),
                Is.EqualTo(BridgeInitialPublicationAction.StopListener));

            Assert.That(retry.AttemptCount, Is.EqualTo(4));
            Assert.That(retry.Exhausted, Is.True);
            Assert.That(listenerRetained, Is.False);
            Assert.That(queueOpen, Is.False);
            Assert.That(listenLoopStarts, Is.EqualTo(0));
            Assert.That(logs.Exists(value => value.Contains("instance_publication_retry_stopped")), Is.True);
        }

        [Test]
        public void NonRetryableFailureStopsImmediatelyAndResetCreatesANewDomainChain()
        {
            attempts.Enqueue(Failure(false, "InvalidOperationException"));
            var retry = CreateRetry();
            var gate = new BridgeInitialPublicationGate();
            var listenerRetained = true;
            var queueOpen = false;
            var listenLoopStarts = 0;
            gate.Start();

            Assert.That(Apply(gate, retry.Request("transitioning"), ref listenerRetained, ref queueOpen, ref listenLoopStarts),
                Is.EqualTo(BridgeInitialPublicationAction.StopListener));
            Assert.That(retry.Exhausted, Is.True);
            Assert.That(retry.AttemptCount, Is.EqualTo(1));
            Assert.That(listenerRetained, Is.False);

            retry.Reset();
            gate.Reset();
            attempts.Enqueue(Success());
            listenerRetained = true;
            gate.Start();
            Assert.That(Apply(gate, retry.Request("transitioning"), ref listenerRetained, ref queueOpen, ref listenLoopStarts),
                Is.EqualTo(BridgeInitialPublicationAction.OpenQueueAndListen));
            Assert.That(retry.Exhausted, Is.False);
            Assert.That(queueOpen, Is.True);
            Assert.That(listenLoopStarts, Is.EqualTo(1));
        }

        private UnityCliInstancePublicationRetry CreateRetry()
        {
            return new UnityCliInstancePublicationRetry(
                4,
                () => now,
                state =>
                {
                    publishedStates.Add(state);
                    if (attempts.Count == 0) throw new InvalidOperationException("Test registry boundary has no configured attempt.");
                    return attempts.Dequeue();
                },
                failedAttempts => failedAttempts <= 1 ? 0.25 : failedAttempts == 2 ? 0.5 : 1.0,
                logs.Add);
        }

        private static BridgeInitialPublicationAction Apply(
            BridgeInitialPublicationGate gate,
            InstancePublicationRetryOutcome outcome,
            ref bool listenerRetained,
            ref bool queueOpen,
            ref int listenLoopStarts)
        {
            var action = gate.Apply(outcome);
            if (action == BridgeInitialPublicationAction.OpenQueueAndListen)
            {
                queueOpen = true;
                listenLoopStarts += 1;
            }
            else if (action == BridgeInitialPublicationAction.StopListener)
            {
                listenerRetained = false;
            }
            return action;
        }

        private static InstancePublicationAttempt Success()
        {
            return new InstancePublicationAttempt(RegistryPublicationResult.Written, false, "none");
        }

        private static InstancePublicationAttempt Failure(bool retryable, string kind)
        {
            return new InstancePublicationAttempt(RegistryPublicationResult.Failed, retryable, kind);
        }
    }
}
#endif
