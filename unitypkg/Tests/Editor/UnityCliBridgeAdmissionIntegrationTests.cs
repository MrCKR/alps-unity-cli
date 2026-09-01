#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityCli.Protocol;

namespace UnityCli.Tests
{
    public sealed class UnityCliBridgeAdmissionIntegrationTests
    {
        private UnityCliAdmissionPool pool;
        private string fingerprint;

        [SetUp]
        public void SetUp()
        {
            pool = new UnityCliAdmissionPool("test-instance", "test-session", 4, 1);
            fingerprint = new string('a', 64);
            UnityCliBridge.BeginQueueTestScope(pool);
        }

        [TearDown]
        public void TearDown()
        {
            UnityCliBridge.EndQueueTestScope();
        }

        [Test]
        public void ExactAttachAndCompletionShareOneQueuePoolBoundary()
        {
            var requestId = "queue-race-attach";
            var raw = RequestBytes(requestId);
            var grant = pool.Acquire(requestId, fingerprint, UnityCliBridge.ListenerIdForTests, UnityCliBridge.ListenerGenerationForTests);
            Assert.That(grant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            var first = Enqueue(requestId, raw, grant.Token);
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            UnityCliBridge.QueueTestBoundary = name =>
            {
                if (name != "before-existing-lookup") return;
                entered.Set();
                release.Wait();
            };

            UnityCliBridge.BridgeWorkItem attached = null;
            var attachTask = Task.Run(() => attached = Enqueue(requestId, raw, grant.Token));
            Assert.That(entered.Wait(5000), Is.True);
            var completionTask = Task.Run(() => UnityCliBridge.CompleteWorkItemForTests(first, Success(requestId)));
            release.Set();

            Assert.That(attachTask.Wait(5000), Is.True);
            Assert.That(completionTask.Wait(5000), Is.True);
            Assert.That(attached, Is.SameAs(first));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void RegisteredInternalCommandsHaveClosedFailClosedQueueClassification()
        {
            var classes = UnityCliBridge.InternalQueueClassesForTests();
            Assert.That(classes.Count, Is.GreaterThan(0));
            foreach (var pair in classes)
            {
                var request = Request("classification-" + pair.Key.Replace('.', '-'));
                request.Command = pair.Key;
                var actual = UnityCliBridge.QueueRequestWorkClass(request);
                Assert.That(actual, Is.EqualTo(pair.Value), pair.Key);
                if (UnityCliBridgeQueuePolicy.IsSafeStatusCommand(pair.Key))
                    Assert.That(actual, Is.EqualTo(UnityCliBridgeQueuePolicy.WorkClass.SafeStatus), pair.Key);
                else
                    Assert.That(actual, Is.EqualTo(UnityCliBridgeQueuePolicy.WorkClass.Mutation), pair.Key);
            }
            var unknown = Request("classification-unknown");
            unknown.Command = "internal.unknown.effect";
            Assert.That(UnityCliBridge.QueueRequestWorkClass(unknown), Is.EqualTo(UnityCliBridgeQueuePolicy.WorkClass.Mutation));
        }

        [Test]
        public void CompletionFirstRemovesMapWithPoolAndCannotRepublishRequest()
        {
            var requestId = "queue-race-complete";
            var raw = RequestBytes(requestId);
            var grant = pool.Acquire(requestId, fingerprint, UnityCliBridge.ListenerIdForTests, UnityCliBridge.ListenerGenerationForTests);
            var first = Enqueue(requestId, raw, grant.Token);
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            UnityCliBridge.QueueTestBoundary = name =>
            {
                if (name != "before-completion-memory") return;
                entered.Set();
                release.Wait();
            };

            var completionTask = Task.Run(() => UnityCliBridge.CompleteWorkItemForTests(first, Success(requestId)));
            Assert.That(entered.Wait(5000), Is.True);
            BridgeCommandException failure = null;
            var duplicateTask = Task.Run(() =>
            {
                try { Enqueue(requestId, raw, grant.Token); }
                catch (BridgeCommandException exception) { failure = exception; }
            });
            release.Set();

            Assert.That(completionTask.Wait(5000), Is.True);
            Assert.That(duplicateTask.Wait(5000), Is.True);
            Assert.That(failure, Is.Not.Null);
            Assert.That(failure.Code, Is.EqualTo("E_ADMISSION_IDENTITY_MISMATCH"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        private static UnityCliBridge.BridgeWorkItem Enqueue(string requestId, byte[] raw, string token)
        {
            var request = Request(requestId);
            var reservation = new UnityCliBridge.AdmissionQueueReservation
            {
                RequestId = requestId,
                RequestDigest = UnityCliRequestDigest.Compute(raw, request.InstanceId),
                RootFingerprint = new string('a', 64),
                Token = token,
                ExecutionDeadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30000,
                HasAdmissionHeaders = true,
                IsBypass = false
            };
            bool created;
            int depth;
            return UnityCliBridge.EnqueuePreparedForTests(request, raw, reservation, out created, out depth);
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

        private static byte[] RequestBytes(string requestId)
        {
            return System.Text.Encoding.UTF8.GetBytes("{\"requestId\":\"" + requestId + "\"}");
        }

        private static string Success(string requestId)
        {
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Success(requestId, new Dictionary<string, object> { { "terminal", true } }));
        }
    }
}
#endif
