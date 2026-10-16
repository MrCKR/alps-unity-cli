#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// debug session 定义、资源占用登记、有序清理管线与持久化载荷的行为测试。
    /// 全部断言只使用 engine-free 的 UnityCliDebugSessionRegistry，不需要 Unity 主线程状态。
    /// </summary>
    public sealed class DebugSessionTests
    {
        private const string EditorSession = "session-test";
        private const string Generation = "generation-a";
        private const string CreatedAtUtc = "2026-01-01T00:00:00.0000000Z";

        private static UnityCliDebugSessionCapacity Capacity()
        {
            return new UnityCliDebugSessionCapacity(16, 4, 32, 50, 200, 32 * 1024, 8L * 1024 * 1024);
        }

        [Test]
        public void SessionIdsAreMonotonicAndNeverReusedInsideOneEditorSession()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var first = registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);
            var second = registry.Create("beta", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 2d, false);
            Assert.That(first.Success, Is.True, first.Message);
            Assert.That(first.Definition.SessionId, Is.EqualTo("dbg-1"));
            Assert.That(second.Definition.SessionId, Is.EqualTo("dbg-2"));

            var stopped = registry.Stop(first.Definition.SessionId, CreatedAtUtc, 3d, 7);
            Assert.That(stopped.Success, Is.True, stopped.Message);
            Assert.That(registry.Remove(first.Definition.SessionId).Success, Is.True);

            var third = registry.Create("gamma", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 4d, false);
            Assert.That(third.Definition.SessionId, Is.EqualTo("dbg-3"), "a removed id must never be handed out again");
            Assert.That(registry.SessionCount, Is.EqualTo(2));
        }

        [Test]
        public void CreateRejectionsDoNotMutateStateAndReportTheCurrentOccupancy()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);

            var duplicate = registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 2d, false);
            Assert.That(duplicate.Failure, Is.EqualTo(UnityCliDebugSessionFailures.DuplicateLabel));
            Assert.That(duplicate.Message, Is.Not.Empty);
            Assert.That(duplicate.SessionsUsed, Is.EqualTo(1));

            var scopeConflict = registry.Create("beta", UnityCliDebugActivationScopes.Play, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 3d, false);
            Assert.That(scopeConflict.Failure, Is.EqualTo(UnityCliDebugSessionFailures.ScopeConflict));

            var tooLarge = registry.Create("beta", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 64L * 1024L, CreatedAtUtc, 4d, false);
            Assert.That(tooLarge.Failure, Is.EqualTo(UnityCliDebugSessionFailures.StateTooLarge));

            Assert.That(registry.SessionCount, Is.EqualTo(1), "every rejection must leave the definition table unchanged");
            Assert.That(registry.NextSessionIndex, Is.EqualTo(2), "a rejected create must not consume a session id");
        }

        [Test]
        public void TheSessionLimitFailsBeforeAnyEffect()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            for (var index = 0; index < Capacity().MaxSessions; index++)
            {
                var created = registry.Create("session-" + index, UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, index, false);
                Assert.That(created.Success, Is.True, created.Message);
            }
            var rejected = registry.Create("one-too-many", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 99d, false);
            Assert.That(rejected.Failure, Is.EqualTo(UnityCliDebugSessionFailures.LimitExceeded));
            Assert.That(rejected.SessionsUsed, Is.EqualTo(Capacity().MaxSessions));
            Assert.That(registry.SessionCount, Is.EqualTo(Capacity().MaxSessions));
        }

        [Test]
        public void ActiveResourceSlotsAreBoundedPerSessionAndReleasedOnRelease()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var created = registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);
            var sessionId = created.Definition.SessionId;
            var reservations = new List<UnityCliDebugResourceReservation>();
            for (var index = 0; index < Capacity().MaxActiveResourcesPerSession; index++)
            {
                var reservation = registry.Reserve(sessionId, UnityCliDebugResourceKinds.Hook, "Target.Method", "runtime-1", report => true);
                Assert.That(reservation.Success, Is.True, reservation.Message);
                Assert.That(registry.Attach(reservation.Resource.ResourceId), Is.True);
                reservations.Add(reservation);
            }
            var rejected = registry.Reserve(sessionId, UnityCliDebugResourceKinds.Input, "Target.Method", "runtime-1", report => true);
            Assert.That(rejected.Failure, Is.EqualTo(UnityCliDebugSessionFailures.LimitExceeded));
            Assert.That(rejected.ActiveResourcesUsed, Is.EqualTo(Capacity().MaxActiveResourcesPerSession));
            Assert.That(registry.ActiveResourceCount(sessionId), Is.EqualTo(Capacity().MaxActiveResourcesPerSession));

            Assert.That(registry.Release(reservations[0].Resource.ResourceId), Is.True);
            var afterRelease = registry.Reserve(sessionId, UnityCliDebugResourceKinds.Input, "Target.Other", "runtime-1", report => true);
            Assert.That(afterRelease.Success, Is.True, afterRelease.Message);
            Assert.That(registry.ActiveResourceCount(sessionId), Is.EqualTo(Capacity().MaxActiveResourcesPerSession));
        }

        [Test]
        public void AStoppedSessionRejectsNewResources()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var created = registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);
            var sessionId = created.Definition.SessionId;
            Assert.That(registry.Stop(sessionId, CreatedAtUtc, 2d, 7).Success, Is.True);
            var reservation = registry.Reserve(sessionId, UnityCliDebugResourceKinds.Hook, "Target.Method", string.Empty, report => true);
            Assert.That(reservation.Failure, Is.EqualTo(UnityCliDebugSessionFailures.WrongState));
        }

        [Test]
        public void StopRunsTheFrozenCleanupOrderAndReportsUnregisteredOwnersExplicitly()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var created = registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);
            var sessionId = created.Definition.SessionId;
            var executed = new List<string>();
            Assert.That(registry.RegisterCleanupStep(sessionId, 4, "close-observation", "observation", report => { executed.Add("close-observation"); return true; }), Is.Null);
            Assert.That(registry.RegisterCleanupStep(sessionId, 7, "release-waits", "run", report => { executed.Add("release-waits"); return true; }), Is.Null);
            Assert.That(registry.RegisterCleanupStep(sessionId, 1, "stop-dispatch", "hook", report => { executed.Add("stop-dispatch"); return true; }), Is.Null);

            var outcome = registry.Stop(sessionId, CreatedAtUtc, 2d, 42);
            Assert.That(outcome.Success, Is.True, outcome.Message);
            Assert.That(outcome.Changed, Is.True);
            Assert.That(executed, Is.EqualTo(new List<string> { "stop-dispatch", "close-observation", "release-waits" }), "cleanup must run in the frozen step order regardless of registration order");

            var report = outcome.Report;
            Assert.That(report.Steps.Count, Is.EqualTo(UnityCliDebugCleanupSteps.Ordered().Length));
            for (var index = 0; index < report.Steps.Count; index++)
            {
                Assert.That(report.Steps[index].Order, Is.EqualTo(index + 1));
                Assert.That(report.Steps[index].Name, Is.EqualTo(UnityCliDebugCleanupSteps.Ordered()[index]));
            }
            Assert.That(report.Steps[0].OwnerRegistered, Is.True);
            Assert.That(report.Steps[0].Owner, Is.EqualTo("hook"));
            Assert.That(report.Steps[2].OwnerRegistered, Is.False, "a step without a registered owner must be reported, not omitted");
            Assert.That(report.Steps[2].Note, Is.EqualTo(UnityCliDebugOwnerMarkers.NoOwnerRegistered));
            Assert.That(report.Steps[2].Entries, Is.EqualTo(0));
            Assert.That(report.Owners.Count, Is.EqualTo(UnityCliDebugFacilityOwners.All().Length));
            var registeredOwners = new List<string> { UnityCliDebugFacilityOwners.Hook, UnityCliDebugFacilityOwners.Observation, UnityCliDebugFacilityOwners.Run };
            foreach (var owner in report.Owners)
            {
                var expected = registeredOwners.Contains(owner.Owner);
                Assert.That(owner.Registered, Is.EqualTo(expected), "only owners that registered a cleanup entry may report a registered owner");
                Assert.That(owner.Note, Is.EqualTo(expected ? string.Empty : UnityCliDebugOwnerMarkers.NoOwnerRegistered));
            }
            Assert.That(report.Status, Is.EqualTo("complete"));
            Assert.That(report.CleanupPending, Is.False);
        }

        [Test]
        public void TheCleanupReportAlwaysContainsEveryFieldCategory()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var created = registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);
            var report = registry.Stop(created.Definition.SessionId, CreatedAtUtc, 2d, 42).Report;
            Assert.That(report.SessionId, Is.EqualTo("dbg-1"));
            Assert.That(report.ActiveThreadId, Is.EqualTo(42));
            Assert.That(report.ActiveRevision, Is.EqualTo(1));
            Assert.That(report.SharedTargets, Is.Not.Null);
            Assert.That(report.RestoredFields, Is.Not.Null);
            Assert.That(report.ConflictingFields, Is.Not.Null);
            Assert.That(report.UnmanagedFacts, Is.Not.Null);
            Assert.That(report.UntrackedEffectsPossible, Is.False);
            Assert.That(report.LoadedNonUnloadableAssemblyCount, Is.EqualTo(0));
            Assert.That(report.LoadedNonUnloadableAssemblyBytes, Is.EqualTo(0L));
        }

        [Test]
        public void CleaningOneSessionNeverTouchesAnotherSessionsResources()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var first = registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);
            var second = registry.Create("beta", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 2d, false);
            var owner = registry.Reserve(first.Definition.SessionId, UnityCliDebugResourceKinds.Hook, "Shared.Target", string.Empty, report => true);
            var other = registry.Reserve(second.Definition.SessionId, UnityCliDebugResourceKinds.Hook, "Shared.Target", string.Empty, report => true);
            Assert.That(registry.Attach(owner.Resource.ResourceId), Is.True);
            Assert.That(registry.Attach(other.Resource.ResourceId), Is.True);

            var outcome = registry.Stop(first.Definition.SessionId, CreatedAtUtc, 3d, 7);
            Assert.That(outcome.Success, Is.True, outcome.Message);
            Assert.That(registry.ActiveResourceCount(first.Definition.SessionId), Is.EqualTo(0), "the stopped session releases its own resource");
            Assert.That(registry.ActiveResourceCount(second.Definition.SessionId), Is.EqualTo(1), "another session keeps its resource");
            Assert.That(outcome.Report.SharedTargets.Count, Is.EqualTo(1));
            Assert.That(outcome.Report.SharedTargets[0].OtherOwners, Is.EqualTo(1), "the report must disclose the other owner of the same target");
        }

        [Test]
        public void PendingCleanupBlocksRemovalUntilThePipelineCompletes()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var created = registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);
            var sessionId = created.Definition.SessionId;
            var cleaned = false;
            var reservation = registry.Reserve(sessionId, UnityCliDebugResourceKinds.Input, "Input.Virtual", string.Empty, report => cleaned);
            Assert.That(registry.Attach(reservation.Resource.ResourceId), Is.True);

            var first = registry.Stop(sessionId, CreatedAtUtc, 2d, 7);
            Assert.That(first.Report.CleanupPending, Is.True, "a failed cleanup entry must keep the session pending");
            Assert.That(first.Report.PendingDetours, Is.EqualTo(1));
            Assert.That(registry.Remove(sessionId).Failure, Is.EqualTo(UnityCliDebugSessionFailures.CleanupPending));

            cleaned = true;
            var second = registry.Stop(sessionId, CreatedAtUtc, 3d, 7);
            Assert.That(second.Success, Is.True, second.Message);
            Assert.That(second.Report.CleanupPending, Is.False);
            Assert.That(second.Report.ExplicitCleanupEntries, Is.EqualTo(1));
            Assert.That(registry.CanRemove(sessionId, out _, out _, out _), Is.Null);
            Assert.That(registry.Remove(sessionId).Success, Is.True);
            Assert.That(registry.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void TheCleanupReportNeverListsAReleasedResourceAsStillRegistered()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var created = registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);
            var sessionId = created.Definition.SessionId;
            // 模拟 owner 在清理过程中自行归位槽位：暂停现场退出时就是这样释放它的 session 资源的。
            // 资源事实必须在管线之后采样，否则报告会一边说自己已释放、一边声称资源仍然注册。
            string resourceId = null;
            var reservation = registry.Reserve(sessionId, UnityCliDebugResourceKinds.Pause, "pause-breakpoint:Prefix", string.Empty, report =>
            {
                registry.Release(resourceId);
                return true;
            });
            resourceId = reservation.Resource.ResourceId;
            Assert.That(registry.Attach(resourceId), Is.True);

            var outcome = registry.Stop(sessionId, CreatedAtUtc, 2d, 7);
            Assert.That(outcome.Success, Is.True, outcome.Message);
            Assert.That(registry.ActiveResourceCount(sessionId), Is.EqualTo(0), "the cleanup released the only resource");
            Assert.That(outcome.Report.CleanupPending, Is.False);
            Assert.That(outcome.Report.Status, Is.EqualTo("complete"));
            Assert.That(registry.Find(sessionId).CleanupPending, Is.False, "the definition must agree with the report");
            foreach (var fact in outcome.Report.UnmanagedFacts)
            {
                Assert.That(fact.Contains("is still registered after stop"), Is.False,
                    "a report that released the resource must not claim it is still registered; got " + fact);
            }
        }

        [Test]
        public void ARegisteredResourceKeepsTheSessionPendingAndBlocksRemovalUntilItIsReleased()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var created = registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);
            var sessionId = created.Definition.SessionId;
            // 一个声明需要主线程的 pause 资源：off-main stop 必须在 effect 之前推迟它并保留自己的槽位。
            var releaseConfirmed = false;
            var reservation = registry.Reserve(sessionId, UnityCliDebugResourceKinds.Pause, "pause-breakpoint:Prefix", string.Empty, report => releaseConfirmed, true);
            Assert.That(registry.Attach(reservation.Resource.ResourceId), Is.True);

            var offMain = registry.Stop(sessionId, CreatedAtUtc, 2d, 7, true);
            Assert.That(offMain.Success, Is.True, offMain.Message);
            Assert.That(offMain.Report.CleanupPending, Is.True, "a resource that keeps its slot must be reported pending");
            Assert.That(offMain.Report.Status, Is.EqualTo("pending"));
            Assert.That(registry.ActiveResourceCount(sessionId), Is.EqualTo(1));
            Assert.That(registry.Find(sessionId).CleanupPending, Is.True, "the definition must agree with its own report");
            Assert.That(
                offMain.Report.UnmanagedFacts.Any(fact => fact.Contains(reservation.Resource.ResourceId) && fact.Contains("is still registered after stop")),
                Is.True,
                "the report must name the still-registered resource");

            string state;
            bool cleanupPending;
            int activeResourcesUsed;
            Assert.That(registry.CanRemove(sessionId, out state, out cleanupPending, out activeResourcesUsed), Is.EqualTo(UnityCliDebugSessionFailures.CleanupPending));
            Assert.That(cleanupPending, Is.True, "a registered resource is pending cleanup, not a completed one");
            Assert.That(activeResourcesUsed, Is.EqualTo(1));
            Assert.That(registry.Remove(sessionId).Failure, Is.EqualTo(UnityCliDebugSessionFailures.CleanupPending));
            Assert.That(registry.Find(sessionId), Is.Not.Null, "a refused remove must keep the definition loaded");

            // 释放真正完成后，同一 session 的正常 stop 必须收敛并留下零个注册资源。
            releaseConfirmed = true;
            var mainThread = registry.Stop(sessionId, CreatedAtUtc, 3d, 7);
            Assert.That(mainThread.Success, Is.True, mainThread.Message);
            Assert.That(mainThread.Report.CleanupPending, Is.False);
            Assert.That(registry.ActiveResourceCount(sessionId), Is.EqualTo(0), "a normal stop must leave no registered resource");
            Assert.That(registry.CanRemove(sessionId, out state, out cleanupPending, out activeResourcesUsed), Is.Null);
            Assert.That(registry.Remove(sessionId).Success, Is.True);
            Assert.That(registry.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void RemovalRequiresAStoppedSessionAndStopIsIdempotent()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var created = registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);
            var sessionId = created.Definition.SessionId;

            string state;
            bool cleanupPending;
            int active;
            Assert.That(registry.CanRemove(sessionId, out state, out cleanupPending, out active), Is.EqualTo(UnityCliDebugSessionFailures.CleanupPending));
            Assert.That(state, Is.EqualTo(UnityCliDebugSessionStates.Open));

            var first = registry.Stop(sessionId, CreatedAtUtc, 2d, 7);
            Assert.That(first.Changed, Is.True);
            var second = registry.Stop(sessionId, CreatedAtUtc, 3d, 7);
            Assert.That(second.Success, Is.True);
            Assert.That(second.Changed, Is.False, "stop must be idempotent");
            Assert.That(second.Definition.State, Is.EqualTo(UnityCliDebugSessionStates.Stopped));
            Assert.That(second.Report.CleanupPending, Is.False);
            Assert.That(registry.Find(sessionId).Revision, Is.EqualTo(2), "a repeated stop must not invent a new revision");
        }

        [Test]
        public void PlayExitInvalidatesOnlyResourcesCreatedInTheEndedSegment()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var created = registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);
            var sessionId = created.Definition.SessionId;
            var editScoped = registry.Reserve(sessionId, UnityCliDebugResourceKinds.Observation, "Scene.Watch", string.Empty, report => true);
            var playScoped = registry.Reserve(sessionId, UnityCliDebugResourceKinds.Input, "Input.Virtual", "runtime-1", report => true);
            Assert.That(registry.Attach(editScoped.Resource.ResourceId), Is.True);
            Assert.That(registry.Attach(playScoped.Resource.ResourceId), Is.True);

            Assert.That(registry.InvalidatePlayScopedResources("runtime-1"), Is.EqualTo(0), "the current segment stays valid");
            var invalidated = registry.InvalidatePlayScopedResources(string.Empty);
            Assert.That(invalidated, Is.EqualTo(1));
            Assert.That(registry.Find(sessionId).CleanupPending, Is.True);
            Assert.That(registry.Find(sessionId).State, Is.EqualTo(UnityCliDebugSessionStates.Open), "definitions keep their state after Play exit");
            Assert.That(registry.ActiveResourceCount(sessionId), Is.EqualTo(2), "invalidated resources still occupy their slot until cleanup releases them");
            var invalidatedStates = string.Empty;
            foreach (var resource in registry.ResourcesFor(sessionId))
            {
                if (string.Equals(resource.State, UnityCliDebugResourceStates.Invalidated, StringComparison.Ordinal)) invalidatedStates += resource.Kind + ";";
            }
            Assert.That(invalidatedStates, Is.EqualTo("input;"));
        }

        [Test]
        public void PayloadRoundTripsThroughTheChecksumAndRestoresWithoutActivation()
        {
            var capacity = Capacity();
            var registry = new UnityCliDebugSessionRegistry(capacity);
            var created = registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);
            var reservation = registry.Reserve(created.Definition.SessionId, UnityCliDebugResourceKinds.Hook, "Target.Method", "runtime-1", report => true);
            Assert.That(registry.Attach(reservation.Resource.ResourceId), Is.True);
            var payload = registry.CapturePayload(EditorSession, Generation, 1L);
            Assert.That(payload.ContainsKey(UnityCliDebugSessionStorePayload.ChecksumField), Is.True);

            UnityCliDebugSessionRegistry restored;
            string failure;
            Assert.That(UnityCliDebugSessionRegistry.TryRestore(payload, capacity, EditorSession, Generation, new List<string>(), 1L, out restored, out failure), Is.True, failure);
            Assert.That(restored.SessionCount, Is.EqualTo(1));
            Assert.That(restored.NextSessionIndex, Is.EqualTo(2));
            Assert.That(restored.Publication, Is.EqualTo(1L));
            var definition = restored.Find("dbg-1");
            Assert.That(definition, Is.Not.Null);
            Assert.That(definition.RestoredWithoutActivation, Is.False, "the same domain generation is not a reload transition");
            Assert.That(restored.ActiveResourceCount("dbg-1"), Is.EqualTo(0), "active resources are never persisted");

            // 上一个 domain generation 发布的文件在 Domain Reload 后仍可读取，但不得带回活动资源。
            Assert.That(UnityCliDebugSessionRegistry.TryRestore(payload, capacity, EditorSession, "generation-b", new List<string> { Generation }, 1L, out restored, out failure), Is.True, failure);
            Assert.That(restored.Find("dbg-1").RestoredWithoutActivation, Is.True);
            Assert.That(restored.ActiveResourceCount("dbg-1"), Is.EqualTo(0));
            Assert.That(restored.Find("dbg-1").CleanupPending, Is.False, "runtime-only cleanup state must not be restored");
        }

        [Test]
        public void UnusablePayloadsAreRejectedWithStableReasons()
        {
            var capacity = Capacity();
            var registry = new UnityCliDebugSessionRegistry(capacity);
            registry.Create("alpha", UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);
            var payload = registry.CapturePayload(EditorSession, Generation, 2L);
            UnityCliDebugSessionRegistry restored;
            string failure;

            Assert.That(UnityCliDebugSessionRegistry.TryRestore(null, capacity, EditorSession, Generation, new List<string>(), 1L, out restored, out failure), Is.False);
            Assert.That(failure, Is.EqualTo("payload-missing"));

            var otherSession = new Dictionary<string, object>(payload);
            otherSession["editorSessionId"] = "session-other";
            Assert.That(UnityCliDebugSessionRegistry.TryRestore(otherSession, capacity, EditorSession, Generation, new List<string>(), 1L, out restored, out failure), Is.False);
            Assert.That(failure, Is.EqualTo("editor-session-mismatch"));

            var otherGeneration = new Dictionary<string, object>(payload);
            otherGeneration["domainGeneration"] = "generation-other";
            Assert.That(UnityCliDebugSessionRegistry.TryRestore(otherGeneration, capacity, EditorSession, Generation, new List<string>(), 1L, out restored, out failure), Is.False);
            Assert.That(failure, Is.EqualTo("domain-generation-mismatch"));

            var otherFormat = new Dictionary<string, object>(payload);
            otherFormat["format"] = "unitycli.debug.session/2";
            Assert.That(UnityCliDebugSessionRegistry.TryRestore(otherFormat, capacity, EditorSession, Generation, new List<string>(), 1L, out restored, out failure), Is.False);
            Assert.That(failure, Is.EqualTo("format-mismatch"));

            var tampered = new Dictionary<string, object>(payload);
            tampered["checksum"] = new string('0', 64);
            Assert.That(UnityCliDebugSessionRegistry.TryRestore(tampered, capacity, EditorSession, Generation, new List<string>(), 1L, out restored, out failure), Is.False);
            Assert.That(failure, Is.EqualTo("checksum-mismatch"));

            var tamperedBody = new Dictionary<string, object>(payload);
            tamperedBody["nextSessionIndex"] = 99L;
            Assert.That(UnityCliDebugSessionRegistry.TryRestore(tamperedBody, capacity, EditorSession, Generation, new List<string>(), 1L, out restored, out failure), Is.False);
            Assert.That(failure, Is.EqualTo("checksum-mismatch"), "any payload change must break the checksum");

            Assert.That(UnityCliDebugSessionRegistry.TryRestore(payload, capacity, EditorSession, Generation, new List<string>(), 5L, out restored, out failure), Is.False);
            Assert.That(failure, Is.EqualTo("publication-regressed"));
        }

        [Test]
        public void CanonicalChecksumIsIndependentOfDictionaryOrder()
        {
            var left = new Dictionary<string, object> { { "a", 1L }, { "b", "text" }, { "c", true } };
            var right = new Dictionary<string, object> { { "c", true }, { "b", "text" }, { "a", 1L } };
            Assert.That(UnityCliDebugSessionStorePayload.Checksum(right), Is.EqualTo(UnityCliDebugSessionStorePayload.Checksum(left)));
            right["a"] = 2L;
            Assert.That(UnityCliDebugSessionStorePayload.Checksum(right), Is.Not.EqualTo(UnityCliDebugSessionStorePayload.Checksum(left)));
        }
    }
}
#endif
