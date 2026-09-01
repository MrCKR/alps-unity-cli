#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEditor;

namespace UnityCli.Tests
{
    public sealed class UnityCliAdmissionCoordinatorIntegrationTests
    {
        private const string InstanceId = "test-instance";
        private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        [Test]
        public void BuildArtifactProofFaultIsRaisedAfterEffectResultValidation()
        {
            UnityCliBridge.UnityCliLiveBuildCoordinator.BuildEffectExecutorForTests = prepared => new UnityCliBridge.UnityCliLiveBuildCoordinator.BuildEffectResult
            {
                Result = "Succeeded",
                Artifact = new Dictionary<string, object>
                {
                    { "exists", false }, { "fileCount", (long)0 }, { "size", (long)0 }
                }
            };
            try
            {
                var exception = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => UnityCliBridge.UnityCliLiveBuildCoordinator.BuildPreparedForTests(new UnityCliBridge.UnityCliLiveBuildCoordinator.Prepared
                {
                    Target = BuildTarget.StandaloneWindows64,
                    TargetName = "StandaloneWindows64",
                    OutputPath = Application.dataPath,
                    PublicOutputPath = "Assets/test-build",
                    Scenes = new string[0],
                    Options = BuildOptions.None
                }));
                Assert.That(exception.Code, Is.EqualTo("E_BUILD_ARTIFACT_MISSING"));
            }
            finally
            {
                UnityCliBridge.UnityCliLiveBuildCoordinator.BuildEffectExecutorForTests = null;
            }
        }

        [Test]
        public void BuildPostRootArtifactFaultUsesFirstTerminalCleanup()
        {
            var requestId = "build-post-root-fault";
            var raw = Encoding.UTF8.GetBytes("build-post-root-fault");
            var digest = UnityCliRequestDigest.Compute(raw, InstanceId);
            var pool = new UnityCliAdmissionPool(InstanceId, "build-test-session", 4, 1);
            UnityCliBridge.BeginQueueTestScope(pool);
            try
            {
                Consume(pool, requestId, digest);
                Assert.That(UnityCliSessionMutationLease.TryAcquire(requestId, digest, out _), Is.True);
                UnityCliActiveOperation.Save("build", new Dictionary<string, object>
                {
                    { "requestId", requestId }, { "requestDigest", digest }, { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "phase", "running" }
                });

                var response = UnityCliBridge.UnityCliLiveBuildCoordinator.FinishPostRootFaultForTests(requestId, digest, "E_BUILD_ARTIFACT_MISSING");

                Assert.That(response, Does.Contain("\"terminal\":true"));
                Assert.That(pool.Capture().Active, Is.EqualTo(0));
                Assert.That(UnityCliActiveOperation.Exists("build"), Is.False);
                Assert.That(UnityCliActiveOperation.Exists("build-terminal-cleanup"), Is.False);
                Assert.That(UnityCliSessionMutationLease.TryIsOwnedBy(requestId, digest, out var owned), Is.True);
                Assert.That(owned, Is.False);
            }
            finally
            {
                UnityCliActiveOperation.Clear("build");
                UnityCliActiveOperation.Clear("build-terminal-cleanup");
                UnityCliActiveOperation.Clear("build-admission-owner");
                UnityCliSessionMutationLease.Release(requestId, digest);
                UnityCliBridge.EndQueueTestScope();
            }
        }

        [Test]
        public void CancelNotInvokedAndOutcomeUnknownUseTheSameTerminalFinalizer()
        {
            var pool = new UnityCliAdmissionPool(InstanceId, "cancel-test-session", 4, 1);
            UnityCliBridge.BeginQueueTestScope(pool);
            try
            {
                var notInvokedId = "cancel-not-invoked";
                var notInvokedDigest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(notInvokedId), InstanceId);
                var notInvokedOwner = Consume(pool, notInvokedId, notInvokedDigest);
                SaveCancelRoot("cancel-parent-a", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "11111111-1111-1111-1111-111111111111", notInvokedId, notInvokedDigest);
                Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.FinalizeCancelForTests("cancel-parent-a", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "11111111-1111-1111-1111-111111111111", notInvokedId, notInvokedDigest, "not_invoked"), Is.True);
                Assert.That(pool.Capture().Active, Is.EqualTo(1));
                Assert.That(UnityCliBridge.CompleteAdmissionRoot(notInvokedOwner), Is.True);
                Assert.That(pool.Capture().Active, Is.EqualTo(0));
                UnityCliActiveOperation.Clear("test-run");

                var unknownId = "cancel-unknown";
                var unknownDigest = UnityCliRequestDigest.Compute(Encoding.UTF8.GetBytes(unknownId), InstanceId);
                Consume(pool, unknownId, unknownDigest);
                SaveCancelRoot("cancel-parent-b", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "22222222-2222-2222-2222-222222222222", unknownId, unknownDigest);
                Assert.That(UnityCliBridge.UnityCliTestRunCoordinator.FinalizeCancelForTests("cancel-parent-b", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "22222222-2222-2222-2222-222222222222", unknownId, unknownDigest, "outcome_unknown"), Is.True);
                Assert.That(pool.Capture().Active, Is.EqualTo(0));
            }
            finally
            {
                UnityCliActiveOperation.Clear("test-run");
                UnityCliActiveOperation.Clear("test-cancel");
                UnityCliActiveOperation.Clear("test-cancel-admission-owner");
                UnityCliBridge.EndQueueTestScope();
            }
        }

        [Test]
        public void MarkerWithAbsentPoolRecordRebuildsWhenRootAndLeaseRemainExact()
        {
            var requestId = "marker-reload-rebuild";
            var raw = Encoding.UTF8.GetBytes("marker-reload-rebuild");
            var digest = UnityCliRequestDigest.Compute(raw, InstanceId);
            var originalPool = new UnityCliAdmissionPool(InstanceId, "marker-test-session", 4, 1);
            var restoredPool = new UnityCliAdmissionPool(InstanceId, "marker-test-session", 4, 1);
            UnityCliBridge.BeginQueueTestScope(originalPool);
            try
            {
                var owner = Consume(originalPool, requestId, digest);
                Assert.That(UnityCliSessionMutationLease.TryAcquire(requestId, digest, out _), Is.True);
                UnityCliActiveOperation.Save("build", new Dictionary<string, object>
                {
                    { "requestId", requestId }, { "requestDigest", digest }, { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "phase", "effect_started" }
                });
                var marker = new Dictionary<string, object> { { "requestId", requestId }, { "requestDigest", digest }, { "editorSessionId", UnityCliBridge.EditorSessionId() } };
                var transferred = UnityCliBridge.PersistAdmissionOwnerTransfer("build-admission-owner", marker, owner, UnityCliAdmissionPool.AdmissionOwnerState.Coordinator);
                UnityCliBridge.EndQueueTestScope();
                UnityCliBridge.BeginQueueTestScope(restoredPool);

                UnityCliBridge.ReconcileAdmissionMarkersForTests();

                Assert.That(restoredPool.Capture().Active, Is.EqualTo(1));
                Assert.That(restoredPool.Capture().RecoveryBlocked, Is.False);
                var restoredIdentity = UnityCliAdmissionPool.CreateConsumedIdentity(transferred.RequestId, transferred.RootFingerprint, transferred.RequestDigest, transferred.Token, transferred.SessionId, transferred.OwnerState, transferred.ListenerId, transferred.ListenerGeneration, transferred.ExecutionDeadlineMs);
                Assert.That(restoredPool.CompleteTrusted(restoredIdentity), Is.EqualTo(UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted));
                UnityCliSessionMutationLease.Release(requestId, digest);
                UnityCliActiveOperation.Clear("build");
                UnityCliActiveOperation.Clear("build-admission-owner");
            }
            finally
            {
                UnityCliActiveOperation.Clear("build");
                UnityCliActiveOperation.Clear("build-admission-owner");
                UnityCliSessionMutationLease.Release(requestId, digest);
                UnityCliBridge.EndQueueTestScope();
            }
        }

        private static UnityCliBridge.AdmissionOwnerIdentity Consume(UnityCliAdmissionPool pool, string requestId, string digest)
        {
            var grant = pool.Acquire(requestId, Fingerprint, UnityCliBridge.ListenerIdForTests, UnityCliBridge.ListenerGenerationForTests);
            Assert.That(grant.Outcome, Is.EqualTo(UnityCliAdmissionPool.AcquireOutcome.Granted));
            var result = pool.AuthorizeAndCapture(requestId, Fingerprint, digest, grant.Token, UnityCliBridge.ListenerIdForTests, UnityCliBridge.ListenerGenerationForTests, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());
            Assert.That(result.Outcome, Is.EqualTo(UnityCliAdmissionPool.AuthorizeOutcome.Authorized));
            return UnityCliBridge.CaptureAdmissionOwner(requestId, digest);
        }

        private static void SaveCancelRoot(string parentId, string parentDigest, string runId, string cancelId, string cancelDigest)
        {
            UnityCliActiveOperation.Save("test-run", new Dictionary<string, object>
            {
                { "requestId", parentId }, { "requestDigest", parentDigest }, { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "absoluteDeadlineUtc", DateTimeOffset.UtcNow.AddMinutes(1).ToString("O") },
                { "testOperation", new Dictionary<string, object>
                    {
                        { "phase", "running" }, { "runId", runId }, { "cancelRequestId", cancelId }, { "cancelRequestDigest", cancelDigest },
                        { "cancelRootRequestDigest", parentDigest }, { "cancelRunId", runId }, { "cancelEffectState", "dispatching" }, { "cancelNeedsContinuation", false }
                    }
                }
            });
        }
    }
}
#endif
