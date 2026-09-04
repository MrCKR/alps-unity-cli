#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace UnityCli.Tests
{
    public sealed class UnityCliRuntimeBridgeIntegrationTests
    {
        private const string ListenerId = "runtime-test-listener";
        private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private GameObject host;
        private UnityCliRuntimeBridge bridge;
        private UnityCliAdmissionPool pool;
        private long utcNowMs;
        private long monotonicNowMs;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("UnityCli.RuntimeBridge.Test");
            bridge = host.AddComponent<UnityCliRuntimeBridge>();
            bridge.enabled = false;
            utcNowMs = 1000000L;
            monotonicNowMs = 5000L;
            bridge.InjectUtcNowProviderForTests(() => utcNowMs);
            bridge.InjectMonotonicNowProviderForTests(() => monotonicNowMs);
            pool = new UnityCliAdmissionPool("runtime-test-instance", "runtime-test-session", 256, 64, null, () => utcNowMs);
            SetField("admissionPool", pool);
            SetField("listenerId", ListenerId);
            SetField("sessionId", "runtime-test-session");
        }

        [TearDown]
        public void TearDown()
        {
            if (bridge != null)
            {
                bridge.StopRuntimeRequestsLocked();
                bridge.AssertRuntimeBoundariesDrainedForTests();
                bridge.ResetClockProvidersForTests();
            }
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
            bridge = null;
            host = null;
            pool = null;
        }

        [Test]
        public void PublicationAndShutdownShareOneProductionBoundary()
        {
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            bridge.ExpectRuntimeBoundaryForTests("before-runtime-publish", () =>
            {
                entered.Set();
                release.Wait();
            });
            UnityCliRuntimeBridge.RuntimePublicationResult publication = null;
            var publishTask = Task.Run(() => publication = PublishBypass("runtime-race"));
            Task stopTask = null;
            try
            {
                Assert.That(entered.Wait(5000), Is.True);
                stopTask = Task.Run(() => bridge.StopRuntimeRequestsLocked());
            }
            finally { release.Set(); }

            Assert.That(publishTask.Wait(5000), Is.True);
            Assert.That(stopTask.Wait(5000), Is.True);
            Assert.That(publication.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
            Assert.That(publication.Work.Completion.Task.Wait(1000), Is.True);
            Assert.That(publication.Work.Completion.Task.Result, Does.Contain("E_RUNTIME_SESSION_STALE"));
            Assert.That(PublishBypass("runtime-after-stop").Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Stopping));
        }

        [Test]
        public void RuntimeAdmissionAcquireAndStopShareThePublicationBoundary()
        {
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            bridge.ExpectRuntimeBoundaryForTests("before-runtime-admission-acquire", () =>
            {
                entered.Set();
                release.Wait();
            });
            UnityCliAdmissionPool.AcquireResult acquired = null;
            var acquireResult = false;
            var acquireTask = Task.Run(() =>
            {
                UnityCliAdmissionPool.AcquireResult local;
                acquireResult = bridge.TryAcquireRuntimeAdmissionForTests("runtime-admission-stop", Fingerprint, out local);
                acquired = local;
            });
            Task stopTask = null;
            try
            {
                Assert.That(entered.Wait(5000), Is.True);
                stopTask = Task.Run(() => bridge.StopRuntimeRequestsLocked());
            }
            finally { release.Set(); }

            Assert.That(acquireTask.Wait(5000), Is.True);
            Assert.That(stopTask.Wait(5000), Is.True);
            Assert.That(acquireResult, Is.True);
            Assert.That(acquired.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(bridge.TryAcquireRuntimeAdmissionForTests("runtime-admission-after-stop", Fingerprint, out acquired), Is.False);
            Assert.That(acquired, Is.Null);
        }

        [Test]
        public void RuntimeStopWinningAdmissionRaceReturnsOnlySessionStale()
        {
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            bridge.ExpectRuntimeBoundaryForTests("before-runtime-stop", () =>
            {
                entered.Set();
                release.Wait();
            });
            var stopTask = Task.Run(() => bridge.StopRuntimeRequestsLocked());
            var acquireResult = true;
            UnityCliAdmissionPool.AcquireResult acquired = null;
            Task acquireTask = null;
            try
            {
                Assert.That(entered.Wait(5000), Is.True);
                acquireTask = Task.Run(() =>
                {
                    UnityCliAdmissionPool.AcquireResult local;
                    acquireResult = bridge.TryAcquireRuntimeAdmissionForTests("runtime-stop-wins", Fingerprint, out local);
                    acquired = local;
                });
            }
            finally { release.Set(); }

            Assert.That(stopTask.Wait(5000), Is.True);
            Assert.That(acquireTask.Wait(5000), Is.True);
            Assert.That(acquireResult, Is.False);
            Assert.That(acquired, Is.Null);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void RecoveryBlockedAuthorizationRevokesItsExactUnconsumedGrant()
        {
            var requestId = "runtime-blocked-authorization";
            var request = Request(requestId, "runtime-tool.run");
            var digest = Digest('2');
            var deadline = Deadline();
            var admission = Admission(requestId, digest, deadline);
            pool.BlockRecovery();

            var publication = bridge.PublishRuntimeRequest(request, deadline, digest, admission);

            Assert.That(publication.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Rejected));
            Assert.That(publication.Error, Does.Contain("E_ADMISSION_IDENTITY_MISMATCH"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void UnauthorizedCapacityAndPublicationFaultDoNotLeakAdmissionOrLoseExistingCache()
        {
            var first = PublishBypass("runtime-first");
            Assert.That(first.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));

            var unauthorizedRequest = Request("runtime-unauthorized", "runtime-tool.run");
            var unauthorized = bridge.PublishRuntimeRequest(unauthorizedRequest, Deadline(), Digest('b'), new UnityCliRuntimeBridge.RuntimeAdmissionInput());
            Assert.That(unauthorized.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Rejected));
            Assert.That(unauthorized.Error, Does.Contain("E_ADMISSION_REQUIRED"));
            Assert.That(PublishBypass("runtime-first").Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Existing));

            var faultRequest = Request("runtime-publication-fault", "runtime-tool.run");
            var faultDigest = Digest('c');
            var faultAdmission = Admission(faultRequest["requestId"] as string, faultDigest, Deadline());
            bridge.ExpectRuntimeBoundaryForTests("before-runtime-publish", () => { throw new InvalidOperationException("injected publication fault"); });
            Assert.Throws<InvalidOperationException>(() => bridge.PublishRuntimeRequest(faultRequest, Deadline(), faultDigest, faultAdmission));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(PublishBypass("runtime-first").Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Existing));

            for (var index = 1; index < 128; index++)
                Assert.That(PublishBypass("runtime-capacity-" + index).Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
            var fullRequest = Request("runtime-capacity-rejected", "runtime-tool.run");
            var fullDigest = Digest('d');
            var full = bridge.PublishRuntimeRequest(fullRequest, Deadline(), fullDigest, Admission("runtime-capacity-rejected", fullDigest, Deadline()));
            Assert.That(full.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.QueueFull));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(PublishBypass("runtime-first").Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Existing));
        }

        [Test]
        public void CompensationFaultRetainsExactOwnerUntilBoundedOnlineRecovery()
        {
            var requestId = "runtime-compensation-fault";
            var request = Request(requestId, "runtime-tool.run");
            var digest = Digest('7');
            var deadline = Deadline();
            var admission = Admission(requestId, digest, deadline);
            bridge.ExpectRuntimeBoundaryForTests("before-runtime-publish", () => { throw new InvalidOperationException("injected publication fault"); });
            bridge.ExpectRuntimeBoundaryForTests("before-runtime-compensation", () =>
            {
                pool.BlockRecovery();
                throw new InvalidOperationException("injected compensation fault");
            });

            var publication = bridge.PublishRuntimeRequest(request, deadline, digest, admission);

            Assert.That(publication.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Rejected));
            Assert.That(publication.Work, Is.Not.Null);
            Assert.That(publication.Work.Completion.Task.IsCompleted, Is.False);
            Assert.That(bridge.PendingRuntimeSettlementCountForTests(), Is.EqualTo(1));
            var duplicate = bridge.PublishRuntimeRequest(request, deadline, digest, new UnityCliRuntimeBridge.RuntimeAdmissionInput());
            Assert.That(duplicate.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Existing));
            Assert.That(duplicate.Work, Is.SameAs(publication.Work));

            pool.Reset();
            monotonicNowMs += 100L;
            bridge.RetryPendingRuntimeSettlementsForTests();

            Assert.That(publication.Work.Completion.Task.IsCompleted, Is.True);
            Assert.That(publication.Work.Completion.Task.Result, Does.Contain("E_RUNTIME_BRIDGE_FAILED"));
            Assert.That(bridge.PendingRuntimeSettlementCountForTests(), Is.EqualTo(0));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void CompletedCacheEvictionCommitsOnlyAfterProductionEnqueue()
        {
            for (var index = 0; index < 160; index++)
            {
                var publication = PublishBypass("runtime-cache-" + index);
                Assert.That(publication.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
                var running = TakeNext();
                Assert.That(running, Is.SameAs(publication.Work));
                Assert.That(Finalize(running, "terminal-" + index, 1), Is.True);
            }
            bridge.ExpectRuntimeBoundaryForTests("before-runtime-publish", () => { throw new InvalidOperationException("injected eviction publication fault"); });
            Assert.Throws<InvalidOperationException>(() => PublishBypass("runtime-cache-new"));
            Assert.That(PublishBypass("runtime-cache-0").Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Existing));

            Assert.That(PublishBypass("runtime-cache-new").Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
            Assert.That(PublishBypass("runtime-cache-0").Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
        }

        [Test]
        public void ExpiredReplayIsLinearizedBeforeCacheMissFreshness()
        {
            var requestId = "runtime-expired-replay";
            var digest = DigestFor(requestId);
            var originalDeadline = utcNowMs + 500L;
            var originalRequest = Request(requestId, "runtime-tool.list", originalDeadline);
            var seeded = bridge.PublishRuntimeRequest(originalRequest, originalDeadline, digest, new UnityCliRuntimeBridge.RuntimeAdmissionInput());
            Assert.That(seeded.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
            var terminalWork = TakeNext();
            Assert.That(terminalWork, Is.SameAs(seeded.Work));
            Assert.That(Finalize(terminalWork, "expired-existing-terminal", 1), Is.True);
            utcNowMs = originalDeadline + 1L;
            Assert.That(utcNowMs, Is.GreaterThanOrEqualTo(originalDeadline));

            var exactTask = Task.Run(() => bridge.PublishRuntimeRequest(originalRequest, originalDeadline, digest, new UnityCliRuntimeBridge.RuntimeAdmissionInput()));
            var mismatchTask = Task.Run(() => bridge.PublishRuntimeRequest(originalRequest, originalDeadline, Digest('f'), new UnityCliRuntimeBridge.RuntimeAdmissionInput()));

            Assert.That(Task.WaitAll(new Task[] { exactTask, mismatchTask }, 5000), Is.True);
            Assert.That(exactTask.Result.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Existing));
            Assert.That(exactTask.Result.Work, Is.SameAs(seeded.Work));
            Assert.That(exactTask.Result.Work.Completion.Task.Result, Is.EqualTo("expired-existing-terminal"));
            Assert.That(mismatchTask.Result.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Rejected));
            Assert.That(mismatchTask.Result.Error, Does.Contain("E_REQUEST_REPLAY_MISMATCH"));

            var expired = utcNowMs - 1L;
            var miss = bridge.PublishRuntimeRequest(Request("runtime-expired-miss", "runtime-tool.list", expired), expired, DigestFor("runtime-expired-miss"), new UnityCliRuntimeBridge.RuntimeAdmissionInput());
            Assert.That(miss.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Rejected));
            Assert.That(miss.Error, Does.Contain("E_SCHEMA_INVALID"));
            Assert.That(bridge.PublishRuntimeRequest(originalRequest, originalDeadline, digest, new UnityCliRuntimeBridge.RuntimeAdmissionInput()).Outcome,
                Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Existing));
        }

        [Test]
        public void RunningRequestDeadlineReturnsTimeoutWithoutWaitingForTerminalOwner()
        {
            var deadline = utcNowMs + 10L;
            var request = Request("runtime-running-deadline", "runtime-tool.run", deadline);
            var digest = Digest('9');
            var publication = bridge.PublishRuntimeRequest(request, deadline, digest, Admission("runtime-running-deadline", digest, deadline));
            Assert.That(publication.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
            var running = TakeNext();
            var waits = 0;
            var response = bridge.AwaitRuntimeResponseForTests(running, new UnityCliRuntimeBridge.RuntimeWaitDependencies(
                () => utcNowMs,
                (completion, timeoutMs) => { waits++; utcNowMs = deadline + 1L; return false; },
                () => false));

            Assert.That(response, Does.Contain("E_EXECUTION_TIMEOUT"));
            Assert.That(waits, Is.EqualTo(1));
            Assert.That(running.Completion.Task.IsCompleted, Is.False);
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(Finalize(running, "running-deadline-terminal", 1), Is.True);
            Assert.That(running.Completion.Task.Result, Is.EqualTo("running-deadline-terminal"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void BlockedCompletionRetainsTerminalAndExactDuplicateUntilOwnerIsDefinitelyAbsent()
        {
            var requestId = "runtime-blocked-completion";
            var request = Request(requestId, "runtime-tool.run");
            var digest = Digest('8');
            var deadline = Deadline();
            var publication = bridge.PublishRuntimeRequest(request, deadline, digest, Admission(requestId, digest, deadline));
            var running = TakeNext();
            pool.BlockRecovery();

            Assert.That(Finalize(running, "blocked-terminal", 1), Is.False);
            Assert.That(running.Completion.Task.IsCompleted, Is.False);
            Assert.That(bridge.PendingRuntimeSettlementCountForTests(), Is.EqualTo(1));
            var duplicate = bridge.PublishRuntimeRequest(request, deadline, digest, new UnityCliRuntimeBridge.RuntimeAdmissionInput());
            Assert.That(duplicate.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Existing));
            Assert.That(duplicate.Work, Is.SameAs(running));

            pool.Reset();
            monotonicNowMs += 100L;
            bridge.RetryPendingRuntimeSettlementsForTests();

            Assert.That(running.Completion.Task.Result, Is.EqualTo("blocked-terminal"));
            Assert.That(bridge.PendingRuntimeSettlementCountForTests(), Is.EqualTo(0));
        }

        [Test]
        public void RecoveryBlockedFrozenTerminalSettlesDuringShutdownWithoutAPump()
        {
            var requestId = "runtime-blocked-frozen-shutdown";
            var request = Request(requestId, "runtime-tool.run");
            var digest = Digest('3');
            var deadline = Deadline();
            var publication = bridge.PublishRuntimeRequest(request, deadline, digest, Admission(requestId, digest, deadline));
            var running = TakeNext();
            pool.BlockRecovery();
            Assert.That(Finalize(running, "frozen-shutdown-terminal", 1), Is.False);
            Assert.That(bridge.PendingRuntimeSettlementCountForTests(), Is.EqualTo(1));

            bridge.StopRuntimeRequestsLocked();

            Assert.That(publication.Work.Completion.Task.IsCompleted, Is.True);
            Assert.That(publication.Work.Completion.Task.Result, Is.EqualTo("frozen-shutdown-terminal"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(bridge.PendingRuntimeSettlementCountForTests(), Is.EqualTo(0));
        }

        [Test]
        public void ShutdownFinalizesQueuedAdmissionOwnerBeforeAnyDequeueEffectOwnership()
        {
            var requestId = "runtime-queued-shutdown";
            var request = Request(requestId, "runtime-tool.run");
            var digest = Digest('6');
            var deadline = Deadline();
            var publication = bridge.PublishRuntimeRequest(request, deadline, digest, Admission(requestId, digest, deadline));
            Assert.That(publication.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
            Assert.That(pool.Capture().Active, Is.EqualTo(1));

            bridge.StopRuntimeRequestsLocked();

            Assert.That(publication.Work.Completion.Task.IsCompleted, Is.True);
            Assert.That(publication.Work.Completion.Task.Result, Does.Contain("E_RUNTIME_SESSION_STALE"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(bridge.PendingRuntimeSettlementCountForTests(), Is.EqualTo(0));
            UnityCliRuntimeBridge.PendingRequest ignored;
            Assert.That(TryTake(out ignored), Is.False);
            Assert.That(ignored, Is.Null);
        }

        [Test]
        public void RecoveryBlockedShutdownSettlesQueuedAndRunningAgainstCapturedPoolExactlyOnce()
        {
            var runningRequest = Request("runtime-running-shutdown", "runtime-tool.run");
            var runningDigest = Digest('e');
            var runningPublication = bridge.PublishRuntimeRequest(runningRequest, Deadline(), runningDigest,
                Admission("runtime-running-shutdown", runningDigest, Deadline()));
            Assert.That(runningPublication.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
            var running = TakeNext();
            var queuedRequest = Request("runtime-blocked-queued-shutdown", "runtime-tool.run");
            var queuedDigest = Digest('5');
            var queuedPublication = bridge.PublishRuntimeRequest(queuedRequest, Deadline(), queuedDigest,
                Admission("runtime-blocked-queued-shutdown", queuedDigest, Deadline()));
            Assert.That(queuedPublication.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
            pool.BlockRecovery();
            Assert.That(pool.Capture().Active, Is.EqualTo(2));

            bridge.StopRuntimeRequestsLocked();

            Assert.That(running.Completion.Task.IsCompleted, Is.True);
            Assert.That(running.Completion.Task.Result, Does.Contain("E_EXECUTION_TIMEOUT"));
            Assert.That(queuedPublication.Work.Completion.Task.IsCompleted, Is.True);
            Assert.That(queuedPublication.Work.Completion.Task.Result, Does.Contain("E_RUNTIME_SESSION_STALE"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(bridge.PendingRuntimeSettlementCountForTests(), Is.EqualTo(0));
            Assert.That(Finalize(running, "late-running-terminal", 1), Is.False);
            Assert.That(Finalize(queuedPublication.Work, "late-queued-terminal", 0), Is.False);
            Assert.That(running.Completion.Task.Result, Does.Contain("E_EXECUTION_TIMEOUT"));
            Assert.That(queuedPublication.Work.Completion.Task.Result, Does.Contain("E_RUNTIME_SESSION_STALE"));
            Assert.That(PublishBypass("runtime-after-running-stop").Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Stopping));
        }

        [Test]
        public void SettlementBackoffUsesMonotonicClockAcrossUtcJumps()
        {
            var requestId = "runtime-monotonic-settlement";
            var request = Request(requestId, "runtime-tool.run");
            var digest = Digest('4');
            var deadline = Deadline();
            var publication = bridge.PublishRuntimeRequest(request, deadline, digest, Admission(requestId, digest, deadline));
            var running = TakeNext();
            pool.BlockRecovery();
            Assert.That(Finalize(running, "monotonic-terminal", 1), Is.False);
            pool.Reset();

            utcNowMs += 86400000L;
            bridge.RetryPendingRuntimeSettlementsForTests();
            Assert.That(publication.Work.Completion.Task.IsCompleted, Is.False);
            utcNowMs -= 172800000L;
            bridge.RetryPendingRuntimeSettlementsForTests();
            Assert.That(publication.Work.Completion.Task.IsCompleted, Is.False);
            monotonicNowMs += 99L;
            bridge.RetryPendingRuntimeSettlementsForTests();
            Assert.That(publication.Work.Completion.Task.IsCompleted, Is.False);
            monotonicNowMs += 1L;
            bridge.RetryPendingRuntimeSettlementsForTests();

            Assert.That(publication.Work.Completion.Task.Result, Is.EqualTo("monotonic-terminal"));
            Assert.That(bridge.PendingRuntimeSettlementCountForTests(), Is.EqualTo(0));
        }

        private UnityCliRuntimeBridge.RuntimePublicationResult PublishBypass(string requestId)
        {
            return bridge.PublishRuntimeRequest(Request(requestId, "runtime-tool.list"), Deadline(), DigestFor(requestId), new UnityCliRuntimeBridge.RuntimeAdmissionInput());
        }

        private UnityCliRuntimeBridge.RuntimeAdmissionInput Admission(string requestId, string digest, long deadline)
        {
            var grant = pool.Acquire(requestId, Fingerprint, ListenerId, 1L);
            Assert.That(grant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            return new UnityCliRuntimeBridge.RuntimeAdmissionInput
            {
                RootId = requestId,
                Fingerprint = Fingerprint,
                Token = grant.Token,
                DeadlineUtc = DateTimeOffset.FromUnixTimeMilliseconds(deadline).ToString("O")
            };
        }

        private UnityCliRuntimeBridge.PendingRequest TakeNext()
        {
            UnityCliRuntimeBridge.PendingRequest request;
            Assert.That(TryTake(out request), Is.True);
            return request;
        }

        private bool TryTake(out UnityCliRuntimeBridge.PendingRequest request)
        {
            var method = typeof(UnityCliRuntimeBridge).GetMethod("TryTakeNextRuntimeRequest", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var arguments = new object[] { null };
            var result = (bool)method.Invoke(bridge, arguments);
            request = (UnityCliRuntimeBridge.PendingRequest)arguments[0];
            return result;
        }

        private bool Finalize(UnityCliRuntimeBridge.PendingRequest request, string terminal, int expectedState)
        {
            var method = typeof(UnityCliRuntimeBridge).GetMethod("FinalizeRuntimeRequest", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(bridge, new object[] { request, terminal, expectedState });
        }

        private void SetField(string name, object value)
        {
            var field = typeof(UnityCliRuntimeBridge).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(bridge, value);
        }

        private Dictionary<string, object> Request(string requestId, string command)
        {
            return Request(requestId, command, Deadline());
        }

        private static Dictionary<string, object> Request(string requestId, string command, long absoluteDeadlineMs)
        {
            return new Dictionary<string, object>
            {
                { "protocolVersion", (long)2 }, { "instanceId", "runtime-test-instance" }, { "command", command },
                { "requestId", requestId }, { "input", new Dictionary<string, object>() },
                { "execution", new Dictionary<string, object>
                    {
                        { "absoluteDeadlineUtc", DateTimeOffset.FromUnixTimeMilliseconds(absoluteDeadlineMs).ToString("O") },
                        { "completionContractVersion", (long)1 }, { "requestDigestVersion", (long)1 }
                    }
                }
            };
        }

        private long Deadline() { return utcNowMs + 60000L; }
        private static string Digest(char value) { return new string(value, 64); }
        private static string DigestFor(string requestId)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(requestId));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
#endif
