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
    /// 离主线程 dispatch 路径的 Unity-API 洁净性证明。
    ///
    /// 判据：一个 domain 里首次 worker 命中会在命中线程上跑完整条派发路径
    /// （定义表读取、认领运行、入口执行、事件入库、终态发布、运行资源释放）。
    /// 该路径上任何一个主线程专属 Unity 读取都会抛 UnityException，并被记成 program fault
    /// 且让运行资源留在会话账本上。
    ///
    /// 因此本测试从真实 worker 线程直接驱动同一条 claim 路径，要求：无异常、无 fault 事实、
    /// 运行真的进入终态、绑定仍然 armed、运行资源真的归还。它不依赖 detour 命中，
    /// 因此能在没有确切目标调用的情况下单独覆盖“首次离主线程执行”这一时刻。
    /// </summary>
    public sealed class DebugOffMainDispatchTests
    {
        /// <summary>method 入口源码；只记录执行线程，不依赖任何 Unity 状态。</summary>
        private const string EntrySource =
            "using UnityCli;\n" +
            "using System.Threading;\n" +
            "public static class OffMainDispatchEntry\n" +
            "{\n" +
            "    public static void OnHit(UnityCliDebugInvocation invocation)\n" +
            "    {\n" +
            "        invocation.Program.Emit(\"off-main-hit\", Thread.CurrentThread.ManagedThreadId);\n" +
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
        public void AFirstWorkerThreadDispatchClaimRunsWithoutAnyMainThreadOnlyUnityRead()
        {
            var sessionId = CreateSession("off-main-dispatch");
            var programId = InstallBoundProgram(sessionId, "off-main-dispatch-program");
            var run = DebugProgramHost.ApplyRun(DebugProgramHost.PrepareRun(new DebugProgramRunRequest
            {
                ProgramId = programId,
                Entry = "OnHit",
                ExecutionSite = UnityCliDebugProgramExecutionSites.WorkerThread,
                DurationMs = 60000L
            }));
            var record = DebugProgramHost.Registry().FindRun(run.ProgramRunId);
            Assert.That(record.State, Is.EqualTo(UnityCliDebugProgramRunStates.Scheduled), "the dispatch run waits for its execution point");
            Assert.That(DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = sessionId }).Budget.ActiveResourcesUsed, Is.EqualTo(1L), "an activated run holds its one session resource slot");

            // 真实 worker 线程上的首次 claim：这条路径在修复前会因主线程专属读取抛 UnityException。
            var workerThreadId = 0;
            var claimed = false;
            Exception error = null;
            var thread = new Thread(() =>
            {
                workerThreadId = Thread.CurrentThread.ManagedThreadId;
                try
                {
                    claimed = DebugProgramHost.DispatchMethod(programId, "OnHit", new UnityCliHookContext(new OffMainInvocation()), true);
                }
                catch (Exception exception)
                {
                    error = exception;
                }
            });
            thread.Start();
            Assert.That(thread.Join(TimeSpan.FromSeconds(30)), Is.True, "an off-main dispatch claim must not deadlock the caller");
            Assert.That(error, Is.Null, "the off-main dispatch path must not call a main-thread-only Unity API");

            Assert.That(claimed, Is.True);
            Assert.That(record.State, Is.EqualTo(UnityCliDebugProgramRunStates.Completed), "the off-main claim must publish a real terminal state");
            Assert.That(record.InFlight, Is.False);
            Assert.That(record.ExecutionThreadId, Is.EqualTo(workerThreadId), "the run must record the thread that claimed it");

            var emitted = ProgramEvents(programId, "emit");
            Assert.That(emitted.Count, Is.EqualTo(1), "the entry must run exactly once for the claimed hit");
            Assert.That(emitted[0].MainThread, Is.False, "the entry ran on a worker thread");
            Assert.That(emitted[0].ThreadId, Is.EqualTo(workerThreadId));
            Assert.That(ProgramEvents(programId, "fault"), Is.Empty, "an off-main claim must not record any program fault");

            var binding = DebugProgramHost.Info(new DebugProgramInfoRequest { ProgramId = programId }).Program.Bindings[0];
            Assert.That(binding.Attached, Is.True, "a healthy off-main claim must keep the binding armed");
            Assert.That(MethodHookHost.Info(new MethodHookInfoRequest { HookId = binding.HookId }).LastFault, Is.Null);

            // 资源释放是首次命中里唯一曾经读取主线程专属 Play 状态的一步：它必须真的发生。
            Assert.That(
                DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = sessionId }).Budget.ActiveResourcesUsed,
                Is.EqualTo(0L),
                "the off-main completion must really release the run resource instead of throwing out of the release call");
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
                    new DebugProgramEntrySpec { Name = "OnHit", Category = UnityCliDebugProgramEntryCategories.Method, DeclaringType = "OffMainDispatchEntry" }
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
                            AssemblyName = typeof(OffMainProbe).Assembly.GetName().Name,
                            TypeName = typeof(OffMainProbe).FullName,
                            MethodName = "Target",
                            ParameterTypes = new List<string> { "System.String" },
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
                    if (DebugProgramHost.Registry().Find(programId) == null) continue;
                    foreach (var run in DebugProgramHost.Registry().RunsFor(programId).Where(candidate => !candidate.IsTerminal).ToList())
                        DebugProgramHost.ApplyStop(DebugProgramHost.PrepareStop(new DebugProgramStopRequest { ProgramRunId = run.ProgramRunId }));
                    DebugProgramHost.ApplyRemove(DebugProgramHost.PrepareRemove(new DebugProgramRemoveRequest { ProgramId = programId }));
                }
                catch (Exception exception)
                {
                    UnityCliBridge.WriteLog("debug_off_main_dispatch_cleanup_failed program=" + programId + " error=" + exception.GetType().Name);
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
                    UnityCliBridge.WriteLog("debug_off_main_session_cleanup_failed session=" + sessionId + " error=" + exception.GetType().Name);
                }
            }
            createdSessions.Clear();
        }

        /// <summary>被绑定的探针类型；本测试不调用它，只用作 exact target 的解析对象。</summary>
        public class OffMainProbe
        {
            public void Target(string label)
            {
            }
        }

        /// <summary>最小的命中上下文：只提供派发路径真正读取的现场字段。</summary>
        private sealed class OffMainInvocation : IUnityCliHookInvocation
        {
            private readonly Dictionary<string, object> state = new Dictionary<string, object>(StringComparer.Ordinal);

            public object Instance { get { return null; } }
            public object[] Arguments { get { return new object[] { "off-main" }; } }
            public object Result { get; set; }
            public void SetArgument(int index, object value) { }
            public void SkipOriginal() { }
            public object ReadField(string declaringType, string fieldName) { return null; }
            public void WriteField(string declaringType, string fieldName, object value) { }
            public void Emit(string name, object value) { }
            public IDictionary<string, object> State { get { return state; } }
            public string HookId { get { return string.Empty; } }
            public string MethodIdentity { get { return "off-main-dispatch-test|Target(System.String) : System.Void instance"; } }
            public string PlaySessionId { get { return string.Empty; } }
            public string ClockSegment { get { return string.Empty; } }
            public double ElapsedMilliseconds { get { return 0d; } }
            public int Frame { get { return 0; } }
            public bool IsPrefix { get { return true; } }
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
