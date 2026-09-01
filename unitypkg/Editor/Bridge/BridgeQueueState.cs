#if UNITY_EDITOR
using System;
using System.Collections.Generic;

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
        internal Action<string> Boundary;
        internal Func<long> MonotonicNowMs;
        internal Func<bool> PersistPoolState;
        internal Func<UnityCliBridge.AdmissionOwnerIdentity, bool> ClearOwnerMarkers;
        internal Func<UnityCliBridge.AdmissionOwnerIdentity, bool> PersistCompletionIntent;
        internal Func<UnityCliBridge.AdmissionOwnerIdentity, bool> HasCompletionIntent;
        internal Func<UnityCliBridge.AdmissionOwnerIdentity, bool> ClearCompletionIntent;
        internal BridgeIsolatedExecutionState IsolatedExecution;

        internal BridgeQueueState(UnityCliAdmissionPool admissionPool = null, Func<long> monotonicNowMs = null)
        {
            AdmissionPool = admissionPool;
            MonotonicNowMs = monotonicNowMs ?? DefaultMonotonicMilliseconds;
            ListenerGeneration = 1;
        }

        internal long NowMs()
        {
            return MonotonicNowMs();
        }

        internal bool PersistPool()
        {
            return PersistPoolState == null || PersistPoolState();
        }

        internal bool ClearMarkers(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            return ClearOwnerMarkers == null
                ? UnityCliBridge.ClearExactAdmissionOwnerMarkers(this, identity)
                : ClearOwnerMarkers(identity);
        }

        internal bool PersistIntent(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            return PersistCompletionIntent == null || PersistCompletionIntent(identity);
        }

        internal bool HasIntent(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            return HasCompletionIntent != null && HasCompletionIntent(identity);
        }

        internal bool ClearIntent(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            return ClearCompletionIntent == null || ClearCompletionIntent(identity);
        }

        internal Dictionary<string, object> LoadOperation(string scope)
        {
            return IsolatedExecution == null ? UnityCliActiveOperation.Load(scope) : IsolatedExecution.Load(scope);
        }

        internal void SaveOperation(string scope, Dictionary<string, object> value)
        {
            if (IsolatedExecution == null) UnityCliActiveOperation.Save(scope, value);
            else IsolatedExecution.Save(scope, value);
        }

        internal void ClearOperation(string scope)
        {
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
            return IsolatedExecution == null
                ? UnityCliActiveOperation.StoreTerminalFirst(scope, requestId, requestDigest, envelope)
                : IsolatedExecution.StoreTerminalFirst(scope, requestId, requestDigest, envelope);
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
        }

        private static long DefaultMonotonicMilliseconds()
        {
            return checked((long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000d / System.Diagnostics.Stopwatch.Frequency)));
        }
    }

    /// <summary>显式测试state的内存journal；不读取或修改production固定文件与全局mutation lease。</summary>
    internal sealed class BridgeIsolatedExecutionState
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, Dictionary<string, object>> operations = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> terminals = new Dictionary<string, object>(StringComparer.Ordinal);
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
                object value;
                if (!terminals.TryGetValue(TerminalKey(scope, requestId, requestDigest), out value))
                {
                    envelope = null;
                    return false;
                }
                envelope = CloneObject(value);
                return true;
            }
        }

        internal object StoreTerminalFirst(string scope, string requestId, string requestDigest, object envelope)
        {
            lock (sync)
            {
                var key = TerminalKey(scope, requestId, requestDigest);
                object current;
                if (!terminals.TryGetValue(key, out current))
                {
                    current = CloneObject(envelope);
                    terminals.Add(key, current);
                }
                return CloneObject(current);
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
