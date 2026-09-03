using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace UnityCli.Tests
{
    public sealed class UnityCliAdmissionPoolTests
    {
        private const string InstanceId = "unity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string SessionId = "session-test";
        private const string ListenerId = "listener-test";

        [Test]
        public void NewSessionStartsOpenAndEmpty()
        {
            var snapshot = Pool().Capture();
            Assert.That(snapshot.Active, Is.EqualTo(0));
            Assert.That(snapshot.GateClosed, Is.False);
            Assert.That(snapshot.RecoveryBlocked, Is.False);
            Assert.That(snapshot.Records, Is.Empty);
        }

        [Test]
        public void HighLowWatermarkClosesAtFiveAndReopensOnlyAtThree()
        {
            var pool = Pool();
            var grants = Enumerable.Range(0, 5).Select(index => pool.Acquire(Request(index), Fingerprint(index), ListenerId, 1)).ToArray();
            Assert.That(grants.All(result => result.Outcome == UnityCliAdmissionPool.AcquireOutcome.Granted), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(5));
            Assert.That(pool.Capture().GateClosed, Is.True);

            Assert.That(pool.Revoke(Request(0), Fingerprint(0), grants[0].Token), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(4));
            Assert.That(pool.Acquire(Request(5), Fingerprint(5), ListenerId, 1).Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Closed));
            Assert.That(pool.Revoke(Request(1), Fingerprint(1), grants[1].Token), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(3));
            Assert.That(pool.Capture().GateClosed, Is.False);
            Assert.That(pool.Acquire(Request(5), Fingerprint(5), ListenerId, 1).Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            Assert.That(pool.Acquire(Request(6), Fingerprint(6), ListenerId, 1).Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            Assert.That(pool.Capture().Active, Is.EqualTo(5));
        }

        [Test]
        public void GrantedHasOnlyAuthorizeOrRevokeExit()
        {
            var pool = Pool();
            var revoked = pool.Acquire(Request(0), Fingerprint(0), ListenerId, 1);
            Assert.That(pool.Revoke(Request(0), Fingerprint(0), revoked.Token), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));

            var granted = pool.Acquire(Request(1), Fingerprint(1), ListenerId, 1);
            var consumed = pool.AuthorizeAndCapture(Request(1), Fingerprint(1), Digest(1), granted.Token, ListenerId, 1, 5000);
            Assert.That(consumed.Outcome, Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            Assert.That(consumed.Identity.OwnerState, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerState.WorkItem));
            Assert.That(pool.Revoke(Request(1), Fingerprint(1), granted.Token), Is.False);
        }

        [Test]
        public void AuthorizeAndCaptureIsAtomicAndCompensationIsExactAndIdempotent()
        {
            var pool = Pool();
            var grant = pool.Acquire(Request(0), Fingerprint(0), ListenerId, 1);
            var result = pool.AuthorizeAndCapture(Request(0), Fingerprint(0), Digest(0), grant.Token, ListenerId, 1, 5000);
            Assert.That(result.Outcome, Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            Assert.That(result.NewlyConsumed, Is.True);
            Assert.That(result.Identity.OwnerState, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerState.WorkItem));
            var replay = pool.AuthorizeAndCapture(Request(0), Fingerprint(0), Digest(0), grant.Token, ListenerId, 1, 5000);
            Assert.That(replay.Outcome, Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.AlreadyConsumed));
            Assert.That(replay.Identity.RequestDigest, Is.EqualTo(result.Identity.RequestDigest));
            Assert.That(replay.Identity.Token, Is.EqualTo(result.Identity.Token));
            Assert.That(replay.Identity.OwnerState, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerState.WorkItem));
            Assert.That(pool.AuthorizeAndCapture(Request(0), Fingerprint(0), Digest(9), grant.Token, ListenerId, 1, 5000).Outcome, Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.IdentityMismatch));
            Assert.That(pool.CompensateAuthorization(result.Identity), Is.True);
            Assert.That(pool.CompensateAuthorization(result.Identity), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));

            var successor = Consume(pool, 0);
            Assert.That(pool.CompensateAuthorization(result.Identity), Is.False);
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(pool.CompleteTrusted(successor), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted));
        }

        [Test]
        public void FullOwnerTransitionTableRejectsEveryIllegalEdge()
        {
            var states = new[]
            {
                UnityCliAdmissionPool.AdmissionOwnerState.WorkItem,
                UnityCliAdmissionPool.AdmissionOwnerState.Coordinator,
                UnityCliAdmissionPool.AdmissionOwnerState.Continuation,
                UnityCliAdmissionPool.AdmissionOwnerState.Recovery,
                UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup
            };
            var legal = new HashSet<string>(StringComparer.Ordinal)
            {
                Edge(UnityCliAdmissionPool.AdmissionOwnerState.WorkItem, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator),
                Edge(UnityCliAdmissionPool.AdmissionOwnerState.WorkItem, UnityCliAdmissionPool.AdmissionOwnerState.Continuation),
                Edge(UnityCliAdmissionPool.AdmissionOwnerState.WorkItem, UnityCliAdmissionPool.AdmissionOwnerState.Recovery),
                Edge(UnityCliAdmissionPool.AdmissionOwnerState.WorkItem, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup),
                Edge(UnityCliAdmissionPool.AdmissionOwnerState.Coordinator, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup),
                Edge(UnityCliAdmissionPool.AdmissionOwnerState.Recovery, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup)
            };

            foreach (var source in states)
            foreach (var target in states)
            {
                var pool = Pool();
                var current = OwnerAt(pool, source);
                var expected = source == target || legal.Contains(Edge(source, target));
                Assert.That(pool.PrepareOwnerTransfer(current, target), Is.EqualTo(expected), Edge(source, target));
                if (!expected || source == target) continue;
                UnityCliAdmissionPool.ConsumedIdentity successor;
                Assert.That(pool.CommitOwnerTransfer(current, target, out successor), Is.True, Edge(source, target));
                Assert.That(successor.OwnerState, Is.EqualTo(target));
                Assert.That(pool.PrepareOwnerTransfer(successor, target), Is.True, "idempotent prepare " + Edge(source, target));
                UnityCliAdmissionPool.ConsumedIdentity repeated;
                Assert.That(pool.CommitOwnerTransfer(successor, target, out repeated), Is.True, "idempotent commit " + Edge(source, target));
                Assert.That(repeated.OwnerState, Is.EqualTo(target));
            }
        }

        [Test]
        public void OnlyWorkItemContinuationAndTerminalCleanupCanComplete()
        {
            foreach (var state in new[] { UnityCliAdmissionPool.AdmissionOwnerState.WorkItem, UnityCliAdmissionPool.AdmissionOwnerState.Continuation, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup })
            {
                var pool = Pool();
                var owner = OwnerAt(pool, state);
                Assert.That(pool.CompleteTrusted(owner), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted), state.ToString());
                Assert.That(pool.CompleteTrusted(owner), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.DefinitelyAbsent), "idempotent " + state);
            }
            foreach (var state in new[] { UnityCliAdmissionPool.AdmissionOwnerState.Coordinator, UnityCliAdmissionPool.AdmissionOwnerState.Recovery })
            {
                var pool = Pool();
                Assert.That(pool.CompleteTrusted(OwnerAt(pool, state)), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.IdentityMismatchOrBlocked), state.ToString());
                Assert.That(pool.Capture().Active, Is.EqualTo(1));
            }
        }

        [Test]
        public void TransferAndCompletionRequireCurrentFullIdentity()
        {
            var pool = Pool();
            var owner = Consume(pool, 0);
            var staleToken = Consume(Pool(), 0);
            Assert.That(pool.PrepareOwnerTransfer(staleToken, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator), Is.False);
            Assert.That(pool.CompleteTrusted(staleToken), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.IdentityMismatchOrBlocked));

            UnityCliAdmissionPool.ConsumedIdentity coordinator;
            Assert.That(pool.PrepareOwnerTransfer(owner, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator), Is.True);
            Assert.That(pool.CommitOwnerTransfer(owner, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator, out coordinator), Is.True);
            Assert.That(pool.PrepareOwnerTransfer(owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup), Is.False);
            Assert.That(pool.CompleteTrusted(owner), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.IdentityMismatchOrBlocked));
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
        }

        [Test]
        public void FrozenTerminalIdentityCannotDeleteSameRequestIdSuccessor()
        {
            var pool = Pool();
            var oldIdentity = Consume(pool, 0);
            Assert.That(pool.CompleteTrusted(oldIdentity), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted));
            var successor = Consume(pool, 0, 1);
            Assert.That(pool.CompleteTrusted(oldIdentity), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.IdentityMismatchOrBlocked));
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(pool.CompleteTrusted(successor), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted));
        }

        [Test]
        public void GrantTtlUsesMonotonicClockAndStaleGenerationRevokesWithoutConsumption()
        {
            long monotonic = 1000;
            long utc = 100000;
            var pool = new UnityCliAdmissionPool(InstanceId, SessionId, 5, 3, () => monotonic, () => utc);
            var stale = pool.Acquire(Request(0), Fingerprint(0), ListenerId, 7);
            Assert.That(stale.Token, Has.Length.EqualTo(64));
            Assert.That(pool.AuthorizeAndCapture(Request(0), Fingerprint(0), Digest(0), stale.Token, ListenerId, 8, 200000).Outcome, Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.IdentityMismatch));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));

            pool.Acquire(Request(1), Fingerprint(1), ListenerId, 8);
            utc = -100000;
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            monotonic = 3001;
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void MaxPlusTwoGrantTimeoutsReturnToBaseline()
        {
            long now = 1000;
            var pool = Pool(() => now);
            var outcomes = Enumerable.Range(0, 7).Select(index => pool.Acquire(Request(index), Fingerprint(index), ListenerId, 1).Outcome).ToArray();
            Assert.That(outcomes.Count(value => value == UnityCliAdmissionPool.AcquireOutcome.Granted), Is.EqualTo(5));
            Assert.That(outcomes.Count(value => value == UnityCliAdmissionPool.AcquireOutcome.Closed), Is.EqualTo(2));
            now = 3001;
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(pool.Capture().GateClosed, Is.False);
        }

        [Test]
        public void MaxPlusTwoPreQueueTimeoutsReturnToBaselineOrOneDurableOwner()
        {
            var pool = Pool();
            var baseline = pool.Capture().Active;
            for (var index = 0; index < 7; index++)
            {
                var grant = pool.Acquire(Request(index), Fingerprint(index), ListenerId, 1);
                if (grant.Outcome != UnityCliAdmissionPool.AcquireOutcome.Granted) continue;
                var authorization = pool.AuthorizeAndCapture(Request(index), Fingerprint(index), Digest(index), grant.Token, ListenerId, 1, 5000);
                Assert.That(authorization.Outcome, Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
                Assert.That(pool.CompensateAuthorization(authorization.Identity), Is.True);
                Assert.That(pool.Capture().Active, Is.EqualTo(baseline));
            }

            var durable = Consume(pool, 20);
            UnityCliAdmissionPool.ConsumedIdentity coordinator;
            Assert.That(pool.PrepareOwnerTransfer(durable, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator), Is.True);
            Assert.That(pool.CommitOwnerTransfer(durable, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator, out coordinator), Is.True);
            for (var index = 21; index < 28; index++)
            {
                var grant = pool.Acquire(Request(index), Fingerprint(index), ListenerId, 1);
                if (grant.Outcome != UnityCliAdmissionPool.AcquireOutcome.Granted) continue;
                var authorization = pool.AuthorizeAndCapture(Request(index), Fingerprint(index), Digest(index), grant.Token, ListenerId, 1, 5000);
                Assert.That(pool.CompensateAuthorization(authorization.Identity), Is.True);
            }
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(pool.Capture().Records.Single().OwnerState, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerState.Coordinator));
        }

        [Test]
        public void TransferIntentSnapshotsConvergeBeforeAndAfterPoolTransfer()
        {
            var source = Pool();
            var workItem = Consume(source, 0);
            var beforeIntent = source.CaptureRecovery();
            Assert.That(beforeIntent.Records.Single().TransferCommitted, Is.True);

            Assert.That(source.PrepareOwnerTransfer(workItem, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator), Is.True);
            var afterIntent = source.CaptureRecovery();
            Assert.That(afterIntent.Records.Single().OwnerState, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerState.WorkItem));
            Assert.That(afterIntent.Records.Single().IntendedOwnerState, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerState.Coordinator));
            Assert.That(afterIntent.Records.Single().TransferCommitted, Is.False);

            UnityCliAdmissionPool.ConsumedIdentity committed;
            Assert.That(source.CommitOwnerTransfer(workItem, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator, out committed), Is.True);
            Assert.That(source.CaptureRecovery().Records.Single().TransferCommitted, Is.True);

            var beforeIntentRestore = Pool();
            beforeIntentRestore.Restore(beforeIntent.Records, beforeIntent.Active, beforeIntent.GateClosed, beforeIntent.RecoveryDigest, beforeIntent.ConfiguredMaxActive, beforeIntent.ReopenAt);
            UnityCliAdmissionPool.ConsumedIdentity unchanged;
            Assert.That(beforeIntentRestore.TryGetConsumedIdentity(Request(0), out unchanged), Is.True);
            Assert.That(unchanged.OwnerState, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerState.WorkItem));

            // intent落盘后与pool已转移但commit marker未落盘都恢复同一pre-transfer snapshot。
            foreach (var faultPoint in new[] { "after-intent-save", "after-pool-transfer-before-commit-save" })
            {
                var restored = Pool();
                restored.Restore(afterIntent.Records, afterIntent.Active, afterIntent.GateClosed, afterIntent.RecoveryDigest, afterIntent.ConfiguredMaxActive, afterIntent.ReopenAt);
                UnityCliAdmissionPool.ConsumedIdentity recoveredWorkItem;
                Assert.That(restored.TryGetConsumedIdentity(Request(0), out recoveredWorkItem), Is.True, faultPoint);
                Assert.That(restored.PrepareOwnerTransfer(recoveredWorkItem, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator), Is.True, faultPoint);
                UnityCliAdmissionPool.ConsumedIdentity recoveredCoordinator;
                Assert.That(restored.CommitOwnerTransfer(recoveredWorkItem, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator, out recoveredCoordinator), Is.True, faultPoint);
                Assert.That(recoveredCoordinator.OwnerState, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerState.Coordinator), faultPoint);
            }
        }

        [TestCase(UnityCliAdmissionPool.AdmissionOwnerState.Coordinator)]
        [TestCase(UnityCliAdmissionPool.AdmissionOwnerState.Continuation)]
        [TestCase(UnityCliAdmissionPool.AdmissionOwnerState.Recovery)]
        [TestCase(UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup)]
        public void PendingTransferCannotCompleteOrDeleteTheWorkItemOwner(UnityCliAdmissionPool.AdmissionOwnerState intended)
        {
            var pool = Pool();
            var owner = Consume(pool, 0);
            Assert.That(pool.PrepareOwnerTransfer(owner, intended), Is.True);
            Assert.That(pool.CompleteTrusted(owner), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.IdentityMismatchOrBlocked));
            var pending = pool.Capture().Records.Single();
            Assert.That(pending.OwnerState, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerState.WorkItem));
            Assert.That(pending.IntendedOwnerState, Is.EqualTo(intended));
            Assert.That(pending.TransferCommitted, Is.False);
            UnityCliAdmissionPool.ConsumedIdentity successor;
            Assert.That(pool.CommitOwnerTransfer(owner, intended, out successor), Is.True);
            Assert.That(successor.OwnerState, Is.EqualTo(intended));
            var expected = intended == UnityCliAdmissionPool.AdmissionOwnerState.Continuation
                || intended == UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup
                ? UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted
                : UnityCliAdmissionPool.CompletionOutcome.IdentityMismatchOrBlocked;
            Assert.That(pool.CompleteTrusted(successor), Is.EqualTo(expected));
        }

        [Test]
        public void RecoverySnapshotExcludesGrantsAndRemainsBounded()
        {
            var pool = Pool();
            Consume(pool, 0);
            pool.Acquire(Request(1), Fingerprint(1), ListenerId, 1);
            pool.Acquire(Request(2), Fingerprint(2), ListenerId, 1);
            var recovery = pool.CaptureRecovery();
            Assert.That(recovery.Active, Is.EqualTo(1));
            Assert.That(recovery.Records.All(record => record.Consumed), Is.True);
            Assert.That(recovery.Active, Is.LessThanOrEqualTo(recovery.ConfiguredMaxActive));
        }

        [Test]
        public void RestorePreservesHysteresisExactAttachAndPendingTransfer()
        {
            var source = Pool();
            var owners = Enumerable.Range(0, 5).Select(index => Consume(source, index)).ToArray();
            Assert.That(source.CompleteTrusted(owners[0]), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted));
            var before = source.CaptureRecovery();
            Assert.That(before.Active, Is.EqualTo(4));
            Assert.That(before.GateClosed, Is.True);

            var restored = Pool();
            restored.Restore(before.Records, before.Active, before.GateClosed, before.RecoveryDigest, before.ConfiguredMaxActive, before.ReopenAt);
            Assert.That(restored.Capture().Active, Is.EqualTo(4));
            Assert.That(restored.Capture().GateClosed, Is.True);
            Assert.That(restored.RecoverConsumed(Request(1), Fingerprint(1), Digest(1), "listener-after-reload", 2, 5000), Is.True);
            Assert.That(restored.AttachConsumed(Request(1), Fingerprint(1), Digest(1), "listener-after-reload", 2, 5000), Is.True);
            Assert.That(restored.Acquire(Request(6), Fingerprint(6), "listener-after-reload", 2).Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Closed));
        }

        [Test]
        public void RestoreReopensExactlyAtThreeAfterFiveOwnerHysteresis()
        {
            var source = Pool();
            var owners = Enumerable.Range(0, 5).Select(index => Consume(source, index)).ToArray();
            var snapshot = source.CaptureRecovery();
            Assert.That(snapshot.Active, Is.EqualTo(5));
            Assert.That(snapshot.GateClosed, Is.True);
            var restored = Pool();
            restored.Restore(snapshot.Records, snapshot.Active, snapshot.GateClosed, snapshot.RecoveryDigest, snapshot.ConfiguredMaxActive, snapshot.ReopenAt);
            Assert.That(restored.CompleteTrusted(owners[0]), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted));
            Assert.That(restored.Capture().Active, Is.EqualTo(4));
            Assert.That(restored.Capture().GateClosed, Is.True);
            Assert.That(restored.CompleteTrusted(owners[1]), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted));
            Assert.That(restored.Capture().Active, Is.EqualTo(3));
            Assert.That(restored.Capture().GateClosed, Is.False);
            Assert.That(restored.Acquire(Request(5), Fingerprint(5), ListenerId, 1).Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
        }

        [Test]
        public void NewSessionRejectsRestoredMechanismState()
        {
            var source = Pool();
            Consume(source, 0);
            var snapshot = source.CaptureRecovery();
            var restarted = new UnityCliAdmissionPool(InstanceId, "session-new", 5, 3, () => 1000, () => 1000);
            Assert.Throws<InvalidOperationException>(() => restarted.Restore(snapshot.Records, snapshot.Active, snapshot.GateClosed, snapshot.RecoveryDigest, snapshot.ConfiguredMaxActive, snapshot.ReopenAt));
            Assert.That(restarted.Capture().RecoveryBlocked, Is.True);
        }

        [Test]
        public void CorruptRestoreKeepsPriorStateAndBlocksAdmission()
        {
            var pool = Pool();
            Consume(pool, 0);
            var before = pool.CaptureRecovery();
            var corrupt = before.Records.Select(record => new UnityCliAdmissionPool.AdmissionRecord
            {
                InstanceId = "another-instance", SessionId = record.SessionId, RequestId = record.RequestId, RootFingerprint = record.RootFingerprint,
                RequestDigest = record.RequestDigest, Token = record.Token, ListenerId = record.ListenerId, ListenerGeneration = record.ListenerGeneration,
                GrantExpiresAtMs = record.GrantExpiresAtMs, ExecutionDeadlineMs = record.ExecutionDeadlineMs,
                OwnerState = record.OwnerState, IntendedOwnerState = record.IntendedOwnerState, TransferCommitted = record.TransferCommitted
            }).ToArray();
            Assert.Throws<InvalidOperationException>(() => pool.Restore(corrupt, before.Active, true, before.RecoveryDigest, before.ConfiguredMaxActive, before.ReopenAt));
            Assert.That(pool.Capture().RecoveryBlocked, Is.True);
            Assert.That(pool.Acquire(Request(1), Fingerprint(1), ListenerId, 1).Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.RecoveryBlocked));
        }

        [Test]
        public void ExactRootIdentityIncludesListenerRebindingAndDeadline()
        {
            var pool = Pool();
            var owner = Consume(pool, 0);
            var wrongDeadline = UnityCliAdmissionPool.CreateConsumedIdentity(owner.RequestId, owner.RootFingerprint, owner.RequestDigest, owner.Token, owner.SessionId, owner.OwnerState, owner.ListenerId, owner.ListenerGeneration, owner.ExecutionDeadlineMs + 1);

            Assert.That(pool.RecoverConsumed(owner.RequestId, owner.RootFingerprint, owner.RequestDigest, "listener-after-reload", 2, owner.ExecutionDeadlineMs), Is.True);
            Assert.That(pool.InspectExactIdentityFields(owner), Is.EqualTo(UnityCliAdmissionPool.ExactIdentityOutcome.IdentityMismatchOrBlocked));
            Assert.That(pool.InspectExactIdentityFields(wrongDeadline), Is.EqualTo(UnityCliAdmissionPool.ExactIdentityOutcome.IdentityMismatchOrBlocked));
            UnityCliAdmissionPool.ConsumedIdentity rebound;
            Assert.That(pool.TryGetConsumedIdentity(owner.RequestId, out rebound), Is.True);
            Assert.That(pool.InspectExactIdentityFields(rebound), Is.EqualTo(UnityCliAdmissionPool.ExactIdentityOutcome.ExactCurrent));
            Assert.That(rebound.ListenerId, Is.EqualTo("listener-after-reload"));
            Assert.That(rebound.ListenerGeneration, Is.EqualTo(2));
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
        }

        [Test]
        public void RestoreConsumedRequiresTheExactTransferSnapshot()
        {
            var source = Pool();
            var owner = Consume(source, 0);
            Assert.That(source.PrepareOwnerTransfer(owner, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator), Is.True);
            var pending = source.Capture().Records.Single();
            var restored = Pool();

            Assert.That(restored.RestoreConsumed(owner, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator, false), Is.True);
            Assert.That(restored.RestoreConsumed(owner, UnityCliAdmissionPool.AdmissionOwnerState.Recovery, false), Is.False);
            Assert.That(restored.RestoreConsumed(owner, UnityCliAdmissionPool.AdmissionOwnerState.WorkItem, true), Is.False);
            Assert.That(pending.TransferCommitted, Is.False);
            Assert.That(restored.Capture().Active, Is.EqualTo(1));
        }

        [Test]
        public void ConcurrentAcquireNeverExceedsConfiguredMaximum()
        {
            var pool = Pool();
            var outcomes = new ConcurrentBag<UnityCliAdmissionPool.AcquireOutcome>();
            Parallel.For(0, 50, index => outcomes.Add(pool.Acquire(Request(index), Fingerprint(index), ListenerId, 1).Outcome));
            var snapshot = pool.Capture();
            Assert.That(snapshot.Active, Is.EqualTo(5));
            Assert.That(snapshot.Records.Count, Is.EqualTo(5));
            Assert.That(snapshot.GateClosed, Is.True);
            Assert.That(outcomes.Count(value => value == UnityCliAdmissionPool.AcquireOutcome.Granted), Is.EqualTo(5));
        }

        private static UnityCliAdmissionPool.ConsumedIdentity Consume(UnityCliAdmissionPool pool, int index, int fingerprintIndex = -1)
        {
            var fingerprint = Fingerprint(fingerprintIndex < 0 ? index : fingerprintIndex);
            var grant = pool.Acquire(Request(index), fingerprint, ListenerId, 1);
            Assert.That(grant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            var authorization = pool.AuthorizeAndCapture(Request(index), fingerprint, Digest(index), grant.Token, ListenerId, 1, 5000);
            Assert.That(authorization.Outcome, Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            return authorization.Identity;
        }

        private static UnityCliAdmissionPool.ConsumedIdentity OwnerAt(UnityCliAdmissionPool pool, UnityCliAdmissionPool.AdmissionOwnerState state)
        {
            var owner = Consume(pool, 0);
            if (state == UnityCliAdmissionPool.AdmissionOwnerState.WorkItem) return owner;
            if (state == UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup)
                return Transfer(pool, owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
            return Transfer(pool, owner, state);
        }

        private static UnityCliAdmissionPool.ConsumedIdentity Transfer(UnityCliAdmissionPool pool, UnityCliAdmissionPool.ConsumedIdentity owner, UnityCliAdmissionPool.AdmissionOwnerState target)
        {
            Assert.That(pool.PrepareOwnerTransfer(owner, target), Is.True);
            UnityCliAdmissionPool.ConsumedIdentity successor;
            Assert.That(pool.CommitOwnerTransfer(owner, target, out successor), Is.True);
            return successor;
        }

        private static UnityCliAdmissionPool Pool(Func<long> now = null)
        {
            var clock = now ?? delegate { return 1000L; };
            return new UnityCliAdmissionPool(InstanceId, SessionId, 5, 3, clock, clock);
        }
        private static string Edge(UnityCliAdmissionPool.AdmissionOwnerState source, UnityCliAdmissionPool.AdmissionOwnerState target) { return source + "->" + target; }
        private static string Request(int index) { return "req-" + index.ToString(); }
        private static string Fingerprint(int index) { return index.ToString("x64"); }
        private static string Digest(int index) { return (index + 1000).ToString("x64"); }
    }
}
