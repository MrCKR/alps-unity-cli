#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// 框架级 wait 能力。工具作者只能表达“等什么”，不能自行管理 update 生命周期。
    /// </summary>
    public interface IUnityCliWaiter
    {
        UnityCliMainThreadAwaitable<bool> NextFrame(int frames = 1, CancellationToken ct = default);
        UnityCliMainThreadAwaitable<bool> Delay(TimeSpan duration, CancellationToken ct = default);
        UnityCliMainThreadAwaitable<bool> Until(Func<bool> predicate, TimeSpan timeout, CancellationToken ct = default);
        UnityCliMainThreadAwaitable<bool> UntilStable(Func<bool> predicate, TimeSpan timeout, int minimumFrames = 2, TimeSpan minimumDuration = default, CancellationToken ct = default);
        UnityCliMainThreadAwaitable<bool> UntilEditorReady(TimeSpan timeout, CancellationToken ct = default);
        UnityCliMainThreadAwaitable<bool> UntilPlayModeState(PlayModeStateChange state, TimeSpan timeout, CancellationToken ct = default);
        UnityCliMainThreadAwaitable<T> ForCallback<T>(Action<Func<Func<T>, bool>, Func<Exception, bool>> start, TimeSpan timeout, CancellationToken ct = default, Action onAbandoned = null);
    }

    internal interface IMainThreadAwaitableSource<out T>
    {
        bool IsCompleted { get; }
        void RegisterContinuation(Action continuation);
        T GetResult();
    }

    /// <summary>
    /// 自定义主线程 awaitable。await continuation 由 scheduler 在 Unity 主线程上恢复。
    /// </summary>
    public readonly struct UnityCliMainThreadAwaitable<T>
    {
        private readonly IMainThreadAwaitableSource<T> source;

        internal UnityCliMainThreadAwaitable(IMainThreadAwaitableSource<T> source)
        {
            this.source = source;
        }

        public Awaiter GetAwaiter() => new Awaiter(source);

        public readonly struct Awaiter : ICriticalNotifyCompletion
        {
            private readonly IMainThreadAwaitableSource<T> source;

            internal Awaiter(IMainThreadAwaitableSource<T> source)
            {
                this.source = source;
            }

            public bool IsCompleted => source == null || source.IsCompleted;
            public T GetResult() => source == null ? default : source.GetResult();
            public void OnCompleted(Action continuation) => source?.RegisterContinuation(continuation);
            public void UnsafeOnCompleted(Action continuation) => source?.RegisterContinuation(continuation);
        }
    }

    internal sealed class UnityCliWaiter : IUnityCliWaiter
    {
        private readonly string requestId;
        private readonly string owner;

        public UnityCliWaiter(string requestId, string owner)
        {
            this.requestId = requestId ?? "unknown";
            this.owner = owner ?? "unknown";
        }

        public UnityCliMainThreadAwaitable<bool> NextFrame(int frames = 1, CancellationToken ct = default)
        {
            return UnityCliWaitScheduler.NextFrame(requestId, owner, frames, ct);
        }

        public UnityCliMainThreadAwaitable<bool> Delay(TimeSpan duration, CancellationToken ct = default)
        {
            return UnityCliWaitScheduler.Delay(requestId, owner, duration, ct);
        }

        public UnityCliMainThreadAwaitable<bool> Until(Func<bool> predicate, TimeSpan timeout, CancellationToken ct = default)
        {
            return UnityCliWaitScheduler.Until(requestId, owner, predicate, timeout, ct);
        }

        public UnityCliMainThreadAwaitable<bool> UntilStable(Func<bool> predicate, TimeSpan timeout, int minimumFrames = 2, TimeSpan minimumDuration = default, CancellationToken ct = default)
        {
            return UnityCliWaitScheduler.UntilStable(requestId, owner, predicate, timeout, minimumFrames, minimumDuration, ct);
        }

        public UnityCliMainThreadAwaitable<bool> UntilEditorReady(TimeSpan timeout, CancellationToken ct = default)
        {
            return UnityCliWaitScheduler.UntilEditorReady(requestId, owner, timeout, ct);
        }

        public UnityCliMainThreadAwaitable<bool> UntilPlayModeState(PlayModeStateChange state, TimeSpan timeout, CancellationToken ct = default)
        {
            return UnityCliWaitScheduler.UntilPlayModeState(requestId, owner, state, timeout, ct);
        }

        public UnityCliMainThreadAwaitable<T> ForCallback<T>(Action<Func<Func<T>, bool>, Func<Exception, bool>> start, TimeSpan timeout, CancellationToken ct = default, Action onAbandoned = null)
        {
            return UnityCliWaitScheduler.ForCallback(requestId, owner, start, timeout, ct, onAbandoned);
        }
    }

    internal static class UnityCliWaitScheduler
    {
        private static readonly object PendingLock = new object();
        private static readonly List<IPendingWait> Pending = new List<IPendingWait>();
        private static readonly System.Diagnostics.Stopwatch AutoTickStopwatch = new System.Diagnostics.Stopwatch();
        private static Action AutoTickSignal;
        private static bool AutoTickEnabled;
        private static long AutoTickIntervalMs = 16L;
        private static long AutoTickForcedCount;
        private static string AutoTickLastError;

        /// <summary>
        /// 自定义 awaitable 的 continuation 只进入显式主线程队列。
        /// 不得替换 UnitySynchronizationContext；Unity 2022 BuildPipeline 会强转当前 context。
        /// </summary>
        private sealed class UnityCliMainThreadContinuationQueue
        {
            private readonly object queueLock = new object();
            private readonly Queue<Action> queue = new Queue<Action>();

            public void Enqueue(Action continuation)
            {
                if (continuation == null) return;
                lock (queueLock)
                {
                    queue.Enqueue(continuation);
                }
            }

            public void Drain()
            {
                while (true)
                {
                    Action continuation;
                    lock (queueLock)
                    {
                        if (queue.Count == 0) return;
                        continuation = queue.Dequeue();
                    }
                    continuation();
                }
            }
        }

        private static readonly UnityCliMainThreadContinuationQueue MainThreadContinuations = new UnityCliMainThreadContinuationQueue();

        private interface IPendingWait
        {
            bool Tick(DateTime utcNow);
            void Cancel(Exception exception);
        }

        private abstract class PendingWaitBase<TResult> : IPendingWait, IMainThreadAwaitableSource<TResult>
        {
            private readonly object stateLock = new object();
            private readonly CancellationTokenRegistration cancellationRegistration;
            private Action continuation;
            private TResult result;
            private Exception exception;
            private bool completed;

            protected PendingWaitBase(string requestId, string owner, CancellationToken cancellationToken)
            {
                RequestId = requestId;
                Owner = owner;
                if (cancellationToken.CanBeCanceled)
                {
                    cancellationRegistration = cancellationToken.Register(() => UnityCliWaitScheduler.QueueOnMainThread(() => Cancel(new OperationCanceledException(cancellationToken))));
                }
            }

            protected string RequestId { get; }
            protected string Owner { get; }

            public bool IsCompleted
            {
                get
                {
                    lock (stateLock) { return completed; }
                }
            }

            public void RegisterContinuation(Action continuation)
            {
                var runNow = false;
                lock (stateLock)
                {
                    if (completed) runNow = true;
                    else this.continuation += continuation;
                }
                if (runNow) UnityCliWaitScheduler.QueueOnMainThread(continuation);
            }

            public TResult GetResult()
            {
                lock (stateLock)
                {
                    if (!completed) throw new InvalidOperationException("Wait is not completed yet.");
                    if (exception != null) ExceptionDispatchInfo.Capture(exception).Throw();
                    return result;
                }
            }

            public bool Tick(DateTime utcNow)
            {
                if (IsCompleted) return true;
                try
                {
                    return Advance(utcNow);
                }
                catch (Exception ex)
                {
                    Fail(ex);
                    return true;
                }
            }

            public virtual void Cancel(Exception exception)
            {
                if (IsCompleted) return;
                Fail(exception);
            }

            protected abstract bool Advance(DateTime utcNow);

            protected bool Succeed(TResult value)
            {
                Action toRun;
                lock (stateLock)
                {
                    if (completed) return true;
                    completed = true;
                    result = value;
                    toRun = continuation;
                    continuation = null;
                }
                cancellationRegistration.Dispose();
                UnityCliWaitScheduler.QueueOnMainThread(toRun);
                return true;
            }

            protected bool Fail(Exception ex)
            {
                Action toRun;
                lock (stateLock)
                {
                    if (completed) return true;
                    completed = true;
                    exception = ex;
                    toRun = continuation;
                    continuation = null;
                }
                cancellationRegistration.Dispose();
                UnityCliWaitScheduler.QueueOnMainThread(toRun);
                return true;
            }
        }

        private sealed class NextFrameWait : PendingWaitBase<bool>
        {
            private int remainingFrames;

            public NextFrameWait(string requestId, string owner, int frames, CancellationToken cancellationToken)
                : base(requestId, owner, cancellationToken)
            {
                remainingFrames = Math.Max(1, frames);
            }

            protected override bool Advance(DateTime utcNow)
            {
                remainingFrames -= 1;
                return remainingFrames <= 0 && Succeed(true);
            }
        }

        private sealed class DelayWait : PendingWaitBase<bool>
        {
            private readonly DateTime deadlineUtc;

            public DelayWait(string requestId, string owner, TimeSpan duration, CancellationToken cancellationToken)
                : base(requestId, owner, cancellationToken)
            {
                deadlineUtc = DateTime.UtcNow.Add(duration < TimeSpan.Zero ? TimeSpan.Zero : duration);
            }

            protected override bool Advance(DateTime utcNow)
            {
                return utcNow >= deadlineUtc && Succeed(true);
            }
        }

        private sealed class PredicateWait : PendingWaitBase<bool>
        {
            private readonly Func<bool> predicate;
            private readonly DateTime deadlineUtc;

            public PredicateWait(string requestId, string owner, Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
                : base(requestId, owner, cancellationToken)
            {
                this.predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
                deadlineUtc = DateTime.UtcNow.Add(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
            }

            protected override bool Advance(DateTime utcNow)
            {
                if (predicate())
                {
                    return Succeed(true);
                }
                if (utcNow >= deadlineUtc)
                {
                    return Succeed(false);
                }
                return false;
            }
        }

        private sealed class StableEditorReadyWait : PendingWaitBase<bool>
        {
            private readonly DateTime deadlineUtc;
            private int consecutiveReadyTicks;
            private DateTime? readySinceUtc;

            public StableEditorReadyWait(string requestId, string owner, TimeSpan timeout, CancellationToken cancellationToken)
                : base(requestId, owner, cancellationToken)
            {
                deadlineUtc = DateTime.UtcNow.Add(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
            }

            protected override bool Advance(DateTime utcNow)
            {
                if (EditorReadyNow())
                {
                    consecutiveReadyTicks += 1;
                    if (!readySinceUtc.HasValue) readySinceUtc = utcNow;
                }
                else
                {
                    consecutiveReadyTicks = 0;
                    readySinceUtc = null;
                }
                if (consecutiveReadyTicks >= 3 && readySinceUtc.HasValue && utcNow - readySinceUtc.Value >= TimeSpan.FromSeconds(1)) return Succeed(true);
                if (utcNow >= deadlineUtc) return Succeed(false);
                return false;
            }
        }

        /// <summary>目标谓词必须连续成立指定帧数和最小时长，避免把瞬态读回作为完成。</summary>
        private sealed class StablePredicateWait : PendingWaitBase<bool>
        {
            private readonly Func<bool> predicate;
            private readonly DateTime deadlineUtc;
            private readonly int minimumFrames;
            private readonly TimeSpan minimumDuration;
            private int consecutiveFrames;
            private DateTime? stableSinceUtc;

            public StablePredicateWait(string requestId, string owner, Func<bool> predicate, TimeSpan timeout, int minimumFrames, TimeSpan minimumDuration, CancellationToken cancellationToken)
                : base(requestId, owner, cancellationToken)
            {
                this.predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
                deadlineUtc = DateTime.UtcNow.Add(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
                this.minimumFrames = Math.Max(1, minimumFrames);
                this.minimumDuration = minimumDuration < TimeSpan.Zero ? TimeSpan.Zero : minimumDuration;
            }

            protected override bool Advance(DateTime utcNow)
            {
                if (predicate())
                {
                    consecutiveFrames += 1;
                    if (!stableSinceUtc.HasValue) stableSinceUtc = utcNow;
                    if (consecutiveFrames >= minimumFrames && utcNow - stableSinceUtc.Value >= minimumDuration) return Succeed(true);
                }
                else
                {
                    consecutiveFrames = 0;
                    stableSinceUtc = null;
                }
                return utcNow >= deadlineUtc && Succeed(false);
            }
        }

        private sealed class CallbackWait<T> : PendingWaitBase<T>
        {
            private readonly object callbackLock = new object();
            private readonly DateTime deadlineUtc;
            private bool callbackCompleted;
            private bool callbackFailed;
            private bool callbackTerminal;
            private T callbackResult;
            private Exception callbackException;
            private readonly Action onAbandoned;

            public CallbackWait(string requestId, string owner, Action<Func<Func<T>, bool>, Func<Exception, bool>> start, TimeSpan timeout, CancellationToken cancellationToken, Action onAbandoned)
                : base(requestId, owner, cancellationToken)
            {
                this.onAbandoned = onAbandoned;
                deadlineUtc = DateTime.UtcNow.Add(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
                start?.Invoke(OnComplete, OnFail);
            }

            protected override bool Advance(DateTime utcNow)
            {
                bool completed;
                bool failed;
                bool abandoned = false;
                T result;
                Exception exception;
                lock (callbackLock)
                {
                    completed = callbackCompleted;
                    failed = callbackFailed;
                    result = callbackResult;
                    exception = callbackException;
                    if (!completed && !failed && utcNow >= deadlineUtc)
                    {
                        callbackTerminal = true;
                        abandoned = true;
                    }
                }
                if (abandoned) NotifyAbandoned();
                if (failed) return Fail(exception ?? new Exception("Callback wait failed."));
                if (completed) return Succeed(result);
                if (utcNow >= deadlineUtc)
                {
                    return Fail(new UnityCliBridge.BridgeCommandException(
                        "E_EXECUTION_TIMEOUT",
                        "Callback wait timed out.",
                        "Retry or reduce scope.",
                        transient: false,
                        retryAfterMs: 0));
                }
                return false;
            }

            public override void Cancel(Exception exception)
            {
                var abandoned = false;
                var completed = false;
                var failed = false;
                T result = default;
                Exception callbackError = null;
                lock (callbackLock)
                {
                    completed = callbackCompleted;
                    failed = callbackFailed;
                    result = callbackResult;
                    callbackError = callbackException;
                    if (!callbackTerminal)
                    {
                        callbackTerminal = true;
                        abandoned = true;
                    }
                }
                if (completed) { Succeed(result); return; }
                if (failed) { Fail(callbackError ?? exception); return; }
                if (abandoned) NotifyAbandoned();
                base.Cancel(exception);
            }

            private void NotifyAbandoned()
            {
                try { onAbandoned?.Invoke(); }
                catch { /* abandonment cleanup must not replace the scheduler terminal */ }
            }

            /// <summary>在 callback lock 内先取得 terminal ownership，再执行结果工厂；拒绝的迟到 callback 不得产生 publication。</summary>
            private bool OnComplete(Func<T> createResult)
            {
                lock (callbackLock)
                {
                    if (callbackTerminal || IsCompleted) return false;
                    callbackTerminal = true;
                    try
                    {
                        callbackResult = createResult == null ? default : createResult();
                        callbackCompleted = true;
                    }
                    catch (Exception exception)
                    {
                        callbackException = exception;
                        callbackFailed = true;
                    }
                    return true;
                }
            }

            private bool OnFail(Exception exception)
            {
                lock (callbackLock)
                {
                    if (callbackTerminal || IsCompleted) return false;
                    callbackTerminal = true;
                    callbackException = exception;
                    callbackFailed = true;
                    return true;
                }
            }
        }

        public static UnityCliMainThreadAwaitable<bool> NextFrame(string requestId, string owner, int frames, CancellationToken cancellationToken)
        {
            var wait = new NextFrameWait(requestId, owner, frames, cancellationToken);
            Register(wait);
            return new UnityCliMainThreadAwaitable<bool>(wait);
        }

        public static UnityCliMainThreadAwaitable<bool> Delay(string requestId, string owner, TimeSpan duration, CancellationToken cancellationToken)
        {
            var wait = new DelayWait(requestId, owner, duration, cancellationToken);
            Register(wait);
            return new UnityCliMainThreadAwaitable<bool>(wait);
        }

        public static UnityCliMainThreadAwaitable<bool> Until(string requestId, string owner, Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var wait = new PredicateWait(requestId, owner, predicate, timeout, cancellationToken);
            Register(wait);
            return new UnityCliMainThreadAwaitable<bool>(wait);
        }

        public static UnityCliMainThreadAwaitable<bool> UntilStable(string requestId, string owner, Func<bool> predicate, TimeSpan timeout, int minimumFrames, TimeSpan minimumDuration, CancellationToken cancellationToken)
        {
            var wait = new StablePredicateWait(requestId, owner, predicate, timeout, minimumFrames, minimumDuration, cancellationToken);
            Register(wait);
            return new UnityCliMainThreadAwaitable<bool>(wait);
        }

        public static UnityCliMainThreadAwaitable<bool> UntilEditorReady(string requestId, string owner, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var wait = new StableEditorReadyWait(requestId, owner, timeout, cancellationToken);
            Register(wait);
            return new UnityCliMainThreadAwaitable<bool>(wait);
        }

        private static bool EditorReadyNow()
        {
            return !EditorApplication.isCompiling
                && !EditorApplication.isUpdating
                && !UnityCliBridge.IsPlayModeChanging()
                && !UnityCliBridge.IsBuildingPlayer()
                && !UnityCliBridge.IsDomainReloading();
        }

        public static UnityCliMainThreadAwaitable<bool> UntilPlayModeState(string requestId, string owner, PlayModeStateChange state, TimeSpan timeout, CancellationToken cancellationToken)
        {
            return Until(requestId, owner, () => MatchesPlayModeState(state), timeout, cancellationToken);
        }

        private static bool MatchesPlayModeState(PlayModeStateChange state)
        {
            var playing = EditorApplication.isPlaying;
            var changing = UnityCliBridge.IsPlayModeChanging();
            switch (state)
            {
                case PlayModeStateChange.EnteredPlayMode: return playing && !changing;
                case PlayModeStateChange.ExitingPlayMode: return playing && changing;
                case PlayModeStateChange.ExitingEditMode: return !playing && changing;
                case PlayModeStateChange.EnteredEditMode: return !playing && !changing;
                default: return false;
            }
        }

        public static UnityCliMainThreadAwaitable<T> ForCallback<T>(string requestId, string owner, Action<Func<Func<T>, bool>, Func<Exception, bool>> start, TimeSpan timeout, CancellationToken cancellationToken, Action onAbandoned = null)
        {
            var wait = new CallbackWait<T>(requestId, owner, start, timeout, cancellationToken, onAbandoned);
            Register(wait);
            return new UnityCliMainThreadAwaitable<T>(wait);
        }

        private static readonly Dictionary<string, string> CoordinatorFailures = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>effect前只验证当前Unity是否仍提供SignalTick，不改变scheduler状态。</summary>
        internal static void PrepareAutoTick(bool enable)
        {
            if (!enable) return;
            var method = SignalTickMethod();
            if (method == null)
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_AUTOTICK_UNAVAILABLE", "EditorApplication.SignalTick is unavailable in Unity " + UnityEngine.Application.unityVersion + ".", "Use a supported Unity version before enabling editor.autotick.set.");
            try
            {
                if (AutoTickSignal == null) AutoTickSignal = (Action)Delegate.CreateDelegate(typeof(Action), method);
            }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_AUTOTICK_UNAVAILABLE", "EditorApplication.SignalTick could not be bound: " + exception.GetType().Name + ".", "Inspect the Unity version before retrying.");
            }
        }

        /// <summary>Tool跨过唯一barrier后更新current-session状态；不注册第二个update callback。</summary>
        internal static void ConfigureAutoTick(bool enable, long intervalMs)
        {
            PrepareAutoTick(enable);
            AutoTickIntervalMs = Math.Max(0L, Math.Min(60000L, intervalMs));
            AutoTickEnabled = enable;
            AutoTickLastError = null;
            if (enable) AutoTickStopwatch.Restart();
            else AutoTickStopwatch.Stop();
        }

        internal static EditorAutoTickState ReadAutoTickState()
        {
            return new EditorAutoTickState
            {
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                Enabled = AutoTickEnabled,
                IntervalMs = AutoTickIntervalMs,
                ForcedTickCount = AutoTickForcedCount,
                SignalTickAvailable = SignalTickMethod() != null,
                LastError = AutoTickLastError
            };
        }

        private static System.Reflection.MethodInfo SignalTickMethod()
        {
            return typeof(EditorApplication).GetMethod("SignalTick", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        }

        /// <summary>中央update尾部唯一调用；SignalTick故障立即关闭，避免每帧重复占用CPU。</summary>
        private static void PumpAutoTick()
        {
            if (!AutoTickEnabled || AutoTickSignal == null) return;
            if (AutoTickIntervalMs > 0 && AutoTickStopwatch.ElapsedMilliseconds < AutoTickIntervalMs) return;
            AutoTickStopwatch.Restart();
            try
            {
                AutoTickSignal();
                AutoTickForcedCount += 1L;
            }
            catch (Exception exception)
            {
                AutoTickEnabled = false;
                AutoTickStopwatch.Stop();
                AutoTickLastError = exception.GetType().Name;
                throw;
            }
        }

        private static void ResetAutoTick()
        {
            AutoTickEnabled = false;
            AutoTickIntervalMs = 16L;
            AutoTickForcedCount = 0L;
            AutoTickLastError = null;
            AutoTickSignal = null;
            AutoTickStopwatch.Reset();
        }

        public static void Tick()
        {
            // 所有跨帧 coordinator 由唯一 Bridge 主循环推进；单个坏 journal 不得中断其他 root 或 heartbeat。
            TickCoordinator("compilation-events", UnityCliCompilationEventBroker.Tick);
            TickCoordinator("editor.compile", UnityCliBridge.UnityCliCompileCoordinator.Tick);
            TickCoordinator("build.run", UnityCliBridge.UnityCliLiveBuildCoordinator.Tick);
            TickCoordinator("editor.refresh", UnityCliBridge.UnityCliLiveRefreshCoordinator.Tick);
            TickCoordinator("asset.refresh", UnityCliBridge.UnityCliAssetRefreshCoordinator.Tick);
            TickCoordinator("package.embed", UnityCliBridge.UnityCliPackageEmbedCoordinator.Tick);
            TickCoordinator("package.resolve", UnityCliBridge.UnityCliPackageResolveCoordinator.Tick);
            TickCoordinator("test.run", UnityCliBridge.UnityCliTestRunCoordinator.Tick);
            TickCoordinator("navmesh.bake", UnityCliBridge.UnityCliNavMeshBakeCoordinator.Tick);
            TickCoordinator("occlusion.bake", UnityCliBridge.UnityCliOcclusionBakeCoordinator.Tick);
            TickCoordinator("editor.autotick", PumpAutoTick);
            MainThreadContinuations.Drain();
            var now = DateTime.UtcNow;
            List<IPendingWait> completed = null;
            List<IPendingWait> snapshot;
            lock (PendingLock)
            {
                snapshot = new List<IPendingWait>(Pending);
            }
            foreach (var wait in snapshot)
            {
                if (!wait.Tick(now)) continue;
                completed ??= new List<IPendingWait>();
                completed.Add(wait);
            }
            if (completed != null)
            {
                lock (PendingLock)
                {
                    foreach (var wait in completed)
                    {
                        Pending.Remove(wait);
                    }
                }
            }
            MainThreadContinuations.Drain();
        }

        public static void CancelAll(Exception exception)
        {
            List<IPendingWait> snapshot;
            lock (PendingLock)
            {
                snapshot = new List<IPendingWait>(Pending);
                Pending.Clear();
            }
            foreach (var wait in snapshot)
            {
                wait.Cancel(exception);
            }
            ResetAutoTick();
        }

        internal static void QueueOnMainThread(Action continuation)
        {
            MainThreadContinuations.Enqueue(continuation);
        }

        /// <summary>隔离 durable coordinator 故障；同一错误只记录一次，避免每帧淹没 Editor 日志。</summary>
        private static void TickCoordinator(string owner, Action tick)
        {
            try
            {
                tick();
                CoordinatorFailures.Remove(owner);
            }
            catch (Exception exception)
            {
                var failure = exception.GetType().Name + ": " + exception.Message;
                string previous;
                if (CoordinatorFailures.TryGetValue(owner, out previous) && string.Equals(previous, failure, StringComparison.Ordinal)) return;
                CoordinatorFailures[owner] = failure;
                UnityEngine.Debug.LogError("UnityCli coordinator_tick_failed owner=" + owner + " error=" + failure);
            }
        }

        private static void Register(IPendingWait wait)
        {
            lock (PendingLock)
            {
                Pending.Add(wait);
            }
        }
    }

    public static partial class UnityCliBridge
    {
        private static IUnityCliWaiter CreateWaiter(string requestId, string owner)
        {
            return new UnityCliWaiter(requestId, owner);
        }

        internal static bool IsDomainReloading()
        {
            return domainReloading;
        }

        internal static bool IsBuildingPlayer()
        {
            return BuildPipeline.isBuildingPlayer;
        }

    }
}
#endif
