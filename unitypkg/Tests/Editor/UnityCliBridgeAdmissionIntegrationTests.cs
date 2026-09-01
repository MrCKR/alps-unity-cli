#if UNITY_EDITOR
using System;
using System.Collections.Generic;
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
            var request = Request("queue-race-attach");
            var raw = RequestBytes(request);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, UnityCliBridge.ListenerIdForTests, UnityCliBridge.ListenerGenerationForTests);
            Assert.That(grant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            bool firstCreated;
            var first = Enqueue(request, raw, grant.Token, deadline, out firstCreated);
            Assert.That(firstCreated, Is.True);
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            UnityCliBridge.QueueTestBoundary = name =>
            {
                if (name != "before-existing-lookup") return;
                entered.Set();
                release.Wait();
            };

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
            var grant = pool.Acquire(request.RequestId, fingerprint, UnityCliBridge.ListenerIdForTests, UnityCliBridge.ListenerGenerationForTests);
            bool ignoredCreated;
            var first = Enqueue(request, raw, grant.Token, deadline, out ignoredCreated);
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            UnityCliBridge.QueueTestBoundary = name =>
            {
                if (name != "before-completion-memory") return;
                entered.Set();
                release.Wait();
            };

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
            var grant = pool.Acquire(request.RequestId, fingerprint, UnityCliBridge.ListenerIdForTests, UnityCliBridge.ListenerGenerationForTests);
            bool created;
            var first = Enqueue(request, raw, grant.Token, deadline, out created);
            Assert.That(created, Is.True);

            UnityCliBridge.SweepQueuedWorkForTests();

            Assert.That(first.Completed.IsSet, Is.True);
            Assert.That(first.Response, Does.Contain("E_UNITY_DOMAIN_RELOAD"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            var exception = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => Enqueue(request, raw, grant.Token, deadline, out created));
            Assert.That(exception.Code, Is.EqualTo("E_UNITY_DOMAIN_RELOAD"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void PublicationCompensationPersistsAndLogsOutsideQueueLock()
        {
            var request = Request("queue-publication-fault");
            var raw = RequestBytes(request);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, UnityCliBridge.ListenerIdForTests, UnityCliBridge.ListenerGenerationForTests);
            var persistOutside = false;
            var logOutside = false;
            UnityCliBridge.QueueTestBoundary = name =>
            {
                if (name == "before-queue-publication") throw new InvalidOperationException("injected publication fault");
                if (name == "before-admission-compensation-persist") persistOutside = !UnityCliBridge.QueueLockHeldForTests;
                if (name == "before-admission-compensation-log") logOutside = !UnityCliBridge.QueueLockHeldForTests;
            };

            Assert.Throws<InvalidOperationException>(() => { bool created; Enqueue(request, raw, grant.Token, deadline, out created); });

            Assert.That(persistOutside, Is.True);
            Assert.That(logOutside, Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests, Is.EqualTo(0));
        }

        [Test]
        public void CompletionBoundaryFaultLogsAndPersistsOutsideQueueLock()
        {
            var request = Request("queue-completion-fault");
            var raw = RequestBytes(request);
            var deadline = Deadline(request);
            var grant = pool.Acquire(request.RequestId, fingerprint, UnityCliBridge.ListenerIdForTests, UnityCliBridge.ListenerGenerationForTests);
            bool created;
            var item = Enqueue(request, raw, grant.Token, deadline, out created);
            var faultObservedInside = false;
            var persistOutside = false;
            var logOutside = false;
            UnityCliBridge.QueueTestBoundary = name =>
            {
                if (name == "before-completion-memory")
                {
                    faultObservedInside = UnityCliBridge.QueueLockHeldForTests;
                    throw new InvalidOperationException("injected completion boundary fault");
                }
                if (name == "before-admission-persist") persistOutside = !UnityCliBridge.QueueLockHeldForTests;
                if (name == "before-queue-boundary-fault-log") logOutside = !UnityCliBridge.QueueLockHeldForTests;
            };

            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, Success(request.RequestId)), Is.True);

            Assert.That(faultObservedInside, Is.True);
            Assert.That(persistOutside, Is.True);
            Assert.That(logOutside, Is.True);
            Assert.That(item.Completed.IsSet, Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests, Is.EqualTo(0));
        }

        [Test]
        public void WorkerClassificationUsesOnlyImmutableInput()
        {
            var requests = new[]
            {
                ProjectToolRequest("worker-rigidbody", "physics.rigidbody", new Dictionary<string, object> { { "target", "MissingTarget" } }),
                ProjectToolRequest("worker-reflection", "graphics.reflection-probe", new Dictionary<string, object> { { "target", "MissingTarget" }, { "create", true } }),
                ProjectToolRequest("worker-light-probes", "graphics.light-probe-group", new Dictionary<string, object> { { "target", "MissingTarget" }, { "create", true } })
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
                "internal.test.run.cancel.plan", "internal.test.run.cancel", "internal.package.files.claim",
                "internal.package.files.commit", "internal.package.files.prove", "internal.package.files.fail",
                "internal.navmesh.bake.start", "internal.navmesh.bake.plan", "internal.navmesh.bake.cancel",
                "internal.occlusion.bake.start", "internal.occlusion.bake.plan", "internal.occlusion.bake.cancel",
                "internal.build.run.start", "internal.instances.prune"
            }) AssertClass(classes, command, UnityCliBridgeQueuePolicy.WorkClass.Mutation);
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
            return UnityCliBridge.EnqueuePreparedForTests(request, raw, reservation, out created, out depth);
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
