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
        private string identityPrefix;
        private string parentLeaseRequestId;
        private string parentLeaseRequestDigest;
        private BridgeQueueState queueState;
        private long monotonicNowMs;

        [SetUp]
        public void SetUp()
        {
            identityPrefix = "admission-" + Guid.NewGuid().ToString("N") + "-";
            Assert.That(UnityCliBridge.UnityCliSessionMutationLease.TryGetCurrentOwner(out parentLeaseRequestId, out parentLeaseRequestDigest), Is.True);
            monotonicNowMs = 1000L;
        }

        [TearDown]
        public void TearDown()
        {
            if (queueState != null) queueState.AssertEmptyForTests();
            string currentRequestId;
            string currentRequestDigest;
            Assert.That(UnityCliBridge.UnityCliSessionMutationLease.TryGetCurrentOwner(out currentRequestId, out currentRequestDigest), Is.True);
            Assert.That(currentRequestId, Is.EqualTo(parentLeaseRequestId));
            Assert.That(currentRequestDigest, Is.EqualTo(parentLeaseRequestDigest));
        }

        [Test]
        public void DirectTerminalRootClearFaultReplaysCleanupUntilAllRecoveryStateIsGone()
        {
            var requestId = Unique("direct-root-clear");
            var digest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(requestId), InstanceId);
            var pool = CreatePool("direct-root-clear-session");
            Consume(pool, requestId, digest, TestDeadline());
            bool acquired;
            Assert.That(queueState.TryAcquireLease(requestId, digest, out acquired), Is.True);
            Assert.That(acquired, Is.True);
            queueState.SaveOperation("direct-mutation", new Dictionary<string, object>
            {
                { "requestId", requestId }, { "requestDigest", digest },
                { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "phase", "effect_started" }
            });
            stateHookClearDirectRootOnce();

            var terminal = UnityCliBridge.PublishDirectMutationTerminalForTests(queueState, requestId, digest,
                UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(requestId, new Dictionary<string, object> { { "changed", true } })));

            Assert.That(terminal, Does.Contain("\"changed\":true"));
            Assert.That(queueState.OperationExists("direct-mutation"), Is.True);
            Assert.That(queueState.OperationExists("direct-mutation-terminal-cleanup"), Is.True);
            var directMarker = queueState.LoadOperation("direct-mutation-terminal-cleanup");
            Assert.That(directMarker["requestId"], Is.EqualTo(requestId));
            Assert.That(directMarker["requestDigest"], Is.EqualTo(digest));
            Assert.That(directMarker["admissionRequestDigest"], Is.EqualTo(digest));
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(queueState.TryIsLeaseOwned(requestId, digest, out var owned), Is.True);
            Assert.That(owned, Is.True);

            var replay = UnityCliBridge.RecoverPublishedDirectMutationForTests(queueState, requestId, digest);
            Assert.That(replay, Is.EqualTo(terminal));
            Assert.That(queueState.OperationExists("direct-mutation"), Is.False);
            Assert.That(queueState.OperationExists("direct-mutation-terminal-cleanup"), Is.False);
            Assert.That(queueState.OperationExists("direct-mutation-admission-owner"), Is.False);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(queueState.TryIsLeaseOwned(requestId, digest, out owned), Is.True);
            Assert.That(owned, Is.False);
        }

        [Test]
        public void CancelContinuationCompletesExactCancelOwnerWithoutIllegalTerminalTransfer()
        {
            var parentRequestId = Unique("test-parent");
            var parentDigest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(parentRequestId), InstanceId);
            var cancelRequestId = Unique("test-cancel");
            var cancelDigest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(cancelRequestId), InstanceId);
            var runId = Guid.NewGuid().ToString();
            var deadline = TestDeadline();
            var pool = CreatePool("cancel-continuation-session");
            var cancelOwner = Consume(pool, cancelRequestId, cancelDigest, deadline);
            queueState.SaveOperation("test-run", new Dictionary<string, object>
            {
                { "requestId", parentRequestId }, { "requestDigest", parentDigest },
                { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "absoluteDeadlineUtc", DateTimeOffset.FromUnixTimeMilliseconds(deadline).ToString("O") },
                { "testOperation", new Dictionary<string, object>
                    {
                        { "phase", "running" }, { "runId", runId }, { "cancelRunId", runId },
                        { "cancelRequestId", cancelRequestId }, { "cancelRequestDigest", cancelDigest },
                        { "cancelRootRequestDigest", parentDigest }, { "cancelEffectState", "dispatched" },
                        { "cancelRequested", true }
                    }
                }
            });
            bool acquired;
            Assert.That(queueState.TryAcquireLease(parentRequestId, parentDigest, out acquired), Is.True);
            Assert.That(acquired, Is.True);
            cancelOwner.TopRequestDigest = parentDigest;
            UnityCliBridge.PersistAdmissionOwnerTransfer(queueState, "test-cancel-admission-owner", new Dictionary<string, object>
            {
                { "requestId", cancelRequestId }, { "requestDigest", parentDigest },
                { "rootRequestId", parentRequestId }, { "rootRequestDigest", parentDigest },
                { "runId", runId }, { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "parentRepairEffectState", "cancelled" }, { "parentRepairPending", false }
            }, cancelOwner, UnityCliAdmissionPool.AdmissionOwnerState.Continuation);

            queueState.TestHooks.ExpectOnce("before-state-clear-markers", () =>
            {
                throw new IOException("injected cancel marker clear fault");
            });
            var result = UnityCliBridge.UnityCliTestRunCoordinator.FinalizeCancelTerminalForTests(queueState,
                parentRequestId, parentDigest, runId, cancelRequestId, cancelDigest,
                ProtocolEnvelopeFactory.Success(cancelRequestId, new Dictionary<string, object> { { "cancelled", true } }), "cancelled");

            Assert.That(result, Is.False);
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(queueState.OperationExists("test-cancel-admission-owner"), Is.True);
            var cancelMarker = queueState.LoadOperation("test-cancel-admission-owner");
            Assert.That(cancelMarker["requestId"], Is.EqualTo(cancelRequestId));
            Assert.That(cancelMarker["requestDigest"], Is.EqualTo(parentDigest));
            Assert.That(cancelMarker["admissionRequestDigest"], Is.EqualTo(cancelDigest));
            Assert.That(cancelMarker["rootRequestDigest"], Is.EqualTo(parentDigest));
            var completionOwner = UnityCliBridge.ReadAdmissionOwnerFields(cancelMarker, cancelRequestId);
            completionOwner.TopRequestDigest = parentDigest;
            Assert.That(stateHasCompletionIntent(completionOwner), Is.True);
            Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.RetryCancelAdmissionOwnerMarkerForTests(queueState), Is.False);
            Assert.That(queueState.OperationExists("test-cancel-admission-owner"), Is.False);
            Assert.That(queueState.OperationExists("test-run"), Is.True);
            Assert.That(queueState.TryIsLeaseOwned(parentRequestId, parentDigest, out var parentOwned), Is.True);
            Assert.That(parentOwned, Is.True);
            Assert.That(queueState.TryGetTerminal("test-cancel", cancelRequestId, cancelDigest, out var exactTerminal), Is.True);
            Assert.That(queueState.TryGetTerminalBySelector("test-cancel", cancelRequestId, parentDigest, out var selectedTerminal), Is.True);
            Assert.That(UnityCliSerializer.Serialize(exactTerminal), Is.EqualTo(UnityCliSerializer.Serialize(selectedTerminal)));
            Assert.That(pool.Capture().RecoveryBlocked, Is.False);
            queueState.ClearOperation("test-run");
            queueState.ReleaseLease(parentRequestId, parentDigest);
        }

        [Test]
        public void CancelParentSaveFaultRetainsOwnerUntilRetryRepairsExactRoot()
        {
            var parentRequestId = Unique("save-fault-parent");
            var parentDigest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(parentRequestId), InstanceId);
            var cancelRequestId = Unique("save-fault-cancel");
            var cancelDigest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(cancelRequestId), InstanceId);
            var runId = Guid.NewGuid().ToString();
            var deadline = TestDeadline();
            var pool = CreatePool("cancel-save-fault-session");
            Consume(pool, cancelRequestId, cancelDigest, deadline);
            queueState.SaveOperation("test-run", new Dictionary<string, object>
            {
                { "requestId", parentRequestId }, { "requestDigest", parentDigest },
                { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "absoluteDeadlineUtc", DateTimeOffset.FromUnixTimeMilliseconds(deadline).ToString("O") },
                { "testOperation", new Dictionary<string, object>
                    {
                        { "phase", "running" }, { "runId", runId }, { "cancelRunId", runId },
                        { "cancelRequestId", cancelRequestId }, { "cancelRequestDigest", cancelDigest },
                        { "cancelRootRequestDigest", parentDigest }, { "cancelEffectState", "dispatching" },
                        { "cancelRequested", true }
                    }
                }
            });
            Assert.That(queueState.TryAcquireLease(parentRequestId, parentDigest, out var acquired), Is.True);
            Assert.That(acquired, Is.True);
            queueState.TestHooks.ExpectOnce("before-operation-save:test-run", () => { throw new IOException("injected parent save fault"); });

            var result = UnityCliBridge.UnityCliTestRunCoordinator.FinalizeCancelTerminalForTests(queueState,
                parentRequestId, parentDigest, runId, cancelRequestId, cancelDigest,
                ProtocolEnvelopeFactory.Success(cancelRequestId, new Dictionary<string, object> { { "cancelled", true } }), "cancelled");

            Assert.That(result, Is.False);
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            var retained = queueState.LoadOperation("test-cancel-admission-owner");
            Assert.That(retained["parentRepairEffectState"], Is.EqualTo("cancelled"));
            Assert.That(retained["parentRepairPending"], Is.EqualTo(true));
            var staleRoot = queueState.LoadOperation("test-run");
            var staleOperation = (Dictionary<string, object>)staleRoot["testOperation"];
            Assert.That(staleOperation["cancelEffectState"], Is.EqualTo("dispatching"));

            Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.RetryCancelAdmissionOwnerMarkerForTests(queueState), Is.False);

            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(queueState.OperationExists("test-cancel-admission-owner"), Is.False);
            var repairedRoot = queueState.LoadOperation("test-run");
            var repairedOperation = (Dictionary<string, object>)repairedRoot["testOperation"];
            Assert.That(repairedOperation["cancelEffectState"], Is.EqualTo("cancelled"));
            Assert.That(repairedOperation["cancelRequested"], Is.EqualTo(true));
            Assert.That(repairedOperation["cancelTerminalEnvelope"], Is.Not.Null);
            queueState.ClearOperation("test-run");
            queueState.ReleaseLease(parentRequestId, parentDigest);
        }

        [Test]
        public void CancelMarkerReloadRestoresExactPoolOwnerBeforeDedicatedCleanup()
        {
            var parentRequestId = Unique("reload-parent");
            var parentDigest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(parentRequestId), InstanceId);
            var cancelRequestId = Unique("reload-cancel");
            var cancelDigest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(cancelRequestId), InstanceId);
            var runId = Guid.NewGuid().ToString();
            var deadline = TestDeadline();
            var original = CreatePool("cancel-reload-session");
            var owner = Consume(original, cancelRequestId, cancelDigest, deadline);
            queueState.SaveOperation("test-run", new Dictionary<string, object>
            {
                { "requestId", parentRequestId }, { "requestDigest", parentDigest },
                { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "absoluteDeadlineUtc", DateTimeOffset.FromUnixTimeMilliseconds(deadline).ToString("O") },
                { "testOperation", new Dictionary<string, object>
                    {
                        { "phase", "running" }, { "runId", runId }, { "cancelRunId", runId },
                        { "cancelRequestId", cancelRequestId }, { "cancelRequestDigest", cancelDigest },
                        { "cancelRootRequestDigest", parentDigest }, { "cancelEffectState", "dispatched" },
                        { "cancelRequested", true }
                    }
                }
            });
            Assert.That(queueState.TryAcquireLease(parentRequestId, parentDigest, out var parentAcquired), Is.True);
            Assert.That(parentAcquired, Is.True);
            owner.TopRequestDigest = parentDigest;
            UnityCliBridge.PersistAdmissionOwnerTransfer(queueState, "test-cancel-admission-owner", new Dictionary<string, object>
            {
                { "requestId", cancelRequestId }, { "requestDigest", parentDigest },
                { "rootRequestId", parentRequestId }, { "rootRequestDigest", parentDigest },
                { "runId", runId }, { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "parentRepairEffectState", "cancelled" }, { "parentRepairPending", true }
            }, owner, UnityCliAdmissionPool.AdmissionOwnerState.Continuation);
            queueState.StoreTerminalFirst("test-cancel", cancelRequestId, cancelDigest, parentDigest,
                ProtocolEnvelopeFactory.Success(cancelRequestId, new Dictionary<string, object> { { "cancelled", true } }));
            var validCancelMarker = queueState.LoadOperation("test-cancel-admission-owner");
            var mismatchedCancelMarker = new Dictionary<string, object>(validCancelMarker)
            {
                ["rootRequestDigest"] = new string('b', 64)
            };
            queueState.SaveOperation("test-cancel-admission-owner", mismatchedCancelMarker);
            queueState.AdmissionPool = new UnityCliAdmissionPool(InstanceId, original.Capture().Records.Single().SessionId, 4, 1);
            Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.RetryCancelAdmissionOwnerMarkerForTests(queueState), Is.True);
            Assert.That(queueState.AdmissionPool.Capture().RecoveryBlocked, Is.False);
            queueState.SaveOperation("test-cancel-admission-owner", validCancelMarker);

            Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.RetryCancelAdmissionOwnerMarkerForTests(queueState), Is.False);
            Assert.That(queueState.AdmissionPool.Capture().Active, Is.EqualTo(0));
            Assert.That(queueState.AdmissionPool.Capture().RecoveryBlocked, Is.False);
            Assert.That(queueState.OperationExists("test-cancel-admission-owner"), Is.False);
            Assert.That(queueState.OperationExists("test-run"), Is.True);
            Assert.That(queueState.TryIsLeaseOwned(parentRequestId, parentDigest, out var parentOwned), Is.True);
            Assert.That(parentOwned, Is.True);
            queueState.ClearOperation("test-run");
            queueState.ReleaseLease(parentRequestId, parentDigest);
        }

        [Test]
        public void DedicatedCancelMarkerRequiresCompleteCancelParentAndAdmissionIdentity()
        {
            var parentRequestId = Unique("identity-parent");
            var parentDigest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(parentRequestId), InstanceId);
            var cancelRequestId = Unique("identity-cancel");
            var cancelDigest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(cancelRequestId), InstanceId);
            var runId = Guid.NewGuid().ToString();
            var deadline = TestDeadline();
            var pool = CreatePool("cancel-identity-session");
            var owner = Consume(pool, cancelRequestId, cancelDigest, deadline);
            queueState.SaveOperation("test-run", new Dictionary<string, object>
            {
                { "requestId", parentRequestId }, { "requestDigest", parentDigest },
                { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "absoluteDeadlineUtc", DateTimeOffset.FromUnixTimeMilliseconds(deadline).ToString("O") },
                { "testOperation", new Dictionary<string, object>
                    {
                        { "phase", "running" }, { "runId", runId }, { "cancelRunId", runId },
                        { "cancelRequestId", cancelRequestId }, { "cancelRequestDigest", cancelDigest },
                        { "cancelRootRequestDigest", parentDigest }, { "cancelEffectState", "dispatched" },
                        { "cancelRequested", true }
                    }
                }
            });
            Assert.That(queueState.TryAcquireLease(parentRequestId, parentDigest, out var acquired), Is.True);
            Assert.That(acquired, Is.True);
            owner.TopRequestDigest = parentDigest;
            UnityCliBridge.PersistAdmissionOwnerTransfer(queueState, "test-cancel-admission-owner", new Dictionary<string, object>
            {
                { "requestId", cancelRequestId }, { "requestDigest", parentDigest },
                { "rootRequestId", parentRequestId }, { "rootRequestDigest", parentDigest },
                { "runId", runId }, { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "parentRepairEffectState", "cancelled" }, { "parentRepairPending", true }
            }, owner, UnityCliAdmissionPool.AdmissionOwnerState.Continuation);
            var record = pool.Capture().Records.Single();
            var valid = queueState.LoadOperation("test-cancel-admission-owner");
            Assert.That(UnityCliBridge.DedicatedCancelMarkerOwnershipForTests(queueState, record), Is.EqualTo("ExactOwner"));

            var wrongDigest = new Dictionary<string, object>(valid) { ["admissionRequestDigest"] = new string('b', 64) };
            queueState.SaveOperation("test-cancel-admission-owner", wrongDigest);
            Assert.That(UnityCliBridge.DedicatedCancelMarkerOwnershipForTests(queueState, record), Is.EqualTo("Blocked"));
            var wrongToken = new Dictionary<string, object>(valid) { ["admissionToken"] = new string('c', 64) };
            queueState.SaveOperation("test-cancel-admission-owner", wrongToken);
            Assert.That(UnityCliBridge.DedicatedCancelMarkerOwnershipForTests(queueState, record), Is.EqualTo("Blocked"));
            var wrongRoot = new Dictionary<string, object>(valid) { ["rootRequestDigest"] = new string('d', 64) };
            queueState.SaveOperation("test-cancel-admission-owner", wrongRoot);
            Assert.That(UnityCliBridge.DedicatedCancelMarkerOwnershipForTests(queueState, record), Is.EqualTo("Blocked"));
            foreach (var mismatch in new[]
            {
                new KeyValuePair<string, object>("admissionSessionId", "other-session"),
                new KeyValuePair<string, object>("admissionListenerId", "other-listener"),
                new KeyValuePair<string, object>("admissionListenerGeneration", (long)99),
                new KeyValuePair<string, object>("admissionExecutionDeadlineMs", deadline + 1L),
                new KeyValuePair<string, object>("runId", Guid.NewGuid().ToString())
            })
            {
                var changed = new Dictionary<string, object>(valid) { [mismatch.Key] = mismatch.Value };
                queueState.SaveOperation("test-cancel-admission-owner", changed);
                Assert.That(UnityCliBridge.DedicatedCancelMarkerOwnershipForTests(queueState, record), Is.EqualTo("Blocked"), mismatch.Key);
                Assert.That(pool.Capture().Active, Is.EqualTo(1), mismatch.Key);
            }

            queueState.ClearOperation("test-cancel-admission-owner");
            queueState.ClearOperation("test-run");
            queueState.ReleaseLease(parentRequestId, parentDigest);
            pool.Reset();
        }

        private bool stateHasCompletionIntent(UnityCliBridge.AdmissionOwnerIdentity owner)
        {
            return queueState != null && queueState.HasIntent(owner);
        }

        private void stateHookClearDirectRootOnce()
        {
            queueState.TestHooks.ExpectOnce("before-operation-clear:direct-mutation", () =>
            {
                throw new IOException("injected direct root clear fault");
            });
        }

        [Test]
        public void BuildArtifactFaultRunsRealExecuteAndExactQueueSettlement()
        {
            var request = BuildRequest(Unique("build-artifact-fault"));
            var raw = RequestBytes(request);
            var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var deadline = Deadline(request);
            var pool = CreatePool("build-artifact-session");
            Consume(pool, request.RequestId, digest, deadline);
            bool created;
            var item = Enqueue(request, raw, pool, out created);
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
            Assert.That(queueState.TryGetTerminal("build", request.RequestId, digest, out terminal), Is.True);
            Assert.That(UnityCliSerializer.Serialize(terminal), Does.Contain("E_BUILD_ARTIFACT_MISSING"));
            Assert.That(pool.Capture().Active, Is.EqualTo(1));
            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, response), Is.True);
            UnityCliBridge.UnityCliLiveBuildCoordinator.Tick(queueState);
            AssertBuildSettled(pool, request.RequestId, digest);
        }

        [TestCase("root-read")]
        [TestCase("terminal-store")]
        public void BuildPostRootFaultKeepsFrozenRecoveryUntilTickConverges(string fault)
        {
            var request = BuildRequest(Unique("build-post-root-" + fault));
            var raw = RequestBytes(request);
            var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
            var pool = CreatePool("build-recovery-session");
            Consume(pool, request.RequestId, digest, Deadline(request));
            bool created;
            var item = Enqueue(request, raw, pool, out created);
            Assert.That(created, Is.True);
            var dependencies = BuildDependencies(prepared => { throw new UnityCliBridge.BridgeCommandException("E_BUILD_EFFECT_TEST_FAULT", "Injected build effect fault."); });
            dependencies.Boundary.ExpectOnce(fault == "root-read" ? "before-build-terminal-root-read" : "before-build-terminal-store",
                () => { throw new IOException(fault == "root-read" ? "injected active root read fault" : "injected terminal store fault"); });

            var response = UnityCliBridge.UnityCliLiveBuildCoordinator.Execute(queueState, request, raw, () => true, CancellationToken.None, dependencies).GetAwaiter().GetResult();

            Assert.That(response, Does.Contain("E_EXECUTION_TIMEOUT"));
            dependencies.Boundary.AssertDrained();
            Assert.That(pool.Capture().Records.Any(record => record.RequestId == request.RequestId
                && (record.OwnerState == UnityCliAdmissionPool.AdmissionOwnerState.Recovery
                    || record.OwnerState == UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup)), Is.True);
            Assert.That(UnityCliBridge.CompleteWorkItemForTests(item, response), Is.True);
            UnityCliBridge.UnityCliLiveBuildCoordinator.Tick(queueState);

            object terminal;
            Assert.That(queueState.TryGetTerminal("build", request.RequestId, digest, out terminal), Is.True);
            Assert.That(UnityCliSerializer.Serialize(terminal), Does.Contain("E_EXECUTION_TIMEOUT"));
            AssertBuildSettled(pool, request.RequestId, digest);
        }

        [TestCase(true, true)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void CleanupMarkerReconcilesRootLeaseWindowsWithoutGlobalState(bool rootExists, bool leaseOwned)
        {
            var requestId = Unique("marker-window");
            var digest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(requestId), InstanceId);
            var original = CreatePool("marker-window-session");
            var owner = Consume(original, requestId, digest, TestDeadline());
            if (leaseOwned)
            {
                bool acquired;
                Assert.That(queueState.TryAcquireLease(requestId, digest, out acquired), Is.True);
                Assert.That(acquired, Is.True);
            }
            if (rootExists) queueState.SaveOperation("build", Root(requestId, digest, "terminal_pending"));
            var marker = new Dictionary<string, object>
            {
                { "requestId", requestId }, { "requestDigest", digest },
                { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "completeAdmission", true }
            };
            UnityCliBridge.PersistAdmissionOwnerTransfer(queueState, "build-terminal-cleanup", marker, owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
            queueState.AdmissionPool = new UnityCliAdmissionPool(InstanceId, original.Capture().Records.Single().SessionId, 4, 1);

            Assert.That(UnityCliActiveOperation.RetryExactTerminalCleanup("build", queueState), Is.False);
            Assert.That(queueState.AdmissionPool.Capture().Active, Is.EqualTo(0));
            Assert.That(queueState.OperationExists("build-terminal-cleanup"), Is.False);
            Assert.That(queueState.OperationExists("build"), Is.False);
            Assert.That(queueState.TryIsLeaseOwned(requestId, digest, out var owned), Is.True);
            Assert.That(owned, Is.False);
        }

        [Test]
        public void ReconciledMarkerRefreshesRecoveredListenerBinding()
        {
            var requestId = Unique("marker-listener-rebind");
            var digest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(requestId), InstanceId);
            var deadline = TestDeadline();
            var pool = CreatePool("marker-listener-session");
            var owner = Consume(pool, requestId, digest, deadline);
            var marker = new Dictionary<string, object>
            {
                { "requestId", requestId }, { "requestDigest", digest }, { "editorSessionId", UnityCliBridge.EditorSessionId() }
            };
            UnityCliBridge.PersistAdmissionOwnerTransfer(queueState, "build-admission-owner", marker, owner, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator);
            Assert.That(pool.RecoverConsumed(requestId, Fingerprint, digest, "listener-after-reload", 2, deadline), Is.True);
            var loaded = queueState.LoadOperation("build-admission-owner");

            var reconciled = UnityCliBridge.ReconcileAdmissionTransferForLiveMarker(queueState, "build-admission-owner", loaded, requestId, digest);

            Assert.That(reconciled.ListenerId, Is.EqualTo("listener-after-reload"));
            Assert.That(reconciled.ListenerGeneration, Is.EqualTo(2));
            var refreshed = queueState.LoadOperation("build-admission-owner");
            Assert.That(refreshed["admissionListenerId"], Is.EqualTo("listener-after-reload"));
            Assert.That(refreshed["admissionListenerGeneration"], Is.EqualTo((long)2));
            queueState.ClearOperation("build-admission-owner");
            var cleanupOwner = UnityCliBridge.CaptureAdmissionOwner(queueState, requestId, digest);
            cleanupOwner = UnityCliBridge.PersistAdmissionOwnerTransfer(queueState, "build-terminal-cleanup", new Dictionary<string, object>
            {
                { "requestId", requestId }, { "requestDigest", digest },
                { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "completeAdmission", true }
            }, cleanupOwner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
            Assert.That(UnityCliBridge.CompleteAdmissionRoot(queueState, cleanupOwner), Is.True);
            queueState.ClearOperation("build-terminal-cleanup");
        }

        [Test]
        public void CleanupMarkerIdentityMismatchBlocksWithoutClearingMarker()
        {
            var requestId = Unique("marker-identity-mismatch");
            var digest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(requestId), InstanceId);
            var deadline = TestDeadline();
            var original = CreatePool("marker-mismatch-session");
            var owner = Consume(original, requestId, digest, deadline);
            var marker = new Dictionary<string, object>
            {
                { "requestId", requestId }, { "requestDigest", digest },
                { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "completeAdmission", true }
            };
            UnityCliBridge.PersistAdmissionOwnerTransfer(queueState, "build-terminal-cleanup", marker, owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
            var restored = new UnityCliAdmissionPool(InstanceId, original.Capture().Records.Single().SessionId, 4, 1);
            queueState.AdmissionPool = restored;
            var otherFingerprint = new string('d', 64);
            var grant = restored.Acquire(requestId, otherFingerprint, queueState.ListenerId, queueState.ListenerGeneration);
            restored.AuthorizeAndCapture(requestId, otherFingerprint, digest, grant.Token, queueState.ListenerId, queueState.ListenerGeneration, deadline);
            var loaded = queueState.LoadOperation("build-terminal-cleanup");

            Assert.Throws<UnityCliBridge.BridgeCommandException>(() => UnityCliBridge.ReconcileAdmissionTransferForLiveMarker(queueState, "build-terminal-cleanup", loaded, requestId, digest));
            Assert.That(restored.Capture().RecoveryBlocked, Is.True);
            Assert.That(queueState.OperationExists("build-terminal-cleanup"), Is.True);
            queueState.ClearOperation("build-terminal-cleanup");
            restored.Reset();
        }

        private UnityCliAdmissionPool CreatePool(string suffix)
        {
            var pool = new UnityCliAdmissionPool(InstanceId, Unique(suffix), 4, 1);
            queueState = UnityCliBridge.CreateQueueStateForTests(pool, () => monotonicNowMs);
            return pool;
        }

        private UnityCliBridge.AdmissionOwnerIdentity Consume(UnityCliAdmissionPool pool, string requestId, string digest, long deadline)
        {
            var grant = pool.Acquire(requestId, Fingerprint, queueState.ListenerId, queueState.ListenerGeneration);
            Assert.That(grant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            var result = pool.AuthorizeAndCapture(requestId, Fingerprint, digest, grant.Token, queueState.ListenerId, queueState.ListenerGeneration, deadline);
            Assert.That(result.Outcome, Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            return UnityCliBridge.CaptureAdmissionOwner(queueState, requestId, digest);
        }

        private UnityCliBridge.BridgeWorkItem Enqueue(BridgeRequest request, byte[] raw, UnityCliAdmissionPool pool, out bool created)
        {
            var record = pool.Capture().Records.First(value => value.RequestId == request.RequestId);
            var reservation = new UnityCliBridge.AdmissionQueueReservation
            {
                RequestId = request.RequestId,
                RequestDigest = UnityCliRequestDigest.Compute(raw, request.InstanceId),
                RootFingerprint = Fingerprint,
                Token = record.Token,
                ExecutionDeadlineMs = Deadline(request),
                HasAdmissionHeaders = true,
                IsBypass = false
            };
            int depth;
            return UnityCliBridge.EnqueuePreparedForTests(request, raw, reservation, queueState, out created, out depth);
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
            return new UnityCliBridge.UnityCliLiveBuildCoordinator.BuildExecutionDependencies(true)
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

        private void AssertBuildSettled(UnityCliAdmissionPool pool, string requestId, string digest)
        {
            Assert.That(pool.Capture().Active, Is.EqualTo(0));
            Assert.That(queueState.OperationExists("build"), Is.False);
            Assert.That(queueState.OperationExists("build-terminal-cleanup"), Is.False);
            Assert.That(queueState.OperationExists("build-admission-owner"), Is.False);
            Assert.That(queueState.TryIsLeaseOwned(requestId, digest, out var owned), Is.True);
            Assert.That(owned, Is.False);
            Assert.That(UnityCliBridge.ActiveRequestWorkCountForTests(queueState), Is.EqualTo(0));
        }

        private static BridgeRequest Request(string requestId, string command, Dictionary<string, object> input)
        {
            return new BridgeRequest
            {
                ProtocolVersion = 2,
                InstanceId = InstanceId,
                Command = command,
                RequestId = requestId,
                Input = input,
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

        private static Dictionary<string, object> Root(string requestId, string digest, string phase)
        {
            return new Dictionary<string, object>
            {
                { "requestId", requestId }, { "requestDigest", digest },
                { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "phase", phase }
            };
        }

        private static long TestDeadline()
        {
            return DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        }

        private string Unique(string suffix)
        {
            return identityPrefix + suffix;
        }
    }
}
#endif
