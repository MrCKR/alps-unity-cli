#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// debug program 定义、revision CAS、programRun 状态机与预算的行为测试。
    /// 全部断言只使用 engine-free 的 UnityCliDebugProgramRegistry 与 ABI 声明，不需要 Unity 主线程状态。
    /// </summary>
    public sealed class DebugProgramTests
    {
        private const string Session = "dbg-1";
        private const string CreatedAtUtc = "2026-01-01T00:00:00.0000000Z";

        private static UnityCliDebugProgramCapacity Capacity()
        {
            return new UnityCliDebugProgramCapacity(
                DebugLimits.MaxProgramsPerSession,
                DebugLimits.MaxProgramsGlobal,
                DebugLimits.MaxActiveProgramRunsGlobal,
                DebugLimits.MaxProgramSourceBytes,
                DebugLimits.MaxProgramEntries,
                DebugLimits.MaxProgramBindings,
                DebugLimits.MaxProgramStateBytes,
                DebugLimits.MaxProgramArgsBytes,
                DebugLimits.MaxProgramRunMaxDurationMs,
                DebugLimits.ProgramListDefaultLimit,
                DebugLimits.ProgramListMaxLimit);
        }

        private static List<UnityCliDebugProgramEntry> StepEntry(string name = "Step")
        {
            return new List<UnityCliDebugProgramEntry>
            {
                new UnityCliDebugProgramEntry { Name = name, Category = UnityCliDebugProgramEntryCategories.Step, DeclaringType = "Probe" }
            };
        }

        private static UnityCliDebugProgramOutcome Install(UnityCliDebugProgramRegistry registry, string label, long sourceBytes = 1024L, long stateBytes = 0L)
        {
            return registry.Install(
                Session,
                label,
                "code",
                "source-" + label,
                "hash-" + label,
                sourceBytes,
                null,
                stateBytes,
                null,
                0L,
                CreatedAtUtc,
                StepEntry(),
                new List<UnityCliDebugProgramBinding>());
        }

        private static UnityCliDebugProgramOutcome ScheduleRun(UnityCliDebugProgramRegistry registry, string programId, long durationMs = 1000L, double nowUtcMs = 1000d, string entry = "Step", string site = "editor-tick")
        {
            return registry.ScheduleRun(programId, entry, UnityCliDebugProgramEntryCategories.Step, site, "main-thread scheduler", null, null, 0L, durationMs, nowUtcMs, CreatedAtUtc, nowUtcMs);
        }

        /// <summary>带自定义 entries 与 bindings 的定义；用于执行点与 target 推导的行为断言。</summary>
        private static UnityCliDebugProgramDefinition InstallWith(UnityCliDebugProgramRegistry registry, string label, IReadOnlyList<UnityCliDebugProgramEntry> entries, IReadOnlyList<UnityCliDebugProgramBinding> bindings)
        {
            var outcome = registry.Install(Session, label, "code", "source-" + label, "hash-" + label, 16L, null, 0L, null, 0L, CreatedAtUtc, entries, bindings);
            Assert.That(outcome.Success, Is.True, outcome.Failure);
            return outcome.Definition;
        }

        [Test]
        public void ProgramIdsAreMonotonicAndRejectionsDoNotConsumeAnId()
        {
            var registry = new UnityCliDebugProgramRegistry(Capacity());
            var first = Install(registry, "alpha");
            Assert.That(first.Success, Is.True);
            Assert.That(first.Definition.ProgramId, Is.EqualTo("prog-1"));
            Assert.That(first.Definition.State, Is.EqualTo(UnityCliDebugProgramStates.Installed));
            Assert.That(registry.PeekProgramId(), Is.EqualTo("prog-2"));

            var duplicate = Install(registry, "alpha");
            Assert.That(duplicate.Failure, Is.EqualTo(UnityCliDebugProgramFailures.DuplicateLabel));
            Assert.That(duplicate.Definition, Is.Null);

            var tooLarge = Install(registry, "beta", DebugLimits.MaxProgramSourceBytes + 1L);
            Assert.That(tooLarge.Failure, Is.EqualTo(UnityCliDebugProgramFailures.SourceTooLarge));
            Assert.That(tooLarge.Budget.ProgramsUsed, Is.EqualTo(1), "a rejection reports the current occupancy");

            Assert.That(registry.PeekProgramId(), Is.EqualTo("prog-2"), "a rejected install must not consume an id");
            Assert.That(registry.Remove(first.Definition.ProgramId).Success, Is.True);
            Assert.That(Install(registry, "gamma").Definition.ProgramId, Is.EqualTo("prog-2"), "a removed id is never handed out twice");
        }

        [Test]
        public void InstallStaysDormantAndReplaceKeepsTheOldConfigurationOnEveryRefusal()
        {
            var registry = new UnityCliDebugProgramRegistry(Capacity());
            var installed = Install(registry, "alpha").Definition;
            Assert.That(installed.State, Is.EqualTo(UnityCliDebugProgramStates.Installed), "install must not activate a listener");
            Assert.That(installed.Revision, Is.EqualTo(1L));
            Assert.That(installed.StateEpoch, Is.EqualTo(1L));

            string state;
            Assert.That(registry.CanReplace(installed.ProgramId, 99L, 16L, 0L, 0L, out state), Is.EqualTo(UnityCliDebugProgramFailures.RevisionConflict));
            var refused = registry.PublishRevision(installed.ProgramId, 99L, installed.StateEpoch, "next", "hash-next", 16L, StepEntry(), new List<UnityCliDebugProgramBinding>(), null, 0L, null, 0L, "artifact", "assembly");
            Assert.That(refused.Failure, Is.EqualTo(UnityCliDebugProgramFailures.RevisionConflict));
            Assert.That(refused.OldRevision, Is.EqualTo(1L));
            Assert.That(installed.Source, Is.EqualTo("source-alpha"), "a refused replacement keeps the previous working configuration");
            Assert.That(installed.Revision, Is.EqualTo(1L));

            registry.EnterCallback(installed.ProgramId);
            var published = registry.PublishRevision(installed.ProgramId, 1L, 1L, "next", "hash-next", 16L, StepEntry("Step"), new List<UnityCliDebugProgramBinding>(), null, 0L, null, 0L, "artifact", "assembly");
            Assert.That(published.Success, Is.True);
            Assert.That(published.OldRevision, Is.EqualTo(1L));
            Assert.That(published.OldInFlightCalls, Is.EqualTo(1), "replace must report the calls that were still in flight");
            Assert.That(published.Definition.Revision, Is.EqualTo(2L));
            Assert.That(published.Definition.StateEpoch, Is.EqualTo(2L));
            Assert.That(published.Definition.Source, Is.EqualTo("next"));
        }

        [Test]
        public void RevisionStateUpdateIsCompareAndSwapOnTheStateEpoch()
        {
            var registry = new UnityCliDebugProgramRegistry(Capacity());
            var program = Install(registry, "alpha").Definition;
            var candidate = new Dictionary<string, object>(StringComparer.Ordinal) { { "attempt", 1L } };

            long epoch;
            Assert.That(registry.TryUpdateState(program.ProgramId, 99L, candidate, 16L, out epoch), Is.EqualTo(UnityCliDebugProgramFailures.StateEpochConflict));
            Assert.That(program.StateEpoch, Is.EqualTo(1L), "a stale epoch must not be merged");
            Assert.That(program.RevisionState, Is.Null);

            Assert.That(registry.TryUpdateState(program.ProgramId, 1L, candidate, 16L, out epoch), Is.Null);
            Assert.That(epoch, Is.EqualTo(2L));
            Assert.That(program.RevisionState, Is.SameAs(candidate));
            Assert.That(registry.TryUpdateState(program.ProgramId, 1L, candidate, 16L, out epoch), Is.EqualTo(UnityCliDebugProgramFailures.StateEpochConflict), "the same epoch cannot be published twice");
        }

        [Test]
        public void ProgramRunFollowsScheduledRunningTerminalAndClosesExpiredPendingWork()
        {
            var registry = new UnityCliDebugProgramRegistry(Capacity());
            var program = Install(registry, "alpha").Definition;
            var scheduled = ScheduleRun(registry, program.ProgramId, 1000L, 1000d);
            Assert.That(scheduled.Success, Is.True);
            Assert.That(scheduled.Run.State, Is.EqualTo(UnityCliDebugProgramRunStates.Scheduled));
            Assert.That(scheduled.Run.Revision, Is.EqualTo(1L), "a run fixes the revision it was scheduled with");

            Assert.That(registry.TryBeginRun(scheduled.Run.ProgramRunId, 1100d, 1), Is.True);
            Assert.That(scheduled.Run.State, Is.EqualTo(UnityCliDebugProgramRunStates.Running));
            Assert.That(registry.CompleteRun(scheduled.Run.ProgramRunId, UnityCliDebugProgramRunStates.Completed, string.Empty, string.Empty, 1200d), Is.False, "a terminal state cannot be published while the code is still in flight");
            registry.MarkCodeReturned(scheduled.Run.ProgramRunId);
            Assert.That(registry.CompleteRun(scheduled.Run.ProgramRunId, UnityCliDebugProgramRunStates.Completed, string.Empty, string.Empty, 1200d), Is.True);
            Assert.That(scheduled.Run.IsTerminal, Is.True);

            var expires = ScheduleRun(registry, program.ProgramId, 100L, 2000d);
            Assert.That(expires.Success, Is.True);
            Assert.That(registry.TryBeginRun(expires.Run.ProgramRunId, 3000d, 1), Is.False, "an expired pending run must never execute late");
            Assert.That(expires.Run.State, Is.EqualTo(UnityCliDebugProgramRunStates.Cancelled));
            Assert.That(expires.Run.FailureCode, Is.EqualTo("E_DEBUG_PROGRAM_RUN_EXPIRED"));

            var scanned = ScheduleRun(registry, program.ProgramId, 100L, 4000d);
            Assert.That(registry.CancelExpiredRuns(4100d), Is.EqualTo(0), "a run inside its deadline stays scheduled");
            Assert.That(scanned.Run.State, Is.EqualTo(UnityCliDebugProgramRunStates.Scheduled));
            Assert.That(registry.CancelExpiredRuns(4200d), Is.EqualTo(1));
            Assert.That(scanned.Run.State, Is.EqualTo(UnityCliDebugProgramRunStates.Cancelled));
        }

        [Test]
        public void PendingCancellationWinsTheRaceAndCancellationIsRequestedWhileRunning()
        {
            var registry = new UnityCliDebugProgramRegistry(Capacity());
            var program = Install(registry, "alpha").Definition;

            var pending = ScheduleRun(registry, program.ProgramId, 1000L, 1000d);
            Assert.That(registry.RequestCancellation(pending.Run.ProgramRunId, "cancelled before the execution right"), Is.True);
            Assert.That(pending.Run.State, Is.EqualTo(UnityCliDebugProgramRunStates.Cancelled));
            Assert.That(registry.TryBeginRun(pending.Run.ProgramRunId, 1100d, 1), Is.False, "a cancelled pending run never starts");

            var running = ScheduleRun(registry, program.ProgramId, 1000L, 1200d);
            Assert.That(registry.TryBeginRun(running.Run.ProgramRunId, 1300d, 1), Is.True);
            Assert.That(registry.RequestCancellation(running.Run.ProgramRunId, "cooperative stop"), Is.False, "running code is only asked to cancel");
            Assert.That(running.Run.CancellationRequested, Is.True);
            Assert.That(running.Run.InFlight, Is.True);
            Assert.That(running.Run.State, Is.EqualTo(UnityCliDebugProgramRunStates.Running));
        }

        [Test]
        public void OnlyOneActiveRunPerProgramAndOnlyOneTerminalPublicationPerRun()
        {
            var registry = new UnityCliDebugProgramRegistry(Capacity());
            var program = Install(registry, "alpha").Definition;
            var first = ScheduleRun(registry, program.ProgramId, 5000L, 1000d);
            Assert.That(first.Success, Is.True);

            var second = ScheduleRun(registry, program.ProgramId, 5000L, 1000d);
            Assert.That(second.Failure, Is.EqualTo(UnityCliDebugProgramFailures.RunActive));
            Assert.That(second.Run, Is.Null);

            registry.TryBeginRun(first.Run.ProgramRunId, 1100d, 1);
            registry.MarkCodeReturned(first.Run.ProgramRunId);
            Assert.That(registry.CompleteRun(first.Run.ProgramRunId, UnityCliDebugProgramRunStates.Completed, string.Empty, string.Empty, 1200d), Is.True);
            Assert.That(registry.CompleteRun(first.Run.ProgramRunId, UnityCliDebugProgramRunStates.Failed, "E_X", "late", 1300d), Is.False, "a terminal run cannot be published twice");
            Assert.That(registry.ActiveRunsFor(program.ProgramId), Is.EqualTo(0));
        }

        [Test]
        public void BudgetsRefuseBeforeAnyEffectAndReportCurrentOccupancy()
        {
            var registry = new UnityCliDebugProgramRegistry(Capacity());
            for (var index = 0; index < DebugLimits.MaxProgramsPerSession; index++)
                Assert.That(Install(registry, "p" + index.ToString()).Success, Is.True, "install " + index);

            var perSession = Install(registry, "overflow");
            Assert.That(perSession.Failure, Is.EqualTo(UnityCliDebugProgramFailures.LimitExceeded));
            Assert.That(perSession.Budget.SessionProgramsUsed, Is.EqualTo(DebugLimits.MaxProgramsPerSession));
            Assert.That(perSession.Budget.SessionProgramsRemaining, Is.EqualTo(0));
            Assert.That(registry.ProgramCount, Is.EqualTo(DebugLimits.MaxProgramsPerSession), "a refused install must not create a definition");
        }

        [Test]
        public void ActiveRunBudgetIsGlobalAndRefusalsReportOccupancy()
        {
            var registry = new UnityCliDebugProgramRegistry(Capacity());
            var programs = new List<UnityCliDebugProgramDefinition>();
            for (var index = 0; index <= DebugLimits.MaxActiveProgramRunsGlobal; index++)
                programs.Add(Install(registry, "p" + index.ToString()).Definition);

            for (var index = 0; index < DebugLimits.MaxActiveProgramRunsGlobal; index++)
            {
                var scheduled = ScheduleRun(registry, programs[index].ProgramId, 10000L, 1000d);
                Assert.That(scheduled.Success, Is.True, "run " + index);
                registry.TryBeginRun(scheduled.Run.ProgramRunId, 1100d, 1);
            }

            var refused = ScheduleRun(registry, programs[DebugLimits.MaxActiveProgramRunsGlobal].ProgramId, 10000L, 1200d);
            Assert.That(refused.Failure, Is.EqualTo(UnityCliDebugProgramFailures.RunLimitExceeded));
            Assert.That(refused.Budget.ActiveRunsUsed, Is.EqualTo(DebugLimits.MaxActiveProgramRunsGlobal));
            Assert.That(refused.Budget.ActiveRunsRemaining, Is.EqualTo(0));
        }

        [Test]
        public void RemoveRequiresNoActiveRunAndInterruptedRunsAreNotTerminalSuccesses()
        {
            var registry = new UnityCliDebugProgramRegistry(Capacity());
            var program = Install(registry, "alpha").Definition;
            var run = ScheduleRun(registry, program.ProgramId, 10000L, 1000d);

            string state;
            int activeRuns;
            int attachedBindings;
            Assert.That(registry.CanRemove(program.ProgramId, out state, out activeRuns, out attachedBindings), Is.EqualTo(UnityCliDebugProgramFailures.RunActive));
            Assert.That(activeRuns, Is.EqualTo(1));
            Assert.That(state, Is.EqualTo(UnityCliDebugProgramStates.Installed));

            Assert.That(registry.InterruptActiveRuns("the runtime segment changed"), Is.EqualTo(1));
            Assert.That(run.Run.State, Is.EqualTo(UnityCliDebugProgramRunStates.Interrupted));
            Assert.That(run.Run.IsTerminal, Is.True);
            Assert.That(run.Run.FailureCode, Is.EqualTo("E_DEBUG_PROGRAM_RUN_INTERRUPTED"));
            Assert.That(registry.CanRemove(program.ProgramId, out state, out activeRuns, out attachedBindings), Is.Null);
            Assert.That(registry.Remove(program.ProgramId).Success, Is.True);
            Assert.That(registry.Find(program.ProgramId), Is.Null);
        }

        [Test]
        public void AContinuousDriverKeepsClaimingStepsWhileTheRunIsLive()
        {
            var registry = new UnityCliDebugProgramRegistry(Capacity());
            var program = Install(registry, "alpha").Definition;
            var run = ScheduleRun(registry, program.ProgramId, 1000L, 1000d);

            Assert.That(registry.TryBeginStep(run.Run.ProgramRunId, 1100d, 1), Is.True, "the first step claims the pending run");
            Assert.That(run.Run.State, Is.EqualTo(UnityCliDebugProgramRunStates.Running));
            Assert.That(registry.TryBeginStep(run.Run.ProgramRunId, 1100d, 1), Is.False, "a step never reenters code that is still in flight");
            registry.MarkCodeReturned(run.Run.ProgramRunId);
            Assert.That(registry.TryBeginStep(run.Run.ProgramRunId, 1100d, 1), Is.True, "the same driver continues its own running run");
            Assert.That(run.Run.StartedAtUtc, Is.Not.Empty);

            registry.MarkCodeReturned(run.Run.ProgramRunId);
            registry.RequestCancellation(run.Run.ProgramRunId, "cooperative stop");
            Assert.That(run.Run.CancellationRequested, Is.True);
            Assert.That(registry.TryBeginStep(run.Run.ProgramRunId, 1100d, 1), Is.False, "a cancelled run never gets another step");

            var expired = ScheduleRun(registry, Install(registry, "beta").Definition.ProgramId, 100L, 5000d);
            registry.MarkCodeReturned(run.Run.ProgramRunId);
            registry.CompleteRun(run.Run.ProgramRunId, UnityCliDebugProgramRunStates.Completed, string.Empty, string.Empty, 5100d);
            Assert.That(registry.TryBeginStep(run.Run.ProgramRunId, 5150d, 1), Is.False, "a terminal run never gets another step");
            Assert.That(registry.TryBeginStep(expired.Run.ProgramRunId, 6000d, 1), Is.False, "a run past its deadline never executes late");
        }

        [Test]
        public void DriverAndBindingFactsAreProgramScoped()
        {
            var registry = new UnityCliDebugProgramRegistry(Capacity());
            var program = Install(registry, "alpha").Definition;
            registry.MarkStepDriverPlanted(program.ProgramId, true);
            Assert.That(program.StepDriverPlanted, Is.True);
            registry.MarkStepDriverPlanted(program.ProgramId, false);
            Assert.That(program.StepDriverPlanted, Is.False);

            registry.MarkActive(program.ProgramId);
            Assert.That(program.State, Is.EqualTo(UnityCliDebugProgramStates.Active));
            registry.MarkDormant(program.ProgramId);
            Assert.That(program.State, Is.EqualTo(UnityCliDebugProgramStates.Installed), "a finished run returns the program to dormant");
            Assert.That(registry.CloseFutureDispatch(program.ProgramId, CreatedAtUtc, 5d), Is.True);
            Assert.That(program.State, Is.EqualTo(UnityCliDebugProgramStates.Stopped));
            Assert.That(program.StoppedAtMonotonicMs, Is.EqualTo(5d));
        }

        [Test]
        public void FrozenStatesCategoriesSitesAndPhasesAreClosedSets()
        {
            Assert.That(UnityCliDebugProgramEntryCategories.All().OrderBy(value => value, StringComparer.Ordinal),
                Is.EqualTo(new[] { "cleanup", "listener", "method", "oneshot", "step" }.OrderBy(value => value, StringComparer.Ordinal)));
            Assert.That(UnityCliDebugProgramExecutionSites.All(),
                Is.EqualTo(new[] { "editor-tick", "method-prefix", "method-postfix", "signal", "worker-thread", "player-loop-phase" }));
            Assert.That(UnityCliDebugProgramExecutionSites.IsDispatchSite("editor-tick"), Is.False);
            Assert.That(UnityCliDebugProgramExecutionSites.IsDispatchSite("signal"), Is.True);
            Assert.That(UnityCliDebugProgramStates.IsDefined("dormant"), Is.False);
            Assert.That(UnityCliDebugProgramStates.IsDefined("installed"), Is.True);
            Assert.That(UnityCliDebugProgramRunStates.IsTerminal("running"), Is.False);
            Assert.That(UnityCliDebugProgramRunStates.IsTerminal("interrupted"), Is.True);
            Assert.That(UnityCliDebugProgramBindingPhases.IsDefined("prefix"), Is.True);
            Assert.That(UnityCliDebugProgramBindingPhases.IsDefined("begin"), Is.False);
            Assert.That(UnityCliDebugProgramEntryCategories.IsDefined("watch"), Is.False);
        }

        [Test]
        public void AbiSignatureTableFreezesExactlyOneParameterShapePerCategory()
        {
            foreach (var category in UnityCliDebugProgramEntryCategories.All())
            {
                var parameters = UnityCliDebugProgramAbi.ParameterTypes(category);
                Assert.That(parameters, Is.Not.Null, category + " must declare parameter types");
                Assert.That(parameters.Length, Is.EqualTo(1), category + " must take exactly one parameter");
                var signature = UnityCliDebugProgramAbi.SignatureOf(category);
                Assert.That(signature, Does.Contain(parameters[0].Name), category + " signature must name its declared parameter type");
            }
            Assert.That(UnityCliDebugProgramAbi.ParameterTypes(UnityCliDebugProgramEntryCategories.Method)[0], Is.EqualTo(typeof(UnityCliDebugInvocation)));
            Assert.That(UnityCliDebugProgramAbi.ParameterTypes(UnityCliDebugProgramEntryCategories.Step)[0], Is.EqualTo(typeof(UnityCliDebugStepContext)));
            Assert.That(UnityCliDebugProgramAbi.ParameterTypes(UnityCliDebugProgramEntryCategories.Listener)[0], Is.EqualTo(typeof(UnityCliDebugEventContext)));
            Assert.That(UnityCliDebugProgramAbi.ParameterTypes(UnityCliDebugProgramEntryCategories.OneShot)[0], Is.EqualTo(typeof(UnityCliDebugOneShotContext)));
            Assert.That(UnityCliDebugProgramAbi.ParameterTypes(UnityCliDebugProgramEntryCategories.Cleanup)[0], Is.EqualTo(typeof(UnityCliDebugCleanupContext)));
            Assert.That(UnityCliDebugProgramAbi.ParameterTypes("watch"), Is.Null, "an unknown category has no legal shape");
            var table = UnityCliDebugProgramAbi.SignatureTable();
            foreach (var category in UnityCliDebugProgramEntryCategories.All()) Assert.That(table, Does.Contain(category));
        }

        [Test]
        public void AbiResolutionRejectsMismatchedOverloadsAsyncVoidAndUnmarkedWrappers()
        {
            var assembly = typeof(DebugProgramTests).Assembly;
            const string probeType = "UnityCli.Tests.DebugProgramTests+ProgramAbiProbe";

            var legal = new List<UnityCliDebugProgramEntry> { new UnityCliDebugProgramEntry { Name = "Step", Category = "step", DeclaringType = probeType } };
            List<ResolvedProgramEntry> resolved;
            string failure;
            string detail;
            Assert.That(UnityCliDebugProgramAbi.TryResolve(assembly, legal, out resolved, out failure, out detail), Is.True, failure + " / " + detail);
            Assert.That(resolved.Count, Is.EqualTo(1));
            Assert.That(resolved[0].Instance, Is.False);
            Assert.That(resolved[0].Entry.Signature, Does.Contain("UnityCliDebugStepResult"));

            var asyncVoid = new List<UnityCliDebugProgramEntry> { new UnityCliDebugProgramEntry { Name = "FireAndForget", Category = "cleanup", DeclaringType = probeType } };
            Assert.That(UnityCliDebugProgramAbi.TryResolve(assembly, asyncVoid, out resolved, out failure, out detail), Is.False);
            Assert.That(detail, Does.Contain("async state machine"));

            var overloads = new List<UnityCliDebugProgramEntry> { new UnityCliDebugProgramEntry { Name = "Ambiguous", Category = "oneshot", DeclaringType = probeType } };
            Assert.That(UnityCliDebugProgramAbi.TryResolve(assembly, overloads, out resolved, out failure, out detail), Is.True, failure + " / " + detail);
            Assert.That(resolved[0].Method.GetParameters().Length, Is.EqualTo(1), "only the overload matching the category signature may be resolved");

            var wrongReturn = new List<UnityCliDebugProgramEntry> { new UnityCliDebugProgramEntry { Name = "VoidStep", Category = "step", DeclaringType = probeType } };
            Assert.That(UnityCliDebugProgramAbi.TryResolve(assembly, wrongReturn, out resolved, out failure, out detail), Is.False);
            Assert.That(detail, Does.Contain("UnityCliDebugStepResult"));

            var unmarkedWrapper = new List<UnityCliDebugProgramEntry> { new UnityCliDebugProgramEntry { Name = "InstanceStep", Category = "step", DeclaringType = probeType } };
            Assert.That(UnityCliDebugProgramAbi.TryResolve(assembly, unmarkedWrapper, out resolved, out failure, out detail), Is.False, "an instance entry requires the wrapper marker");
            Assert.That(detail, Does.Contain("UnityCliDebugProgramWrapperAttribute"));

            var missingType = new List<UnityCliDebugProgramEntry> { new UnityCliDebugProgramEntry { Name = "Step", Category = "step", DeclaringType = "UnityCli.Tests.DoesNotExist" } };
            Assert.That(UnityCliDebugProgramAbi.TryResolve(assembly, missingType, out resolved, out failure, out detail), Is.False);
            Assert.That(detail, Does.Contain("does not contain"));
        }

        [Test]
        public void RunTargetNamesTheDeclaredExecutionPointForEverySite()
        {
            var registry = new UnityCliDebugProgramRegistry(Capacity());
            var step = new UnityCliDebugProgramEntry { Name = "Step", Category = UnityCliDebugProgramEntryCategories.Step, DeclaringType = "Probe" };
            var unbound = InstallWith(registry, "alpha", new List<UnityCliDebugProgramEntry> { step }, new List<UnityCliDebugProgramBinding>());

            Assert.That(DebugProgramHost.TargetText(step, unbound, UnityCliDebugProgramExecutionSites.EditorTick, string.Empty),
                Is.EqualTo("main-thread scheduler"), "only a run that really is served by the central editor tick uses the central-tick wording");
            var phaseTarget = DebugProgramHost.TargetText(step, unbound, UnityCliDebugProgramExecutionSites.PlayerLoopPhase, "after-update");
            Assert.That(phaseTarget, Does.Contain("after-update"), "a phase run must name the real PlayerLoop phase it declared");
            Assert.That(phaseTarget, Is.Not.EqualTo("main-thread scheduler"), "a phase run must never be described as the central editor tick");
            Assert.That(DebugProgramHost.TargetText(step, unbound, UnityCliDebugProgramExecutionSites.WorkerThread, string.Empty),
                Does.Contain("worker-thread"), "a site without an exact target states its declared execution point instead of claiming the central tick");

            // 有 exact target 的运行说该 target；无 target 的 signal 投递真的由中央 tick 取得执行权。
            var listener = new UnityCliDebugProgramEntry { Name = "OnSignal", Category = UnityCliDebugProgramEntryCategories.Listener, DeclaringType = "Probe" };
            var bound = InstallWith(registry, "beta", new List<UnityCliDebugProgramEntry> { listener }, new List<UnityCliDebugProgramBinding>
            {
                new UnityCliDebugProgramBinding { BindingId = "b1", Phase = UnityCliDebugProgramBindingPhases.Signal, Entry = "OnSignal", TargetSummary = "Assembly|Type.Method() : System.Void static", TargetKey = "key" }
            });
            Assert.That(DebugProgramHost.TargetText(listener, bound, UnityCliDebugProgramExecutionSites.Signal, string.Empty),
                Is.EqualTo("Assembly|Type.Method() : System.Void static"), "a run with an exact target reports that target summary");

            var frameworkSignal = InstallWith(registry, "gamma", new List<UnityCliDebugProgramEntry> { listener }, new List<UnityCliDebugProgramBinding>
            {
                new UnityCliDebugProgramBinding { BindingId = "b1", Phase = UnityCliDebugProgramBindingPhases.Signal, Entry = "OnSignal", TargetSummary = string.Empty, TargetKey = string.Empty }
            });
            Assert.That(DebugProgramHost.TargetText(listener, frameworkSignal, UnityCliDebugProgramExecutionSites.Signal, string.Empty),
                Is.EqualTo("main-thread scheduler"), "a signal run without an exact target is delivered by the central editor tick");
        }

        [Test]
        public void TheStepEntryMemberAbiIsPublicInEveryDescriptorThatDescribesCheckAndEmit()
        {
            // 陌生人无法通过公开面查询 UnityCliDebugStepContext（它在 CLI 自己的编辑器程序集），
            // 因此成员签名必须由单一 ABI owner 进入每个描述 check/emit 语义的描述符。
            var abi = UnityCliDebugProgramAbi.StepContextAbi();
            Assert.That(abi, Does.Contain("void Emit(string name, object value)"));
            Assert.That(abi, Does.Contain("void Check(string name, object expected, object actual, bool passed, string coverage = null, params string[] evidence)"));
            Assert.That(abi, Does.Contain("long StepIndex { get; }"));
            Assert.That(abi, Does.Contain("NextFrame(long frames = 1)"));
            Assert.That(abi, Does.Contain("Finish(object state, long stateEpoch)"));
            Assert.That(abi, Does.Contain("object Arg(string name)"));
            Assert.That(abi, Does.Contain("string MethodIdentity { get; }"));

            var install = new DebugProgramInstallTool().Describe();
            Assert.That(install.Description, Does.Contain(abi), "install must publish the step member ABI");
            Assert.That(install.Description, Does.Contain("void Check("), "install must name the Check signature");
            Assert.That(install.Description, Does.Contain("void Emit(string name, object value)"), "install must name the Emit signature");

            var replace = new DebugProgramReplaceTool().Describe();
            Assert.That(replace.Description, Does.Contain(abi), "replace publishes the same entry ABI and must carry it too");

            var run = new DebugProgramRunTool().Describe();
            Assert.That(run.Description, Does.Contain(abi), "program run publishes the same entry ABI and must carry it too");

            var begin = new DebugRunBeginTool().Describe();
            Assert.That(begin.Description, Does.Contain(abi), "run.begin names context.Check for program checks and must carry the same ABI");
        }

        [Test]
        public void TheStepIndexBaseAndTheStepEvidenceSemanticsArePublic()
        {
            var install = new DebugProgramInstallTool().Describe();
            Assert.That(install.Description, Does.Contain("the first executed step of a run has StepIndex 1"), "the entry ABI text must state the StepIndex base");
            Assert.That(install.Description, Does.Contain("performs exactly three steps"), "the same text must let a reader count the embedded example's steps");
            Assert.That(UnityCliDebugProgramAbi.ExampleSource(), Does.Contain("if (context.StepIndex == 3)"),
                "the example must test one exact index instead of an open comparison");

            var wait = new DebugProgramWaitTool().Describe();
            Assert.That(wait.Description, Does.Contain("number of step invocations that actually executed"), "wait must define stepCount");
            Assert.That(wait.Description, Does.Contain("exactly one revision per executed step"), "wait must define the per-step revision list");
            Assert.That(wait.Description, Does.Contain("the real PlayerLoop phase name for a phase run"), "wait must state how target names its execution point");
        }

        [Test]
        public void ProgramCapacityIsProjectedFromTheSingleDebugLimitOwner()
        {
            var capacity = Capacity();
            Assert.That(capacity.MaxProgramsPerSession, Is.EqualTo(32));
            Assert.That(capacity.MaxProgramsGlobal, Is.EqualTo(64));
            Assert.That(capacity.MaxActiveRunsGlobal, Is.EqualTo(16));
            Assert.That(capacity.MaxSourceBytes, Is.EqualTo(256 * 1024));
            Assert.That(capacity.MaxStateBytes, Is.EqualTo(DebugLimits.MaxProgramStateBytes));
            Assert.That(capacity.MaxArgsBytes, Is.EqualTo(DebugLimits.MaxProgramArgsBytes));
            Assert.That(capacity.MaxRunDurationMs, Is.EqualTo(DebugLimits.MaxProgramRunMaxDurationMs));
            Assert.That(DebugLimits.ProgramSummary(), Does.Contain("32 programs per session"));
        }

        /// <summary>ABI 校验用的探针；包含合法、错误返回类型、async void、歧义与未标记 wrapper 的形状。</summary>
        public class ProgramAbiProbe
        {
            public static UnityCliDebugStepResult Step(UnityCliDebugStepContext context) { return UnityCliDebugStepResult.Finish(); }

            public static void VoidStep(UnityCliDebugStepContext context) { }

            public static object Ambiguous(UnityCliDebugOneShotContext context) { return null; }

            public static string Ambiguous(UnityCliDebugOneShotContext context, int extra) { return extra.ToString(); }

            public static async void FireAndForget(UnityCliDebugCleanupContext context) { await System.Threading.Tasks.Task.Yield(); }

            public UnityCliDebugStepResult InstanceStep(UnityCliDebugStepContext context) { return UnityCliDebugStepResult.Finish(); }
        }
    }
}
#endif
