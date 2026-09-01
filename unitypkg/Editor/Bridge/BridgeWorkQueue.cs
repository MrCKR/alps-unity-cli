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
        private static readonly Dictionary<string, BridgeWorkItem> ActiveRequestWork = new Dictionary<string, BridgeWorkItem>(StringComparer.Ordinal);

        private static void CancelQueuedWork()
        {
            List<BridgeWorkItem> pending;
            lock (QueueLock)
            {
                pending = new List<BridgeWorkItem>(WorkQueue);
                WorkQueue.Clear();
            }
            foreach (var item in pending)
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

        /// <summary>先在QueueLock外完成authorize与文件读取，再在锁内只提交map和queue的publication。</summary>
        private static BridgeWorkItem EnqueueOrAttach(HttpListenerRequest httpRequest, UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes, string admissionListenerId, long admissionGeneration, out bool created, out int queueDepth)
        {
            var digest = UnityCliRequestDigest.Compute(rawRequestBytes, request.InstanceId);
            var mutation = QueueRequestIsMutation(request);
            var workClass = UnityCliBridgeQueuePolicy.Classify(request.Command, mutation);
            AdmissionQueueReservation reservation = null;
            BridgeWorkItem item = null;
            try
            {
                reservation = AuthorizeAdmissionForQueue(httpRequest, request, rawRequestBytes);
                lock (QueueLock)
                {
                    var bypass = AdmissionBypassCommand(request.Command) && reservation.Owner == null;
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
                    }
                    BridgeWorkItem existing;
                    if (ActiveRequestWork.TryGetValue(request.RequestId, out existing))
                    {
                        if (!string.Equals(existing.RequestDigest, digest, StringComparison.Ordinal))
                            throw new BridgeCommandException("E_REQUEST_ID_REUSED", "requestId is already bound to different request bytes.", "Generate a new requestId before retrying this command.");
                        if (!SameAdmissionOwner(existing.AdmissionOwner, reservation.Owner))
                            throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "requestId is already attached to another admission root.");
                        MarkAdmissionQueuePublished(reservation);
                        created = false;
                        queueDepth = WorkQueue.Count;
                        return existing;
                    }
                    if (bypass && bypassDecision.Outcome == UnityCliBridgeQueuePolicy.BypassAdmissionOutcome.Attach)
                        throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Admission-bypass attach target disappeared before publication.");

                    item = new BridgeWorkItem(request, rawRequestBytes, digest, admissionListenerId, admissionGeneration, reservation.Owner, workClass, bypass);
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
                if (!CompensateAdmissionBeforeQueuePublication(reservation))
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission compensation could not prove exact pre-queue cleanup.");
                throw;
            }
        }

        private static bool SameAdmissionOwner(AdmissionOwnerIdentity left, AdmissionOwnerIdentity right)
        {
            if (left == null || right == null) return left == null && right == null;
            return string.Equals(left.RequestId, right.RequestId, StringComparison.Ordinal)
                && string.Equals(left.RootFingerprint, right.RootFingerprint, StringComparison.Ordinal)
                && string.Equals(left.Token, right.Token, StringComparison.Ordinal)
                && string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal);
        }

        /// <summary>durable settlement在锁外执行；无论settlement异常与否都移除队列状态并发完成信号。</summary>
        private static void CompleteWorkItem(BridgeWorkItem item)
        {
            var settlementException = UnityCliQueueSettlement.Complete(
                () => SettleAdmissionAfterWorkItem(item.AdmissionOwner, item.Request.Command, item.Response),
                () =>
                {
                    lock (QueueLock)
                    {
                        RunningWork.Remove(item);
                        BridgeWorkItem current;
                        if (ActiveRequestWork.TryGetValue(item.Request.RequestId, out current) && object.ReferenceEquals(current, item))
                            ActiveRequestWork.Remove(item.Request.RequestId);
                    }
                },
                () => item.Completed.Set());
            if (settlementException != null)
                WriteLog("admission_settlement_failed requestId=" + item.Request.RequestId + " " + settlementException.GetType().Name);
            try
            {
                UnityCliTransactionLog.Append(item.Request.RequestId, item.Request.Command, Encoding.UTF8.GetString(item.RawRequestBytes), item.Response ?? UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    item.Request.RequestId,
                    "E_BRIDGE_COMMAND_FAILED",
                    "Bridge request completed without a response.")));
            }
            catch (Exception exception) { WriteLog("transaction_log_append_failed requestId=" + item.Request.RequestId + " " + exception.GetType().Name); }
        }

        private sealed class BridgeWorkItem
        {
            private readonly object StateLock = new object();
            private bool cancelled;
            private bool started;
            private bool accepted;
            private bool acceptanceCancelled;
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
                PolicyNode = new UnityCliBridgeQueuePolicy.QueueNode(request.RequestId, request.Command, workClass == UnityCliBridgeQueuePolicy.WorkClass.Mutation);
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
