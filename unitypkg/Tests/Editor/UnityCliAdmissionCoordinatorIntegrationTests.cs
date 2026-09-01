#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityCli.Protocol;

namespace UnityCli.Tests
{
    [NonParallelizable]
    public sealed class UnityCliAdmissionCoordinatorIntegrationTests
    {
        private const string InstanceId = "test-instance";
        private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        [TearDown]
        public void TearDown()
        {
            UnityCliBridge.UnityCliPackageResolveCoordinator.PackageStartTestBoundary = null;
            UnityCliBridge.UnityCliPackageResolveCoordinator.PackageResolveEffectTestBoundary = null;
            UnityCliBridge.UnityCliTestRunCoordinator.CancelFinalizerTestBoundary = null;
            try
            {
                string leaseRequestId;
                string leaseRequestDigest;
                if (UnityCliBridge.UnityCliSessionMutationLease.TryGetCurrentOwner(out leaseRequestId, out leaseRequestDigest)
                    && !string.IsNullOrEmpty(leaseRequestId) && !string.IsNullOrEmpty(leaseRequestDigest))
                    UnityCliBridge.UnityCliSessionMutationLease.Release(leaseRequestId, leaseRequestDigest);
            }
            catch { }
            try { UnityCliBridge.EndQueueTestScope(); } catch { }
            foreach (var scope in new[]
            {
                "build", "build-terminal-cleanup", "build-admission-owner", "package-resolve",
                "package-resolve-terminal-cleanup", "package-resolve-admission-owner", "test-run",
                "test-run-terminal-cleanup", "test-run-admission-owner", "test-cancel-admission-owner"
            })
            {
                try { UnityCliActiveOperation.Clear(scope); } catch { }
            }
        }

        [Test]
        public void BuildArtifactProofFaultUsesProductionResultValidation()
        {
            var exception = Assert.Throws<UnityCliBridge.BridgeCommandException>(() =>
                UnityCliBridge.UnityCliLiveBuildCoordinator.ValidateBuildEffectForTests(
                    new UnityCliBridge.UnityCliLiveBuildCoordinator.Prepared
                    {
                        Target = BuildTarget.StandaloneWindows64,
                        TargetName = "StandaloneWindows64",
                        OutputPath = Application.dataPath,
                        PublicOutputPath = "Assets/test-build",
                        Scenes = new string[0],
                        Options = BuildOptions.None
                    },
                    new UnityCliBridge.UnityCliLiveBuildCoordinator.BuildEffectResult
                    {
                        Result = "Succeeded",
                        Artifact = new Dictionary<string, object>
                        {
                            { "exists", false }, { "fileCount", (long)0 }, { "size", (long)0 },
                            { "hash", null }, { "treeHash", null }
                        }
                    }));
            Assert.That(exception.Code, Is.EqualTo("E_BUILD_ARTIFACT_MISSING"));
        }

        [Test]
        public void BuildPostRootFaultDefersAdmissionUntilQueueCompletion()
        {
            var request = Request("build-post-root-fault", "internal.build.run.start", new Dictionary<string, object>());
            var raw = RequestBytes(request);
            var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var deadline = Deadline(request);
            var pool = new UnityCliAdmissionPool(InstanceId, "build-test-session", 4, 1);
            UnityCliBridge.BeginQueueTestScope(pool);
            Consume(pool, request.RequestId, digest, deadline);
            bool created;
            var item = Enqueue(request, raw, Fingerprint, deadline, pool, out created);
            Assert.That(created, Is.True);
            Assert.That(UnityCliBridge.UnityCliSessionMutationLease.TryAcquire(request.RequestId, digest, out _), Is.True);
            UnityCliActiveOperation.Save("build", Root(request.RequestId, digest, "running"));

            var response = UnityCliBridge.UnityCliLiveBuildCoordinator.FinishPostRootFaultForTests(request.RequestId, digest, "E_BUILD_ARTIFACT_MISSING");

            Assert.That(response, Does.Contain("E_BUILD_ARTIFACT_MISSING"));
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, response), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            UnityCliActiveOperation.RetryExactTerminalCleanup("build");
            Assert.That(UnityCliActiveOperation.Exists("build"), Is.False);
            Assert.That(UnityCliActiveOperation.Exists("build-terminal-cleanup"), Is.False);
            Assert.That(UnityCliBridge.UnityCliSessionMutationLease.TryIsOwnedBy(request.RequestId, digest, out var owned), Is.True);
            Assert.That(owned, Is.False);
        }

        [Test]
        public void PackageRootCreatedBridgeFaultUsesFirstTerminalAndExactCleanup()
        {
            var request = Request("package-root-created-fault", "internal.package.resolve.start", new Dictionary<string, object>
            {
                { "expectedEditorSessionId", UnityCliBridge.EditorSessionId() },
                { "operation", "resolve" },
                { "input", new Dictionary<string, object>() }
            });
            var raw = RequestBytes(request);
            var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var deadline = Deadline(request);
            var pool = new UnityCliAdmissionPool(InstanceId, "package-test-session", 4, 1);
            UnityCliBridge.BeginQueueTestScope(pool);
            Consume(pool, request.RequestId, digest, deadline);
            bool created;
            var item = Enqueue(request, raw, Fingerprint, deadline, pool, out created);
            Assert.That(created, Is.True);
            UnityCliBridge.UnityCliPackageResolveCoordinator.PackageStartTestBoundary = name =>
            {
                if (name == "after-package-root-created")
                    throw new UnityCliBridge.BridgeCommandException("E_PACKAGE_ROOT_TEST_FAULT", "Injected root-created package fault.");
            };

            var response = UnityCliBridge.UnityCliPackageResolveCoordinator.Execute(request, raw, () => true, CancellationToken.None).GetAwaiter().GetResult();

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
            var request = Request("package-post-effect-fault", "internal.package.resolve.start", new Dictionary<string, object>
            {
                { "expectedEditorSessionId", UnityCliBridge.EditorSessionId() },
                { "operation", "resolve" },
                { "input", new Dictionary<string, object>() }
            });
            var raw = RequestBytes(request);
            var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var deadline = Deadline(request);
            var pool = new UnityCliAdmissionPool(InstanceId, "package-effect-session", 4, 1);
            UnityCliBridge.BeginQueueTestScope(pool);
            Consume(pool, request.RequestId, digest, deadline);
            bool created;
            var item = Enqueue(request, raw, Fingerprint, deadline, pool, out created);
            var accepted = UnityCliBridge.UnityCliPackageResolveCoordinator.Execute(request, raw, () => true, CancellationToken.None).GetAwaiter().GetResult();
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
            var pool = new UnityCliAdmissionPool(InstanceId, "cancel-test-session", 4, 1);
            UnityCliBridge.BeginQueueTestScope(pool);
            var notInvoked = PrepareCancel(pool, "cancel-parent-a", new string('a', 64), "11111111-1111-1111-1111-111111111111", "cancel-not-invoked");

            Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.FinalizeCancelForTests(notInvoked.ParentId, notInvoked.ParentDigest, notInvoked.RunId, notInvoked.CancelId, notInvoked.CancelDigest, "not_invoked"), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            var saved = UnityCliActiveOperation.Load("test-run");
            var operation = (Dictionary<string, object>)saved["testOperation"];
            Assert.That(operation.ContainsKey("cancelRequestId"), Is.False);
            CompleteParent(notInvoked);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));

            var unknown = PrepareCancel(pool, "cancel-parent-b", new string('b', 64), "22222222-2222-2222-2222-222222222222", "cancel-unknown");
            Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.FinalizeCancelForTests(unknown.ParentId, unknown.ParentDigest, unknown.RunId, unknown.CancelId, unknown.CancelDigest, "outcome_unknown"), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(UnityCliActiveOperation.Exists("test-run"), Is.False);
        }

        [Test]
        public void CancelReturnedTransfersToContinuationBeforeTerminalCompletion()
        {
            var pool = new UnityCliAdmissionPool(InstanceId, "cancel-returned-session", 4, 1);
            UnityCliBridge.BeginQueueTestScope(pool);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
            var request = Request("cancel-returned", "internal.test.run.cancel", new Dictionary<string, object>());
            request.Execution.AbsoluteDeadlineUtc = DateTimeOffset.FromUnixTimeMilliseconds(deadline).ToString("O");
            var raw = RequestBytes(request);
            var cancelDigest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var fixture = PrepareCancel(pool, "cancel-parent-returned", new string('e', 64), "44444444-4444-4444-4444-444444444444", "cancel-returned", cancelDigest, deadline);
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
            var item = UnityCliBridge.EnqueuePreparedForTests(request, raw, reservation, out created, out depth);
            var accepted = UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(fixture.CancelId, new Dictionary<string, object>
            {
                { "terminal", false }, { "accepted", true }, { "rootRequestDigest", fixture.ParentDigest }
            }));

            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, accepted), Is.True);
            var continuation = UnityCliBridge.CaptureAdmissionOwner(fixture.CancelId, fixture.CancelDigest);
            Assert.That(continuation.OwnerState, Is.EqualTo(UnityCliAdmissionPool.AdmissionOwnerState.Continuation));
            Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.FinalizeCancelForTests(fixture.ParentId, fixture.ParentDigest, fixture.RunId, fixture.CancelId, fixture.CancelDigest, "cancelled"), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            CompleteParent(fixture);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [Test]
        public void CancelDispatchSaveFaultRetainsRecoveryUntilRetryConverges()
        {
            var pool = new UnityCliAdmissionPool(InstanceId, "cancel-save-fault-session", 4, 1);
            UnityCliBridge.BeginQueueTestScope(pool);
            var fixture = PrepareCancel(pool, "cancel-parent-save", new string('c', 64), "33333333-3333-3333-3333-333333333333", "cancel-save-fault");
            UnityCliBridge.UnityCliTestRunCoordinator.CancelFinalizerTestBoundary = name =>
            {
                if (name == "before-cancel-root-save") throw new InvalidOperationException("injected save fault");
            };

            Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.FinalizeCancelForTests(fixture.ParentId, fixture.ParentDigest, fixture.RunId, fixture.CancelId, fixture.CancelDigest, "outcome_unknown"), Is.False);
            Assert.That(pool.Capture().Active, Is.EqualTo(2));
            Assert.That(pool.Capture().Records.Any(record => record.RequestId == fixture.CancelId && record.OwnerState == UnityCliAdmissionPool.AdmissionOwnerState.Recovery), Is.True);

            UnityCliBridge.UnityCliTestRunCoordinator.CancelFinalizerTestBoundary = null;
            Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.FinalizeCancelForTests(fixture.ParentId, fixture.ParentDigest, fixture.RunId, fixture.CancelId, fixture.CancelDigest, "outcome_unknown"), Is.True);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
        }

        [TestCase(true, true)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void CleanupMarkerReconcilesAllRootLeaseWindows(bool rootExists, bool leaseOwned)
        {
            var requestId = "marker-window-" + (rootExists ? "root" : "no-root") + "-" + (leaseOwned ? "lease" : "no-lease");
            var digest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(requestId), InstanceId);
            var original = new UnityCliAdmissionPool(InstanceId, "marker-window-session", 4, 1);
            var restored = new UnityCliAdmissionPool(InstanceId, "marker-window-session", 4, 1);
            UnityCliBridge.BeginQueueTestScope(original);
            var owner = Consume(original, requestId, digest, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());
            if (leaseOwned) Assert.That(UnityCliBridge.UnityCliSessionMutationLease.TryAcquire(requestId, digest, out _), Is.True);
            if (rootExists) UnityCliActiveOperation.Save("build", Root(requestId, digest, "terminal_pending"));
            var marker = new Dictionary<string, object>
            {
                { "requestId", requestId }, { "requestDigest", digest }, { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "completeAdmission", true }
            };
            UnityCliBridge.PersistAdmissionOwnerTransfer("build-terminal-cleanup", marker, owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
            UnityCliBridge.EndQueueTestScope();
            UnityCliBridge.BeginQueueTestScope(restored);

            Assert.That(UnityCliActiveOperation.RetryExactTerminalCleanup("build"), Is.False);
            Assert.That(restored.Capture().Active, Is.EqualTo(0));
            Assert.That(restored.Capture().RecoveryBlocked, Is.False);
            Assert.That(UnityCliActiveOperation.Exists("build-terminal-cleanup"), Is.False);
            Assert.That(UnityCliActiveOperation.Exists("build"), Is.False);
        }

        [Test]
        public void ReconciledMarkerRefreshesTheRecoveredListenerBinding()
        {
            var requestId = "marker-listener-rebind";
            var digest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(requestId), InstanceId);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
            var pool = new UnityCliAdmissionPool(InstanceId, "marker-listener-session", 4, 1);
            UnityCliBridge.BeginQueueTestScope(pool);
            var owner = Consume(pool, requestId, digest, deadline);
            var marker = new Dictionary<string, object>
            {
                { "requestId", requestId }, { "requestDigest", digest }, { "editorSessionId", UnityCliBridge.EditorSessionId() }
            };
            UnityCliBridge.PersistAdmissionOwnerTransfer("build-admission-owner", marker, owner, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator);
            Assert.That(pool.RecoverConsumed(requestId, Fingerprint, digest, "listener-after-reload", 2, deadline), Is.True);
            var loaded = UnityCliActiveOperation.Load("build-admission-owner");

            var reconciled = UnityCliBridge.ReconcileAdmissionTransferForLiveMarker("build-admission-owner", loaded, requestId, digest);

            Assert.That(reconciled.ListenerId, Is.EqualTo("listener-after-reload"));
            Assert.That(reconciled.ListenerGeneration, Is.EqualTo(2));
            var refreshed = UnityCliActiveOperation.Load("build-admission-owner");
            Assert.That(refreshed["admissionListenerId"], Is.EqualTo("listener-after-reload"));
            Assert.That(refreshed["admissionListenerGeneration"], Is.EqualTo((long)2));
        }

        [Test]
        public void CleanupMarkerIdentityMismatchBlocksRecovery()
        {
            var requestId = "marker-identity-mismatch";
            var digest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(requestId), InstanceId);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
            var original = new UnityCliAdmissionPool(InstanceId, "marker-mismatch-session", 4, 1);
            var restored = new UnityCliAdmissionPool(InstanceId, "marker-mismatch-session", 4, 1);
            UnityCliBridge.BeginQueueTestScope(original);
            var owner = Consume(original, requestId, digest, deadline);
            var marker = new Dictionary<string, object>
            {
                { "requestId", requestId }, { "requestDigest", digest }, { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "completeAdmission", true }
            };
            UnityCliBridge.PersistAdmissionOwnerTransfer("build-terminal-cleanup", marker, owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
            UnityCliBridge.EndQueueTestScope();
            UnityCliBridge.BeginQueueTestScope(restored);
            var otherFingerprint = new string('d', 64);
            var grant = restored.Acquire(requestId, otherFingerprint, UnityCliBridge.ListenerIdForTests, UnityCliBridge.ListenerGenerationForTests);
            restored.AuthorizeAndCapture(requestId, otherFingerprint, digest, grant.Token, UnityCliBridge.ListenerIdForTests, UnityCliBridge.ListenerGenerationForTests, deadline);
            var loaded = UnityCliActiveOperation.Load("build-terminal-cleanup");

            Assert.Throws<UnityCliBridge.BridgeCommandException>(() => UnityCliBridge.ReconcileAdmissionTransferForLiveMarker("build-terminal-cleanup", loaded, requestId, digest));
            Assert.That(restored.Capture().RecoveryBlocked, Is.True);
            Assert.That(UnityCliActiveOperation.Exists("build-terminal-cleanup"), Is.True);
        }

        private static CancelFixture PrepareCancel(UnityCliAdmissionPool pool, string parentId, string parentDigest, string runId, string cancelId, string frozenCancelDigest = null, long? frozenDeadline = null)
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
            UnityCliBridge.PersistAdmissionOwnerTransfer("test-run-admission-owner", new Dictionary<string, object>
            {
                { "requestId", parentId }, { "requestDigest", parentDigest }, { "editorSessionId", UnityCliBridge.EditorSessionId() }
            }, parentOwner, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator);
            return new CancelFixture { ParentId = parentId, ParentDigest = parentDigest, RunId = runId, CancelId = cancelId, CancelDigest = cancelDigest, Deadline = deadline };
        }

        private static void CompleteParent(CancelFixture fixture)
        {
            var owner = UnityCliBridge.CaptureAdmissionOwner(fixture.ParentId, fixture.ParentDigest);
            owner = UnityCliBridge.PersistAdmissionOwnerTransfer("test-run-terminal-cleanup", new Dictionary<string, object>
            {
                { "requestId", fixture.ParentId }, { "requestDigest", fixture.ParentDigest },
                { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "completeAdmission", true }
            }, owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
            UnityCliActiveOperation.Clear("test-run");
            UnityCliBridge.UnityCliSessionMutationLease.Release(fixture.ParentId, fixture.ParentDigest);
            Assert.That(UnityCliBridge.CompleteAdmissionRoot(owner), Is.True);
            UnityCliActiveOperation.Clear("test-run-terminal-cleanup");
            UnityCliActiveOperation.Clear("test-run-admission-owner");
        }

        private static UnityCliBridge.AdmissionOwnerIdentity Consume(UnityCliAdmissionPool pool, string requestId, string digest, long deadline)
        {
            var grant = pool.Acquire(requestId, Fingerprint, UnityCliBridge.ListenerIdForTests, UnityCliBridge.ListenerGenerationForTests);
            Assert.That(grant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            var result = pool.AuthorizeAndCapture(requestId, Fingerprint, digest, grant.Token, UnityCliBridge.ListenerIdForTests, UnityCliBridge.ListenerGenerationForTests, deadline);
            Assert.That(result.Outcome, Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            return UnityCliBridge.CaptureAdmissionOwner(requestId, digest);
        }

        private static UnityCliBridge.BridgeWorkItem Enqueue(BridgeRequest request, byte[] raw, string fingerprint, long deadline, UnityCliAdmissionPool pool, out bool created)
        {
            var record = pool.Capture().Records.First(value => value.RequestId == request.RequestId);
            var reservation = new UnityCliBridge.AdmissionQueueReservation
            {
                RequestId = request.RequestId, RequestDigest = UnityCliRequestDigest.Compute(raw, request.InstanceId),
                RootFingerprint = fingerprint, Token = record.Token, ExecutionDeadlineMs = deadline,
                HasAdmissionHeaders = true, IsBypass = false
            };
            int depth;
            return UnityCliBridge.EnqueuePreparedForTests(request, raw, reservation, out created, out depth);
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
