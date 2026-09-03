#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityCli.Protocol;

namespace UnityCli.Tests
{
    [NonParallelizable]
    public sealed class UnityCliBridgeAdmissionIntegrationTests
    {
        private UnityCliAdmissionPool pool;
        private BridgeQueueState state;
        private string fingerprint;
        private long monotonicNowMs;

        [SetUp]
        public void SetUp()
        {
            pool = new UnityCliAdmissionPool("test-instance", "test-session", 4, 1);
            fingerprint = new string('a', 64);
            monotonicNowMs = 1000L;
            state = UnityCliBridge.CreateQueueStateForTests(pool, () => monotonicNowMs);
        }

        [TearDown]
        public void TearDown()
        {
            state.AssertEmptyForTests();
            Assert.That(state.Sinks.Production, Is.False);
        }

        [Test]
        public void ExactAttachAndCompletionShareOneQueuePoolBoundary()
        {
            var request = Request("queue-race-attach");
            var raw = RequestBytes(request);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            Assert.That(grant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            bool firstCreated;
            var first = Enqueue(request, raw, grant.Token, deadline, out firstCreated);
            Assert.That(firstCreated, Is.True);
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            state.TestHooks.ExpectOnce("before-existing-lookup", () =>
            {
                entered.Set();
                release.Wait();
            });

            UnityCliBridge.BridgeWorkItem attached = null;
            var attachedCreated = true;
            var attachTask = Task.Run(() => attached = Enqueue(request, raw, grant.Token, deadline, out attachedCreated));
            Task<bool> completionTask = null;
            try
            {
                Assert.That(entered.Wait(5000), Is.True);
                completionTask = Task.Run(() => UnityCliBridge.CompleteWorkItemForTests(first, Success(request.RequestId)));
            }
            finally { release.Set(); }

            Assert.That(attachTask.Wait(5000), Is.True);
            Assert.That(completionTask.Wait(5000), Is.True);
            Assert.That(completionTask.Result, Is.True);
            Assert.That(attached, Is.SameAs(first));
            Assert.That(attachedCreated, Is.False);
            Assert.That(first.Completed.IsSet, Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void CompletionFirstRejectsRepublishAndCompletesOnlyOnce()
        {
            var request = Request("queue-race-complete");
            var raw = RequestBytes(request);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            bool ignoredCreated;
            var first = Enqueue(request, raw, grant.Token, deadline, out ignoredCreated);
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            state.TestHooks.ExpectOnce("before-completion-memory", () =>
            {
                entered.Set();
                release.Wait();
            });

            var completionTask = Task.Run(() => UnityCliBridge.CompleteWorkItemForTests(first, Success(request.RequestId)));
            UnityCliBridge.BridgeCommandException failure = null;
            Task duplicateTask = null;
            try
            {
                Assert.That(entered.Wait(5000), Is.True);
                duplicateTask = Task.Run(() =>
                {
                    try { bool created; Enqueue(request, raw, grant.Token, deadline, out created); }
                    catch (UnityCliBridge.BridgeCommandException exception) { failure = exception; }
                });
            }
            finally { release.Set(); }

            Assert.That(completionTask.Wait(5000), Is.True);
            Assert.That(duplicateTask.Wait(5000), Is.True);
            Assert.That(completionTask.Result, Is.True);
            Assert.That(UnityCliBridge.CompleteWorkItemForTests(first, Success(request.RequestId)), Is.False);
            Assert.That(failure, Is.Not.Null);
            Assert.That(failure.Code, Is.EqualTo("E_ADMISSION_IDENTITY_MISMATCH"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void QueueCloseAndSweepPreventOldListenerPublication()
        {
            var request = Request("queue-reload-sweep");
            var raw = RequestBytes(request);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            bool created;
            var first = Enqueue(request, raw, grant.Token, deadline, out created);
            Assert.That(created, Is.True);

            UnityCliBridge.SweepQueuedWorkForTests(state);

            Assert.That(first.Completed.IsSet, Is.True);
            Assert.That(first.Response, Does.Contain("E_UNITY_DOMAIN_RELOAD"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            var exception = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => Enqueue(request, raw, grant.Token, deadline, out created));
            Assert.That(exception.Code, Is.EqualTo("E_UNITY_DOMAIN_RELOAD"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void AdmissionAcquireAndShutdownShareQueuePublicationLinearization()
        {
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            state.TestHooks.ExpectOnce("before-admission-endpoint-acquire", () =>
            {
                entered.Set();
                release.Wait();
            });
            UnityCliAdmissionPool.AcquireResult acquired = null;
            var acquireTask = Task.Run(() => acquired = UnityCliBridge.AcquireAdmissionForTests(state, "endpoint-stop-race", fingerprint));
            Task shutdownTask = null;
            try
            {
                Assert.That(entered.Wait(5000), Is.True);
                shutdownTask = Task.Run(() => UnityCliBridge.ShutdownQueuedWorkForTests(state));
            }
            finally { release.Set(); }

            Assert.That(acquireTask.Wait(5000), Is.True);
            Assert.That(shutdownTask.Wait(5000), Is.True);
            Assert.That(acquired.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            var stale = Assert.Throws<UnityCliBridge.BridgeCommandException>(() =>
                UnityCliBridge.AcquireAdmissionForTests(state, "endpoint-after-stop", fingerprint));
            Assert.That(stale.Code, Is.EqualTo("E_RUNTIME_SESSION_STALE"));
        }

        [Test]
        public void AdmissionShutdownWinningTheRaceReturnsOnlySessionStale()
        {
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            state.TestHooks.ExpectOnce("before-queue-shutdown-close", () =>
            {
                entered.Set();
                release.Wait();
            });
            var shutdownTask = Task.Run(() => UnityCliBridge.ShutdownQueuedWorkForTests(state));
            Task<UnityCliBridge.BridgeCommandException> acquireTask = null;
            try
            {
                Assert.That(entered.Wait(5000), Is.True);
                acquireTask = Task.Run(() =>
                {
                    try
                    {
                        UnityCliBridge.AcquireAdmissionForTests(state, "endpoint-stop-wins", fingerprint);
                        return null;
                    }
                    catch (UnityCliBridge.BridgeCommandException exception) { return exception; }
                });
            }
            finally { release.Set(); }

            Assert.That(shutdownTask.Wait(5000), Is.True);
            Assert.That(acquireTask.Wait(5000), Is.True);
            Assert.That(acquireTask.Result, Is.Not.Null);
            Assert.That(acquireTask.Result.Code, Is.EqualTo("E_RUNTIME_SESSION_STALE"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void DistinctCompletionIntentsSettleConcurrentlyWithoutLostUpdateOrResurrection()
        {
            var firstRequest = Request("journal-concurrent-first");
            var secondRequest = Request("journal-concurrent-second");
            var firstRaw = RequestBytes(firstRequest);
            var secondRaw = RequestBytes(secondRequest);
            var firstDigest = UnityCliRequestDigest.Compute(firstRaw, firstRequest.InstanceId);
            var secondDigest = UnityCliRequestDigest.Compute(secondRaw, secondRequest.InstanceId);
            var firstGrant = pool.Acquire(firstRequest.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            var secondGrant = pool.Acquire(secondRequest.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            Assert.That(pool.AuthorizeAndCapture(firstRequest.RequestId, fingerprint, firstDigest, firstGrant.Token, state.ListenerId, state.ListenerGeneration, Deadline(firstRequest)).Outcome,
                Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            Assert.That(pool.AuthorizeAndCapture(secondRequest.RequestId, fingerprint, secondDigest, secondGrant.Token, state.ListenerId, state.ListenerGeneration, Deadline(secondRequest)).Outcome,
                Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            var firstOwner = UnityCliBridge.CaptureAdmissionOwner(state, firstRequest.RequestId, firstDigest);
            var secondOwner = UnityCliBridge.CaptureAdmissionOwner(state, secondRequest.RequestId, secondDigest);

            var firstPersist = Task.Run(() => state.PersistIntent(firstOwner));
            var secondPersist = Task.Run(() => state.PersistIntent(secondOwner));
            Assert.That(Task.WaitAll(new Task[] { firstPersist, secondPersist }, 5000), Is.True);
            Assert.That(firstPersist.Result, Is.True);
            Assert.That(secondPersist.Result, Is.True);
            Assert.That(state.HasIntent(firstOwner), Is.True);
            Assert.That(state.HasIntent(secondOwner), Is.True);

            var firstComplete = Task.Run(() => UnityCliBridge.CompleteAdmissionRoot(state, firstOwner));
            var secondComplete = Task.Run(() => UnityCliBridge.CompleteAdmissionRoot(state, secondOwner));
            Assert.That(Task.WaitAll(new Task[] { firstComplete, secondComplete }, 5000), Is.True);
            Assert.That(firstComplete.Result, Is.True);
            Assert.That(secondComplete.Result, Is.True);
            Assert.That(state.HasIntent(firstOwner), Is.False);
            Assert.That(state.HasIntent(secondOwner), Is.False);
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(0));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void PublicationCompensationPersistsAndLogsOutsideQueueLock()
        {
            var request = Request("queue-publication-fault");
            var raw = RequestBytes(request);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            var persistOutside = false;
            var logOutside = false;
            state.TestHooks.ExpectOnce("before-queue-publication", () => { throw new InvalidOperationException("injected publication fault"); });
            state.TestHooks.ExpectOnce("before-admission-compensation-persist", () => persistOutside = !UnityCliBridge.QueueLockHeldForTests(state));
            state.TestHooks.ExpectOnce("before-admission-compensation-log", () => logOutside = !UnityCliBridge.QueueLockHeldForTests(state));

            Assert.Throws<InvalidOperationException>(() => { bool created; Enqueue(request, raw, grant.Token, deadline, out created); });

            Assert.That(persistOutside, Is.True);
            Assert.That(logOutside, Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));
        }

        [Test]
        public void PublicationFaultTombstoneBlocksDuplicateBeforeDurableCompensation()
        {
            var request = Request("queue-publication-duplicate-race");
            var raw = RequestBytes(request);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            var tombstoneEntered = new ManualResetEventSlim(false);
            var releaseTombstone = new ManualResetEventSlim(false);
            var intentEntered = new ManualResetEventSlim(false);
            var releaseIntent = new ManualResetEventSlim(false);
            var duplicateBeforeLock = new ManualResetEventSlim(false);
            var duplicateEntered = new ManualResetEventSlim(false);
            var tombstoneTrackedUnderLock = false;
            state.TestHooks.ExpectOnce("before-queue-publication", () => { throw new InvalidOperationException("injected publication fault"); });
            state.TestHooks.ExpectOnce("after-publication-compensation-tombstone", () =>
            {
                tombstoneTrackedUnderLock = UnityCliBridge.QueueLockHeldForTests(state);
                tombstoneEntered.Set();
                releaseTombstone.Wait();
            });
            state.TestHooks.ExpectOnce("before-state-persist-intent", () =>
            {
                intentEntered.Set();
                releaseIntent.Wait();
            });

            var publicationTask = Task.Run<Exception>(() =>
            {
                try
                {
                    bool created;
                    Enqueue(request, raw, grant.Token, deadline, out created);
                    return null;
                }
                catch (Exception exception) { return exception; }
            });
            Task<UnityCliBridge.BridgeCommandException> duplicateTask = null;
            try
            {
                Assert.That(tombstoneEntered.Wait(5000), Is.True);
                Assert.That(tombstoneTrackedUnderLock, Is.True);
                Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(1));
                state.TestHooks.ExpectOnce("before-queue-lock", () => duplicateBeforeLock.Set());
                state.TestHooks.ExpectOnce("before-existing-lookup", () => duplicateEntered.Set());
                duplicateTask = Task.Run<UnityCliBridge.BridgeCommandException>(() =>
                {
                    try
                    {
                        bool created;
                        Enqueue(request, raw, grant.Token, deadline, out created);
                        return null;
                    }
                    catch (UnityCliBridge.BridgeCommandException exception) { return exception; }
                });
                Assert.That(duplicateBeforeLock.Wait(5000), Is.True);
                Assert.That(duplicateEntered.IsSet, Is.False, "duplicate must remain behind QueueLock until the tombstone is visible");
                releaseTombstone.Set();
                Assert.That(intentEntered.Wait(5000), Is.True);
                Assert.That(duplicateEntered.Wait(5000), Is.True);
                Assert.That(duplicateTask.Wait(5000), Is.True);
                Assert.That(duplicateTask.Result, Is.Not.Null);
                Assert.That(duplicateTask.Result.Code, Is.EqualTo("E_EXECUTION_TIMEOUT"));
                Assert.That(pool.Capture().Active, Is.EqualTo(1));
                Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));
                Assert.That(state.WorkQueue, Is.Empty);
            }
            finally
            {
                releaseTombstone.Set();
                releaseIntent.Set();
            }

            Assert.That(publicationTask.Wait(5000), Is.True);
            Assert.That(publicationTask.Result, Is.TypeOf<InvalidOperationException>());
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(0));
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));

            var successorGrant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            Assert.That(successorGrant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            var successor = Enqueue(request, raw, successorGrant.Token, deadline, out var successorCreated);
            Assert.That(successorCreated, Is.True);
            Assert.That(successor, Is.Not.Null);
            UnityCliBridge.ShutdownQueuedWorkForTests(state);
        }

        [Test]
        public void RecoveryBlockedAuthorizationRevokesItsExactGrantBeforeQueueRejection()
        {
            var request = Request("queue-recovery-blocked-authorization");
            var raw = RequestBytes(request);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            Assert.That(grant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            pool.BlockRecovery();

            var failure = Assert.Throws<UnityCliBridge.BridgeCommandException>(() =>
            {
                bool created;
                Enqueue(request, raw, grant.Token, deadline, out created);
            });

            Assert.That(failure.Code, Is.EqualTo("E_ADMISSION_IDENTITY_MISMATCH"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(0));
            pool.Reset();
        }

        [Test]
        public void FirstCompensationIntentWriteFailureRetriesWithoutLosingTheExactOwner()
        {
            var request = Request("queue-compensation-intent-first-write");
            var raw = RequestBytes(request);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            state.TestHooks.ExpectOnce("before-queue-publication", () => { throw new InvalidOperationException("injected publication fault"); });
            state.TestHooks.ExpectOnce("before-state-persist-intent", () => { throw new IOException("injected first intent write fault"); });

            var failure = Assert.Throws<UnityCliBridge.BridgeCommandException>(() =>
            {
                bool created;
                Enqueue(request, raw, grant.Token, deadline, out created);
            });

            Assert.That(failure.Code, Is.EqualTo("E_EXECUTION_TIMEOUT"));
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(1));
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));
            var logsAfterFailure = state.Sinks.LogCount;
            Assert.Throws<UnityCliBridge.BridgeCommandException>(() =>
            {
                bool created;
                Enqueue(request, raw, grant.Token, deadline, out created);
            });
            Assert.That(pool.Capture().Active, Is.EqualTo(1));

            monotonicNowMs += 99L;
            UnityCliBridge.RetryPendingWorkItemSettlementsForTests(state);
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(1));
            Assert.That(state.Sinks.LogCount, Is.EqualTo(logsAfterFailure));
            monotonicNowMs += 1L;
            UnityCliBridge.RetryPendingWorkItemSettlementsForTests(state);

            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(0));
            Assert.That(state.Sinks.TransactionCount, Is.EqualTo(0));
        }

        [Test]
        public void CompensationRetryDoesNotDeletePublishedSuccessorOwner()
        {
            var request = Request("queue-compensation-successor-owner");
            var raw = RequestBytes(request);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            state.TestHooks.ExpectOnce("before-queue-publication", () => { throw new InvalidOperationException("injected publication fault"); });
            state.TestHooks.ExpectOnce("before-admission-compensation-persist", () => { throw new IOException("injected pool persistence fault"); });

            var failure = Assert.Throws<UnityCliBridge.BridgeCommandException>(() =>
            {
                bool created;
                Enqueue(request, raw, grant.Token, deadline, out created);
            });

            Assert.That(failure.Code, Is.EqualTo("E_EXECUTION_TIMEOUT"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(1));
            var successorRequest = Request(request.RequestId);
            successorRequest.Input = new Dictionary<string, object> { { "successor", true } };
            var successorRaw = RequestBytes(successorRequest);
            var successorGrant = pool.Acquire(successorRequest.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            Assert.That(successorGrant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            var successor = Enqueue(successorRequest, successorRaw, successorGrant.Token, Deadline(successorRequest), out var successorCreated);
            Assert.That(successorCreated, Is.True);

            monotonicNowMs += 100L;
            UnityCliBridge.RetryPendingWorkItemSettlementsForTests(state);

            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(1));
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(1));
            Assert.That(UnityCliBridge.PreviewQueuedStartRequestIdsForTests(state), Is.EqualTo(new[] { successorRequest.RequestId }));
            Assert.That(UnityCliBridge.CompleteWorkItemForTests(successor, Success(successorRequest.RequestId)), Is.True);
            monotonicNowMs += 100L;
            UnityCliBridge.RetryPendingWorkItemSettlementsForTests(state);
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(0));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void CompletionBoundaryFaultLogsAndPersistsOutsideQueueLock()
        {
            var request = Request("queue-completion-fault");
            var raw = RequestBytes(request);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            bool created;
            var item = Enqueue(request, raw, grant.Token, deadline, out created);
            var faultObservedInside = false;
            var persistOutside = false;
            var logOutside = false;
            state.TestHooks.ExpectOnce("before-completion-memory", () =>
            {
                faultObservedInside = UnityCliBridge.QueueLockHeldForTests(state);
                throw new InvalidOperationException("injected completion boundary fault");
            });
            state.TestHooks.ExpectOnce("before-queue-boundary-fault-log", () => logOutside = !UnityCliBridge.QueueLockHeldForTests(state));

            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, Success(request.RequestId)), Is.False);

            Assert.That(faultObservedInside, Is.True);
            Assert.That(logOutside, Is.True);
            Assert.That(item.Completed.IsSet, Is.False);
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(1));

            state.TestHooks.ExpectOnce("before-admission-persist", () => persistOutside = !UnityCliBridge.QueueLockHeldForTests(state));
            monotonicNowMs += 100L;
            UnityCliBridge.RetryPendingWorkItemSettlementsForTests(state);

            Assert.That(persistOutside, Is.True);
            Assert.That(item.Completed.IsSet, Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));
        }

        [TestCase("persist")]
        [TestCase("marker-clear")]
        public void DurableSettlementFailureRetainsMapAndSignalUntilRetry(string fault)
        {
            var request = Request("queue-durable-" + fault);
            var raw = RequestBytes(request);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            bool created;
            var item = Enqueue(request, raw, grant.Token, deadline, out created);
            Assert.That(created, Is.True);
            state.TestHooks.ExpectOnce(fault == "persist" ? "before-state-persist-pool" : "before-state-clear-markers",
                () => { throw new IOException("injected durable settlement fault"); });

            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, Success(request.RequestId)), Is.False);

            Assert.That(item.Completed.IsSet, Is.False);
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(1));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(state.Sinks.TransactionCount, Is.EqualTo(0));
            var ioBeforeAttach = state.IoCallbackCount;
            bool duplicateCreated;
            var duplicate = Enqueue(request, raw, grant.Token, deadline, out duplicateCreated);
            Assert.That(state.IoCallbackCount, Is.EqualTo(ioBeforeAttach), "completion-pending attach must not invoke state I/O while QueueLock is held");
            Assert.That(duplicate, Is.SameAs(item));
            Assert.That(duplicateCreated, Is.False);
            Assert.That(item.Completed.IsSet, Is.False);

            monotonicNowMs += 100L;
            UnityCliBridge.RetryPendingWorkItemSettlementsForTests(state);

            Assert.That(item.Completed.IsSet, Is.True);
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));
            Assert.That(state.Sinks.TransactionCount, Is.EqualTo(1));
        }

        [Test]
        public void CompletionIntentWithoutWorkItemRetriesOnlineAtFixedBackoff()
        {
            var request = Request("queue-intent-online");
            var raw = RequestBytes(request);
            var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            var authorized = pool.AuthorizeAndCapture(request.RequestId, fingerprint, digest, grant.Token, state.ListenerId, state.ListenerGeneration, deadline);
            Assert.That(authorized.Outcome, Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            var owner = UnityCliBridge.CaptureAdmissionOwner(state, request.RequestId, digest);
            Assert.That(state.PersistIntent(owner), Is.True);
            Assert.That(pool.CompleteTrusted(authorized.Identity), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted));
            state.TestHooks.ExpectOnce("before-state-clear-intent", () => { throw new IOException("injected intent cleanup fault"); });

            UnityCliBridge.RetryPendingWorkItemSettlementsForTests(state);

            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(1));
            var logsAfterFailure = state.Sinks.LogCount;
            monotonicNowMs += 99L;
            UnityCliBridge.RetryPendingWorkItemSettlementsForTests(state);
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(1));
            Assert.That(state.Sinks.LogCount, Is.EqualTo(logsAfterFailure));
            monotonicNowMs += 1L;
            UnityCliBridge.RetryPendingWorkItemSettlementsForTests(state);
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(0));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void QueuedShutdownUsesCapturedPoolAndSignalsTerminalExactlyOnce()
        {
            var request = Request("queue-shutdown-owner");
            var raw = RequestBytes(request);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            bool created;
            var item = Enqueue(request, raw, grant.Token, deadline, out created);
            Assert.That(created, Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            pool.BlockRecovery();

            UnityCliBridge.ShutdownQueuedWorkForTests(state);

            Assert.That(item.Completed.IsSet, Is.True);
            Assert.That(item.Response, Does.Contain("E_UNITY_QUITTING"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));
            Assert.That(state.PendingSettlementWork, Is.Empty);
            Assert.That(state.Sinks.TransactionCount, Is.EqualTo(1));
            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, Success(request.RequestId)), Is.False);
            Assert.That(state.Sinks.TransactionCount, Is.EqualTo(1));
        }

        [TestCase("internal.test.run.cancel.plan", "test-run", "expectedEditorSessionId")]
        [TestCase("internal.test.run.cancel", "test-run", "expectedEditorSessionId")]
        [TestCase("internal.package.files.claim", "package-resolve", "rootRequestId")]
        [TestCase("internal.package.files.commit", "package-resolve", "rootRequestId")]
        [TestCase("internal.package.files.prove", "package-resolve", "rootRequestId")]
        [TestCase("internal.package.files.fail", "package-resolve", "rootRequestId")]
        [TestCase("internal.navmesh.bake.cancel", "navmesh-bake", "operationId")]
        [TestCase("internal.occlusion.bake.cancel", "occlusion-bake", "operationId")]
        public void DurableControlLaneStartsOnlyExactVerifiedParent(string command, string scope, string selectorKey)
        {
            var parentRequestId = "parent-" + Guid.NewGuid().ToString("N");
            var parentDigest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(parentRequestId), "test-instance");
            var operationId = "operation-" + Guid.NewGuid().ToString("N");
            var root = new Dictionary<string, object>
            {
                { "requestId", parentRequestId }, { "requestDigest", parentDigest },
                { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "operationId", operationId }
            };
            if (scope == "test-run") root["testOperation"] = new Dictionary<string, object>
            {
                { "phase", "running" }, { "runId", Guid.NewGuid().ToString() }
            };
            state.SaveOperation(scope, root);
            Assert.That(state.TryAcquireLease(parentRequestId, parentDigest, out var acquired), Is.True);
            Assert.That(acquired, Is.True);
            var exactValue = selectorKey == "expectedEditorSessionId" ? UnityCliBridge.EditorSessionId()
                : selectorKey == "rootRequestId" ? parentRequestId : operationId;
            var rejected = Request("control-rejected-" + command.Replace('.', '-'));
            rejected.Command = command;
            rejected.Input = new Dictionary<string, object> { { selectorKey, "mismatch" } };
            var exact = Request("control-exact-" + command.Replace('.', '-'));
            exact.Command = command;
            exact.Input = new Dictionary<string, object> { { selectorKey, exactValue } };
            bool created;
            UnityCliBridge.BridgeWorkItem rejectedItem;
            if (command == "internal.test.run.cancel")
            {
                var rejectedRaw = RequestBytes(rejected);
                var rejectedGrant = pool.Acquire(rejected.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
                rejectedItem = Enqueue(rejected, rejectedRaw, rejectedGrant.Token, Deadline(rejected), out created);
                Assert.That(created, Is.True);
                var exactRaw = RequestBytes(exact);
                var exactGrant = pool.Acquire(exact.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
                Enqueue(exact, exactRaw, exactGrant.Token, Deadline(exact), out created);
                Assert.That(created, Is.True);
            }
            else
            {
                rejectedItem = EnqueueBypass(rejected, out created);
                Assert.That(created, Is.True);
                EnqueueBypass(exact, out created);
                Assert.That(created, Is.True);
            }

            var starts = UnityCliBridge.PreviewQueuedStartRequestIdsForTests(state);
            var rejectedIds = UnityCliBridge.RejectTerminalControlWorkForTests(state);

            Assert.That(starts, Is.EqualTo(new[] { exact.RequestId }));
            Assert.That(rejectedIds, Is.EqualTo(new[] { rejected.RequestId }));
            Assert.That(rejectedItem.Completed.IsSet, Is.True);
            Assert.That(rejectedItem.NoEffectRejected, Is.True);
            Assert.That(rejectedItem.Response, Does.Contain("E_ADMISSION_IDENTITY_MISMATCH"));
            Assert.That(pool.Capture().Active, Is.EqualTo(command == "internal.test.run.cancel" ? 1 : 0));
            Assert.That(state.PendingSettlementWork, Is.Empty);
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(0));
            Assert.That(UnityCliBridge.PreviewQueuedStartRequestIdsForTests(state), Is.EqualTo(new[] { exact.RequestId }));
            Assert.That(state.LastControlNodesExamined, Is.EqualTo(1));
            Assert.That(state.LastFifoNodesExamined, Is.EqualTo(1));
            Assert.That(state.LastQueueNodesExamined, Is.EqualTo(2));
            UnityCliBridge.ShutdownQueuedWorkForTests(state);
            state.ClearOperation(scope);
            state.ReleaseLease(parentRequestId, parentDigest);
        }

        [Test]
        public void RejectedTestCancelIdentityMismatchUsesExactNoEffectSettlement()
        {
            var parentRequestId = "parent-no-effect-" + Guid.NewGuid().ToString("N");
            var parentDigest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(parentRequestId), "test-instance");
            var root = new Dictionary<string, object>
            {
                { "requestId", parentRequestId }, { "requestDigest", parentDigest },
                { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "testOperation", new Dictionary<string, object>
                    {
                        { "phase", "running" }, { "runId", Guid.NewGuid().ToString() }
                    }
                }
            };
            state.SaveOperation("test-run", root);
            Assert.That(state.TryAcquireLease(parentRequestId, parentDigest, out var acquired), Is.True);
            Assert.That(acquired, Is.True);
            var rejected = Request("control-no-effect-mismatch");
            rejected.Command = "internal.test.run.cancel";
            rejected.Input = new Dictionary<string, object> { { "expectedEditorSessionId", "mismatch" } };
            var raw = RequestBytes(rejected);
            var grant = pool.Acquire(rejected.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            Assert.That(grant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            bool created;
            var item = Enqueue(rejected, raw, grant.Token, Deadline(rejected), out created);
            Assert.That(created, Is.True);
            var settlementOrder = new List<string>();
            state.TestHooks.ExpectOnce("before-state-persist-intent", () => settlementOrder.Add("intent"));
            state.TestHooks.ExpectOnce("before-completion-memory", () =>
            {
                Assert.That(state.HasIntentInMemory(item.AdmissionOwner), Is.True);
                Assert.That(pool.TryGetConsumedIdentity(rejected.RequestId, out var exactOwner), Is.True);
                Assert.That(exactOwner.OwnerState, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerState.WorkItem));
                settlementOrder.Add("complete");
            });
            state.TestHooks.ExpectOnce("before-admission-persist", () =>
            {
                Assert.That(pool.Capture().Active, Is.EqualTo(0));
                settlementOrder.Add("persist");
            });
            state.TestHooks.ExpectOnce("before-state-clear-markers", () =>
            {
                Assert.That(state.HasIntentInMemory(item.AdmissionOwner), Is.True);
                settlementOrder.Add("markers");
            });
            state.TestHooks.ExpectOnce("before-state-clear-intent", () =>
            {
                Assert.That(state.HasIntentInMemory(item.AdmissionOwner), Is.True);
                settlementOrder.Add("intent-cleanup");
            });

            var rejectedIds = UnityCliBridge.RejectTerminalControlWorkForTests(state);

            Assert.That(rejectedIds, Is.EqualTo(new[] { rejected.RequestId }));
            Assert.That(settlementOrder, Is.EqualTo(new[] { "intent", "complete", "persist", "markers", "intent-cleanup" }));
            Assert.That(item.NoEffectRejected, Is.True);
            Assert.That(item.HasStarted, Is.False);
            Assert.That(item.Completed.IsSet, Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(state.PendingSettlementWork, Is.Empty);
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(0));
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));
            Assert.That(state.OperationExists("test-cancel-admission-owner"), Is.False);
            Assert.That(state.OperationExists("test-run"), Is.True);
            Assert.That(state.TryIsLeaseOwned(parentRequestId, parentDigest, out var parentOwned), Is.True);
            Assert.That(parentOwned, Is.True);
            state.ClearOperation("test-run");
            state.ReleaseLease(parentRequestId, parentDigest);
        }

        [TestCase(UnityCliAdmissionPool.AdmissionOwnerState.Continuation, false)]
        [TestCase(UnityCliAdmissionPool.AdmissionOwnerState.Recovery, false)]
        [TestCase(UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup, false)]
        [TestCase(UnityCliAdmissionPool.AdmissionOwnerState.Coordinator, false)]
        [TestCase(UnityCliAdmissionPool.AdmissionOwnerState.Continuation, true)]
        public void RejectedOrExpiredAttachToExistingDurableOwnerOnlyCompletesItsLocalWorkItem(UnityCliAdmissionPool.AdmissionOwnerState durableState, bool expire)
        {
            var request = Request("attached-durable-no-effect-" + durableState.ToString().ToLowerInvariant());
            request.Command = "internal.test.run.cancel";
            request.Input = new Dictionary<string, object> { { "expectedEditorSessionId", "missing-parent" } };
            var raw = RequestBytes(request);
            var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            var authorized = pool.AuthorizeAndCapture(request.RequestId, fingerprint, digest, grant.Token,
                state.ListenerId, state.ListenerGeneration, deadline);
            Assert.That(authorized.Outcome, Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            var owner = authorized.Identity;
            if (durableState == UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup)
            {
                owner = Transfer(owner, UnityCliAdmissionPool.AdmissionOwnerState.Recovery);
                owner = Transfer(owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
            }
            else owner = Transfer(owner, durableState);
            if (expire) request.Execution.AbsoluteDeadlineUtc = DateTimeOffset.FromUnixTimeMilliseconds(0).ToString("O");

            for (var attempt = 0; attempt < 2; attempt++)
            {
                bool created;
                var item = Enqueue(request, raw, null, deadline, out created);
                Assert.That(created, Is.True);
                Assert.That(item.AdmissionNewlyConsumed, Is.False);
                Assert.That(item.AdmissionOwnerStateAtPublication, Is.EqualTo(durableState));
                Assert.That(item.AttachedToExistingDurableOwner, Is.True);

                var rejected = UnityCliBridge.RejectTerminalControlWorkForTests(state);

                Assert.That(rejected, Is.EqualTo(new[] { request.RequestId }));
                Assert.That(item.Completed.IsSet, Is.True);
                Assert.That(item.NoEffectRejected, Is.True);
                Assert.That(item.Response, Does.Contain(expire ? "deadline elapsed" : "parent is no longer active"));
                Assert.That(item.HasStarted, Is.False);
                Assert.That(state.PendingSettlementWork, Is.Empty);
                Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(0));
                Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));
                Assert.That(pool.TryGetConsumedIdentity(request.RequestId, out var retained), Is.True);
                Assert.That(retained.OwnerState, Is.EqualTo(durableState));
            }
            Assert.That(state.Sinks.TransactionCount, Is.EqualTo(2));
            pool.Reset();
        }

        [TestCase("internal.test.run.cancel")]
        [TestCase("internal.package.files.claim")]
        [TestCase("internal.package.files.commit")]
        [TestCase("internal.package.files.prove")]
        [TestCase("internal.package.files.fail")]
        [TestCase("internal.navmesh.bake.cancel")]
        [TestCase("internal.occlusion.bake.cancel")]
        public void MissingOrExpiredControlReachesTerminalAndAllowsQueueProgress(string command)
        {
            var stale = Request("stale-control-" + command.Replace('.', '-'));
            stale.Command = command;
            stale.Input = new Dictionary<string, object>
            {
                { command.StartsWith("internal.package.files", StringComparison.Ordinal) ? "rootRequestId"
                    : command.StartsWith("internal.test.run", StringComparison.Ordinal) ? "expectedEditorSessionId" : "operationId", "missing-parent" }
            };
            bool created;
            UnityCliBridge.BridgeWorkItem staleItem;
            if (command == "internal.test.run.cancel")
            {
                var raw = RequestBytes(stale);
                var grant = pool.Acquire(stale.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
                staleItem = Enqueue(stale, raw, grant.Token, Deadline(stale), out created);
            }
            else staleItem = EnqueueBypass(stale, out created);
            Assert.That(created, Is.True);
            var ordinary = Request("ordinary-after-stale-" + command.Replace('.', '-'));
            ordinary.Command = "project-tool.list";
            EnqueueBypass(ordinary, out created);
            Assert.That(created, Is.True);

            var rejected = UnityCliBridge.RejectTerminalControlWorkForTests(state);

            Assert.That(rejected, Is.EqualTo(new[] { stale.RequestId }));
            Assert.That(staleItem.Completed.IsSet, Is.True);
            Assert.That(staleItem.NoEffectRejected, Is.True);
            Assert.That(staleItem.HasStarted, Is.False);
            Assert.That(staleItem.Response, Does.Contain("E_EXECUTION_TIMEOUT"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(state.PendingSettlementWork, Is.Empty);
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(0));
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(1));
            Assert.That(state.OperationExists("test-cancel-admission-owner"), Is.False);
            Assert.That(UnityCliBridge.PreviewQueuedStartRequestIdsForTests(state), Is.EqualTo(new[] { ordinary.RequestId }));
            UnityCliBridge.ShutdownQueuedWorkForTests(state);
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));
        }

        [Test]
        public void ExpiredTestCancelSettlesWithoutReadableParentOrPendingSlot()
        {
            var stale = Request("expired-test-cancel-unreadable");
            stale.Command = "internal.test.run.cancel";
            stale.Input = new Dictionary<string, object> { { "expectedEditorSessionId", UnityCliBridge.EditorSessionId() } };
            var raw = RequestBytes(stale);
            var deadline = Deadline(stale);
            var grant = pool.Acquire(stale.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            Assert.That(grant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            bool created;
            var item = Enqueue(stale, raw, grant.Token, deadline, out created);
            Assert.That(created, Is.True);
            // publication后只推进测试持有的request时钟视图，确定性模拟排队期间deadline流逝。
            stale.Execution.AbsoluteDeadlineUtc = DateTimeOffset.FromUnixTimeMilliseconds(0).ToString("O");
            state.TestHooks.ExpectOnce("before-operation-load:test-run", () => { throw new IOException("injected unreadable parent"); });

            var rejected = UnityCliBridge.RejectTerminalControlWorkForTests(state);

            Assert.That(rejected, Is.EqualTo(new[] { stale.RequestId }));
            Assert.That(item.Response, Does.Contain("deadline elapsed"));
            Assert.That(item.NoEffectRejected, Is.True);
            Assert.That(item.HasStarted, Is.False);
            Assert.That(item.Completed.IsSet, Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(state.PendingSettlementWork, Is.Empty);
            Assert.That(state.PendingCompletionIntentCount, Is.EqualTo(0));
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));
            Assert.That(state.OperationExists("test-cancel-admission-owner"), Is.False);
        }

        [Test]
        public void ExpiredControlTerminatesEvenWhenItsParentStateIsUnreadable()
        {
            var stale = Request("expired-control-unreadable");
            stale.Command = "internal.navmesh.bake.cancel";
            stale.Input = new Dictionary<string, object> { { "operationId", "expired-operation" } };
            stale.Execution.AbsoluteDeadlineUtc = DateTimeOffset.FromUnixTimeMilliseconds(0).ToString("O");
            bool created;
            var staleItem = EnqueueBypass(stale, out created);
            Assert.That(created, Is.True);
            state.TestHooks.ExpectOnce("before-operation-load:navmesh-bake", () => { throw new IOException("injected unreadable parent"); });

            var rejected = UnityCliBridge.RejectTerminalControlWorkForTests(state);

            Assert.That(rejected, Is.EqualTo(new[] { stale.RequestId }));
            Assert.That(staleItem.Completed.IsSet, Is.True);
            Assert.That(staleItem.NoEffectRejected, Is.True);
            Assert.That(staleItem.Response, Does.Contain("deadline elapsed"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(state.PendingSettlementWork, Is.Empty);
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));
        }

        [Test]
        public void ProductionQueueTraversalCountsActualLinkedListNodesWithinHardBound()
        {
            for (var index = 0; index < UnityCliBridgeQueuePolicy.MaxFifoNodesExaminedPerTick + 1; index++)
            {
                var request = Request("actual-queue-scan-" + index.ToString());
                request.Command = "project-tool.list";
                var raw = RequestBytes(request);
                var item = new UnityCliBridge.BridgeWorkItem(state, request, raw,
                    UnityCliRequestDigest.Compute(raw, request.InstanceId), state.ListenerId, state.ListenerGeneration,
                    null, UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead, true, false);
                lock (state.QueueLock)
                {
                    state.ActiveRequestWork.Add(request.RequestId, item);
                    item.QueueNode = state.WorkQueue.AddLast(item);
                }
            }

            var starts = UnityCliBridge.PreviewQueuedStartRequestIdsForTests(state);

            Assert.That(state.LastSafeStatusNodesExamined, Is.EqualTo(0));
            Assert.That(state.LastControlNodesExamined, Is.EqualTo(0));
            Assert.That(state.LastFifoNodesExamined, Is.EqualTo(UnityCliBridgeQueuePolicy.MaxFifoNodesExaminedPerTick));
            Assert.That(state.LastQueueNodesExamined, Is.EqualTo(UnityCliBridgeQueuePolicy.MaxFifoNodesExaminedPerTick));
            Assert.That(starts.Length, Is.EqualTo(UnityCliBridgeQueuePolicy.MaxStartsPerTick));
            Assert.That(starts, Does.Not.Contain("actual-queue-scan-128"));
            UnityCliBridge.ShutdownQueuedWorkForTests(state);
        }

        [Test]
        public void SafeStatusBeyondBlockedPrefixStartsWithinBoundAndIndexSurvivesAttachAndClose()
        {
            PublishQueueItem("blocked-mutation", "project-tool.run", UnityCliBridgeQueuePolicy.WorkClass.Mutation);
            for (var index = 0; index < 127; index++)
                PublishQueueItem("pending-control-" + index.ToString(), "internal.navmesh.bake.cancel", UnityCliBridgeQueuePolicy.WorkClass.Control);
            var safeRequest = Request("safe-status-129");
            safeRequest.Command = "internal.editor.compile.status";
            var safeRaw = RequestBytes(safeRequest);
            var safeGrant = pool.Acquire(safeRequest.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            var safe = Enqueue(safeRequest, safeRaw, safeGrant.Token, Deadline(safeRequest), out var safeCreated);
            Assert.That(safeCreated, Is.True);

            var firstTick = UnityCliBridge.TakeQueuedStartsForTests(state, UnityCliBridgeQueuePolicy.DurableMutationBarrier.Unreadable);

            Assert.That(firstTick.Select(item => item.Request.RequestId), Is.EqualTo(new[] { safe.Request.RequestId }));
            Assert.That(state.LastSafeStatusNodesExamined, Is.EqualTo(1));
            Assert.That(state.LastControlNodesExamined, Is.EqualTo(127));
            Assert.That(state.LastFifoNodesExamined, Is.EqualTo(UnityCliBridgeQueuePolicy.MaxFifoNodesExaminedPerTick));
            Assert.That(state.LastQueueNodesExamined, Is.EqualTo(256));
            Assert.That(state.SafeStatusQueue, Is.Empty);
            Assert.That(UnityCliBridge.CompleteWorkItemForTests(safe, Success(safe.Request.RequestId)), Is.True);
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(128));

            var attachedRequest = Request("safe-status-attach");
            attachedRequest.Command = "internal.editor.compile.status";
            var attachedRaw = RequestBytes(attachedRequest);
            var attachedGrant = pool.Acquire(attachedRequest.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            var attachedSafe = Enqueue(attachedRequest, attachedRaw, attachedGrant.Token, Deadline(attachedRequest), out var attachedCreated);
            Assert.That(attachedCreated, Is.True);
            var duplicate = Enqueue(attachedRequest, attachedRaw, attachedGrant.Token, Deadline(attachedRequest), out var created);
            Assert.That(created, Is.False);
            Assert.That(duplicate, Is.SameAs(attachedSafe));
            Assert.That(state.SafeStatusQueue.Count, Is.EqualTo(1));

            UnityCliBridge.ShutdownQueuedWorkForTests(state);

            Assert.That(state.SafeStatusQueue, Is.Empty);
            Assert.That(state.ControlQueue, Is.Empty);
            Assert.That(state.WorkQueue, Is.Empty);
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));
        }

        [Test]
        public void ContinuousSafeStatusCannotHideBoundaryAuthorizedControlFromItsIndependentBudget()
        {
            var parentRequestId = "control-budget-parent-" + Guid.NewGuid().ToString("N");
            var parentDigest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(parentRequestId), "test-instance");
            var operationId = "control-budget-operation-" + Guid.NewGuid().ToString("N");
            state.SaveOperation("navmesh-bake", new Dictionary<string, object>
            {
                { "requestId", parentRequestId }, { "requestDigest", parentDigest },
                { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "operationId", operationId }
            });
            Assert.That(state.TryAcquireLease(parentRequestId, parentDigest, out var acquired), Is.True);
            Assert.That(acquired, Is.True);
            for (var index = 0; index < UnityCliBridgeQueuePolicy.MaxSafeStatusNodesExaminedPerTick; index++)
                PublishQueueItem("control-budget-safe-" + index.ToString(), "internal.editor.compile.status", UnityCliBridgeQueuePolicy.WorkClass.SafeStatus);
            for (var index = 0; index < UnityCliBridgeQueuePolicy.MaxControlNodesExaminedPerTick - 1; index++)
                PublishQueueItem("control-budget-pending-" + index.ToString(), "internal.navmesh.bake.cancel", UnityCliBridgeQueuePolicy.WorkClass.Control);
            var boundary = PublishQueueItem("control-budget-boundary", "internal.navmesh.bake.cancel", UnityCliBridgeQueuePolicy.WorkClass.Control);
            boundary.Request.Input = new Dictionary<string, object> { { "operationId", operationId } };

            var firstTick = UnityCliBridge.PreviewQueuedStartRequestIdsForTests(state);
            for (var index = 0; index < UnityCliBridgeQueuePolicy.MaxSafeStatusNodesExaminedPerTick; index++)
                PublishQueueItem("control-budget-refill-" + index.ToString(), "internal.editor.compile.status", UnityCliBridgeQueuePolicy.WorkClass.SafeStatus);
            var secondTick = UnityCliBridge.PreviewQueuedStartRequestIdsForTests(state);

            Assert.That(firstTick, Does.Contain(boundary.Request.RequestId));
            Assert.That(secondTick, Does.Contain(boundary.Request.RequestId));
            Assert.That(state.LastSafeStatusNodesExamined,
                Is.EqualTo(UnityCliBridgeQueuePolicy.MaxSafeStatusNodesExaminedPerTick));
            Assert.That(state.LastControlNodesExamined,
                Is.EqualTo(UnityCliBridgeQueuePolicy.MaxControlNodesExaminedPerTick));
            Assert.That(state.LastFifoNodesExamined,
                Is.EqualTo(UnityCliBridgeQueuePolicy.MaxFifoNodesExaminedPerTick));
            Assert.That(state.LastQueueNodesExamined,
                Is.EqualTo(UnityCliBridgeQueuePolicy.MaxNodesExaminedPerTick));
            UnityCliBridge.ShutdownQueuedWorkForTests(state);
            state.ClearOperation("navmesh-bake");
            state.ReleaseLease(parentRequestId, parentDigest);
        }

        [Test]
        public void SuccessorIdentityMismatchBlocksFrozenCompletionUntilSuccessorEnds()
        {
            var request = Request("queue-successor-mismatch");
            var raw = RequestBytes(request);
            var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
            bool created;
            var item = Enqueue(request, raw, grant.Token, deadline, out created);
            Assert.That(created, Is.True);
            UnityCliAdmissionPool.ConsumedIdentity successor = null;
            state.TestHooks.ExpectOnce("before-completion-memory", () =>
            {
                UnityCliAdmissionPool.ConsumedIdentity current;
                Assert.That(pool.TryGetConsumedIdentity(request.RequestId, out current), Is.True);
                Assert.That(pool.CompleteTrusted(current), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted));
                var nextGrant = pool.Acquire(request.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
                Assert.That(nextGrant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
                var authorized = pool.AuthorizeAndCapture(request.RequestId, fingerprint, digest, nextGrant.Token, state.ListenerId, state.ListenerGeneration, deadline);
                Assert.That(authorized.Outcome, Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
                successor = authorized.Identity;
            });

            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, Success(request.RequestId)), Is.False);

            Assert.That(successor, Is.Not.Null);
            Assert.That(item.Completed.IsSet, Is.False);
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(1));
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(pool.CompleteTrusted(successor), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted));
            monotonicNowMs += 100L;
            UnityCliBridge.RetryPendingWorkItemSettlementsForTests(state);

            Assert.That(item.Completed.IsSet, Is.True);
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(state), Is.EqualTo(0));
        }

        [Test]
        public void WorkerClassificationUsesOnlyImmutableInput()
        {
            var requests = new[]
            {
                ProjectToolRequest("worker-rigidbody", "physics.rigidbody", new Dictionary<string, object> { { "target", "MissingTarget" } }),
                ProjectToolRequest("worker-reflection", "graphics.reflection-probe", new Dictionary<string, object> { { "target", "MissingTarget" }, { "create", true } }),
                ProjectToolRequest("worker-light-probes", "graphics.light-probe-group", new Dictionary<string, object> { { "target", "MissingTarget" }, { "create", true } }),
                ProjectToolRequest("worker-scene-read", "scene.active", new Dictionary<string, object>())
            };

            var task = Task.Run(() => requests.Select(UnityCliBridge.QueueRequestWorkClass).ToArray());

            Assert.That(task.Wait(5000), Is.True);
            Assert.That(task.Result, Is.All.EqualTo(UnityCliBridgeQueuePolicy.WorkClass.Mutation));
        }

        [Test]
        public void ListenerGenerationNeverStartsAtZeroAndDoesNotFork()
        {
            Assert.That(UnityCliBridge.ComputeListenerGenerationForTests(0, 0), Is.EqualTo(1));
            Assert.That(UnityCliBridge.ComputeListenerGenerationForTests(0, 7), Is.EqualTo(8));
            Assert.That(UnityCliBridge.ComputeListenerGenerationForTests(9, 99), Is.EqualTo(9));
        }

        [Test]
        public void RegisteredInternalCommandsUseTheClosedProductionClassification()
        {
            var classes = UnityCliBridge.InternalQueueClassesForTests();
            foreach (var command in new[]
            {
                "internal.editor.compile.start", "internal.editor.refresh.start", "internal.asset.refresh.start",
                "internal.package.embed.start", "internal.package.resolve.start", "internal.test.run.start",
                "internal.navmesh.bake.start", "internal.navmesh.bake.plan",
                "internal.occlusion.bake.start", "internal.occlusion.bake.plan",
                "internal.build.run.start", "internal.instances.prune"
            }) AssertClass(classes, command, UnityCliBridgeQueuePolicy.WorkClass.Mutation);
            foreach (var command in new[]
            {
                "internal.test.run.cancel.plan", "internal.test.run.cancel", "internal.package.files.claim",
                "internal.package.files.commit", "internal.package.files.prove", "internal.package.files.fail",
                "internal.navmesh.bake.cancel", "internal.occlusion.bake.cancel"
            }) AssertClass(classes, command, UnityCliBridgeQueuePolicy.WorkClass.Control);
            foreach (var command in new[] { "internal.navmesh.bake.snapshot", "internal.occlusion.bake.snapshot" })
                AssertClass(classes, command, UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead);
            foreach (var command in new[]
            {
                "internal.editor.compile.status", "internal.editor.refresh.status", "internal.editor.state.snapshot",
                "internal.asset.refresh.status", "internal.package.embed.status", "internal.package.resolve.status",
                "internal.test.run.status", "internal.test.run.cancel.status", "internal.navmesh.bake.status",
                "internal.occlusion.bake.status"
            }) AssertClass(classes, command, UnityCliBridgeQueuePolicy.WorkClass.SafeStatus);

            var unknown = Request("classification-unknown");
            unknown.Command = "internal.unknown.effect";
            Assert.That(UnityCliBridge.QueueRequestWorkClass(unknown), Is.EqualTo(UnityCliBridgeQueuePolicy.WorkClass.Mutation));
        }

        private static void AssertClass(IReadOnlyDictionary<string, UnityCliBridgeQueuePolicy.WorkClass> classes, string command, UnityCliBridgeQueuePolicy.WorkClass expected)
        {
            Assert.That(classes.ContainsKey(command), Is.True, command);
            Assert.That(classes[command], Is.EqualTo(expected), command);
            var request = Request("classification-" + command.Replace('.', '-'));
            request.Command = command;
            Assert.That(UnityCliBridge.QueueRequestWorkClass(request), Is.EqualTo(expected), command);
        }

        private UnityCliAdmissionPool.ConsumedIdentity Transfer(UnityCliAdmissionPool.ConsumedIdentity owner, UnityCliAdmissionPool.AdmissionOwnerState target)
        {
            Assert.That(pool.PrepareOwnerTransfer(owner, target), Is.True);
            Assert.That(pool.CommitOwnerTransfer(owner, target, out var successor), Is.True);
            return successor;
        }

        private UnityCliBridge.BridgeWorkItem PublishQueueItem(string requestId, string command, UnityCliBridgeQueuePolicy.WorkClass workClass)
        {
            var request = Request(requestId);
            request.Command = command;
            if (workClass == UnityCliBridgeQueuePolicy.WorkClass.Control)
                request.Input = new Dictionary<string, object> { { "operationId", requestId } };
            var raw = RequestBytes(request);
            var item = new UnityCliBridge.BridgeWorkItem(state, request, raw,
                UnityCliRequestDigest.Compute(raw, request.InstanceId), state.ListenerId, state.ListenerGeneration,
                null, workClass, true, false);
            lock (state.QueueLock)
            {
                state.ActiveRequestWork.Add(request.RequestId, item);
                item.QueueNode = state.WorkQueue.AddLast(item);
                if (workClass == UnityCliBridgeQueuePolicy.WorkClass.SafeStatus)
                    item.SafeStatusNode = state.SafeStatusQueue.AddLast(item);
                if (workClass == UnityCliBridgeQueuePolicy.WorkClass.Control)
                {
                    item.ControlNode = state.ControlQueue.AddLast(item);
                    if (state.ControlScanCursor == null) state.ControlScanCursor = item.ControlNode;
                }
            }
            return item;
        }

        private UnityCliBridge.BridgeWorkItem Enqueue(BridgeRequest request, byte[] raw, string token, long deadline, out bool created)
        {
            var reservation = new UnityCliBridge.AdmissionQueueReservation
            {
                RequestId = request.RequestId,
                RequestDigest = UnityCliRequestDigest.Compute(raw, request.InstanceId),
                RootFingerprint = fingerprint,
                Token = token,
                ExecutionDeadlineMs = deadline,
                HasAdmissionHeaders = true,
                IsBypass = false
            };
            int depth;
            return UnityCliBridge.EnqueuePreparedForTests(request, raw, reservation, state, out created, out depth);
        }

        private UnityCliBridge.BridgeWorkItem EnqueueBypass(BridgeRequest request, out bool created)
        {
            var raw = RequestBytes(request);
            var reservation = new UnityCliBridge.AdmissionQueueReservation
            {
                RequestId = request.RequestId,
                RequestDigest = UnityCliRequestDigest.Compute(raw, request.InstanceId),
                HasAdmissionHeaders = false,
                IsBypass = true
            };
            int depth;
            return UnityCliBridge.EnqueuePreparedForTests(request, raw, reservation, state, out created, out depth);
        }

        private static BridgeRequest ProjectToolRequest(string requestId, string toolName, Dictionary<string, object> input)
        {
            var request = Request(requestId);
            request.Command = "project-tool.run";
            request.Input = new Dictionary<string, object> { { "name", toolName }, { "input", input } };
            return request;
        }

        private static BridgeRequest Request(string requestId)
        {
            return new BridgeRequest
            {
                ProtocolVersion = 2,
                InstanceId = "test-instance",
                Command = "internal.editor.state.snapshot",
                RequestId = requestId,
                Input = new Dictionary<string, object>(),
                Execution = new BridgeExecution
                {
                    AbsoluteDeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
                    CompletionContractVersion = 1,
                    RequestDigestVersion = 1
                }
            };
        }

        private static byte[] RequestBytes(BridgeRequest request)
        {
            return Encoding.UTF8.GetBytes(UnityCliSerializer.Serialize(request));
        }

        private static long Deadline(BridgeRequest request)
        {
            return DateTimeOffset.Parse(request.Execution.AbsoluteDeadlineUtc).ToUnixTimeMilliseconds();
        }

        private static string Success(string requestId)
        {
            return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(requestId, new Dictionary<string, object> { { "terminal", true } }));
        }
    }
}
#endif
