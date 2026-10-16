#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// dispatch 执行点（method-prefix / worker-thread 等）程序运行的端到端行为测试。
    ///
    /// 覆盖三条互相独立的判据：
    /// - exact 目标在 worker 线程命中时，绑定的 method 入口真的在命中线程执行一次，并在该线程上完成运行；
    /// - 目标始终不到达的运行由声明时长关闭为 cancelled，绝不永久停在 running/inFlight；
    /// - stop 真正关闭一个活动 dispatch 运行，且记账、移除前置条件与 program.remove 一致。
    /// 测试必须走真实链路：真的安装 detour、真的从工作线程调用目标方法、只通过中央 tick 关闭超期运行。
    /// </summary>
    public sealed class DebugProgramDispatchTests
    {
        /// <summary>method 入口的程序源码；emit 记录执行线程，用于证明入口运行在命中线程上。</summary>
        private const string EntrySource =
            "using UnityCli;\n" +
            "using System.Threading;\n" +
            "public static class DispatchEntry\n" +
            "{\n" +
            "    public static void OnHit(UnityCliDebugInvocation invocation)\n" +
            "    {\n" +
            "        invocation.Program.Emit(\"dispatch-hit\", Thread.CurrentThread.ManagedThreadId);\n" +
            "    }\n" +
            "}\n";

        private static readonly List<string> installedPrograms = new List<string>();
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
        public void AWorkerThreadHitRunsTheMethodEntryOnTheHittingThreadAndCompletesTheRun()
        {
            var sessionId = CreateSession("dispatch-worker");
            var programId = InstallBoundProgram(sessionId, "dispatch-worker-program");
            var run = DebugProgramHost.ApplyRun(DebugProgramHost.PrepareRun(new DebugProgramRunRequest
            {
                ProgramId = programId,
                Entry = "OnHit",
                ExecutionSite = UnityCliDebugProgramExecutionSites.WorkerThread,
                DurationMs = 60000L
            }));

            Assert.That(run.Terminal, Is.False, "a dispatch-site run that is still waiting for its target is a bounded non-terminal observation, not a completed run");
            Assert.That(run.Scheduled, Is.True);
            Assert.That(run.Running, Is.False, "activation must not claim synchronous code is already in flight");
            Assert.That(run.BindingsAttached, Is.EqualTo(1L), "the declared binding must be attached for the run");
            Assert.That(run.ExecutionSite, Is.EqualTo(UnityCliDebugProgramExecutionSites.WorkerThread));

            var record = DebugProgramHost.Registry().FindRun(run.ProgramRunId);
            Assert.That(record, Is.Not.Null);
            Assert.That(record.State, Is.EqualTo(UnityCliDebugProgramRunStates.Scheduled));
            Assert.That(record.InFlight, Is.False, "a run waiting for its execution point has no synchronous entry code in flight");

            // 真实命中：目标方法必须从工作线程调用，且原始调用语义不变。
            var instance = new ProgramDispatchProbe();
            var target = typeof(ProgramDispatchProbe).GetMethod("Target");
            var workerThreadId = 0;
            Exception error = null;
            var thread = new Thread(() =>
            {
                workerThreadId = Thread.CurrentThread.ManagedThreadId;
                try { target.Invoke(instance, new object[] { "hit", 1 }); }
                catch (Exception exception) { error = exception; }
            });
            thread.Start();
            Assert.That(thread.Join(TimeSpan.FromSeconds(30)), Is.True, "a worker-thread hit must not deadlock the caller");
            Assert.That(error, Is.Null, "the host must never surface a framework exception to the game call");
            Assert.That(instance.Hits, Is.EqualTo(1), "the original method must still run on the worker thread");

            Assert.That(record.State, Is.EqualTo(UnityCliDebugProgramRunStates.Completed), "the run must complete when its entry really ran");
            Assert.That(record.InFlight, Is.False);
            Assert.That(record.ExecutionThreadId, Is.EqualTo(workerThreadId), "the run must record the thread that claimed it");
            Assert.That(record.StartedAtUtc, Is.Not.Empty);
            Assert.That(DebugProgramHost.Info(new DebugProgramInfoRequest { ProgramId = programId }).ActiveRuns, Is.Empty);

            var emitted = ProgramEvents(programId, "emit");
            Assert.That(emitted.Count, Is.EqualTo(1), "the bound method entry must run exactly once for this hit");
            Assert.That(emitted[0].MainThread, Is.False, "an entry that ran on a worker thread must be recorded as off-main");
            Assert.That(emitted[0].ThreadId, Is.EqualTo(workerThreadId), "the entry must run on the thread that hit the exact target");
            Assert.That(emitted[0].ExecutionPoint, Is.EqualTo(UnityCliDebugProgramExecutionSites.WorkerThread), "the emitted event must name the declared execution point");
            Assert.That(Convert.ToInt64(emitted[0].Value, CultureInfo.InvariantCulture), Is.EqualTo((long)workerThreadId), "the entry must observe the hitting thread id");

            // 根因回归：命中路径不得调用主线程专属 Unity API，因此绑定定义必须仍然是 armed 且没有故障。
            var binding = DebugProgramHost.Info(new DebugProgramInfoRequest { ProgramId = programId }).Program.Bindings[0];
            Assert.That(binding.Attached, Is.True);
            Assert.That(binding.HookId, Is.Not.Empty);
            var hook = MethodHookHost.Info(new MethodHookInfoRequest { HookId = binding.HookId });
            Assert.That(hook.State, Is.EqualTo("armed"), "a worker-thread dispatch must not fault the program binding");
            Assert.That(hook.LastFault, Is.Null);
            Assert.That(ProgramEvents(programId, "fault"), Is.Empty, "a healthy dispatch must not record a program fault");
        }

        [Test]
        public void ARunWhoseTargetNeverArrivesIsClosedByItsDeadline()
        {
            var sessionId = CreateSession("dispatch-deadline");
            var programId = InstallBoundProgram(sessionId, "dispatch-deadline-program");
            var run = DebugProgramHost.ApplyRun(DebugProgramHost.PrepareRun(new DebugProgramRunRequest
            {
                ProgramId = programId,
                Entry = "OnHit",
                ExecutionSite = UnityCliDebugProgramExecutionSites.WorkerThread,
                DurationMs = 5L
            }));

            var record = DebugProgramHost.Registry().FindRun(run.ProgramRunId);
            Assert.That(record.State, Is.EqualTo(UnityCliDebugProgramRunStates.Scheduled), "the target has not arrived yet");
            Assert.That(record.InFlight, Is.False);

            for (var tick = 0; tick < 40 && !record.IsTerminal; tick++)
            {
                Thread.Sleep(5);
                DebugScheduler.Tick();
            }

            Assert.That(record.IsTerminal, Is.True, "a dispatch-site run whose target never arrives must not stay running forever");
            Assert.That(record.State, Is.EqualTo(UnityCliDebugProgramRunStates.Cancelled));
            Assert.That(record.FailureCode, Is.EqualTo("E_DEBUG_PROGRAM_RUN_EXPIRED"));
            Assert.That(record.InFlight, Is.False, "a closed run never reports synchronous code still in flight");
            Assert.That(record.CompletedAtUtc, Is.Not.Empty);
            Assert.That(ProgramEvents(programId, "emit"), Is.Empty, "an expired run must never execute late");

            // 关闭必须恰好发生一次，并且把该运行拥有的资源与 detour 一并释放。
            for (var tick = 0; tick < 5; tick++) DebugScheduler.Tick();
            Assert.That(ProgramEvents(programId, "lifecycle").Count(item => item.Name == "cancelled"), Is.EqualTo(1), "the deadline must close the run exactly once");
            Assert.That(DebugProgramHost.Info(new DebugProgramInfoRequest { ProgramId = programId }).ActiveRuns, Is.Empty);
            Assert.That(DebugProgramHost.Registry().RunsFor(programId).Any(candidate => !candidate.IsTerminal), Is.False, "no non-terminal run may be left behind");

            DebugProgramHost.ApplyRemove(DebugProgramHost.PrepareRemove(new DebugProgramRemoveRequest { ProgramId = programId }));
            installedPrograms.Remove(programId);
            Assert.That(DebugProgramHost.Registry().Find(programId), Is.Null, "a cancelled run must not block removing its program");
        }

        [Test]
        public void StopClosesALiveDispatchRunAndLeavesConsistentAccounting()
        {
            var sessionId = CreateSession("dispatch-stop");
            var programId = InstallBoundProgram(sessionId, "dispatch-stop-program");
            var run = DebugProgramHost.ApplyRun(DebugProgramHost.PrepareRun(new DebugProgramRunRequest
            {
                ProgramId = programId,
                Entry = "OnHit",
                ExecutionSite = UnityCliDebugProgramExecutionSites.WorkerThread,
                DurationMs = 60000L
            }));

            var stop = DebugProgramHost.ApplyStop(DebugProgramHost.PrepareStop(new DebugProgramStopRequest { ProgramRunId = run.ProgramRunId }));
            Assert.That(stop.Terminal, Is.True, "stop must really close a run that never obtained its execution right");
            Assert.That(stop.State, Is.EqualTo(UnityCliDebugProgramRunStates.Cancelled));
            Assert.That(stop.NotYetExecutedStepsCancelled, Is.True);
            Assert.That(stop.DefinitionDeleted, Is.False);
            Assert.That(stop.Budget.ActiveRunsUsed, Is.EqualTo(0L), "a closed run must not keep counting against the active run budget");

            var info = DebugProgramHost.Info(new DebugProgramInfoRequest { ProgramId = programId });
            Assert.That(info.ActiveRuns, Is.Empty, "the per-program active-run view must agree with the budget");
            Assert.That(info.Program.Bindings.All(binding => !binding.Attached), Is.True, "stop must release the binding it activated");

            DebugProgramHost.ApplyRemove(DebugProgramHost.PrepareRemove(new DebugProgramRemoveRequest { ProgramId = programId }));
            installedPrograms.Remove(programId);
            Assert.That(DebugProgramHost.Registry().Find(programId), Is.Null, "program.remove must succeed after stop");
        }

        /// <summary>建立一个 session 与一个绑定到探针目标的 method 入口程序。</summary>
        private static string InstallBoundProgram(string sessionId, string labelPrefix)
        {
            var request = new DebugProgramInstallRequest
            {
                SessionId = sessionId,
                Label = labelPrefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                EntryType = "code",
                Source = EntrySource,
                Entries = new List<DebugProgramEntrySpec>
                {
                    new DebugProgramEntrySpec { Name = "OnHit", Category = UnityCliDebugProgramEntryCategories.Method, DeclaringType = "DispatchEntry" }
                },
                Bindings = new List<DebugProgramBindingSpec>
                {
                    new DebugProgramBindingSpec
                    {
                        BindingId = "b1",
                        Phase = UnityCliDebugProgramBindingPhases.Prefix,
                        Entry = "OnHit",
                        Target = new MethodHookTargetSpec
                        {
                            AssemblyName = typeof(ProgramDispatchProbe).Assembly.GetName().Name,
                            TypeName = typeof(ProgramDispatchProbe).FullName,
                            MethodName = "Target",
                            ParameterTypes = new List<string> { "System.String", "System.Int32" },
                            ReturnType = "System.Void",
                            IsStatic = false
                        }
                    }
                }
            };
            var plan = DebugProgramHost.PrepareInstallAsync(request, new TestWaiter(), TimeSpan.FromSeconds(180), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            var installed = DebugProgramHost.ApplyInstall(plan);
            installedPrograms.Add(installed.ProgramId);
            var definition = DebugProgramHost.Registry().Find(installed.ProgramId);
            Assert.That(definition, Is.Not.Null);
            Assert.That(definition.Bindings.Count, Is.EqualTo(1));
            return installed.ProgramId;
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

        /// <summary>只读取属于该 program 的事件；program 事件以 programId:entry 作为方法身份。</summary>
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
        private static void CleanupFacilities()
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
                    UnityCliBridge.WriteLog("debug_program_dispatch_test_cleanup_failed program=" + programId + " error=" + exception.GetType().Name);
                }
            }
            installedPrograms.Clear();
            foreach (var sessionId in createdSessions)
            {
                try
                {
                    DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
                    DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = sessionId }));
                }
                catch (Exception exception)
                {
                    UnityCliBridge.WriteLog("debug_session_dispatch_test_cleanup_failed session=" + sessionId + " error=" + exception.GetType().Name);
                }
            }
            createdSessions.Clear();
        }

        /// <summary>被绑定的探针类型：hit 计数证明原始方法仍在执行，方法本身不依赖 Unity 状态。</summary>
        public class ProgramDispatchProbe
        {
            public int Hits;

            public void Target(string label, int delta)
            {
                Hits += delta;
            }
        }

        /// <summary>同步 waiter：EditMode 测试在主线程上同步运行，产品路径只用中央 scheduler 的 waiter。</summary>
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
