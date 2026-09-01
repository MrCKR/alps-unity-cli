#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityCli.Protocol;

namespace UnityCli.Tests
{
    [NonParallelizable]
    public sealed class UnityCliAdmissionCoordinatorIntegrationTests
    {
        private const string InstanceId = "test-instance";
        private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private static readonly string[] TrackedScopes =
        {
            "build", "build-terminal-cleanup", "build-admission-owner", "package-resolve",
            "package-resolve-terminal-cleanup", "package-resolve-admission-owner", "test-run",
            "test-run-terminal-cleanup", "test-run-admission-owner", "test-cancel-admission-owner",
            "direct-mutation", "direct-mutation-terminal-cleanup", "direct-mutation-admission-owner"
        };
        private Dictionary<string, byte[]> fileSnapshots;
        private BridgeQueueState queueState;
        private string identityPrefix;
        private string originalLeaseRequestId;
        private string originalLeaseRequestDigest;
        private Action<string> originalPackageStartBoundary;
        private Action<string> originalPackageEffectBoundary;
        private Action<string> originalCancelBoundary;
        private bool originalBuildReloadRecoveryPending;

        [SetUp]
        public void SetUp()
        {
            identityPrefix = "admission-" + Guid.NewGuid().ToString("N") + "-";
            originalPackageStartBoundary = UnityCliBridge.UnityCliPackageResolveCoordinator.PackageStartTestBoundary;
            originalPackageEffectBoundary = UnityCliBridge.UnityCliPackageResolveCoordinator.PackageResolveEffectTestBoundary;
            originalCancelBoundary = UnityCliBridge.UnityCliTestRunCoordinator.CancelFinalizerTestBoundary;
            originalBuildReloadRecoveryPending = UnityCliBridge.UnityCliLiveBuildCoordinator.ReloadRecoveryPendingForTests;
            Assert.That(UnityCliBridge.UnityCliSessionMutationLease.TryGetCurrentOwner(out originalLeaseRequestId, out originalLeaseRequestDigest), Is.True, "parent lease must be readable before isolation");
            fileSnapshots = TrackedPaths().ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null, StringComparer.Ordinal);
            foreach (var path in fileSnapshots.Keys) if (File.Exists(path)) File.Delete(path);
            UnityCliBridge.UnityCliSessionMutationLease.Initialize();
        }

        [TearDown]
        public void TearDown()
        {
            var failures = new List<string>();
            try { if (queueState != null) queueState.AssertEmptyForTests(); } catch (Exception exception) { failures.Add("local queue state: " + exception.Message); }
            UnityCliBridge.UnityCliPackageResolveCoordinator.PackageStartTestBoundary = originalPackageStartBoundary;
            UnityCliBridge.UnityCliPackageResolveCoordinator.PackageResolveEffectTestBoundary = originalPackageEffectBoundary;
            UnityCliBridge.UnityCliTestRunCoordinator.CancelFinalizerTestBoundary = originalCancelBoundary;
            UnityCliBridge.UnityCliLiveBuildCoordinator.ReloadRecoveryPendingForTests = originalBuildReloadRecoveryPending;
            foreach (var snapshot in fileSnapshots ?? new Dictionary<string, byte[]>())
            {
                try
                {
                    if (File.Exists(snapshot.Key)) File.Delete(snapshot.Key);
                    if (snapshot.Value != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(snapshot.Key));
                        File.WriteAllBytes(snapshot.Key, snapshot.Value);
                    }
                    var restored = File.Exists(snapshot.Key) ? File.ReadAllBytes(snapshot.Key) : null;
                    if ((snapshot.Value == null) != (restored == null) || snapshot.Value != null && !snapshot.Value.SequenceEqual(restored))
                        failures.Add("snapshot mismatch: " + snapshot.Key);
                }
                catch (Exception exception) { failures.Add(snapshot.Key + ": " + exception.Message); }
            }
            try
            {
                UnityCliBridge.UnityCliSessionMutationLease.Initialize();
                string requestId;
                string requestDigest;
                if (!UnityCliBridge.UnityCliSessionMutationLease.TryGetCurrentOwner(out requestId, out requestDigest)
                    || !string.Equals(requestId, originalLeaseRequestId, StringComparison.Ordinal)
                    || !string.Equals(requestDigest, originalLeaseRequestDigest, StringComparison.Ordinal))
                    failures.Add("parent lease identity was not restored exactly");
            }
            catch (Exception exception) { failures.Add("lease restore: " + exception.Message); }
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        [Test]
        public void BuildArtifactFaultRunsExecuteRootTerminalAndQueueSettlement()
        {
            var request = BuildRequest(Unique("build-artifact-fault"));
            var raw = RequestBytes(request);
            var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var deadline = Deadline(request);
            var pool = new UnityCliAdmissionPool(InstanceId, Unique("build-artifact-session"), 4, 1);
            queueState = UnityCliBridge.CreateQueueStateForTests(pool);
            Consume(pool, request.RequestId, digest, deadline);
            bool created;
            var item = Enqueue(request, raw, Fingerprint, deadline, pool, out created);
            Assert.That(created, Is.True);
            var dependencies = BuildDependencies(prepared => new UnityCliBridge.UnityCliLiveBuildCoordinator.BuildEffectResult
            {
                Result = "Succeeded",
                Artifact = new Dictionary<string, object>
                {
                    { "exists", false }, { "fileCount", (long)0 }, { "size", (long)0 },
                    { "hash", null }, { "treeHash", null }
                }
            });

            var response = UnityCliBridge.UnityCliLiveBuildCoordinator.Execute(queueState, request, raw, () => true, CancellationToken.None, dependencies).GetAwaiter().GetResult();

            Assert.That(response, Does.Contain("E_BUILD_ARTIFACT_MISSING"));
            Assert.That(response, Does.Contain("terminal"));
            object terminal;
            Assert.That(UnityCliActiveOperation.TryGetTerminal("build", request.RequestId, digest, out terminal), Is.True);
            Assert.That(UnityCliSerializer.Serialize(terminal), Does.Contain("E_BUILD_ARTIFACT_MISSING"));
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, response), Is.True);
            Assert.That(item.Completed.IsSet, Is.True);
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(queueState), Is.EqualTo(0));
            UnityCliBridge.UnityCliLiveBuildCoordinator.Tick();
            AssertBuildSettled(pool, request.RequestId, digest);
        }

        [TestCase("root-read")]
        [TestCase("terminal-store")]
        public void BuildPostRootFaultUsesFrozenRecoveryAndTickConverges(string fault)
        {
            var request = BuildRequest(Unique("build-post-root-" + fault));
            var raw = RequestBytes(request);
            var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var deadline = Deadline(request);
            var pool = new UnityCliAdmissionPool(InstanceId, Unique("build-recovery-session"), 4, 1);
            queueState = UnityCliBridge.CreateQueueStateForTests(pool);
            Consume(pool, request.RequestId, digest, deadline);
            bool created;
            var item = Enqueue(request, raw, Fingerprint, deadline, pool, out created);
            Assert.That(created, Is.True);
            var dependencies = BuildDependencies(prepared => { throw new UnityCliBridge.BridgeCommandException("E_BUILD_EFFECT_TEST_FAULT", "Injected build effect fault."); });
            dependencies.Boundary = name =>
            {
                if (fault == "root-read" && name == "before-build-terminal-root-read") throw new IOException("injected active root read fault");
                if (fault == "terminal-store" && name == "before-build-terminal-store") throw new IOException("injected terminal store fault");
            };

            var response = UnityCliBridge.UnityCliLiveBuildCoordinator.Execute(queueState, request, raw, () => true, CancellationToken.None, dependencies).GetAwaiter().GetResult();

            Assert.That(response, Does.Contain("E_EXECUTION_TIMEOUT"));
            Assert.That(pool.Capture().Records.Any(record => record.RequestId == request.RequestId
                && (record.OwnerState == UnityCliAdmissionPool.AdmissionOwnerState.Recovery
                    || record.OwnerState == UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup)), Is.True);
            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, response), Is.True);
            Assert.That(item.Completed.IsSet, Is.True);
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(queueState), Is.EqualTo(0));

            UnityCliBridge.UnityCliLiveBuildCoordinator.Tick();

            object terminal;
            Assert.That(UnityCliActiveOperation.TryGetTerminal("build", request.RequestId, digest, out terminal), Is.True);
            Assert.That(UnityCliSerializer.Serialize(terminal), Does.Contain("E_EXECUTION_TIMEOUT"));
            AssertBuildSettled(pool, request.RequestId, digest);
        }

        [Test]
        public void DirectSuccessRetainsPendingWorkAndCompletesExactOwnerAfterCleanupRetry()
        {
            var request = Request(Unique("direct-success-recovery"), "project-tool.run", new Dictionary<string, object>());
            var raw = RequestBytes(request);
            var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var deadline = Deadline(request);
            var pool = new UnityCliAdmissionPool(InstanceId, Unique("direct-success-session"), 4, 1);
            queueState = UnityCliBridge.CreateQueueStateForTests(pool);
            Consume(pool, request.RequestId, digest, deadline);
            bool created;
            var item = Enqueue(request, raw, Fingerprint, deadline, pool, out created);
            Assert.That(created, Is.True);
            Assert.That(UnityCliBridge.UnityCliSessionMutationLease.TryAcquire(request.RequestId, digest, out var newlyAcquired), Is.True);
            Assert.That(newlyAcquired, Is.True);
            UnityCliActiveOperation.Save("direct-mutation", Root(request.RequestId, digest, "terminal_pending"));
            var success = ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "result", new Dictionary<string, object> { { "applied", true } } } });
            UnityCliActiveOperation.StoreTerminalFirst("direct-mutation", request.RequestId, digest, success);
            UnityCliActiveOperation.Save("direct-mutation-admission-owner", new Dictionary<string, object>
            {
                { "requestId", Unique("conflicting-owner") }, { "requestDigest", new string('f', 64) }, { "editorSessionId", UnityCliBridge.EditorSessionId() }
            });

            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, UnityCliSerializer.Serialize(success)), Is.False);
            Assert.That(item.Completed.IsSet, Is.False);
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(queueState), Is.EqualTo(1));
            Assert.That(pool.Capture().Active, Is.EqualTo(1));

            UnityCliActiveOperation.Clear("direct-mutation-admission-owner");
            UnityCliBridge.RetryPendingWorkItemSettlementsForTests(queueState);
            Assert.That(item.Completed.IsSet, Is.True);
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(queueState), Is.EqualTo(0));
            Assert.That(pool.Capture().Records.Single().OwnerState, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerState.Recovery));

            UnityCliBridge.CleanupPublishedDirectMutationForTests(request.RequestId, digest, scope =>
            {
                if (scope == "direct-mutation") throw new IOException("injected direct root clear fault");
            });
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(UnityCliActiveOperation.Exists("direct-mutation"), Is.True);
            Assert.That(UnityCliActiveOperation.Exists("direct-mutation-terminal-cleanup"), Is.True);

            UnityCliBridge.CleanupPublishedDirectMutationForTests(request.RequestId, digest);

            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(UnityCliActiveOperation.Exists("direct-mutation"), Is.False);
            Assert.That(UnityCliActiveOperation.Exists("direct-mutation-terminal-cleanup"), Is.False);
            Assert.That(UnityCliBridge.UnityCliSessionMutationLease.TryIsOwnedBy(request.RequestId, digest, out var owned), Is.True);
            Assert.That(owned, Is.False);
        }

        [Test]
        public void PackageRootCreatedBridgeFaultUsesFirstTerminalAndExactCleanup()
        {
            var request = Request(Unique("package-root-created-fault"), "internal.package.resolve.start", new Dictionary<string, object>
            {
                { "expectedEditorSessionId", UnityCliBridge.EditorSessionId() },
                { "operation", "resolve" },
                { "input", new Dictionary<string, object>() }
            });
            var raw = RequestBytes(request);
            var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var deadline = Deadline(request);
            var pool = new UnityCliAdmissionPool(InstanceId, Unique("package-test-session"), 4, 1);
            queueState = UnityCliBridge.CreateQueueStateForTests(pool);
            Consume(pool, request.RequestId, digest, deadline);
            bool created;
            var item = Enqueue(request, raw, Fingerprint, deadline, pool, out created);
            Assert.That(created, Is.True);
            UnityCliBridge.UnityCliPackageResolveCoordinator.PackageStartTestBoundary = name =>
            {
                if (name == "after-package-root-created")
                    throw new UnityCliBridge.BridgeCommandException("E_PACKAGE_ROOT_TEST_FAULT", "Injected root-created package fault.");
            };

            var response = UnityCliBridge.UnityCliPackageResolveCoordinator.Execute(queueState, request, raw, () => true, CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(response, Does.Contain("E_PACKAGE_ROOT_TEST_FAULT"));
            Assert.That(response, Does.Contain("terminal"));
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, response), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            UnityCliActiveOperation.RetryExactTerminalCleanup("package-resolve");
            Assert.That(UnityCliActiveOperation.Exists("package-resolve"), Is.False);
            Assert.That(UnityCliBridge.UnityCliSessionMutationLease.TryIsOwnedBy(request.RequestId, digest, out var owned), Is.True);
            Assert.That(owned, Is.False);
        }

        [Test]
        public void PackagePostEffectBridgeFaultUsesFirstTerminalAndExactCleanup()
        {
            var request = Request(Unique("package-post-effect-fault"), "internal.package.resolve.start", new Dictionary<string, object>
            {
                { "expectedEditorSessionId", UnityCliBridge.EditorSessionId() },
                { "operation", "resolve" },
                { "input", new Dictionary<string, object>() }
            });
            var raw = RequestBytes(request);
            var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var deadline = Deadline(request);
            var pool = new UnityCliAdmissionPool(InstanceId, Unique("package-effect-session"), 4, 1);
            queueState = UnityCliBridge.CreateQueueStateForTests(pool);
            Consume(pool, request.RequestId, digest, deadline);
            bool created;
            var item = Enqueue(request, raw, Fingerprint, deadline, pool, out created);
            var accepted = UnityCliBridge.UnityCliPackageResolveCoordinator.Execute(queueState, request, raw, () => true, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, accepted), Is.True);
            var coordinator = UnityCliBridge.CaptureAdmissionOwner(request.RequestId, digest);
            Assert.That(coordinator.OwnerState, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerState.Coordinator));
            UnityCliBridge.UnityCliPackageResolveCoordinator.PackageResolveEffectTestBoundary = name =>
            {
                if (name == "after-package-resolve-effect-started")
                    throw new UnityCliBridge.BridgeCommandException("E_PACKAGE_EFFECT_TEST_FAULT", "Injected post-effect package fault.");
            };

            UnityCliBridge.UnityCliPackageResolveCoordinator.Tick();

            object terminal;
            Assert.That(UnityCliActiveOperation.TryGetTerminal("package-resolve", request.RequestId, digest, out terminal), Is.True);
            Assert.That(UnityCliSerializer.Serialize(terminal), Does.Contain("E_EXECUTION_TIMEOUT"));
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(UnityCliActiveOperation.Exists("package-resolve"), Is.False);
            Assert.That(UnityCliBridge.UnityCliSessionMutationLease.TryIsOwnedBy(request.RequestId, digest, out var owned), Is.True);
            Assert.That(owned, Is.False);
        }

        [Test]
        public void CancelNotInvokedAndOutcomeUnknownUseOneExactFinalizer()
        {
            var pool = new UnityCliAdmissionPool(InstanceId, Unique("cancel-test-session"), 4, 1);
            queueState = UnityCliBridge.CreateQueueStateForTests(pool);
            var notInvoked = PrepareCancel(pool, Unique("cancel-parent-a"), new string('a', 64), Guid.NewGuid().ToString(), Unique("cancel-not-invoked"));

            Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.FinalizeCancelForTests(notInvoked.ParentId, notInvoked.ParentDigest, notInvoked.RunId, notInvoked.CancelId, notInvoked.CancelDigest, "not_invoked", queueState), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            var saved = UnityCliActiveOperation.Load("test-run");
            var operation = (Dictionary<string, object>)saved["testOperation"];
            Assert.That(operation.ContainsKey("cancelRequestId"), Is.False);
            CompleteParent(notInvoked);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));

            var unknown = PrepareCancel(pool, Unique("cancel-parent-b"), new string('b', 64), Guid.NewGuid().ToString(), Unique("cancel-unknown"));
            Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.FinalizeCancelForTests(unknown.ParentId, unknown.ParentDigest, unknown.RunId, unknown.CancelId, unknown.CancelDigest, "outcome_unknown", queueState), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(UnityCliActiveOperation.Exists("test-run"), Is.False);
        }

        [Test]
        public void CancelReturnedTransfersToContinuationBeforeTerminalCompletion()
        {
            var pool = new UnityCliAdmissionPool(InstanceId, Unique("cancel-returned-session"), 4, 1);
            queueState = UnityCliBridge.CreateQueueStateForTests(pool);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
            var cancelId = Unique("cancel-returned");
            var request = Request(cancelId, "internal.test.run.cancel", new Dictionary<string, object>());
            request.Execution.AbsoluteDeadlineUtc = DateTimeOffset.FromUnixTimeMilliseconds(deadline).ToString("O");
            var raw = RequestBytes(request);
            var cancelDigest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var fixture = PrepareCancel(pool, Unique("cancel-parent-returned"), new string('e', 64), Guid.NewGuid().ToString(), cancelId, cancelDigest, deadline);
            var root = UnityCliActiveOperation.Load("test-run");
            var operation = (Dictionary<string, object>)root["testOperation"];
            operation["cancelEffectState"] = "dispatched";
            operation["cancelNeedsContinuation"] = true;
            UnityCliActiveOperation.Save("test-run", root);
            var record = pool.Capture().Records.First(value => value.RequestId == fixture.CancelId);
            var reservation = new UnityCliBridge.AdmissionQueueReservation
            {
                RequestId = fixture.CancelId, RequestDigest = fixture.CancelDigest, RootFingerprint = Fingerprint,
                Token = record.Token, ExecutionDeadlineMs = fixture.Deadline, HasAdmissionHeaders = true, IsBypass = false
            };
            bool created;
            int depth;
            var item = UnityCliBridge.EnqueuePreparedForTests(request, raw, reservation, queueState, out created, out depth);
            var accepted = UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(fixture.CancelId, new Dictionary<string, object>
            {
                { "terminal", false }, { "accepted", true }, { "rootRequestDigest", fixture.ParentDigest }
            }));

            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, accepted), Is.True);
            var continuation = UnityCliBridge.CaptureAdmissionOwner(queueState, fixture.CancelId, fixture.CancelDigest);
            Assert.That(continuation.OwnerState, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerState.Continuation));
            Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.FinalizeCancelForTests(fixture.ParentId, fixture.ParentDigest, fixture.RunId, fixture.CancelId, fixture.CancelDigest, "cancelled", queueState), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            CompleteParent(fixture);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void CancelDispatchSaveFaultRetainsRecoveryUntilRetryConverges()
        {
            var pool = new UnityCliAdmissionPool(InstanceId, Unique("cancel-save-fault-session"), 4, 1);
            queueState = UnityCliBridge.CreateQueueStateForTests(pool);
            var fixture = PrepareCancel(pool, Unique("cancel-parent-save"), new string('c', 64), Guid.NewGuid().ToString(), Unique("cancel-save-fault"));
            UnityCliBridge.UnityCliTestRunCoordinator.CancelFinalizerTestBoundary = name =>
            {
                if (name == "before-cancel-root-save") throw new InvalidOperationException("injected save fault");
            };

            Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.FinalizeCancelForTests(fixture.ParentId, fixture.ParentDigest, fixture.RunId, fixture.CancelId, fixture.CancelDigest, "outcome_unknown", queueState), Is.False);
            Assert.That(pool.Capture().Active, Is.EqualTo(2));
            Assert.That(pool.Capture().Records.Any(record => record.RequestId == fixture.CancelId && record.OwnerState == UnityCliAdmissionPool.AdmissionOwnerState.Recovery), Is.True);

            UnityCliBridge.UnityCliTestRunCoordinator.CancelFinalizerTestBoundary = null;
            Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.FinalizeCancelForTests(fixture.ParentId, fixture.ParentDigest, fixture.RunId, fixture.CancelId, fixture.CancelDigest, "outcome_unknown", queueState), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [TestCase(true, true)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void CleanupMarkerReconcilesAllRootLeaseWindows(bool rootExists, bool leaseOwned)
        {
            var requestId = Unique("marker-window-" + (rootExists ? "root" : "no-root") + "-" + (leaseOwned ? "lease" : "no-lease"));
            var digest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(requestId), InstanceId);
            var markerSession = Unique("marker-window-session");
            var original = new UnityCliAdmissionPool(InstanceId, markerSession, 4, 1);
            var restored = new UnityCliAdmissionPool(InstanceId, markerSession, 4, 1);
            queueState = UnityCliBridge.CreateQueueStateForTests(original);
            var owner = Consume(original, requestId, digest, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());
            if (leaseOwned) Assert.That(UnityCliBridge.UnityCliSessionMutationLease.TryAcquire(requestId, digest, out _), Is.True);
            if (rootExists) UnityCliActiveOperation.Save("build", Root(requestId, digest, "terminal_pending"));
            var marker = new Dictionary<string, object>
            {
                { "requestId", requestId }, { "requestDigest", digest }, { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "completeAdmission", true }
            };
            UnityCliBridge.PersistAdmissionOwnerTransfer(queueState, "build-terminal-cleanup", marker, owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
            queueState.AssertEmptyForTests();
            queueState = UnityCliBridge.CreateQueueStateForTests(restored);

            Assert.That(UnityCliActiveOperation.RetryExactTerminalCleanup("build", queueState), Is.False);
            Assert.That(restored.Capture().Active, Is.EqualTo(0));
            Assert.That(restored.Capture().RecoveryBlocked, Is.False);
            Assert.That(UnityCliActiveOperation.Exists("build-terminal-cleanup"), Is.False);
            Assert.That(UnityCliActiveOperation.Exists("build"), Is.False);
        }

        [Test]
        public void ReconciledMarkerRefreshesTheRecoveredListenerBinding()
        {
            var requestId = Unique("marker-listener-rebind");
            var digest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(requestId), InstanceId);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
            var pool = new UnityCliAdmissionPool(InstanceId, Unique("marker-listener-session"), 4, 1);
            queueState = UnityCliBridge.CreateQueueStateForTests(pool);
            var owner = Consume(pool, requestId, digest, deadline);
            var marker = new Dictionary<string, object>
            {
                { "requestId", requestId }, { "requestDigest", digest }, { "editorSessionId", UnityCliBridge.EditorSessionId() }
            };
            UnityCliBridge.PersistAdmissionOwnerTransfer(queueState, "build-admission-owner", marker, owner, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator);
            Assert.That(pool.RecoverConsumed(requestId, Fingerprint, digest, "listener-after-reload", 2, deadline), Is.True);
            var loaded = UnityCliActiveOperation.Load("build-admission-owner");

            var reconciled = UnityCliBridge.ReconcileAdmissionTransferForLiveMarker(queueState, "build-admission-owner", loaded, requestId, digest);

            Assert.That(reconciled.ListenerId, Is.EqualTo("listener-after-reload"));
            Assert.That(reconciled.ListenerGeneration, Is.EqualTo(2));
            var refreshed = UnityCliActiveOperation.Load("build-admission-owner");
            Assert.That(refreshed["admissionListenerId"], Is.EqualTo("listener-after-reload"));
            Assert.That(refreshed["admissionListenerGeneration"], Is.EqualTo((long)2));
        }

        [Test]
        public void CleanupMarkerIdentityMismatchBlocksRecovery()
        {
            var requestId = Unique("marker-identity-mismatch");
            var digest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(requestId), InstanceId);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
            var mismatchSession = Unique("marker-mismatch-session");
            var original = new UnityCliAdmissionPool(InstanceId, mismatchSession, 4, 1);
            var restored = new UnityCliAdmissionPool(InstanceId, mismatchSession, 4, 1);
            queueState = UnityCliBridge.CreateQueueStateForTests(original);
            var owner = Consume(original, requestId, digest, deadline);
            var marker = new Dictionary<string, object>
            {
                { "requestId", requestId }, { "requestDigest", digest }, { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "completeAdmission", true }
            };
            UnityCliBridge.PersistAdmissionOwnerTransfer(queueState, "build-terminal-cleanup", marker, owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
            queueState.AssertEmptyForTests();
            queueState = UnityCliBridge.CreateQueueStateForTests(restored);
            var otherFingerprint = new string('d', 64);
            var grant = restored.Acquire(requestId, otherFingerprint, queueState.ListenerId, queueState.ListenerGeneration);
            restored.AuthorizeAndCapture(requestId, otherFingerprint, digest, grant.Token, queueState.ListenerId, queueState.ListenerGeneration, deadline);
            var loaded = UnityCliActiveOperation.Load("build-terminal-cleanup");

            Assert.Throws<UnityCliBridge.BridgeCommandException>(() => UnityCliBridge.ReconcileAdmissionTransferForLiveMarker(queueState, "build-terminal-cleanup", loaded, requestId, digest));
            Assert.That(restored.Capture().RecoveryBlocked, Is.True);
            Assert.That(UnityCliActiveOperation.Exists("build-terminal-cleanup"), Is.True);
        }

        private CancelFixture PrepareCancel(UnityCliAdmissionPool pool, string parentId, string parentDigest, string runId, string cancelId, string frozenCancelDigest = null, long? frozenDeadline = null)
        {
            var deadline = frozenDeadline ?? DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
            var parentOwner = Consume(pool, parentId, parentDigest, deadline);
            var cancelDigest = frozenCancelDigest ?? UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(cancelId), InstanceId);
            var cancelOwner = Consume(pool, cancelId, cancelDigest, deadline);
            Assert.That(UnityCliBridge.UnityCliSessionMutationLease.TryAcquire(parentId, parentDigest, out _), Is.True);
            var testOperation = new Dictionary<string, object>
            {
                { "phase", "running" }, { "runId", runId }, { "cancelRequestId", cancelId }, { "cancelRequestDigest", cancelDigest },
                { "cancelRootRequestDigest", parentDigest }, { "cancelRunId", runId }, { "cancelEffectState", "dispatching" },
                { "cancelNeedsContinuation", false }, { "cancelRequested", true }
            };
            UnityCliBridge.AddAdmissionOwnerFields(testOperation, cancelOwner);
            UnityCliActiveOperation.Save("test-run", new Dictionary<string, object>
            {
                { "requestId", parentId }, { "requestDigest", parentDigest }, { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "absoluteDeadlineUtc", DateTimeOffset.FromUnixTimeMilliseconds(deadline).ToString("O") }, { "testOperation", testOperation }
            });
            UnityCliBridge.PersistAdmissionOwnerTransfer(queueState, "test-run-admission-owner", new Dictionary<string, object>
            {
                { "requestId", parentId }, { "requestDigest", parentDigest }, { "editorSessionId", UnityCliBridge.EditorSessionId() }
            }, parentOwner, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator);
            return new CancelFixture { ParentId = parentId, ParentDigest = parentDigest, RunId = runId, CancelId = cancelId, CancelDigest = cancelDigest, Deadline = deadline };
        }

        private void CompleteParent(CancelFixture fixture)
        {
            var owner = UnityCliBridge.CaptureAdmissionOwner(queueState, fixture.ParentId, fixture.ParentDigest);
            owner = UnityCliBridge.PersistAdmissionOwnerTransfer(queueState, "test-run-terminal-cleanup", new Dictionary<string, object>
            {
                { "requestId", fixture.ParentId }, { "requestDigest", fixture.ParentDigest },
                { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "completeAdmission", true }
            }, owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
            UnityCliActiveOperation.Clear("test-run");
            UnityCliBridge.UnityCliSessionMutationLease.Release(fixture.ParentId, fixture.ParentDigest);
            Assert.That(UnityCliBridge.CompleteAdmissionRoot(queueState, owner), Is.True);
            UnityCliActiveOperation.Clear("test-run-terminal-cleanup");
            UnityCliActiveOperation.Clear("test-run-admission-owner");
        }

        private UnityCliBridge.AdmissionOwnerIdentity Consume(UnityCliAdmissionPool pool, string requestId, string digest, long deadline)
        {
            var grant = pool.Acquire(requestId, Fingerprint, queueState.ListenerId, queueState.ListenerGeneration);
            Assert.That(grant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            var result = pool.AuthorizeAndCapture(requestId, Fingerprint, digest, grant.Token, queueState.ListenerId, queueState.ListenerGeneration, deadline);
            Assert.That(result.Outcome, Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            return UnityCliBridge.CaptureAdmissionOwner(queueState, requestId, digest);
        }

        private UnityCliBridge.BridgeWorkItem Enqueue(BridgeRequest request, byte[] raw, string fingerprint, long deadline, UnityCliAdmissionPool pool, out bool created)
        {
            var record = pool.Capture().Records.First(value => value.RequestId == request.RequestId);
            var reservation = new UnityCliBridge.AdmissionQueueReservation
            {
                RequestId = request.RequestId, RequestDigest = UnityCliRequestDigest.Compute(raw, request.InstanceId),
                RootFingerprint = fingerprint, Token = record.Token, ExecutionDeadlineMs = deadline,
                HasAdmissionHeaders = true, IsBypass = false
            };
            int depth;
            return UnityCliBridge.EnqueuePreparedForTests(request, raw, reservation, queueState, out created, out depth);
        }

        private static BridgeRequest Request(string requestId, string command, Dictionary<string, object> input)
        {
            return new BridgeRequest
            {
                ProtocolVersion = 2, InstanceId = InstanceId, Command = command, RequestId = requestId, Input = input,
                Execution = new BridgeExecution
                {
                    AbsoluteDeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
                    CompletionContractVersion = 1, RequestDigestVersion = 1
                }
            };
        }

        private static byte[] RequestBytes(BridgeRequest request) { return Encoding.UTF8.GetBytes(UnityCliSerializer.Serialize(request)); }
        private static long Deadline(BridgeRequest request) { return DateTimeOffset.Parse(request.Execution.AbsoluteDeadlineUtc).ToUnixTimeMilliseconds(); }
        private static Dictionary<string, object> Root(string requestId, string digest, string phase)
        {
            return new Dictionary<string, object>
            {
                { "requestId", requestId }, { "requestDigest", digest }, { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "phase", phase }
            };
        }

        private string Unique(string suffix) { return identityPrefix + suffix; }

        private static IEnumerable<string> TrackedPaths()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var stateRoot = Path.Combine(projectRoot, "Library", "UnityCli");
            foreach (var scope in TrackedScopes) yield return Path.Combine(stateRoot, "active-" + scope + ".json");
            yield return Path.Combine(stateRoot, "terminal-cache.json");
            var lease = UnityCliBridge.UnityCliSessionMutationLease.LeasePathForTests();
            yield return lease;
            yield return lease + ".recovery";
            yield return lease + ".quarantine";
        }

        private BridgeRequest BuildRequest(string requestId)
        {
            return Request(requestId, "internal.build.run.start", new Dictionary<string, object>
            {
                { "expectedEditorSessionId", UnityCliBridge.EditorSessionId() },
                { "targets", new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            { "target", "StandaloneWindows64" }, { "outputPath", "Builds/test-player.exe" }
                        }
                    }
                }
            });
        }

        private static UnityCliBridge.UnityCliLiveBuildCoordinator.BuildExecutionDependencies BuildDependencies(Func<UnityCliBridge.UnityCliLiveBuildCoordinator.Prepared, UnityCliBridge.UnityCliLiveBuildCoordinator.BuildEffectResult> effect)
        {
            return new UnityCliBridge.UnityCliLiveBuildCoordinator.BuildExecutionDependencies
            {
                PrepareTarget = (target, outputPath, options) => new UnityCliBridge.UnityCliLiveBuildCoordinator.Prepared
                {
                    Target = BuildTarget.StandaloneWindows64,
                    TargetName = target,
                    OutputPath = Application.dataPath,
                    PublicOutputPath = outputPath,
                    Scenes = new string[0],
                    Options = BuildOptions.None
                },
                RunEffect = effect
            };
        }

        private static void AssertBuildSettled(UnityCliAdmissionPool pool, string requestId, string digest)
        {
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(UnityCliActiveOperation.Exists("build"), Is.False);
            Assert.That(UnityCliActiveOperation.Exists("build-terminal-cleanup"), Is.False);
            Assert.That(UnityCliActiveOperation.Exists("build-admission-owner"), Is.False);
            Assert.That(UnityCliBridge.UnityCliSessionMutationLease.TryIsOwnedBy(requestId, digest, out var owned), Is.True);
            Assert.That(owned, Is.False);
        }

        private sealed class CancelFixture
        {
            internal string ParentId;
            internal string ParentDigest;
            internal string RunId;
            internal string CancelId;
            internal string CancelDigest;
            internal long Deadline;
        }
    }
}
#endif
