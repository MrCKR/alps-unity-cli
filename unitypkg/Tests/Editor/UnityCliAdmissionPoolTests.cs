using System;
using System.Collections.Concurrent;
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
            Assert.That(pool.Capture().GateClosed, Is.True);
            Assert.That(pool.Acquire(Request(5), Fingerprint(5), ListenerId, 1).Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Closed));

            Assert.That(pool.Revoke(Request(1), Fingerprint(1), grants[1].Token), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(3));
            Assert.That(pool.Capture().GateClosed, Is.False);
            Assert.That(pool.Acquire(Request(5), Fingerprint(5), ListenerId, 1).Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            Assert.That(pool.Acquire(Request(6), Fingerprint(6), ListenerId, 1).Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            Assert.That(pool.Capture().Active, Is.EqualTo(5));
            Assert.That(pool.Capture().GateClosed, Is.True);
        }

        [Test]
        public void SameIdentityAttachesWithoutCountingAndDifferentFingerprintFailsClosed()
        {
            var pool = Pool();
            var first = pool.Acquire(Request(0), Fingerprint(0), ListenerId, 1);
            var attached = pool.Acquire(Request(0), Fingerprint(0), ListenerId, 1);
            var conflict = pool.Acquire(Request(0), Fingerprint(1), ListenerId, 1);
            Assert.That(attached.Token, Is.EqualTo(first.Token));
            Assert.That(conflict.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.IdentityMismatch));
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
        }

        [Test]
        public void TokenIs256BitConsumedOnceAndOldGenerationIsAtomicallyRevoked()
        {
            long now = 1000;
            var pool = Pool(() => now);
            var stale = pool.Acquire(Request(0), Fingerprint(0), ListenerId, 7);
            Assert.That(stale.Token, Has.Length.EqualTo(64));
            Assert.That(stale.Token.All(Uri.IsHexDigit), Is.True);
            Assert.That(pool.Authorize(Request(0), Fingerprint(0), stale.Token, ListenerId, 8, 5000), Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.IdentityMismatch));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(pool.Authorize(Request(0), Fingerprint(0), stale.Token, ListenerId, 7, 5000), Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Missing));

            var grant = pool.Acquire(Request(0), Fingerprint(0), ListenerId, 8);
            Assert.That(pool.Authorize(Request(0), Fingerprint(0), grant.Token, ListenerId, 8, 5000), Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            Assert.That(pool.Authorize(Request(0), Fingerprint(0), grant.Token, ListenerId, 8, 5000), Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.AlreadyConsumed));
            var consumed = pool.Capture().Records.Single();
            Assert.That(consumed.State, Is.EqualTo(UnityCliAdmissionPool.AdmissionState.Consumed));
            Assert.That(consumed.InstanceId, Is.EqualTo(InstanceId));
            Assert.That(consumed.SessionId, Is.EqualTo(SessionId));
            Assert.That(pool.AttachConsumed(Request(0), Fingerprint(0), ListenerId, 8, 5000), Is.True);
            Assert.That(pool.AttachConsumed(Request(0), Fingerprint(0), "listener-new", 9, 5000), Is.False);
            Assert.That(pool.RecoverConsumed(Request(0), Fingerprint(0), "listener-new", 9, 5000), Is.True);
            Assert.That(pool.AttachConsumed(Request(0), Fingerprint(0), "listener-new", 9, 5000), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
        }

        [Test]
        public void GrantTtlUsesMonotonicClockIndependentlyFromUtcDeadline()
        {
            long monotonic = 1000;
            long utc = 100000;
            var pool = new UnityCliAdmissionPool(InstanceId, SessionId, 5, 3, () => monotonic, () => utc);
            pool.Acquire(Request(0), Fingerprint(0), ListenerId, 1);
            utc = -100000;
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            monotonic = 3001;
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void UnconsumedTokenExpiresOrRevokesAndReleasesSlot()
        {
            long now = 1000;
            var pool = Pool(() => now);
            var expired = pool.Acquire(Request(0), Fingerprint(0), ListenerId, 1);
            now = 3001;
            Assert.That(pool.Authorize(Request(0), Fingerprint(0), expired.Token, ListenerId, 1, 5000), Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Missing));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            var revoked = pool.Acquire(Request(1), Fingerprint(1), ListenerId, 2);
            pool.RevokeUnconsumed(ListenerId, 2);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(pool.Authorize(Request(1), Fingerprint(1), revoked.Token, ListenerId, 2, 5000), Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Missing));
        }

        [Test]
        public void StateTransitionsUseSeparateTerminalAndRevokePaths()
        {
            var pool = Pool();
            var grant = pool.Acquire(Request(0), Fingerprint(0), ListenerId, 1);
            Assert.That(pool.CompleteTrusted(Request(0), Fingerprint(0), grant.Token, SessionId, UnityCliAdmissionPool.AdmissionOwnerKind.WorkItem), Is.False);
            Assert.That(pool.Revoke(Request(0), Fingerprint(0), grant.Token), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));

            grant = pool.Acquire(Request(1), Fingerprint(1), ListenerId, 1);
            Assert.That(pool.Authorize(Request(1), Fingerprint(1), grant.Token, ListenerId, 1, 5000), Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            Assert.That(pool.Revoke(Request(1), Fingerprint(1), grant.Token), Is.False);
            Assert.That(pool.CompleteTrusted(Request(1), Fingerprint(1), grant.Token, SessionId, UnityCliAdmissionPool.AdmissionOwnerKind.WorkItem), Is.True);
            Assert.That(pool.CompleteTrusted(Request(1), Fingerprint(1), grant.Token, SessionId, UnityCliAdmissionPool.AdmissionOwnerKind.WorkItem), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void OwnerTransferMakesPreviousWorkItemIdentityUnableToComplete()
        {
            var pool = Pool();
            var grant = pool.Acquire(Request(0), Fingerprint(0), ListenerId, 1);
            Assert.That(pool.Authorize(Request(0), Fingerprint(0), grant.Token, ListenerId, 1, 5000), Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            UnityCliAdmissionPool.ConsumedIdentity workItem;
            Assert.That(pool.TryGetConsumedIdentity(Request(0), out workItem), Is.True);
            Assert.That(workItem.OwnerKind, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerKind.WorkItem));
            Assert.That(pool.TransferOwner(workItem.RequestId, workItem.RootFingerprint, workItem.Token, workItem.SessionId, UnityCliAdmissionPool.AdmissionOwnerKind.Coordinator), Is.True);
            Assert.That(pool.CompleteTrusted(workItem.RequestId, workItem.RootFingerprint, workItem.Token, workItem.SessionId, UnityCliAdmissionPool.AdmissionOwnerKind.WorkItem), Is.False);
            Assert.That(pool.CompleteTrusted(workItem.RequestId, workItem.RootFingerprint, workItem.Token, workItem.SessionId, UnityCliAdmissionPool.AdmissionOwnerKind.Coordinator), Is.True);
        }

        [Test]
        public void FrozenTerminalIdentityCannotDeleteSameRequestIdSuccessor()
        {
            var pool = Pool();
            var oldGrant = pool.Acquire(Request(0), Fingerprint(0), ListenerId, 1);
            Assert.That(pool.Authorize(Request(0), Fingerprint(0), oldGrant.Token, ListenerId, 1, 5000), Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            UnityCliAdmissionPool.ConsumedIdentity oldIdentity;
            Assert.That(pool.TryGetConsumedIdentity(Request(0), out oldIdentity), Is.True);
            Assert.That(pool.CompleteTrusted(oldIdentity.RequestId, oldIdentity.RootFingerprint, oldIdentity.Token, oldIdentity.SessionId, oldIdentity.OwnerKind), Is.True);

            var successor = pool.Acquire(Request(0), Fingerprint(1), ListenerId, 1);
            Assert.That(pool.Authorize(Request(0), Fingerprint(1), successor.Token, ListenerId, 1, 5000), Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            Assert.That(pool.CompleteTrusted(oldIdentity.RequestId, oldIdentity.RootFingerprint, oldIdentity.Token, oldIdentity.SessionId, oldIdentity.OwnerKind), Is.False);
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(pool.CompleteTrusted(Request(0), Fingerprint(1), successor.Token, SessionId, UnityCliAdmissionPool.AdmissionOwnerKind.WorkItem), Is.True);
        }

        [Test]
        public void ValidRestorePreservesClosedHysteresisAndExactAttach()
        {
            var source = Pool();
            var grants = Enumerable.Range(0, 5).Select(index => source.Acquire(Request(index), Fingerprint(index), ListenerId, 1)).ToArray();
            for (var index = 0; index < grants.Length; index++) Assert.That(source.Authorize(Request(index), Fingerprint(index), grants[index].Token, ListenerId, 1, 5000), Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            Assert.That(source.CompleteTrusted(Request(0), Fingerprint(0), grants[0].Token, SessionId, UnityCliAdmissionPool.AdmissionOwnerKind.WorkItem), Is.True);
            var before = source.Capture();
            Assert.That(before.Active, Is.EqualTo(4));
            Assert.That(before.GateClosed, Is.True);

            var restored = Pool();
            restored.Restore(before.Records, before.Active, before.GateClosed, before.RecoveryDigest, before.ConfiguredMaxActive, before.ReopenAt);
            Assert.That(restored.Capture().Active, Is.EqualTo(4));
            Assert.That(restored.Capture().GateClosed, Is.True);
            var reacquire = restored.Acquire(Request(1), Fingerprint(1), "listener-after-reload", 2);
            Assert.That(reacquire.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.AlreadyConsumed));
            Assert.That(reacquire.Token, Is.Null);
            Assert.That(restored.RecoverConsumed(Request(1), Fingerprint(1), "listener-after-reload", 2, 5000), Is.True);
            Assert.That(restored.AttachConsumed(Request(1), Fingerprint(1), "listener-after-reload", 2, 5000), Is.True);
            Assert.That(restored.Capture().Active, Is.EqualTo(4));
            Assert.That(restored.Acquire(Request(6), Fingerprint(6), "listener-after-reload", 2).Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Closed));
        }

        [Test]
        public void SmallerConfigurationRecoversExistingRootsAndStaysClosedUntilNewReopenAt()
        {
            var source = new UnityCliAdmissionPool(InstanceId, SessionId, 7, 5, () => 1000, () => 1000);
            var grants = Enumerable.Range(0, 6).Select(index => source.Acquire(Request(index), Fingerprint(index), ListenerId, 1)).ToArray();
            for (var index = 0; index < grants.Length; index++) Assert.That(source.Authorize(Request(index), Fingerprint(index), grants[index].Token, ListenerId, 1, 5000), Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            var before = source.Capture();

            var restored = Pool();
            restored.Restore(before.Records, before.Active, before.GateClosed, before.RecoveryDigest, before.ConfiguredMaxActive, before.ReopenAt);
            Assert.That(restored.Capture().Active, Is.EqualTo(6));
            Assert.That(restored.Capture().MaxActive, Is.EqualTo(6));
            Assert.That(restored.Capture().ConfiguredMaxActive, Is.EqualTo(5));
            Assert.That(restored.Capture().GateClosed, Is.True);
            Assert.That(restored.CompleteTrusted(Request(0), Fingerprint(0), grants[0].Token, SessionId, UnityCliAdmissionPool.AdmissionOwnerKind.WorkItem), Is.True);
            Assert.That(restored.CompleteTrusted(Request(1), Fingerprint(1), grants[1].Token, SessionId, UnityCliAdmissionPool.AdmissionOwnerKind.WorkItem), Is.True);
            Assert.That(restored.Capture().Active, Is.EqualTo(4));
            Assert.That(restored.Capture().GateClosed, Is.True);
            Assert.That(restored.CompleteTrusted(Request(2), Fingerprint(2), grants[2].Token, SessionId, UnityCliAdmissionPool.AdmissionOwnerKind.WorkItem), Is.True);
            Assert.That(restored.Capture().Active, Is.EqualTo(3));
            Assert.That(restored.Capture().GateClosed, Is.False);
            Assert.That(restored.Capture().MaxActive, Is.EqualTo(5));
        }

        [Test]
        public void CorruptRestoreKeepsPriorStateAndBlocksAdmission()
        {
            var pool = Pool();
            var grant = pool.Acquire(Request(0), Fingerprint(0), ListenerId, 1);
            Assert.That(pool.Authorize(Request(0), Fingerprint(0), grant.Token, ListenerId, 1, 5000), Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            var before = pool.Capture();
            var corrupt = before.Records.Select(record => new UnityCliAdmissionPool.AdmissionRecord
            {
                InstanceId = "another-instance", SessionId = record.SessionId, RequestId = record.RequestId, RootFingerprint = record.RootFingerprint,
                Token = record.Token, ListenerId = record.ListenerId, ListenerGeneration = record.ListenerGeneration,
                GrantExpiresAtMs = record.GrantExpiresAtMs, ExecutionDeadlineMs = record.ExecutionDeadlineMs, State = record.State
            }).ToArray();

            Assert.Throws<InvalidOperationException>(() => pool.Restore(corrupt, before.Active, true, before.RecoveryDigest, before.ConfiguredMaxActive, before.ReopenAt));
            var blocked = pool.Capture();
            Assert.That(blocked.RecoveryBlocked, Is.True);
            Assert.That(blocked.GateClosed, Is.True);
            Assert.That(blocked.Active, Is.EqualTo(before.Active));
            Assert.That(pool.Acquire(Request(1), Fingerprint(1), ListenerId, 1).Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.RecoveryBlocked));

            var countMismatch = Pool();
            Assert.Throws<InvalidOperationException>(() => countMismatch.Restore(before.Records, before.Active + 1, false, before.RecoveryDigest, before.ConfiguredMaxActive, before.ReopenAt));
            Assert.That(countMismatch.Capture().RecoveryBlocked, Is.True);

            var digestMismatch = Pool();
            Assert.Throws<InvalidOperationException>(() => digestMismatch.Restore(before.Records, before.Active, before.GateClosed, DifferentDigest(before.RecoveryDigest), before.ConfiguredMaxActive, before.ReopenAt));
            Assert.That(digestMismatch.Acquire(Request(2), Fingerprint(2), ListenerId, 1).Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.RecoveryBlocked));
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

        private static UnityCliAdmissionPool Pool(Func<long> now = null)
        {
            var clock = now ?? delegate { return 1000L; };
            return new UnityCliAdmissionPool(InstanceId, SessionId, 5, 3, clock, clock);
        }
        private static string Request(int index) { return "req-" + index.ToString(); }
        private static string DifferentDigest(string value) { return (value[0] == '0' ? "1" : "0") + value.Substring(1); }
        private static string Fingerprint(int index) { return index.ToString("x64"); }
    }
}
