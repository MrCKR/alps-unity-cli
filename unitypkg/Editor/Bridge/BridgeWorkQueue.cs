#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static readonly LinkedList<BridgeWorkItem> WorkQueue = new LinkedList<BridgeWorkItem>();
        private static readonly HashSet<BridgeWorkItem> RunningWork = new HashSet<BridgeWorkItem>();
        private static readonly HashSet<BridgeWorkItem> PendingSettlementWork = new HashSet<BridgeWorkItem>();
        private static readonly Dictionary<string, BridgeWorkItem> ActiveRequestWork = new Dictionary<string, BridgeWorkItem>(StringComparer.Ordinal);

        /// <summary>关闭publication与摘除全部未启动WorkItem共享同一QueueLock线性化点。</summary>
        private static List<BridgeWorkItem> CloseQueuePublicationAndTakeQueuedWork()
        {
            lock (QueueLock)
            {
                queuePublicationOpen = false;
                var pending = new List<BridgeWorkItem>(WorkQueue);
                WorkQueue.Clear();
                return pending;
            }
        }

        private static void CancelQueuedWork(IEnumerable<BridgeWorkItem> pending)
        {
            foreach (var item in pending ?? Array.Empty<BridgeWorkItem>())
            {
                item.CancelBeforeStart();
                item.Response = UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    item.Request.RequestId,
                    "E_UNITY_DOMAIN_RELOAD",
                    "Unity reloaded before the queued request began any effect.",
                    nextActions: new[] { "Keep the same root requestId and immutable request bytes while the session supervisor rebinds." },
                    transient: true,
                    retryAfterMs: 1000));
                CompleteWorkItem(item);
            }
        }

        internal static void SweepQueuedWorkForTests()
        {
            CancelQueuedWork(CloseQueuePublicationAndTakeQueuedWork());
        }

        private static void ProcessQueuedWork()
        {
            // pulse只证明EditorApplication.update最近推进；具体请求仍由TryStart证明开始。
            Volatile.Write(ref mainThreadPulseTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            Interlocked.Increment(ref mainThreadPulseSequence);
            var listenerWasStarted = started;
            var isCompiling = EditorApplication.isCompiling;
            var isUpdating = EditorApplication.isUpdating;
            var isBuilding = BuildPipeline.isBuildingPlayer;
            var isPlayModeChanging = IsPlayModeChanging();
            var lifecycleChanged = cachedIsCompiling != isCompiling
                || cachedIsUpdating != isUpdating
                || cachedIsBuilding != isBuilding
                || cachedIsPlayModeChanging != isPlayModeChanging;
            cachedIsCompiling = isCompiling;
            cachedIsUpdating = isUpdating;
            cachedIsPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode;
            cachedIsPlaying = EditorApplication.isPlaying;
            cachedIsBuilding = isBuilding;
            cachedIsPlayModeChanging = isPlayModeChanging;
            AdvanceBridgeStartup();
            UnityCliSessionMutationLease.TryFinalizeOwnedPlayModeRoot();
            UnityCliWaitScheduler.Tick();
            RetryPendingWorkItemSettlements();

            // 新listener先保留至少一个主线程update的transitioning状态；后续生命周期变化即时发布，30秒刷新只修复漏事件。
            if (listenerWasStarted && started
                && (readyPublicationPending || lifecycleChanged || EditorApplication.timeSinceStartup >= nextRegistryRefreshAt))
            {
                readyPublicationPending = false;
                RegisterInstance("ready");
                nextRegistryRefreshAt = EditorApplication.timeSinceStartup + RegistryRefreshSeconds;
            }
            TickInstancePublicationRetry();
            var durableMutationBarrier = CurrentDurableMutationBarrier();
            List<BridgeWorkItem> selected;
            var queueDepth = 0;
            lock (QueueLock)
            {
                if (WorkQueue.Count == 0) return;
                var snapshotItems = new List<BridgeWorkItem>(Math.Min(WorkQueue.Count, UnityCliBridgeQueuePolicy.MaxNodesExaminedPerTick));
                var cursor = WorkQueue.First;
                while (cursor != null && snapshotItems.Count < UnityCliBridgeQueuePolicy.MaxNodesExaminedPerTick)
                {
                    snapshotItems.Add(cursor.Value);
                    cursor = cursor.Next;
                }
                var selection = UnityCliBridgeQueuePolicy.SelectStarts(
                    snapshotItems.Select(item => item.PolicyNode).ToArray(),
                    RunningWork.Count(item => item.WorkClass == UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead),
                    RunningWork.Any(item => item.WorkClass == UnityCliBridgeQueuePolicy.WorkClass.Mutation),
                    durableMutationBarrier);
                selected = new List<BridgeWorkItem>(selection.Starts.Count);
                foreach (var decision in selection.Starts)
                {
                    var item = snapshotItems[decision.QueueIndex];
                    if (item.QueueNode == null || item.QueueNode.List != WorkQueue) continue;
                    WorkQueue.Remove(item.QueueNode);
                    item.QueueNode = null;
                    selected.Add(item);
                }
                queueDepth = WorkQueue.Count;
            }

            foreach (var item in selected)
            {
                WriteLog("bridge_request_dequeued requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " queueDepth=" + queueDepth.ToString(CultureInfo.InvariantCulture));
                if (!item.TryStart())
                {
                    WriteLog("bridge_request_skipped_expired requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                    item.Response = item.Response ?? UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                        item.Request.RequestId,
                        "E_BRIDGE_REQUEST_EXPIRED",
                        "Bridge request expired before Unity started executing it.",
                        nextActions: new[] { "Retry after Unity is no longer busy." }));
                    CompleteWorkItem(item);
                    continue;
                }
                lock (QueueLock) RunningWork.Add(item);
                _ = ExecuteWorkItemAsync(item);
            }
        }

        private static async Task ExecuteWorkItemAsync(BridgeWorkItem item)
        {
            try
            {
                WriteLog("bridge_request_started requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                item.Response = await ExecuteCoordinatedRequest(item.Request, item.RawRequestBytes, item.TryAccept, item.Token);
                WriteLog("bridge_request_finished requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " ok=" + Bool(item.Response != null && item.Response.Contains("\"ok\":true")));
            }
            catch (BridgeCommandException exception)
            {
                item.Response = SerializeBridgeExceptionEnvelope(item.Request.RequestId, exception);
            }
            catch (OperationCanceledException)
            {
                item.Response = UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    item.Request.RequestId,
                    "E_EXECUTION_TIMEOUT",
                    "Bridge request execution was cancelled because the immutable deadline elapsed or the Unity session ended.",
                    nextActions: new[] { "Inspect editor.state and retry only after Unity is responsive." }));
            }
            catch (Exception exception)
            {
                item.Response = UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    item.Request.RequestId,
                    "E_BRIDGE_COMMAND_FAILED",
                    exception.Message,
                    nextActions: new[] { "Check Unity Console and UnityCliBridge.log." }));
            }
            finally
            {
                CompleteWorkItem(item);
            }
        }

        /// <summary>immutable输入在锁外准备；existing attach与首次consume/publication共用QueueLock→pool线性化边界。</summary>
        private static BridgeWorkItem EnqueueOrAttach(HttpListenerRequest httpRequest, UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes, string admissionListenerId, long admissionGeneration, out bool created, out int queueDepth)
        {
            var reservation = PrepareAdmissionForQueue(httpRequest, request, rawRequestBytes);
            if (reservation.HasAdmissionHeaders) EnsureAdmissionPool();
            return EnqueuePrepared(request, rawRequestBytes, admissionListenerId, admissionGeneration, reservation, out created, out queueDepth);
        }

        internal static BridgeWorkItem EnqueuePreparedForTests(UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes, AdmissionQueueReservation reservation, out bool created, out int queueDepth)
        {
            return EnqueuePrepared(request, rawRequestBytes, ListenerId, CurrentListenerGeneration(), reservation, out created, out queueDepth);
        }

        private static BridgeWorkItem EnqueuePrepared(UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes, string admissionListenerId, long admissionGeneration, AdmissionQueueReservation reservation, out bool created, out int queueDepth)
        {
            var digest = UnityCliRequestDigest.Compute(rawRequestBytes, request.InstanceId);
            var workClass = QueueRequestWorkClass(request);
            if (reservation == null) throw new ArgumentNullException("reservation");
            BridgeWorkItem item = null;
            try
            {
                lock (QueueLock)
                {
                    if (!queuePublicationOpen) throw new BridgeCommandException("E_UNITY_DOMAIN_RELOAD", "Bridge queue admission is closed for the current Unity lifecycle.", null, new[] { "Wait for Unity to finish reloading and retry." }, true, 1000);
                    HitQueueTestBoundary("before-existing-lookup");
                    BridgeWorkItem existing;
                    if (ActiveRequestWork.TryGetValue(request.RequestId, out existing))
                    {
                        if (!string.Equals(existing.RequestDigest, digest, StringComparison.Ordinal))
                            throw new BridgeCommandException("E_REQUEST_ID_REUSED", "requestId is already bound to different request bytes.", "Generate a new requestId before retrying this command.");
                        AuthorizeAdmissionForQueue(reservation, request);
                        if (!SameAdmissionOwner(existing.AdmissionOwner, reservation.Owner))
                            throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "requestId is already attached to another admission root.");
                        MarkAdmissionQueuePublished(reservation);
                        created = false;
                        queueDepth = WorkQueue.Count;
                        return existing;
                    }
                    AuthorizeAdmissionForQueue(reservation, request);
                    var bypass = reservation.IsBypass && reservation.Owner == null;
                    UnityCliBridgeQueuePolicy.BypassAdmissionDecision bypassDecision = null;
                    if (bypass)
                    {
                        var identities = ActiveRequestWork.Values.Where(active => active.AdmissionBypass)
                            .Select(active => new UnityCliBridgeQueuePolicy.BypassIdentity(active.Request.RequestId, active.RequestDigest)).ToArray();
                        bypassDecision = UnityCliBridgeQueuePolicy.DecideBypassAdmission(identities, new UnityCliBridgeQueuePolicy.BypassIdentity(request.RequestId, digest));
                        if (bypassDecision.Outcome == UnityCliBridgeQueuePolicy.BypassAdmissionOutcome.RejectCapacity)
                            throw new BridgeCommandException("E_UNITY_BUSY", "Admission-bypass Bridge capacity is full.", "Wait for an active status or package-control request to complete before retrying.");
                        if (bypassDecision.Outcome == UnityCliBridgeQueuePolicy.BypassAdmissionOutcome.RejectIdentityMismatch)
                            throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Admission-bypass requestId identity is inconsistent.");
                        if (bypassDecision.Outcome == UnityCliBridgeQueuePolicy.BypassAdmissionOutcome.Attach)
                            throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Admission-bypass attach target disappeared before publication.");
                    }

                    item = new BridgeWorkItem(request, rawRequestBytes, digest, admissionListenerId, admissionGeneration, reservation.Owner, workClass, bypass);
                    HitQueueTestBoundary("before-queue-publication");
                    UnityCliQueuePublication.Publish(
                        () => ActiveRequestWork.Add(request.RequestId, item),
                        () => item.QueueNode = WorkQueue.AddLast(item),
                        () => { BridgeWorkItem current; if (ActiveRequestWork.TryGetValue(request.RequestId, out current) && object.ReferenceEquals(current, item)) ActiveRequestWork.Remove(request.RequestId); },
                        () => { if (item.QueueNode != null && item.QueueNode.List == WorkQueue) { WorkQueue.Remove(item.QueueNode); item.QueueNode = null; } },
                        () => MarkAdmissionQueuePublished(reservation));
                    created = true;
                    queueDepth = WorkQueue.Count;
                    return item;
                }
            }
            catch
            {
                if (item != null && !reservation.QueuePublished) item.DisposeUnpublished();
                var compensationRequired = reservation.NewlyConsumed && reservation.Owner != null && !reservation.QueuePublished;
                var compensated = CompensateAdmissionBeforeQueuePublication(reservation);
                if (!compensated)
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission compensation could not prove exact pre-queue cleanup.");
                if (compensationRequired)
                {
                    try
                    {
                        HitQueueTestBoundary("before-admission-compensation-persist");
                        PersistAdmissionPoolState();
                        HitQueueTestBoundary("before-admission-compensation-log");
                        WriteLog("admission_prequeue_compensated requestId=" + reservation.Owner.RequestId + " active=" + admissionPool.Capture().Active.ToString(CultureInfo.InvariantCulture));
                    }
                    catch (Exception exception)
                    {
                        var pool = admissionPool;
                        if (pool != null) pool.BlockRecovery();
                        WriteLog("admission_prequeue_compensation_failed requestId=" + reservation.Owner.RequestId + " " + exception.GetType().Name);
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission compensation could not be durably recorded after queue publication failed.");
                    }
                }
                throw;
            }
        }

        private static bool SameAdmissionOwner(AdmissionOwnerIdentity left, AdmissionOwnerIdentity right)
        {
            if (left == null || right == null) return left == null && right == null;
            return SameAdmissionImmutableIdentity(left, right);
        }

        /// <summary>durable transfer先在锁外准备；pool completion与map/running removal在同一QueueLock临界区收敛。</summary>
        internal static bool CompleteWorkItemForTests(BridgeWorkItem item, string response)
        {
            if (item == null) throw new ArgumentNullException("item");
            item.SetResponseForTests(response);
            return CompleteWorkItem(item);
        }

        /// <summary>同session主循环只重试已执行完、但尚无durable transfer证据的WorkItem settlement。</summary>
        private static void RetryPendingWorkItemSettlements()
        {
            BridgeWorkItem[] pending;
            lock (QueueLock) pending = PendingSettlementWork.ToArray();
            foreach (var item in pending) CompleteWorkItem(item);
        }

        internal static void RetryPendingWorkItemSettlementsForTests() { RetryPendingWorkItemSettlements(); }
        internal static int ActiveRequestWorkCountForTests { get { lock (QueueLock) return ActiveRequestWork.Count; } }
        internal static bool QueueLockHeldForTests { get { return Monitor.IsEntered(QueueLock); } }

        private static bool CompleteWorkItem(BridgeWorkItem item)
        {
            if (item == null || !item.TryBeginCompletion()) return false;
            var transfer = AdmissionTransferResult.TransferPending;
            Exception settlementException = null;
            try { transfer = SettleAdmissionAfterWorkItem(item.AdmissionOwner, item.Request.Command, item.Response); }
            catch (Exception exception) { settlementException = exception; }
            if (transfer == AdmissionTransferResult.TransferPending)
            {
                lock (QueueLock)
                {
                    RunningWork.Remove(item);
                    PendingSettlementWork.Add(item);
                }
                item.AllowCompletionRetry();
                WriteLog("admission_settlement_deferred requestId=" + item.Request.RequestId + " reason=" + (settlementException == null ? "recovery-marker-pending" : settlementException.GetType().Name));
                return false;
            }

            var completedAdmission = false;
            Exception boundaryException = null;
            Exception memoryException = null;
            lock (QueueLock)
            {
                try
                {
                    try { HitQueueTestBoundary("before-completion-memory"); }
                    catch (Exception exception) { boundaryException = exception; }
                    var pool = admissionPool;
                    UnityCliAdmissionPool.ConsumedIdentity current;
                    if (pool != null && item.AdmissionOwner != null && pool.TryGetConsumedIdentity(item.AdmissionOwner.RequestId, out current))
                    {
                        var frozen = ToAdmissionOwner(current, item.AdmissionOwner.RequestDigest);
                        var canComplete = frozen.OwnerState == UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup
                            || transfer == AdmissionTransferResult.NotAccepted && frozen.OwnerState == UnityCliAdmissionPool.AdmissionOwnerState.WorkItem;
                        if (canComplete && SameAdmissionOwner(frozen, item.AdmissionOwner))
                            completedAdmission = pool.CompleteTrusted(ToPoolIdentity(frozen)) == UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted;
                    }
                }
                catch (Exception exception)
                {
                    memoryException = exception;
                    var pool = admissionPool;
                    if (pool != null) pool.BlockRecovery();
                }

                RunningWork.Remove(item);
                if (memoryException == null)
                {
                    PendingSettlementWork.Remove(item);
                    BridgeWorkItem currentItem;
                    if (ActiveRequestWork.TryGetValue(item.Request.RequestId, out currentItem) && object.ReferenceEquals(currentItem, item))
                        ActiveRequestWork.Remove(item.Request.RequestId);
                }
                else PendingSettlementWork.Add(item);
            }

            if (boundaryException != null)
            {
                try { HitQueueTestBoundary("before-queue-boundary-fault-log"); } catch { }
                WriteLog("queue_test_boundary_failed " + boundaryException.GetType().Name);
            }
            if (memoryException != null)
            {
                try { HitQueueTestBoundary("before-admission-memory-fault-log"); } catch { }
                WriteLog("admission_memory_settlement_failed requestId=" + item.Request.RequestId + " " + memoryException.GetType().Name);
                item.AllowCompletionRetry();
                return false;
            }
            if (completedAdmission)
            {
                try
                {
                    HitQueueTestBoundary("before-admission-persist");
                    PersistAdmissionPoolState();
                    ClearExactAdmissionOwnerMarkers(item.AdmissionOwner);
                }
                catch (Exception exception) { WriteLog("admission_settlement_failed requestId=" + item.Request.RequestId + " " + exception.GetType().Name); }
            }
            if (settlementException != null)
                WriteLog("admission_settlement_failed requestId=" + item.Request.RequestId + " " + settlementException.GetType().Name);
            item.Completed.Set();
            try
            {
                UnityCliTransactionLog.Append(item.Request.RequestId, item.Request.Command, Encoding.UTF8.GetString(item.RawRequestBytes), item.Response ?? UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    item.Request.RequestId,
                    "E_BRIDGE_COMMAND_FAILED",
                    "Bridge request completed without a response.")));
            }
            catch (Exception exception) { WriteLog("transaction_log_append_failed requestId=" + item.Request.RequestId + " " + exception.GetType().Name); }
            return true;
        }

        internal sealed class BridgeWorkItem
        {
            private readonly object StateLock = new object();
            private bool cancelled;
            private bool started;
            private bool accepted;
            private bool acceptanceCancelled;
            private bool completionStarted;
            private readonly UnityCliBridgeWorkItemState lifecycle = new UnityCliBridgeWorkItemState();
            private readonly CancellationTokenSource cancellation;

            public BridgeWorkItem(UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes, string requestDigest, string admissionListenerId, long admissionGeneration, AdmissionOwnerIdentity admissionOwner, UnityCliBridgeQueuePolicy.WorkClass workClass, bool admissionBypass)
            {
                Request = request;
                RawRequestBytes = rawRequestBytes;
                RequestDigest = requestDigest;
                AdmissionListenerId = admissionListenerId;
                AdmissionGeneration = admissionGeneration;
                AdmissionOwner = admissionOwner;
                WorkClass = workClass;
                AdmissionBypass = admissionBypass;
                PolicyNode = new UnityCliBridgeQueuePolicy.QueueNode(request.RequestId, request.Command, workClass);
                cancellation = CreateExecutionLifetime(request);
            }

            public UnityCli.Protocol.BridgeRequest Request { get; private set; }
            public byte[] RawRequestBytes { get; private set; }
            public string RequestDigest { get; private set; }
            public string AdmissionListenerId { get; private set; }
            public long AdmissionGeneration { get; private set; }
            public AdmissionOwnerIdentity AdmissionOwner { get; private set; }
            public UnityCliBridgeQueuePolicy.WorkClass WorkClass { get; private set; }
            public UnityCliBridgeQueuePolicy.QueueNode PolicyNode { get; private set; }
            public bool AdmissionBypass { get; private set; }
            public LinkedListNode<BridgeWorkItem> QueueNode { get; set; }
            public bool HasStarted { get { lock (StateLock) return started; } }
            public CancellationToken Token { get { return cancellation.Token; } }
            public ManualResetEventSlim Started { get; private set; } = new ManualResetEventSlim(false);
            public ManualResetEventSlim Completed { get; private set; } = new ManualResetEventSlim(false);
            private string response;
            public string Response
            {
                get { lock (StateLock) return response; }
                set { lock (StateLock) response = value; }
            }
            public bool TimedOut { get; private set; }

            internal string RequestDigestValue { get { return RequestDigest; } }
            internal void SetResponseForTests(string response) { Response = response; }

            internal bool TryBeginCompletion()
            {
                lock (StateLock)
                {
                    if (completionStarted) return false;
                    completionStarted = true;
                    return true;
                }
            }

            internal void AllowCompletionRetry()
            {
                lock (StateLock) completionStarted = false;
            }

            public bool TryStart()
            {
                lock (StateLock)
                {
                    // admission与主线程开始必须属于同一listener epoch；reload后的旧队列项不得执行。
                    int remainingMs = 0;
                    if (Request != null) Request.TryDeadlineRemainingMs(out remainingMs);
                    var outcome = lifecycle.TryStart(
                        string.Equals(AdmissionListenerId, ListenerId, StringComparison.Ordinal) && AdmissionGeneration == CurrentListenerGeneration(),
                        remainingMs);
                    if (outcome != UnityCliBridgeWorkItemState.StartOutcome.Started)
                    {
                        if (outcome == UnityCliBridgeWorkItemState.StartOutcome.DeadlineElapsed) cancelled = true;
                        if (outcome == UnityCliBridgeWorkItemState.StartOutcome.Cancelled) cancelled = true;
                        if (outcome == UnityCliBridgeWorkItemState.StartOutcome.ListenerChanged)
                        {
                            cancelled = true;
                            response = UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                                Request.RequestId,
                                "E_UNITY_DOMAIN_RELOAD",
                                "Unity listener generation changed before the queued request started.",
                                nextActions: new[] { "Keep the same root requestId and immutable request bytes while the session supervisor rebinds." },
                                transient: true,
                                retryAfterMs: 1000));
                        }
                        cancellation.Cancel();
                        return false;
                    }
                    cancellation.CancelAfter(TimeSpan.FromMilliseconds(remainingMs));
                    started = true;
                    Started.Set();
                    return true;
                }
            }

            public bool CancelBeforeStart()
            {
                lock (StateLock)
                {
                    if (started || !lifecycle.CancelBeforeStart())
                    {
                        return false;
                    }
                    cancelled = true;
                    cancellation.Cancel();
                    return true;
                }
            }

            /// <summary>StateLock 使接受与 HTTP 超时取消使用同一内存裁决顺序。</summary>
            public bool TryAccept()
            {
                lock (StateLock)
                {
                    if (acceptanceCancelled || !lifecycle.TryAccept()) return false;
                    accepted = true;
                    return true;
                }
            }

            /// <summary>HTTP timeout 仅能取消尚未被 coordinator 接受的 mutation。</summary>
            public bool CancelBeforeAcceptance()
            {
                lock (StateLock)
                {
                    if (accepted || !lifecycle.CancelBeforeAcceptance()) return false;
                    acceptanceCancelled = true;
                    cancellation.Cancel();
                    return true;
                }
            }

            public void Cancel()
            {
                lock (StateLock)
                {
                    TimedOut = true;
                }
            }

            public void DisposeUnpublished()
            {
                lock (StateLock)
                {
                    cancelled = true;
                    try { cancellation.Cancel(); } catch { }
                    Completed.Set();
                }
                cancellation.Dispose();
                Started.Dispose();
                Completed.Dispose();
            }

        }
    }
}
#endif
