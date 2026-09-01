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
            return ClearOwnerMarkers == null || ClearOwnerMarkers(identity);
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

        internal void AssertEmptyForTests()
        {
            lock (QueueLock)
            {
                if (WorkQueue.Count != 0 || RunningWork.Count != 0 || PendingSettlementWork.Count != 0 || ActiveRequestWork.Count != 0)
                    throw new InvalidOperationException("Local BridgeQueueState still owns work.");
            }
        }

        private static long DefaultMonotonicMilliseconds()
        {
            return checked((long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000d / System.Diagnostics.Stopwatch.Frequency)));
        }
    }
}
#endif
