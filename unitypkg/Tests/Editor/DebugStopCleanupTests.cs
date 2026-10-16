#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// session 停止顺序、清理报告字段、条件恢复终态与生命周期边界的测试。
    ///
    /// 断言只使用 engine-free 的 UnityCliDebugSessionRegistry 与值写入/恢复 owner，
    /// 因此它们证明的是产品语义本身，而不是某个 Unity 版本的行为。
    /// </summary>
    public sealed class DebugStopCleanupTests
    {
        private const string EditorSession = "stop-cleanup-test";
        private const string Generation = "generation-a";
        private const string CreatedAtUtc = "2026-01-01T00:00:00.0000000Z";

        private sealed class RestoreProbe
        {
            public int Value;
        }

        private static class RestoreProbeHost
        {
            public static RestoreProbe Current;
        }

        private static UnityCliDebugSessionCapacity Capacity()
        {
            return new UnityCliDebugSessionCapacity(16, 4, 32, 50, 200, 32 * 1024, 8L * 1024 * 1024);
        }

        private static string CreateSession(UnityCliDebugSessionRegistry registry, string label)
        {
            var created = registry.Create(label, UnityCliDebugActivationScopes.EditorAndPlay, UnityCliDebugClocks.Monotonic, null, 0L, CreatedAtUtc, 1d, false);
            Assert.That(created.Success, Is.True, created.Message);
            return created.Definition.SessionId;
        }

        // ------------------------------------------------------------------
        // 有序停止：第 1 阶段先关闭未来派发
        // ------------------------------------------------------------------

        [Test]
        public void StopClosesFutureDispatchBeforeTheFirstCleanupStepRuns()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var sessionId = CreateSession(registry, "dispatch-gate");
            Assert.That(registry.IsDispatchOpen(sessionId), Is.True, "an open session must accept new dispatch");

            var dispatchOpenInsideStep = true;
            Assert.That(registry.RegisterCleanupStep(sessionId, UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.StopDispatch), "probe-dispatch", "hook", report =>
            {
                dispatchOpenInsideStep = registry.IsDispatchOpen(sessionId);
                report.StoppedFutureEntries++;
                return true;
            }), Is.Null);

            var outcome = registry.Stop(sessionId, CreatedAtUtc, 2d, 7);

            Assert.That(outcome.Success, Is.True, outcome.Message);
            Assert.That(dispatchOpenInsideStep, Is.False, "the first cleanup stage must already see a closed session dispatch");
            Assert.That(registry.IsDispatchOpen(sessionId), Is.False, "a stopped session never accepts new dispatch again");
            Assert.That(registry.IsDispatchOpen("dbg-missing"), Is.False, "an unknown session has no open dispatch");
            Assert.That(outcome.Report.StoppedFutureEntries, Is.EqualTo(1L));
        }

        [Test]
        public void StopEntryNamesTheEntryThatServedTheStop()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var mainThread = CreateSession(registry, "entry-main");
            var mainThreadReport = registry.Stop(mainThread, CreatedAtUtc, 2d, 7).Report;
            Assert.That(mainThreadReport.StopEntry, Is.EqualTo(UnityCliDebugStopEntries.MainThread));

            var offMain = CreateSession(registry, "entry-off-main");
            var offMainReport = registry.Stop(offMain, CreatedAtUtc, 3d, 8, true).Report;
            Assert.That(offMainReport.StopEntry, Is.EqualTo(UnityCliDebugStopEntries.PauseControlChannel));

            var explicitEntry = CreateSession(registry, "entry-explicit");
            var explicitReport = registry.Stop(explicitEntry, CreatedAtUtc, 4d, 9, false, UnityCliDebugStopEntries.PauseControlChannel).Report;
            Assert.That(explicitReport.StopEntry, Is.EqualTo(UnityCliDebugStopEntries.PauseControlChannel), "a declared entry is reported verbatim");
        }

        // ------------------------------------------------------------------
        // 重复 stop 的幂等与终态事实
        // ------------------------------------------------------------------

        [Test]
        public void RepeatedStopReturnsTheSameTerminalFactsWithoutANewRevision()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var sessionId = CreateSession(registry, "repeat-stop");
            Assert.That(registry.RegisterCleanupStep(sessionId, UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.ExplicitCleanup), "probe-restore", "value", report =>
            {
                report.RestoredFields.Add("Probe.Value (probe-1)");
                report.ConflictingFields.Add("Probe.Other (probe-1): the current value does not equal the value this session wrote");
                report.StaleTargets.Add("Probe.Gone (probe-1): the path no longer resolves (null at Probe.Gone)");
                report.UnrecoverableEffects.Add("field write at Probe.Other (probe-1) was not restored: another writer changed it; the business value is kept");
                return true;
            }), Is.Null);

            var first = registry.Stop(sessionId, CreatedAtUtc, 2d, 7);
            Assert.That(first.Changed, Is.True);
            var second = registry.Stop(sessionId, CreatedAtUtc, 3d, 7);

            Assert.That(second.Success, Is.True, second.Message);
            Assert.That(second.Changed, Is.False, "a repeated stop must be idempotent");
            Assert.That(registry.Find(sessionId).Revision, Is.EqualTo(2L), "a repeated stop must not invent a new revision");
            Assert.That(second.Report.CleanupPending, Is.False);
            Assert.That(second.Report.StopEntry, Is.EqualTo(UnityCliDebugStopEntries.MainThread));
            Assert.That(second.Report.RestoredFields, Has.Count.EqualTo(1), "the terminal facts of the last real cleanup must stay readable");
            Assert.That(second.Report.ConflictingFields, Has.Count.EqualTo(1));
            Assert.That(second.Report.StaleTargets, Has.Count.EqualTo(1));
            Assert.That(second.Report.UnrecoverableEffects, Has.Count.EqualTo(1));
            Assert.That(second.Report.Steps, Has.Count.EqualTo(UnityCliDebugCleanupSteps.Ordered().Length), "the step categories are still reported in full");
        }

        [Test]
        public void DeferredMainThreadCleanupKeepsPendingAndRefusesRemovalUntilItCompletes()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var sessionId = CreateSession(registry, "deferred-entry");
            var deferredRan = false;
            Assert.That(registry.RegisterCleanupStep(sessionId, UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.DetachHooks), "probe-detach", "hook", report => { deferredRan = true; return true; }, true), Is.Null);

            var offMain = registry.Stop(sessionId, CreatedAtUtc, 2d, 7, true, UnityCliDebugStopEntries.PauseControlChannel);
            Assert.That(offMain.Report.StopEntry, Is.EqualTo(UnityCliDebugStopEntries.PauseControlChannel));
            Assert.That(offMain.Report.CleanupPending, Is.True, "an entry that needs the main thread must stay pending instead of running off-main");
            Assert.That(offMain.Report.PendingDetours, Is.EqualTo(1L), "a deferred detach counts as a pending detour");
            Assert.That(deferredRan, Is.False, "the deferred entry must not run on the serving thread");
            Assert.That(registry.CanRemove(sessionId, out _, out _, out _), Is.EqualTo(UnityCliDebugSessionFailures.CleanupPending));
            Assert.That(registry.IsDispatchOpen(sessionId), Is.False);

            var retry = registry.Stop(sessionId, CreatedAtUtc, 3d, 7);
            Assert.That(retry.Report.CleanupPending, Is.False, "the retried stop on the main thread must complete the deferred entry");
            Assert.That(deferredRan, Is.True);
            Assert.That(registry.Remove(sessionId).Success, Is.True, "removal is only allowed after the cleanup completed");
        }

        // ------------------------------------------------------------------
        // 报告字段类别的诚实性
        // ------------------------------------------------------------------

        [Test]
        public void AResourceWithoutACleanupDelegateIsReportedAsAnUnrecoverableEffect()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var sessionId = CreateSession(registry, "no-delegate");
            var reservation = registry.Reserve(sessionId, UnityCliDebugResourceKinds.Hook, "Probe.Target", string.Empty, null);
            Assert.That(registry.Attach(reservation.Resource.ResourceId), Is.True);

            var report = registry.Stop(sessionId, CreatedAtUtc, 2d, 7).Report;

            Assert.That(report.CleanupPending, Is.True, "an effect that cannot be cleaned stays pending");
            Assert.That(report.PendingDetours, Is.EqualTo(1L));
            Assert.That(report.UntrackedEffectsPossible, Is.True);
            Assert.That(report.UnmanagedUnknown, Is.True);
            Assert.That(report.UnrecoverableEffects, Has.Count.EqualTo(1));
            Assert.That(string.Join(" ", report.UnrecoverableEffects), Does.Contain(reservation.Resource.ResourceId));
            Assert.That(string.Join(" ", report.UnmanagedFacts), Does.Contain("cannot be statically proven absent"), "the unknown fact is stated in words, not only as a flag");
        }

        [Test]
        public void ASessionWithoutOwnedWorkDeclaresNoUnmanagedUnknown()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var sessionId = CreateSession(registry, "nothing-owned");
            var report = registry.Stop(sessionId, CreatedAtUtc, 2d, 7).Report;

            Assert.That(report.UnmanagedUnknown, Is.False, "nothing was owned, so no unmanaged object can have been created by this session");
            Assert.That(report.UntrackedEffectsPossible, Is.False);
            Assert.That(report.UnrecoverableEffects, Is.Empty);
            Assert.That(report.StaleTargets, Is.Empty);
            Assert.That(report.DeclaredEffects, Is.Empty);
        }

        [Test]
        public void TheReportAlwaysCarriesEveryFieldCategoryEvenWhenNothingWasOwned()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var sessionId = CreateSession(registry, "categories");
            var report = registry.Stop(sessionId, CreatedAtUtc, 2d, 7).Report;

            Assert.That(report.StopEntry, Is.EqualTo(UnityCliDebugStopEntries.MainThread));
            Assert.That(report.SharedTargets, Is.Not.Null);
            Assert.That(report.RestoredFields, Is.Not.Null);
            Assert.That(report.ConflictingFields, Is.Not.Null);
            Assert.That(report.StaleTargets, Is.Not.Null);
            Assert.That(report.UnrecoverableEffects, Is.Not.Null);
            Assert.That(report.DeclaredEffects, Is.Not.Null);
            Assert.That(report.UnmanagedFacts, Is.Not.Null);
            Assert.That(report.Owners, Has.Count.EqualTo(UnityCliDebugFacilityOwners.All().Length));
            Assert.That(report.ActiveRevision, Is.EqualTo(1L));
            Assert.That(report.LoadedNonUnloadableAssemblyCount, Is.EqualTo(0));
            Assert.That(report.LoadedNonUnloadableAssemblyBytes, Is.EqualTo(0L));
        }

        // ------------------------------------------------------------------
        // 条件恢复：恢复、冲突与目标失效是三种不同终态
        // ------------------------------------------------------------------

        [Test]
        public void AConditionalRestoreWritesBackOnlyWhileTheTargetStillHoldsTheWrittenValue()
        {
            using (var scope = new ValueSessionScope("restore-success"))
            {
                RestoreProbeHost.Current = new RestoreProbe { Value = 1 };
                var plan = WritePlan(scope.SessionId, 5, registerRestore: true);
                var write = DebugValueSupport.ApplyWrite(plan, WriteRequest(scope.SessionId, 5, true));
                Assert.That(write.Applied, Is.True, write.Outcome);
                Assert.That(RestoreProbeHost.Current.Value, Is.EqualTo(5));

                var report = new UnityCliDebugCleanupReport();
                Assert.That(DebugValueRestore.RestoreSession(scope.SessionId, report), Is.True);
                Assert.That(RestoreProbeHost.Current.Value, Is.EqualTo(1), "the original value is written back");
                Assert.That(report.RestoredFields, Has.Count.EqualTo(1));
                Assert.That(report.ConflictingFields, Is.Empty);
                Assert.That(report.StaleTargets, Is.Empty);
            }
            RestoreProbeHost.Current = null;
        }

        [Test]
        public void ABusinessWriteAfterOursIsReportedAsAConflictAndNeverOverwritten()
        {
            using (var scope = new ValueSessionScope("restore-conflict"))
            {
                RestoreProbeHost.Current = new RestoreProbe { Value = 1 };
                var plan = WritePlan(scope.SessionId, 5, registerRestore: true);
                DebugValueSupport.ApplyWrite(plan, WriteRequest(scope.SessionId, 5, true));
                // 业务自己写入新值：条件恢复必须报告冲突并保留业务值。
                RestoreProbeHost.Current.Value = 42;

                var report = new UnityCliDebugCleanupReport();
                Assert.That(DebugValueRestore.RestoreSession(scope.SessionId, report), Is.True, "a conflict is a terminal, reported outcome rather than an endless retry");
                Assert.That(RestoreProbeHost.Current.Value, Is.EqualTo(42), "the business value must never be overwritten");
                Assert.That(report.RestoredFields, Is.Empty);
                Assert.That(report.ConflictingFields, Has.Count.EqualTo(1));
                Assert.That(string.Join(" ", report.ConflictingFields), Does.Contain("does not equal the value this session wrote"));
                Assert.That(report.UnrecoverableEffects, Has.Count.EqualTo(1));
                Assert.That(DebugValueRestore.CountFor(scope.SessionId), Is.EqualTo(0), "a reported conflict is closed, not left for a retry that can never succeed");
            }
            RestoreProbeHost.Current = null;
        }

        [Test]
        public void AnInvalidatedRestoreTargetIsReportedAsStaleInsteadOfAsAConflict()
        {
            using (var scope = new ValueSessionScope("restore-stale"))
            {
                RestoreProbeHost.Current = new RestoreProbe { Value = 1 };
                var plan = WritePlan(scope.SessionId, 5, registerRestore: true);
                DebugValueSupport.ApplyWrite(plan, WriteRequest(scope.SessionId, 5, true));
                // 目标被移除：原值随目标一起消失，因此恢复既不可能也不应该覆盖任何东西。
                RestoreProbeHost.Current = null;

                var report = new UnityCliDebugCleanupReport();
                Assert.That(DebugValueRestore.RestoreSession(scope.SessionId, report), Is.True);
                Assert.That(report.RestoredFields, Is.Empty);
                Assert.That(report.ConflictingFields, Is.Empty, "an invalid target is not a conflict with another writer");
                Assert.That(report.StaleTargets, Has.Count.EqualTo(1));
                Assert.That(report.UnrecoverableEffects, Has.Count.EqualTo(1));
                Assert.That(string.Join(" ", report.StaleTargets), Does.Contain("no longer resolves"));
            }
        }

        // ------------------------------------------------------------------
        // 第 16 节生命周期边界
        // ------------------------------------------------------------------

        [Test]
        public void DomainReloadRestoresDefinitionsWithNoActiveWorkAndNoCleanupPending()
        {
            var capacity = Capacity();
            var registry = new UnityCliDebugSessionRegistry(capacity);
            var open = CreateSession(registry, "reload-open");
            var stopped = CreateSession(registry, "reload-stopped");
            registry.Stop(stopped, CreatedAtUtc, 2d, 7);
            var reservation = registry.Reserve(open, UnityCliDebugResourceKinds.Hook, "Probe.Target", "runtime-1", report => true);
            Assert.That(registry.Attach(reservation.Resource.ResourceId), Is.True);
            var payload = registry.CapturePayload(EditorSession, Generation, 1L);

            UnityCliDebugSessionRegistry restored;
            string failure;
            Assert.That(UnityCliDebugSessionRegistry.TryRestore(payload, capacity, EditorSession, "generation-b", new List<string> { Generation }, 1L, out restored, out failure), Is.True, failure);

            Assert.That(restored.ActiveResourceCount(open), Is.EqualTo(0), "a reload never brings execution resources back to life");
            Assert.That(restored.Find(open).CleanupPending, Is.False, "runtime-only cleanup state is not restored");
            Assert.That(restored.Find(open).RestoredWithoutActivation, Is.True);
            Assert.That(restored.IsDispatchOpen(open), Is.True, "an open definition keeps accepting dispatch after an exact rebind");
            Assert.That(restored.IsDispatchOpen(stopped), Is.False, "a definition that was stopped before the reload stays closed");
        }

        [Test]
        public void EditorRestartRejectsTheStoredDefinitionsInsteadOfReusingThemAStaleEditorSession()
        {
            var capacity = Capacity();
            var registry = new UnityCliDebugSessionRegistry(capacity);
            var sessionId = CreateSession(registry, "restart");
            var payload = registry.CapturePayload(EditorSession, Generation, 1L);

            UnityCliDebugSessionRegistry restored;
            string failure;
            Assert.That(UnityCliDebugSessionRegistry.TryRestore(payload, capacity, "session-after-restart", Generation, new List<string>(), 1L, out restored, out failure), Is.False);
            Assert.That(failure, Is.EqualTo("editor-session-mismatch"));
            Assert.That(restored, Is.Null, "no definition may be loaded from another Editor session");
            Assert.That(registry.IsDispatchOpen(sessionId), Is.True);
        }

        [Test]
        public void PlayExitInvalidatesPlayScopedResourcesAndTheStopReportStaysPendingUntilTheyAreReleased()
        {
            var registry = new UnityCliDebugSessionRegistry(Capacity());
            var sessionId = CreateSession(registry, "play-exit");
            var released = false;
            var playScoped = registry.Reserve(sessionId, UnityCliDebugResourceKinds.Input, "Input.Virtual", "runtime-1", report => released);
            var editScoped = registry.Reserve(sessionId, UnityCliDebugResourceKinds.Observation, "Scene.Watch", string.Empty, report => true);
            Assert.That(registry.Attach(playScoped.Resource.ResourceId), Is.True);
            Assert.That(registry.Attach(editScoped.Resource.ResourceId), Is.True);

            Assert.That(registry.InvalidatePlayScopedResources("runtime-1"), Is.EqualTo(0), "the current Play segment stays valid");
            Assert.That(registry.InvalidatePlayScopedResources(string.Empty), Is.EqualTo(1), "exiting Play invalidates the resources of the ended segment");
            Assert.That(registry.Find(sessionId).CleanupPending, Is.True);

            var stop = registry.Stop(sessionId, CreatedAtUtc, 2d, 7);
            Assert.That(stop.Report.CleanupPending, Is.True, "an invalidated resource that cannot be released keeps the cleanup honest");
            Assert.That(stop.Report.PendingDetours, Is.EqualTo(1L));
            Assert.That(registry.CanRemove(sessionId, out _, out _, out _), Is.EqualTo(UnityCliDebugSessionFailures.CleanupPending));

            released = true;
            var completed = registry.Stop(sessionId, CreatedAtUtc, 3d, 7);
            Assert.That(completed.Report.CleanupPending, Is.False, "a repeated stop completes the Play-exit cleanup");
            Assert.That(registry.ActiveResourceCount(sessionId), Is.EqualTo(0));
        }

        // ------------------------------------------------------------------
        // 夹具：一个真实 session 与一次真实条件写入
        // ------------------------------------------------------------------

        private sealed class ValueSessionScope : IDisposable
        {
            internal string SessionId;
            private readonly bool created;

            internal ValueSessionScope(string label)
            {
                // editor-and-play：本夹具断言的是条件恢复语义，与编辑器模式无关；
                // 只有两种模式都可激活的范围才能让 EditMode 与 PlayMode 运行都建立出真实定义。
                var plan = DebugSessionHost.PrepareCreate(new DebugSessionCreateRequest
                {
                    Label = label,
                    ActivationScope = UnityCliDebugActivationScopes.EditorAndPlay,
                    ClockKind = UnityCliDebugClocks.Monotonic
                });
                var outcome = DebugSessionHost.ApplyCreate(plan);
                // 夹具先证明自己真的拥有一个定义，否则模式冲突会伪装成后续断言的失败。
                Assert.That(outcome.SessionId, Is.Not.Empty, "the fixture must own a real session before it asserts restore semantics");
                SessionId = outcome.SessionId;
                created = true;
            }

            public void Dispose()
            {
                if (!created) return;
                DebugValueRestore.RestoreSession(SessionId, new UnityCliDebugCleanupReport());
                try { DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = SessionId })); }
                catch (Exception) { /* already stopped is fine */ }
                try { DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = SessionId })); }
                catch (Exception) { /* a pending cleanup is reported by the test itself */ }
            }
        }

        private static DebugValueWritePlan WritePlan(string sessionId, int value, bool registerRestore)
        {
            var ctx = new UnityCliProjectToolContext { TimeoutMs = 30000 };
            return DebugValueSupport.ValidateWrite(WriteRequest(sessionId, value, registerRestore), ctx.UseReadBudget());
        }

        private static DebugValueWriteRequest WriteRequest(string sessionId, int value, bool registerRestore)
        {
            return new DebugValueWriteRequest
            {
                RootKind = "static-field",
                TypeName = typeof(RestoreProbeHost).FullName,
                FieldName = "Current",
                FieldPath = new List<string> { "Value" },
                Value = value,
                RegisterRestore = registerRestore,
                SessionId = sessionId
            };
        }
    }
}
#endif
