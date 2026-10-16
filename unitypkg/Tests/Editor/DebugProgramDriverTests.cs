#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// step driver 只经唯一中央 scheduler 的注册入口推进的端到端行为测试。
    ///
    /// 该测试必须走真实链路：建立 session → 安装并编译 program → run 排定 step driver →
    /// 只调用 DebugScheduler.Tick()，也就是 UnityCliWaiter 用
    /// TickCoordinator("debug.program", DebugScheduler.Tick) 登记进唯一主循环的那个入口。
    /// 它不得直接调用 DebugProgramHost.AdvanceDriver，否则会绕过 driver 取得步骤执行权的真实路径。
    /// </summary>
    public sealed class DebugProgramDriverTests
    {
        /// <summary>三步后 Finish 的 step 程序；每步 emit 一次，用于证明每个 step 恰好执行一次。</summary>
        private const string StepSource =
            "using UnityCli;\n" +
            "public static class DriverProbe\n" +
            "{\n" +
            "    public static UnityCliDebugStepResult Step(UnityCliDebugStepContext context)\n" +
            "    {\n" +
            "        context.Emit(\"driver-tick\", context.StepIndex);\n" +
            "        if (context.StepIndex >= 3) return UnityCliDebugStepResult.Finish();\n" +
            "        return UnityCliDebugStepResult.Continue(UnityCliDebugStepWait.NextFrame());\n" +
            "    }\n" +
            "}\n";

        private readonly List<string> installedPrograms = new List<string>();
        private static readonly List<string> createdSessions = new List<string>();

        [SetUp]
        public void ResetFacilities()
        {
            DebugCompilerOwner.ResetDomain();
            DebugScheduler.Reset();
        }

        [TearDown]
        public void ResetFacilitiesAfterTest()
        {
            CleanupFacilities();
            DebugScheduler.Reset();
            DebugCompilerOwner.ResetDomain();
        }

        [Test]
        public void TheRegisteredCentralTickAdvancesOneStepPerTickUntilTheEntryFinishes()
        {
            var sessionId = CreateSession("driver-advance");
            var programId = InstallStepProgram(sessionId, "driver-advance-program");
            var run = DebugProgramHost.ApplyRun(DebugProgramHost.PrepareRun(new DebugProgramRunRequest
            {
                ProgramId = programId,
                Entry = "Step",
                ExecutionSite = UnityCliDebugProgramExecutionSites.EditorTick,
                DurationMs = 60000L
            }));

            Assert.That(run.StepDriver, Is.True, "an editor-tick step run must plant the single continuous driver");
            Assert.That(run.Target, Is.EqualTo("main-thread scheduler"), "an editor-tick run must name the central editor tick that serves it");
            Assert.That(run.State, Is.EqualTo(UnityCliDebugProgramRunStates.Scheduled), "a scheduled run has not executed yet");
            Assert.That(DebugProgramHost.Info(new DebugProgramInfoRequest { ProgramId = programId }).Driver.Planted, Is.True);

            var record = DebugProgramHost.Registry().FindRun(run.ProgramRunId);
            Assert.That(record, Is.Not.Null);
            for (var tick = 0; tick < 60 && !record.IsTerminal; tick++)
            {
                DebugScheduler.Tick();
                Thread.Sleep(2);
            }

            Assert.That(record.IsTerminal, Is.True, "the step driver must reach a terminal state through the registered tick path");
            Assert.That(record.State, Is.EqualTo(UnityCliDebugProgramRunStates.Completed));
            Assert.That(record.FailureCode, Is.Empty);
            Assert.That(record.StepCount, Is.EqualTo(3L), "every Continue step must execute exactly once before Finish");
            Assert.That(record.StepRevisions.Count, Is.EqualTo(3));
            Assert.That(record.StepRevisions, Is.All.EqualTo(run.Revision), "every executed step records exactly one revision and that revision is the run's own");
            Assert.That(record.InFlight, Is.False, "no synchronous entry code may stay in flight after a terminal state");

            var info = DebugProgramHost.Info(new DebugProgramInfoRequest { ProgramId = programId });
            Assert.That(info.Driver.Planted, Is.False, "the driver must be closed with its run");
            Assert.That(info.ActiveRuns, Is.Empty);
            Assert.That(info.Program.StepDriver, Is.False);
            Assert.That(GetSchedulerCount("drivers"), Is.EqualTo(0L), "a terminal run must not leave a scheduler driver behind");

            var emitted = ProgramEvents(programId, "emit").Select(item => Convert.ToInt64(item.Value, CultureInfo.InvariantCulture)).OrderBy(value => value).ToList();
            Assert.That(emitted, Is.EqualTo(new List<long> { 1L, 2L, 3L }), "each step must run exactly once with its own 1-based step index, so the first executed step sees StepIndex 1");
            Assert.That(ProgramEvents(programId, "fault"), Is.Empty, "a healthy driver must not record a fault");
        }

        [Test]
        public void ADriverPastItsDeadlineIsClosedOnceInsteadOfStallingTheScheduler()
        {
            var sessionId = CreateSession("driver-deadline");
            var programId = InstallStepProgram(sessionId, "driver-deadline-program");
            var run = DebugProgramHost.ApplyRun(DebugProgramHost.PrepareRun(new DebugProgramRunRequest
            {
                ProgramId = programId,
                Entry = "Step",
                ExecutionSite = UnityCliDebugProgramExecutionSites.EditorTick,
                DurationMs = 1L
            }));

            var record = DebugProgramHost.Registry().FindRun(run.ProgramRunId);
            for (var tick = 0; tick < 20 && !record.IsTerminal; tick++)
            {
                Thread.Sleep(2);
                DebugScheduler.Tick();
            }

            Assert.That(record.IsTerminal, Is.True);
            Assert.That(record.State, Is.EqualTo(UnityCliDebugProgramRunStates.Cancelled));
            Assert.That(record.FailureCode, Is.EqualTo("E_DEBUG_PROGRAM_RUN_EXPIRED"));
            Assert.That(record.StepCount, Is.EqualTo(0L), "a run past its deadline must never execute late");
            Assert.That(ProgramEvents(programId, "emit"), Is.Empty);

            // 关闭必须恰好发生一次：既不能留下 driver，也不能每 tick 重复记录 terminal-pending 故障。
            for (var tick = 0; tick < 5; tick++) DebugScheduler.Tick();
            Assert.That(GetSchedulerCount("drivers"), Is.EqualTo(0L), "an expired run must not leave a scheduler driver behind");
            var faults = ProgramEvents(programId, "fault");
            Assert.That(faults.Where(item => string.Equals(item.FaultCode, "E_DEBUG_PROGRAM_TERMINAL_PENDING", StringComparison.Ordinal)), Is.Empty);
            Assert.That(DebugProgramHost.Info(new DebugProgramInfoRequest { ProgramId = programId }).Driver.Planted, Is.False);

            var lifecycles = ProgramEvents(programId, "lifecycle").Where(item => string.Equals(item.Name, "cancelled", StringComparison.Ordinal)).ToList();
            Assert.That(lifecycles.Count, Is.EqualTo(1), "a cancelled terminal must be recorded exactly once");
        }

        private static string CreateSession(string labelPrefix)
        {
            var plan = DebugSessionHost.PrepareCreate(new DebugSessionCreateRequest
            {
                Label = labelPrefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                ActivationScope = UnityCliDebugActivationScopes.EditorAndPlay,
                ClockKind = UnityCliDebugClocks.Monotonic
            });
            var created = DebugSessionHost.ApplyCreate(plan);
            createdSessions.Add(created.SessionId);
            return created.SessionId;
        }

        private string InstallStepProgram(string sessionId, string labelPrefix)
        {
            var request = new DebugProgramInstallRequest
            {
                SessionId = sessionId,
                Label = labelPrefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                EntryType = "code",
                Source = StepSource,
                Entries = new List<DebugProgramEntrySpec>
                {
                    new DebugProgramEntrySpec { Name = "Step", Category = UnityCliDebugProgramEntryCategories.Step, DeclaringType = "DriverProbe" }
                }
            };
            // install 只编译与准备，不执行业务代码；这里与 debug.program.install Tool 使用同一 owner 入口。
            var plan = DebugProgramHost.PrepareInstallAsync(request, new TestWaiter(), TimeSpan.FromSeconds(180), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            var installed = DebugProgramHost.ApplyInstall(plan);
            installedPrograms.Add(installed.ProgramId);
            return installed.ProgramId;
        }

        private static long GetSchedulerCount(string key)
        {
            return Convert.ToInt64(DebugScheduler.Describe()[key], CultureInfo.InvariantCulture);
        }

        private static List<DebugEventItem> ProgramEvents(string programId, string kind)
        {
            var result = DebugEventStore.ReadEvents(new DebugEventsRequest
            {
                SourceId = UnityCliDebugProgramAbi.EventSource,
                Kind = kind,
                Limit = DebugLimits.EventsMaxLimit
            });
            return result.Events
                .Where(item => item.MethodIdentity != null && item.MethodIdentity.StartsWith(programId + ":", StringComparison.Ordinal))
                .ToList();
        }

        /// <summary>释放本测试建立的定义、运行与 session，使测试不改变 Editor 的后续观察状态。</summary>
        private void CleanupFacilities()
        {
            foreach (var programId in installedPrograms)
            {
                try
                {
                    var definition = DebugProgramHost.Registry().Find(programId);
                    if (definition == null) continue;
                    foreach (var run in DebugProgramHost.Registry().RunsFor(programId).Where(candidate => !candidate.IsTerminal).ToList())
                        DebugProgramHost.ApplyStop(DebugProgramHost.PrepareStop(new DebugProgramStopRequest { ProgramRunId = run.ProgramRunId }));
                    DebugProgramHost.ApplyRemove(DebugProgramHost.PrepareRemove(new DebugProgramRemoveRequest { ProgramId = programId }));
                }
                catch (Exception exception)
                {
                    UnityCliBridge.WriteLog("debug_program_driver_test_cleanup_failed program=" + programId + " error=" + exception.GetType().Name);
                }
            }
            installedPrograms.Clear();
            foreach (var sessionId in createdSessions)
            {
                try
                {
                    DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
                    // 测试必须把 session 定义也移除：session 定义会跨 Domain Reload 保留并占用
                    // 每 session 16 个的预算，只停不删会让重复跑测试逐步吃满上限并相互污染。
                    DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = sessionId }));
                }
                catch (Exception exception)
                {
                    UnityCliBridge.WriteLog("debug_session_driver_test_cleanup_failed session=" + sessionId + " error=" + exception.GetType().Name);
                }
            }
            createdSessions.Clear();
        }

        /// <summary>
        /// 同步 waiter：EditMode 测试在主线程上同步运行，无法等中央 scheduler 推进；
        /// 编译与 ABI 准备在此 waiter 上立即完成。产品路径只用中央 scheduler 的 waiter。
        /// </summary>
        private sealed class TestWaiter : IUnityCliWaiter
        {
            private const int PollMilliseconds = 20;

            public UnityCliMainThreadAwaitable<bool> NextFrame(int frames = 1, CancellationToken ct = default)
            {
                return Completed(true);
            }

            public UnityCliMainThreadAwaitable<bool> Delay(TimeSpan duration, CancellationToken ct = default)
            {
                return Poll(() => true, duration, ct);
            }

            public UnityCliMainThreadAwaitable<bool> Until(Func<bool> predicate, TimeSpan timeout, CancellationToken ct = default)
            {
                return Poll(predicate, timeout, ct);
            }

            public UnityCliMainThreadAwaitable<bool> UntilStable(Func<bool> predicate, TimeSpan timeout, int minimumFrames = 2, TimeSpan minimumDuration = default, CancellationToken ct = default)
            {
                return Poll(predicate, timeout, ct);
            }

            public UnityCliMainThreadAwaitable<bool> UntilEditorReady(TimeSpan timeout, CancellationToken ct = default)
            {
                return Poll(() => !UnityEditor.EditorApplication.isCompiling && !UnityEditor.EditorApplication.isUpdating, timeout, ct);
            }

            public UnityCliMainThreadAwaitable<bool> UntilPlayModeState(UnityEditor.PlayModeStateChange state, TimeSpan timeout, CancellationToken ct = default)
            {
                return Poll(() => UnityEditor.EditorApplication.isPlaying == (state == UnityEditor.PlayModeStateChange.EnteredPlayMode || state == UnityEditor.PlayModeStateChange.ExitingPlayMode), timeout, ct);
            }

            public UnityCliMainThreadAwaitable<T> ForCallback<T>(Action<Func<Func<T>, bool>, Func<Exception, bool>> start, TimeSpan timeout, CancellationToken ct = default, Action onAbandoned = null)
            {
                Func<T> complete = null;
                start((factory) => { complete = factory; return true; }, _ => false);
                return Completed(complete == null ? default : complete());
            }

            private static UnityCliMainThreadAwaitable<T> Completed<T>(T value)
            {
                return new UnityCliMainThreadAwaitable<T>(new ImmediateSource<T>(value));
            }

            private static UnityCliMainThreadAwaitable<bool> Poll(Func<bool> predicate, TimeSpan timeout, CancellationToken ct)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    if (predicate()) return Completed(true);
                    Thread.Sleep(PollMilliseconds);
                }
                return Completed(predicate());
            }

            private sealed class ImmediateSource<T> : IMainThreadAwaitableSource<T>
            {
                private readonly T value;

                internal ImmediateSource(T value)
                {
                    this.value = value;
                }

                public bool IsCompleted { get { return true; } }
                public void RegisterContinuation(Action continuation) { if (continuation != null) continuation(); }
                public T GetResult() { return value; }
            }
        }
    }
}
#endif
