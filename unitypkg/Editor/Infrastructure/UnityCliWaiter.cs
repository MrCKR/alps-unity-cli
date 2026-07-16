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
        UnityCliMainThreadAwaitable<bool> UntilEditorReady(TimeSpan timeout, CancellationToken ct = default);
        UnityCliMainThreadAwaitable<bool> UntilPlayModeState(PlayModeStateChange state, TimeSpan timeout, CancellationToken ct = default);
        UnityCliMainThreadAwaitable<T> ForCallback<T>(Action<Action<T>, Action<Exception>> start, TimeSpan timeout, CancellationToken ct = default);
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

        public UnityCliMainThreadAwaitable<bool> UntilEditorReady(TimeSpan timeout, CancellationToken ct = default)
        {
            return UnityCliWaitScheduler.UntilEditorReady(requestId, owner, timeout, ct);
        }

        public UnityCliMainThreadAwaitable<bool> UntilPlayModeState(PlayModeStateChange state, TimeSpan timeout, CancellationToken ct = default)
        {
            return UnityCliWaitScheduler.UntilPlayModeState(requestId, owner, state, timeout, ct);
        }

        public UnityCliMainThreadAwaitable<T> ForCallback<T>(Action<Action<T>, Action<Exception>> start, TimeSpan timeout, CancellationToken ct = default)
        {
            return UnityCliWaitScheduler.ForCallback(requestId, owner, start, timeout, ct);
        }
    }

    internal static class UnityCliWaitScheduler
    {
        private static readonly object PendingLock = new object();
        private static readonly List<IPendingWait> Pending = new List<IPendingWait>();

        private sealed class UnityCliMainThreadSynchronizationContext : SynchronizationContext
        {
            private readonly object queueLock = new object();
            private readonly Queue<(SendOrPostCallback callback, object state)> queue = new Queue<(SendOrPostCallback callback, object state)>();

            public override void Post(SendOrPostCallback d, object state)
            {
                lock (queueLock)
                {
                    queue.Enqueue((d, state));
                }
            }

            public void Drain()
            {
                while (true)
                {
                    (SendOrPostCallback callback, object state) work;
                    lock (queueLock)
                    {
                        if (queue.Count == 0) return;
                        work = queue.Dequeue();
                    }
                    work.callback(work.state);
                }
            }
        }

        private static readonly UnityCliMainThreadSynchronizationContext MainThreadSynchronizationContext = new UnityCliMainThreadSynchronizationContext();

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

            public void Cancel(Exception exception)
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

        private sealed class CallbackWait<T> : PendingWaitBase<T>
        {
            private readonly object callbackLock = new object();
            private readonly DateTime deadlineUtc;
            private bool callbackCompleted;
            private bool callbackFailed;
            private bool callbackTerminal;
            private T callbackResult;
            private Exception callbackException;

            public CallbackWait(string requestId, string owner, Action<Action<T>, Action<Exception>> start, TimeSpan timeout, CancellationToken cancellationToken)
                : base(requestId, owner, cancellationToken)
            {
                deadlineUtc = DateTime.UtcNow.Add(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
                start?.Invoke(OnComplete, OnFail);
            }

            protected override bool Advance(DateTime utcNow)
            {
                bool completed;
                bool failed;
                T result;
                Exception exception;
                lock (callbackLock)
                {
                    completed = callbackCompleted;
                    failed = callbackFailed;
                    result = callbackResult;
                    exception = callbackException;
                    if (!completed && !failed && utcNow >= deadlineUtc) callbackTerminal = true;
                }
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

            private void OnComplete(T result)
            {
                lock (callbackLock)
                {
                    if (callbackTerminal || IsCompleted) return;
                    callbackTerminal = true;
                    callbackResult = result;
                    callbackCompleted = true;
                }
            }

            private void OnFail(Exception exception)
            {
                lock (callbackLock)
                {
                    if (callbackTerminal || IsCompleted) return;
                    callbackTerminal = true;
                    callbackException = exception;
                    callbackFailed = true;
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

        public static UnityCliMainThreadAwaitable<bool> UntilEditorReady(string requestId, string owner, TimeSpan timeout, CancellationToken cancellationToken)
        {
            return Until(requestId, owner, () => !EditorApplication.isCompiling && !EditorApplication.isUpdating && !UnityCliBridge.IsPlayModeChanging() && !UnityCliBridge.IsBuildingPlayer() && !UnityCliBridge.IsDomainReloading(), timeout, cancellationToken);
        }

        public static UnityCliMainThreadAwaitable<bool> UntilPlayModeState(string requestId, string owner, PlayModeStateChange state, TimeSpan timeout, CancellationToken cancellationToken)
        {
            return Until(requestId, owner, () => MatchesPlayModeState(state), timeout, cancellationToken);
        }

        private static bool MatchesPlayModeState(PlayModeStateChange state)
        {
            var playing = EditorApplication.isPlaying;
            var changing = EditorApplication.isPlayingOrWillChangePlaymode;
            switch (state)
            {
                case PlayModeStateChange.EnteredPlayMode: return playing && !changing;
                case PlayModeStateChange.ExitingPlayMode: return playing && changing;
                case PlayModeStateChange.ExitingEditMode: return !playing && changing;
                case PlayModeStateChange.EnteredEditMode: return !playing && !changing;
                default: return false;
            }
        }

        public static UnityCliMainThreadAwaitable<T> ForCallback<T>(string requestId, string owner, Action<Action<T>, Action<Exception>> start, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var wait = new CallbackWait<T>(requestId, owner, start, timeout, cancellationToken);
            Register(wait);
            return new UnityCliMainThreadAwaitable<T>(wait);
        }

        public static void Tick()
        {
            EnsureMainThreadSynchronizationContext();
            MainThreadSynchronizationContext.Drain();
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
            MainThreadSynchronizationContext.Drain();
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
        }

        internal static void QueueOnMainThread(Action continuation)
        {
            if (continuation == null) return;
            MainThreadSynchronizationContext.Post(_ => continuation(), null);
        }

        public static void EnsureMainThreadSynchronizationContext()
        {
            if (!ReferenceEquals(SynchronizationContext.Current, MainThreadSynchronizationContext))
            {
                SynchronizationContext.SetSynchronizationContext(MainThreadSynchronizationContext);
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
