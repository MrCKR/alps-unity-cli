#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace UnityCli
{
    /// <summary>一次Bridge队列实例的全部可变状态；production与测试状态永不互换。</summary>
    internal sealed class BridgeQueueState
    {
        internal readonly object QueueLock = new object();
        internal readonly LinkedList<UnityCliBridge.BridgeWorkItem> WorkQueue = new LinkedList<UnityCliBridge.BridgeWorkItem>();
        internal readonly HashSet<UnityCliBridge.BridgeWorkItem> RunningWork = new HashSet<UnityCliBridge.BridgeWorkItem>();
        internal readonly HashSet<UnityCliBridge.BridgeWorkItem> PendingSettlementWork = new HashSet<UnityCliBridge.BridgeWorkItem>();
        internal readonly Dictionary<string, UnityCliBridge.BridgeWorkItem> ActiveRequestWork = new Dictionary<string, UnityCliBridge.BridgeWorkItem>(StringComparer.Ordinal);
        internal UnityCliAdmissionPool AdmissionPool;
        internal bool QueuePublicationOpen;
        internal string ListenerId;
        internal long ListenerGeneration;
        internal readonly BridgeQueueSinks Sinks;
        internal readonly BridgeQueueTestHooks TestHooks;
        internal readonly BridgeIsolatedExecutionState IsolatedExecution;
        private readonly Func<long> monotonicNowMs;
        private readonly Func<bool> persistPoolState;
        private readonly Func<UnityCliBridge.AdmissionOwnerIdentity, bool> persistCompletionIntent;
        private readonly Func<UnityCliBridge.AdmissionOwnerIdentity, bool> hasCompletionIntent;
        private readonly Func<UnityCliBridge.AdmissionOwnerIdentity, bool> clearCompletionIntent;

        internal BridgeQueueState(
            UnityCliAdmissionPool admissionPool,
            Func<long> monotonicNowMs,
            BridgeQueueSinks sinks,
            BridgeQueueTestHooks testHooks,
            Func<bool> persistPoolState,
            Func<UnityCliBridge.AdmissionOwnerIdentity, bool> persistCompletionIntent,
            Func<UnityCliBridge.AdmissionOwnerIdentity, bool> hasCompletionIntent,
            Func<UnityCliBridge.AdmissionOwnerIdentity, bool> clearCompletionIntent,
            BridgeIsolatedExecutionState isolatedExecution)
        {
            if (sinks == null || testHooks == null || persistPoolState == null || persistCompletionIntent == null
                || hasCompletionIntent == null || clearCompletionIntent == null)
                throw new ArgumentNullException("BridgeQueueState dependencies must be explicit.");
            AdmissionPool = admissionPool;
            this.monotonicNowMs = monotonicNowMs ?? DefaultMonotonicMilliseconds;
            Sinks = sinks;
            TestHooks = testHooks;
            this.persistPoolState = persistPoolState;
            this.persistCompletionIntent = persistCompletionIntent;
            this.hasCompletionIntent = hasCompletionIntent;
            this.clearCompletionIntent = clearCompletionIntent;
            IsolatedExecution = isolatedExecution;
            ListenerGeneration = 1;
        }

        internal long NowMs()
        {
            return monotonicNowMs();
        }

        internal bool PersistPool()
        {
            TestHooks.Hit("before-state-persist-pool");
            return persistPoolState();
        }

        internal bool ClearMarkers(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            TestHooks.Hit("before-state-clear-markers");
            return UnityCliBridge.ClearAdmissionOwnerMarkersForState(this, identity);
        }

        internal bool PersistIntent(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            TestHooks.Hit("before-state-persist-intent");
            return persistCompletionIntent(identity);
        }

        internal bool HasIntent(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            return hasCompletionIntent(identity);
        }

        internal bool ClearIntent(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            TestHooks.Hit("before-state-clear-intent");
            return clearCompletionIntent(identity);
        }

        internal Dictionary<string, object> LoadOperation(string scope)
        {
            TestHooks.Hit("before-operation-load:" + scope);
            return IsolatedExecution == null ? UnityCliActiveOperation.Load(scope) : IsolatedExecution.Load(scope);
        }

        internal void SaveOperation(string scope, Dictionary<string, object> value)
        {
            TestHooks.Hit("before-operation-save:" + scope);
            if (IsolatedExecution == null) UnityCliActiveOperation.Save(scope, value);
            else IsolatedExecution.Save(scope, value);
        }

        internal void ClearOperation(string scope)
        {
            TestHooks.Hit("before-operation-clear:" + scope);
            if (IsolatedExecution == null) UnityCliActiveOperation.Clear(scope);
            else IsolatedExecution.Clear(scope);
        }

        internal bool OperationExists(string scope)
        {
            return IsolatedExecution == null ? UnityCliActiveOperation.Exists(scope) : IsolatedExecution.Exists(scope);
        }

        internal bool TryGetTerminal(string scope, string requestId, string requestDigest, out object envelope)
        {
            return IsolatedExecution == null
                ? UnityCliActiveOperation.TryGetTerminal(scope, requestId, requestDigest, out envelope)
                : IsolatedExecution.TryGetTerminal(scope, requestId, requestDigest, out envelope);
        }

        internal object StoreTerminalFirst(string scope, string requestId, string requestDigest, object envelope)
        {
            return StoreTerminalFirst(scope, requestId, requestDigest, null, envelope);
        }

        internal object StoreTerminalFirst(string scope, string requestId, string requestDigest, string selectorDigest, object envelope)
        {
            TestHooks.Hit("before-terminal-store:" + scope);
            return IsolatedExecution == null
                ? UnityCliActiveOperation.StoreTerminalFirst(scope, requestId, requestDigest, selectorDigest, envelope)
                : IsolatedExecution.StoreTerminalFirst(scope, requestId, requestDigest, selectorDigest, envelope);
        }

        internal bool TryGetTerminalBySelector(string scope, string requestId, string selectorDigest, out object envelope)
        {
            return IsolatedExecution == null
                ? UnityCliActiveOperation.TryGetTerminalBySelector(scope, requestId, selectorDigest, out envelope)
                : IsolatedExecution.TryGetTerminalBySelector(scope, requestId, selectorDigest, out envelope);
        }

        internal bool TryAcquireLease(string requestId, string requestDigest, out bool newlyAcquired)
        {
            return IsolatedExecution == null
                ? UnityCliBridge.UnityCliSessionMutationLease.TryAcquire(requestId, requestDigest, out newlyAcquired)
                : IsolatedExecution.TryAcquireLease(requestId, requestDigest, out newlyAcquired);
        }

        internal void ReleaseLease(string requestId, string requestDigest)
        {
            if (IsolatedExecution == null) UnityCliBridge.UnityCliSessionMutationLease.Release(requestId, requestDigest);
            else IsolatedExecution.ReleaseLease(requestId, requestDigest);
        }

        internal bool TryIsLeaseOwned(string requestId, string requestDigest, out bool isOwned)
        {
            return IsolatedExecution == null
                ? UnityCliBridge.UnityCliSessionMutationLease.TryIsOwnedBy(requestId, requestDigest, out isOwned)
                : IsolatedExecution.TryIsLeaseOwned(requestId, requestDigest, out isOwned);
        }

        internal void AssertEmptyForTests()
        {
            lock (QueueLock)
            {
                if (WorkQueue.Count != 0 || RunningWork.Count != 0 || PendingSettlementWork.Count != 0 || ActiveRequestWork.Count != 0)
                    throw new InvalidOperationException("Local BridgeQueueState still owns work.");
            }
            if (IsolatedExecution != null) IsolatedExecution.AssertEmpty();
            TestHooks.AssertDrained();
        }

        private static long DefaultMonotonicMilliseconds()
        {
            return checked((long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000d / System.Diagnostics.Stopwatch.Frequency)));
        }
    }

    /// <summary>每个queue state显式持有日志与transaction输出；测试sink不触碰Library/UnityCli。</summary>
    internal sealed class BridgeQueueSinks
    {
        private readonly Action<string> log;
        private readonly Action<string, string, string, string> appendTransaction;
        internal bool Production { get; private set; }
        private int logCount;
        private int transactionCount;
        internal int LogCount { get { return Volatile.Read(ref logCount); } }
        internal int TransactionCount { get { return Volatile.Read(ref transactionCount); } }

        internal BridgeQueueSinks(Action<string> log, Action<string, string, string, string> appendTransaction, bool production)
        {
            if (log == null || appendTransaction == null) throw new ArgumentNullException("Bridge queue sinks must be explicit.");
            this.log = log;
            this.appendTransaction = appendTransaction;
            Production = production;
        }

        internal void WriteLog(string line)
        {
            log(line);
            Interlocked.Increment(ref logCount);
        }

        internal void AppendTransaction(string requestId, string command, string request, string response)
        {
            appendTransaction(requestId, command, request, response);
            Interlocked.Increment(ref transactionCount);
        }
    }

    /// <summary>测试边界按名称注册且只消费一次；未消费的故障点在TearDown硬失败。</summary>
    internal sealed class BridgeQueueTestHooks
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, Action> expected = new Dictionary<string, Action>(StringComparer.Ordinal);
        private readonly bool enabled;

        internal BridgeQueueTestHooks(bool enabled)
        {
            this.enabled = enabled;
        }

        internal void ExpectOnce(string name, Action action)
        {
            if (!enabled) throw new InvalidOperationException("Production queue state does not accept test hooks.");
            if (string.IsNullOrWhiteSpace(name) || action == null) throw new ArgumentException("One-shot test hook is invalid.");
            lock (sync)
            {
                if (expected.ContainsKey(name)) throw new InvalidOperationException("One-shot test hook is already registered: " + name);
                expected.Add(name, action);
            }
        }

        internal void Hit(string name)
        {
            Action action;
            lock (sync)
            {
                if (!expected.TryGetValue(name, out action)) return;
                expected.Remove(name);
            }
            action();
        }

        internal void AssertDrained()
        {
            lock (sync)
            {
                if (expected.Count != 0)
                    throw new InvalidOperationException("One-shot queue test hooks were not consumed: " + string.Join(",", expected.Keys));
            }
        }
    }

    /// <summary>显式测试state的内存journal；不读取或修改production固定文件与全局mutation lease。</summary>
    internal sealed class BridgeIsolatedExecutionState
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, Dictionary<string, object>> operations = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private readonly Dictionary<string, TerminalEntry> terminals = new Dictionary<string, TerminalEntry>(StringComparer.Ordinal);
        private string leaseRequestId;
        private string leaseRequestDigest;

        internal Dictionary<string, object> Load(string scope)
        {
            lock (sync)
            {
                Dictionary<string, object> value;
                return operations.TryGetValue(scope, out value) ? Clone(value) : null;
            }
        }

        internal void Save(string scope, Dictionary<string, object> value)
        {
            if (string.IsNullOrEmpty(scope) || value == null) throw new ArgumentException("Isolated operation state is invalid.");
            lock (sync) operations[scope] = Clone(value);
        }

        internal void Clear(string scope)
        {
            lock (sync) operations.Remove(scope);
        }

        internal bool Exists(string scope)
        {
            lock (sync) return operations.ContainsKey(scope);
        }

        internal bool TryGetTerminal(string scope, string requestId, string requestDigest, out object envelope)
        {
            lock (sync)
            {
                TerminalEntry value;
                if (!terminals.TryGetValue(TerminalKey(scope, requestId, requestDigest), out value))
                {
                    envelope = null;
                    return false;
                }
                envelope = CloneObject(value.Envelope);
                return true;
            }
        }

        internal bool TryGetTerminalBySelector(string scope, string requestId, string selectorDigest, out object envelope)
        {
            lock (sync)
            {
                var value = terminals.Values.FirstOrDefault(entry => string.Equals(entry.Scope, scope, StringComparison.Ordinal)
                    && string.Equals(entry.RequestId, requestId, StringComparison.Ordinal));
                if (value == null || !string.Equals(value.SelectorDigest, selectorDigest, StringComparison.Ordinal))
                {
                    envelope = null;
                    return false;
                }
                envelope = CloneObject(value.Envelope);
                return true;
            }
        }

        internal object StoreTerminalFirst(string scope, string requestId, string requestDigest, string selectorDigest, object envelope)
        {
            lock (sync)
            {
                var key = TerminalKey(scope, requestId, requestDigest);
                TerminalEntry current;
                if (!terminals.TryGetValue(key, out current))
                {
                    current = new TerminalEntry
                    {
                        Scope = scope,
                        RequestId = requestId,
                        SelectorDigest = selectorDigest,
                        Envelope = CloneObject(envelope)
                    };
                    terminals.Add(key, current);
                }
                else if (!string.Equals(current.SelectorDigest, selectorDigest, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Isolated terminal selector identity changed.");
                }
                return CloneObject(current.Envelope);
            }
        }

        internal bool TryAcquireLease(string requestId, string requestDigest, out bool newlyAcquired)
        {
            lock (sync)
            {
                newlyAcquired = false;
                if (leaseRequestId != null)
                    return string.Equals(leaseRequestId, requestId, StringComparison.Ordinal)
                        && string.Equals(leaseRequestDigest, requestDigest, StringComparison.Ordinal);
                leaseRequestId = requestId;
                leaseRequestDigest = requestDigest;
                newlyAcquired = true;
                return true;
            }
        }

        internal void ReleaseLease(string requestId, string requestDigest)
        {
            lock (sync)
            {
                if (!string.Equals(leaseRequestId, requestId, StringComparison.Ordinal)
                    || !string.Equals(leaseRequestDigest, requestDigest, StringComparison.Ordinal)) return;
                leaseRequestId = null;
                leaseRequestDigest = null;
            }
        }

        internal bool TryIsLeaseOwned(string requestId, string requestDigest, out bool isOwned)
        {
            lock (sync)
            {
                isOwned = string.Equals(leaseRequestId, requestId, StringComparison.Ordinal)
                    && string.Equals(leaseRequestDigest, requestDigest, StringComparison.Ordinal);
                return true;
            }
        }

        internal void AssertEmpty()
        {
            lock (sync)
            {
                if (operations.Count != 0 || leaseRequestId != null)
                    throw new InvalidOperationException("Isolated execution state still owns an operation or lease.");
            }
        }

        private sealed class TerminalEntry
        {
            internal string Scope;
            internal string RequestId;
            internal string SelectorDigest;
            internal object Envelope;
        }

        private static string TerminalKey(string scope, string requestId, string requestDigest)
        {
            return (scope ?? string.Empty) + "\0" + (requestId ?? string.Empty) + "\0" + (requestDigest ?? string.Empty);
        }

        private static Dictionary<string, object> Clone(Dictionary<string, object> value)
        {
            return (Dictionary<string, object>)CloneObject(value);
        }

        private static object CloneObject(object value)
        {
            return value == null ? null : UnityCliSerializer.Parse(UnityCliSerializer.Serialize(value));
        }
    }
}
#endif
