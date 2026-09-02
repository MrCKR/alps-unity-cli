#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
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
            if (command == "internal.test.run.cancel")
            {
                var rejectedRaw = RequestBytes(rejected);
                var rejectedGrant = pool.Acquire(rejected.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
                Enqueue(rejected, rejectedRaw, rejectedGrant.Token, Deadline(rejected), out created);
                Assert.That(created, Is.True);
                var exactRaw = RequestBytes(exact);
                var exactGrant = pool.Acquire(exact.RequestId, fingerprint, state.ListenerId, state.ListenerGeneration);
                Enqueue(exact, exactRaw, exactGrant.Token, Deadline(exact), out created);
                Assert.That(created, Is.True);
            }
            else
            {
                EnqueueBypass(rejected, out created);
                Assert.That(created, Is.True);
                EnqueueBypass(exact, out created);
                Assert.That(created, Is.True);
            }

            var starts = UnityCliBridge.PreviewQueuedStartRequestIdsForTests(state);

            Assert.That(starts, Is.EqualTo(new[] { exact.RequestId }));
            Assert.That(state.LastQueueNodesExamined, Is.EqualTo(2));
            UnityCliBridge.ShutdownQueuedWorkForTests(state);
            state.ClearOperation(scope);
            state.ReleaseLease(parentRequestId, parentDigest);
        }

        [Test]
        public void ProductionQueueTraversalCountsActualLinkedListNodesWithinHardBound()
        {
            for (var index = 0; index < UnityCliBridgeQueuePolicy.MaxNodesExaminedPerTick + 1; index++)
            {
                var request = Request("actual-queue-scan-" + index.ToString());
                request.Command = "project-tool.list";
                var raw = RequestBytes(request);
                var item = new UnityCliBridge.BridgeWorkItem(state, request, raw,
                    UnityCliRequestDigest.Compute(raw, request.InstanceId), state.ListenerId, state.ListenerGeneration,
                    null, UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead, true);
                lock (state.QueueLock)
                {
                    state.ActiveRequestWork.Add(request.RequestId, item);
                    item.QueueNode = state.WorkQueue.AddLast(item);
                }
            }

            var starts = UnityCliBridge.PreviewQueuedStartRequestIdsForTests(state);

            Assert.That(state.LastQueueNodesExamined, Is.EqualTo(UnityCliBridgeQueuePolicy.MaxNodesExaminedPerTick));
            Assert.That(starts.Length, Is.EqualTo(UnityCliBridgeQueuePolicy.MaxStartsPerTick));
            Assert.That(starts, Does.Not.Contain("actual-queue-scan-128"));
            UnityCliBridge.ShutdownQueuedWorkForTests(state);
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
