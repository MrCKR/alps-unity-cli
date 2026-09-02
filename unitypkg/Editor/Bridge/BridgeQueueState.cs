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
        private readonly object SettlementLock = new object();
        internal readonly LinkedList<UnityCliBridge.BridgeWorkItem> WorkQueue = new LinkedList<UnityCliBridge.BridgeWorkItem>();
        internal readonly HashSet<UnityCliBridge.BridgeWorkItem> RunningWork = new HashSet<UnityCliBridge.BridgeWorkItem>();
        internal readonly HashSet<UnityCliBridge.BridgeWorkItem> PendingSettlementWork = new HashSet<UnityCliBridge.BridgeWorkItem>();
        internal readonly Dictionary<string, UnityCliBridge.BridgeWorkItem> ActiveRequestWork = new Dictionary<string, UnityCliBridge.BridgeWorkItem>(StringComparer.Ordinal);
        internal UnityCliAdmissionPool AdmissionPool;
        internal bool QueuePublicationOpen;
        internal string ListenerId;
        internal long ListenerGeneration;
        internal bool AdmissionPoolShutdownPending;
        internal readonly BridgeQueueSinks Sinks;
        internal readonly BridgeQueueTestHooks TestHooks;
        internal readonly BridgeIsolatedExecutionState IsolatedExecution;
        private readonly Func<long> monotonicNowMs;
        private readonly Func<bool> persistPoolState;
        private readonly Func<UnityCliBridge.AdmissionOwnerIdentity, bool> persistCompletionIntent;
        private readonly Func<UnityCliBridge.AdmissionOwnerIdentity, bool> hasCompletionIntent;
        private readonly Func<UnityCliBridge.AdmissionOwnerIdentity, bool> clearCompletionIntent;
        private readonly Dictionary<string, PendingCompletionIntent> pendingCompletionIntents = new Dictionary<string, PendingCompletionIntent>(StringComparer.Ordinal);
        private int ioCallbackCount;
        private int lastQueueNodesExamined;
        internal int IoCallbackCount { get { return Volatile.Read(ref ioCallbackCount); } }
        internal int LastQueueNodesExamined { get { return Volatile.Read(ref lastQueueNodesExamined); } }

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
            AssertIoOutsideQueueLock();
            TestHooks.Hit("before-state-persist-pool");
            Interlocked.Increment(ref ioCallbackCount);
            return persistPoolState();
        }

        internal bool ClearMarkers(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            AssertIoOutsideQueueLock();
            TestHooks.Hit("before-state-clear-markers");
            Interlocked.Increment(ref ioCallbackCount);
            return UnityCliBridge.ClearAdmissionOwnerMarkersForState(this, identity);
        }

        internal bool PersistIntent(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            AssertIoOutsideQueueLock();
            if (!CanTrackCompletionIntent(identity)) return false;
            TestHooks.Hit("before-state-persist-intent");
            Interlocked.Increment(ref ioCallbackCount);
            var persisted = persistCompletionIntent(identity);
            if (persisted) TrackCompletionIntent(identity);
            return persisted;
        }

        internal bool HasIntent(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            if (HasIntentInMemory(identity)) return true;
            AssertIoOutsideQueueLock();
            TestHooks.Hit("before-state-has-intent");
            Interlocked.Increment(ref ioCallbackCount);
            var exists = hasCompletionIntent(identity);
            if (exists) TrackCompletionIntent(identity);
            return exists;
        }

        internal bool HasIntentInMemory(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            if (identity == null) return false;
            lock (SettlementLock)
            {
                PendingCompletionIntent pending;
                return pendingCompletionIntents.TryGetValue(IntentKey(identity), out pending) && pending.IntentPersisted;
            }
        }

        internal bool HasCompensationContinuation(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            if (identity == null) return false;
            lock (SettlementLock)
            {
                PendingCompletionIntent pending;
                return pendingCompletionIntents.TryGetValue(IntentKey(identity), out pending) && pending.Compensation;
            }
        }

        internal bool ClearIntent(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            AssertIoOutsideQueueLock();
            TestHooks.Hit("before-state-clear-intent");
            Interlocked.Increment(ref ioCallbackCount);
            var cleared = clearCompletionIntent(identity);
            if (cleared)
            {
                lock (SettlementLock) pendingCompletionIntents.Remove(IntentKey(identity));
            }
            return cleared;
        }

        private bool CanTrackCompletionIntent(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            if (identity == null) return false;
            var key = IntentKey(identity);
            lock (SettlementLock) return pendingCompletionIntents.ContainsKey(key) || pendingCompletionIntents.Count < 256;
        }

        internal void TrackCompletionIntent(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            TrackIntent(identity, false, true);
        }

        /// <summary>publication失败后先登记内存continuation，再尝试首次durable intent写入。</summary>
        internal void TrackCompensationContinuation(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            TrackIntent(identity, true, false);
        }

        private void TrackIntent(UnityCliBridge.AdmissionOwnerIdentity identity, bool compensation, bool persisted)
        {
            if (identity == null) throw new ArgumentNullException("identity");
            var key = IntentKey(identity);
            lock (SettlementLock)
            {
                PendingCompletionIntent current;
                if (pendingCompletionIntents.TryGetValue(key, out current))
                {
                    if (compensation) current.Compensation = true;
                    if (persisted) current.IntentPersisted = true;
                    return;
                }
                if (pendingCompletionIntents.Count >= 256) throw new InvalidOperationException("Admission completion intent journal reached its fixed bound.");
                pendingCompletionIntents.Add(key, new PendingCompletionIntent
                {
                    Owner = CloneIdentity(identity), Compensation = compensation, IntentPersisted = persisted
                });
            }
        }

        internal PendingCompletionIntent[] DueCompletionIntents(long nowMs, int limit)
        {
            lock (SettlementLock)
            {
                return pendingCompletionIntents.Values.Where(value => value.NextRetryAtMs <= nowMs)
                    .OrderBy(value => value.Owner.RequestId, StringComparer.Ordinal).Take(Math.Max(0, limit)).ToArray();
            }
        }

        internal void DeferCompletionIntent(PendingCompletionIntent intent, long nowMs)
        {
            if (intent == null) return;
            lock (SettlementLock)
            {
                PendingCompletionIntent current;
                if (!pendingCompletionIntents.TryGetValue(IntentKey(intent.Owner), out current) || !object.ReferenceEquals(current, intent)) return;
                current.NextRetryAtMs = checked(nowMs + 100L);
            }
        }

        internal void MarkCompletionIntentFailureLogged(PendingCompletionIntent intent)
        {
            if (intent == null) return;
            lock (SettlementLock) intent.FailureLogged = true;
        }

        internal bool DeferTrackedCompletionIntent(UnityCliBridge.AdmissionOwnerIdentity identity, long nowMs)
        {
            if (identity == null) return false;
            lock (SettlementLock)
            {
                PendingCompletionIntent pending;
                if (!pendingCompletionIntents.TryGetValue(IntentKey(identity), out pending)) return false;
                pending.NextRetryAtMs = checked(nowMs + 100L);
                if (pending.FailureLogged) return false;
                pending.FailureLogged = true;
                return true;
            }
        }

        internal int PendingCompletionIntentCount
        {
            get { lock (SettlementLock) return pendingCompletionIntents.Count; }
        }

        internal Dictionary<string, object> LoadOperation(string scope)
        {
            AssertIoOutsideQueueLock();
            TestHooks.Hit("before-operation-load:" + scope);
            Interlocked.Increment(ref ioCallbackCount);
            return IsolatedExecution == null ? UnityCliActiveOperation.Load(scope) : IsolatedExecution.Load(scope);
        }

        internal void SaveOperation(string scope, Dictionary<string, object> value)
        {
            AssertIoOutsideQueueLock();
            TestHooks.Hit("before-operation-save:" + scope);
            Interlocked.Increment(ref ioCallbackCount);
            if (IsolatedExecution == null) UnityCliActiveOperation.Save(scope, value);
            else IsolatedExecution.Save(scope, value);
        }

        internal void ClearOperation(string scope)
        {
            AssertIoOutsideQueueLock();
            TestHooks.Hit("before-operation-clear:" + scope);
            Interlocked.Increment(ref ioCallbackCount);
            if (IsolatedExecution == null) UnityCliActiveOperation.Clear(scope);
            else IsolatedExecution.Clear(scope);
        }

        internal bool OperationExists(string scope)
        {
            AssertIoOutsideQueueLock();
            Interlocked.Increment(ref ioCallbackCount);
            return IsolatedExecution == null ? UnityCliActiveOperation.Exists(scope) : IsolatedExecution.Exists(scope);
        }

        internal bool TryGetTerminal(string scope, string requestId, string requestDigest, out object envelope)
        {
            AssertIoOutsideQueueLock();
            Interlocked.Increment(ref ioCallbackCount);
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
            AssertIoOutsideQueueLock();
            TestHooks.Hit("before-terminal-store:" + scope);
            Interlocked.Increment(ref ioCallbackCount);
            return IsolatedExecution == null
                ? UnityCliActiveOperation.StoreTerminalFirst(scope, requestId, requestDigest, selectorDigest, envelope)
                : IsolatedExecution.StoreTerminalFirst(scope, requestId, requestDigest, selectorDigest, envelope);
        }

        internal bool TryGetTerminalBySelector(string scope, string requestId, string selectorDigest, out object envelope)
        {
            AssertIoOutsideQueueLock();
            Interlocked.Increment(ref ioCallbackCount);
            return IsolatedExecution == null
                ? UnityCliActiveOperation.TryGetTerminalBySelector(scope, requestId, selectorDigest, out envelope)
                : IsolatedExecution.TryGetTerminalBySelector(scope, requestId, selectorDigest, out envelope);
        }

        internal bool TryAcquireLease(string requestId, string requestDigest, out bool newlyAcquired)
        {
            AssertIoOutsideQueueLock();
            Interlocked.Increment(ref ioCallbackCount);
            return IsolatedExecution == null
                ? UnityCliBridge.UnityCliSessionMutationLease.TryAcquire(requestId, requestDigest, out newlyAcquired)
                : IsolatedExecution.TryAcquireLease(requestId, requestDigest, out newlyAcquired);
        }

        internal void ReleaseLease(string requestId, string requestDigest)
        {
            AssertIoOutsideQueueLock();
            Interlocked.Increment(ref ioCallbackCount);
            if (IsolatedExecution == null) UnityCliBridge.UnityCliSessionMutationLease.Release(requestId, requestDigest);
            else IsolatedExecution.ReleaseLease(requestId, requestDigest);
        }

        internal bool TryIsLeaseOwned(string requestId, string requestDigest, out bool isOwned)
        {
            AssertIoOutsideQueueLock();
            Interlocked.Increment(ref ioCallbackCount);
            return IsolatedExecution == null
                ? UnityCliBridge.UnityCliSessionMutationLease.TryIsOwnedBy(requestId, requestDigest, out isOwned)
                : IsolatedExecution.TryIsLeaseOwned(requestId, requestDigest, out isOwned);
        }

        internal bool TryGetCurrentLeaseOwner(out string requestId, out string requestDigest)
        {
            AssertIoOutsideQueueLock();
            Interlocked.Increment(ref ioCallbackCount);
            return IsolatedExecution == null
                ? UnityCliBridge.UnityCliSessionMutationLease.TryGetCurrentOwner(out requestId, out requestDigest)
                : IsolatedExecution.TryGetCurrentOwner(out requestId, out requestDigest);
        }

        internal void RecordQueueNodesExamined(int count)
        {
            if (count < 0 || count > UnityCliBridgeQueuePolicy.MaxNodesExaminedPerTick)
                throw new InvalidOperationException("Bridge queue scan exceeded its fixed production bound.");
            Volatile.Write(ref lastQueueNodesExamined, count);
        }

        internal void AssertEmptyForTests()
        {
            lock (QueueLock)
            {
                if (WorkQueue.Count != 0 || RunningWork.Count != 0 || PendingSettlementWork.Count != 0 || ActiveRequestWork.Count != 0)
                    throw new InvalidOperationException("Local BridgeQueueState still owns work.");
            }
            if (PendingCompletionIntentCount != 0)
                throw new InvalidOperationException("Local BridgeQueueState still owns admission completion intents.");
            if (IsolatedExecution != null) IsolatedExecution.AssertEmpty();
            TestHooks.AssertDrained();
        }

        private void AssertIoOutsideQueueLock()
        {
            if (Monitor.IsEntered(QueueLock)) throw new InvalidOperationException("Bridge queue state I/O is forbidden while QueueLock is held.");
        }

        private static string IntentKey(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            return identity.RequestId + "\0" + identity.RequestDigest + "\0" + (identity.TopRequestDigest ?? string.Empty)
                + "\0" + identity.RootFingerprint + "\0" + identity.Token + "\0" + identity.SessionId
                + "\0" + identity.ListenerId + "\0" + identity.ListenerGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\0" + identity.ExecutionDeadlineMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\0" + ((int)identity.OwnerState).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static UnityCliBridge.AdmissionOwnerIdentity CloneIdentity(UnityCliBridge.AdmissionOwnerIdentity identity)
        {
            return new UnityCliBridge.AdmissionOwnerIdentity
            {
                RequestId = identity.RequestId, RequestDigest = identity.RequestDigest, TopRequestDigest = identity.TopRequestDigest,
                RootFingerprint = identity.RootFingerprint, Token = identity.Token, SessionId = identity.SessionId,
                ListenerId = identity.ListenerId, ListenerGeneration = identity.ListenerGeneration,
                ExecutionDeadlineMs = identity.ExecutionDeadlineMs, EditorSessionId = identity.EditorSessionId, OwnerState = identity.OwnerState
            };
        }

        internal sealed class PendingCompletionIntent
        {
            internal UnityCliBridge.AdmissionOwnerIdentity Owner;
            internal long NextRetryAtMs;
            internal bool FailureLogged;
            internal bool Compensation;
            internal bool IntentPersisted;
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

        internal bool TryGetCurrentOwner(out string requestId, out string requestDigest)
        {
            lock (sync)
            {
                requestId = leaseRequestId;
                requestDigest = leaseRequestDigest;
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
