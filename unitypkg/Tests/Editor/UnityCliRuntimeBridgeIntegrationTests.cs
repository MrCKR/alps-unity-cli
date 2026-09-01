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
    [NonParallelizable]
    public sealed class UnityCliRuntimeBridgeIntegrationTests
    {
        private const string ListenerId = "runtime-test-listener";
        private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private GameObject host;
        private UnityCliRuntimeBridge bridge;
        private UnityCliAdmissionPool pool;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("UnityCli.RuntimeBridge.Test");
            bridge = host.AddComponent<UnityCliRuntimeBridge>();
            bridge.enabled = false;
            pool = new UnityCliAdmissionPool("runtime-test-instance", "runtime-test-session", 256, 64);
            SetField("admissionPool", pool);
            SetField("listenerId", ListenerId);
            SetField("sessionId", "runtime-test-session");
        }

        [TearDown]
        public void TearDown()
        {
            if (bridge != null)
            {
                bridge.RuntimeTestBoundary = null;
                bridge.StopRuntimeRequestsLocked();
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
            bridge.RuntimeTestBoundary = name =>
            {
                if (name != "before-runtime-publish") return;
                entered.Set();
                release.Wait();
            };
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
            bridge.RuntimeTestBoundary = name =>
            {
                if (name == "before-runtime-publish") throw new InvalidOperationException("injected publication fault");
            };
            Assert.Throws<InvalidOperationException>(() => bridge.PublishRuntimeRequest(faultRequest, Deadline(), faultDigest, faultAdmission));
            bridge.RuntimeTestBoundary = null;
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
            bridge.RuntimeTestBoundary = name =>
            {
                if (name == "before-runtime-publish") throw new InvalidOperationException("injected eviction publication fault");
            };
            Assert.Throws<InvalidOperationException>(() => PublishBypass("runtime-cache-new"));
            bridge.RuntimeTestBoundary = null;
            Assert.That(PublishBypass("runtime-cache-0").Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Existing));

            Assert.That(PublishBypass("runtime-cache-new").Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
            Assert.That(PublishBypass("runtime-cache-0").Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
        }

        [Test]
        public void RunningRequestCompletesAgainstCapturedPoolAfterShutdown()
        {
            var request = Request("runtime-running-shutdown", "runtime-tool.run");
            var digest = Digest('e');
            var publication = bridge.PublishRuntimeRequest(request, Deadline(), digest, Admission("runtime-running-shutdown", digest, Deadline()));
            Assert.That(publication.Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
            var running = TakeNext();
            Assert.That(running, Is.SameAs(publication.Work));
            Assert.That(pool.Capture().Active, Is.EqualTo(1));

            bridge.StopRuntimeRequestsLocked();

            Assert.That(running.Completion.Task.IsCompleted, Is.False);
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(Finalize(running, "running-terminal", 1), Is.True);
            Assert.That(running.Completion.Task.Result, Is.EqualTo("running-terminal"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(PublishBypass("runtime-after-running-stop").Outcome, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Stopping));
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
            var method = typeof(UnityCliRuntimeBridge).GetMethod("TryTakeNextRuntimeRequest", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var arguments = new object[] { null };
            Assert.That((bool)method.Invoke(bridge, arguments), Is.True);
            return (UnityCliRuntimeBridge.PendingRequest)arguments[0];
        }

        private static bool Finalize(UnityCliRuntimeBridge.PendingRequest request, string terminal, int expectedState)
        {
            var method = typeof(UnityCliRuntimeBridge).GetMethod("FinalizeRuntimeRequest", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(null, new object[] { request, terminal, expectedState });
        }

        private void SetField(string name, object value)
        {
            var field = typeof(UnityCliRuntimeBridge).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(bridge, value);
        }

        private static Dictionary<string, object> Request(string requestId, string command)
        {
            return new Dictionary<string, object>
            {
                { "protocolVersion", (long)2 }, { "instanceId", "runtime-test-instance" }, { "command", command },
                { "requestId", requestId }, { "input", new Dictionary<string, object>() },
                { "execution", new Dictionary<string, object>
                    {
                        { "absoluteDeadlineUtc", DateTimeOffset.UtcNow.AddMinutes(1).ToString("O") },
                        { "completionContractVersion", (long)1 }, { "requestDigestVersion", (long)1 }
                    }
                }
            };
        }

        private static long Deadline() { return DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(); }
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
