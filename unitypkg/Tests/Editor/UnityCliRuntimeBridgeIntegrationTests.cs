#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace UnityCli.Tests
{
    public sealed class UnityCliRuntimeBridgeIntegrationTests
    {
        private GameObject host;
        private UnityCliRuntimeBridge bridge;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("UnityCli.RuntimeBridge.Test");
            bridge = host.AddComponent<UnityCliRuntimeBridge>();
            bridge.enabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            if (bridge != null) bridge.RuntimeTestBoundary = null;
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
            bridge = null;
            host = null;
        }

        [Test]
        public void PublicationAndShutdownShareOneRuntimeCacheBoundary()
        {
            var request = Work("runtime-race");
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            bridge.RuntimeTestBoundary = name =>
            {
                if (name != "before-runtime-publish") return;
                entered.Set();
                release.Wait();
            };

            UnityCliRuntimeBridge.PendingRequest published = null;
            var publishTask = Task.Run(() =>
            {
                UnityCliRuntimeBridge.PendingRequest existing;
                var outcome = bridge.TryPublishPreparedRequest(request, out existing);
                published = outcome == UnityCliRuntimeBridge.RuntimePublicationOutcome.Published ? request : existing;
                return outcome;
            });
            Assert.That(entered.Wait(5000), Is.True);
            var stopTask = Task.Run(() => bridge.StopRuntimeRequestsLocked());
            release.Set();

            Assert.That(publishTask.Wait(5000), Is.True);
            Assert.That(stopTask.Wait(5000), Is.True);
            Assert.That(publishTask.Result, Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
            Assert.That(published.Completion.Task.Wait(1000), Is.True);
            Assert.That(published.Completion.Task.Result, Does.Contain("E_RUNTIME_SESSION_STALE"));

            UnityCliRuntimeBridge.PendingRequest ignored;
            Assert.That(bridge.TryPublishPreparedRequest(Work("runtime-after-stop"), out ignored), Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Stopping));
        }

        [Test]
        public void PublicationFailureLeavesEarlierCacheEntryAndQueueConsistent()
        {
            var first = Work("runtime-first");
            UnityCliRuntimeBridge.PendingRequest existing;
            Assert.That(bridge.TryPublishPreparedRequest(first, out existing), Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Published));
            bridge.StopRuntimeRequestsLocked();
            Assert.That(first.Completion.Task.IsCompleted, Is.True);
            Assert.That(bridge.TryPublishPreparedRequest(Work("runtime-second"), out existing), Is.EqualTo(UnityCliRuntimeBridge.RuntimePublicationOutcome.Stopping));
        }

        private static UnityCliRuntimeBridge.PendingRequest Work(string requestId)
        {
            return new UnityCliRuntimeBridge.PendingRequest(
                new Dictionary<string, object> { { "requestId", requestId } },
                DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
                new string('b', 64),
                1,
                null);
        }
    }
}
#endif
