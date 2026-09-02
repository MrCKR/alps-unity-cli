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
        private static readonly BridgeQueueState ProductionQueueState = CreateProductionQueueState();

        private static BridgeQueueState CreateProductionQueueState()
        {
            return new BridgeQueueState(
                null,
                null,
                new BridgeQueueSinks(WriteLog, UnityCliTransactionLog.Append, true),
                new BridgeQueueTestHooks(false),
                PersistProductionAdmissionPoolState,
                identity => PersistProductionAdmissionCompletionIntent(identity, "admission-completion"),
                HasProductionAdmissionCompletionIntent,
                ClearProductionAdmissionCompletionIntent,
                null);
        }

        internal static BridgeQueueState CreateQueueStateForTests(UnityCliAdmissionPool pool, Func<long> monotonicNowMs = null)
        {
            if (pool == null) throw new ArgumentNullException("pool");
            var intents = new HashSet<string>(StringComparer.Ordinal);
            var state = new BridgeQueueState(
                pool,
                monotonicNowMs,
                new BridgeQueueSinks(_ => { }, (_, __, ___, ____) => { }, false),
                new BridgeQueueTestHooks(true),
                () => true,
                identity => intents.Add(CompletionIntentKey(identity)),
                identity => intents.Contains(CompletionIntentKey(identity)),
                identity => intents.Remove(CompletionIntentKey(identity)),
                new BridgeIsolatedExecutionState())
            {
                QueuePublicationOpen = true,
                ListenerId = "test-listener-" + Guid.NewGuid().ToString("N"),
                ListenerGeneration = 1
            };
            return state;
        }

        /// <summary>关闭publication与摘除全部未启动WorkItem共享同一状态锁线性化点。</summary>
        private static List<BridgeWorkItem> CloseQueuePublicationAndTakeQueuedWork()
        {
            return CloseQueuePublicationAndTakeQueuedWork(ProductionQueueState);
        }

        private static List<BridgeWorkItem> CloseQueuePublicationAndTakeQueuedWork(BridgeQueueState state)
        {
            lock (state.QueueLock)
            {
                state.QueuePublicationOpen = false;
                var pending = new List<BridgeWorkItem>(state.WorkQueue);
                state.WorkQueue.Clear();
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

        /// <summary>Editor退出时不再依赖后续update；只按captured pool完整身份隔离完成未启动WorkItem。</summary>
        private static void CancelQueuedWorkForShutdown(IEnumerable<BridgeWorkItem> pending)
        {
            foreach (var item in pending ?? Array.Empty<BridgeWorkItem>())
            {
                if (!item.CancelBeforeStart()) continue;
                item.Response = UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    item.Request.RequestId,
                    "E_UNITY_QUITTING",
                    "Unity quit before the queued request began any effect.",
                    nextActions: new[] { "Restart Unity and submit a new request." }));
                CompleteQueuedWorkForShutdown(item);
            }
        }

        private static bool CompleteQueuedWorkForShutdown(BridgeWorkItem item)
        {
            if (item == null || !item.TryBeginCompletion()) return false;
            var outcome = item.AdmissionOwner == null
                ? UnityCliAdmissionPool.CompletionOutcome.DefinitelyAbsent
                : item.CapturedAdmissionPool == null
                    ? UnityCliAdmissionPool.CompletionOutcome.IdentityMismatchOrBlocked
                    : item.CapturedAdmissionPool.CompleteQueuedForShutdown(ToPoolIdentity(item.AdmissionOwner));
            if (outcome != UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted
                && outcome != UnityCliAdmissionPool.CompletionOutcome.DefinitelyAbsent)
            {
                item.AllowCompletionRetry();
                return false;
            }
            var state = item.QueueState;
            lock (state.QueueLock)
            {
                state.RunningWork.Remove(item);
                state.PendingSettlementWork.Remove(item);
                RemoveActiveWorkItem(state, item);
            }
            CompleteAndLogWorkItem(item);
            return true;
        }

        internal static void SweepQueuedWorkForTests(BridgeQueueState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            CancelQueuedWork(CloseQueuePublicationAndTakeQueuedWork(state));
        }

        internal static void ShutdownQueuedWorkForTests(BridgeQueueState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            CancelQueuedWorkForShutdown(CloseQueuePublicationAndTakeQueuedWork(state));
        }

        private static void ProcessQueuedWork()
        {
            var state = ProductionQueueState;
            state.ListenerId = ListenerId;
            state.ListenerGeneration = Math.Max(1, CurrentListenerGeneration());
            state.AdmissionPool = admissionPool;
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
            RetryDirectMutationTerminalCleanup();
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
            int queueDepth;
            var selected = SelectQueuedStarts(state, CurrentDurableMutationBarrier(state), true, out queueDepth);

            foreach (var item in selected)
            {
                state.Sinks.WriteLog("bridge_request_dequeued requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " queueDepth=" + queueDepth.ToString(CultureInfo.InvariantCulture));
                if (!item.TryStart())
                {
                    state.Sinks.WriteLog("bridge_request_skipped_expired requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                    item.Response = item.Response ?? UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                        item.Request.RequestId,
                        "E_EXECUTION_TIMEOUT",
                        "Bridge request deadline elapsed before Unity started executing it.",
                        nextActions: new[] { "Retry after Unity is no longer busy." }));
                    CompleteWorkItem(item);
                    continue;
                }
                lock (state.QueueLock) state.RunningWork.Add(item);
                _ = ExecuteWorkItemAsync(item);
            }
        }

        /// <summary>实际linked-list扫描、锁外control proof与锁内identity复核共用唯一生产路径。</summary>
        private static List<BridgeWorkItem> SelectQueuedStarts(BridgeQueueState state, DurableMutationBarrierProof barrier, bool remove, out int queueDepth)
        {
            List<BridgeWorkItem> snapshotItems;
            int runningOrdinaryReads;
            bool mutationRunning;
            lock (state.QueueLock)
            {
                snapshotItems = new List<BridgeWorkItem>(Math.Min(state.WorkQueue.Count, UnityCliBridgeQueuePolicy.MaxNodesExaminedPerTick));
                var cursor = state.WorkQueue.First;
                while (cursor != null && snapshotItems.Count < UnityCliBridgeQueuePolicy.MaxNodesExaminedPerTick)
                {
                    snapshotItems.Add(cursor.Value);
                    cursor = cursor.Next;
                }
                runningOrdinaryReads = state.RunningWork.Count(item => item.WorkClass == UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead);
                mutationRunning = state.RunningWork.Any(item => item.WorkClass == UnityCliBridgeQueuePolicy.WorkClass.Mutation
                    || item.WorkClass == UnityCliBridgeQueuePolicy.WorkClass.Control);
                queueDepth = state.WorkQueue.Count;
            }
            state.RecordQueueNodesExamined(snapshotItems.Count);
            if (snapshotItems.Count == 0) return new List<BridgeWorkItem>();

            var controlRoots = LoadControlRoots(state, snapshotItems, barrier);
            var policyNodes = snapshotItems.Select(item => item.WorkClass == UnityCliBridgeQueuePolicy.WorkClass.Control
                ? item.PolicyNode.WithControlAuthorization(ControlRequestMatchesParent(item.Request, barrier, controlRoots))
                : item.PolicyNode).ToArray();
            var selection = UnityCliBridgeQueuePolicy.SelectStarts(policyNodes, runningOrdinaryReads, mutationRunning, barrier.State);
            var selected = new List<BridgeWorkItem>(selection.Starts.Count);
            lock (state.QueueLock)
            {
                foreach (var decision in selection.Starts)
                {
                    var item = snapshotItems[decision.QueueIndex];
                    if (item.QueueNode == null || item.QueueNode.List != state.WorkQueue) continue;
                    selected.Add(item);
                    if (!remove) continue;
                    state.WorkQueue.Remove(item.QueueNode);
                    item.QueueNode = null;
                }
                queueDepth = state.WorkQueue.Count;
            }
            return selected;
        }

        internal static string[] PreviewQueuedStartRequestIdsForTests(BridgeQueueState state)
        {
            if (state == null || state.IsolatedExecution == null) throw new ArgumentException("Queue preview test state must be isolated.", "state");
            int queueDepth;
            return SelectQueuedStarts(state, CurrentDurableMutationBarrier(state), false, out queueDepth)
                .Select(item => item.Request.RequestId).ToArray();
        }

        private static Dictionary<string, Dictionary<string, object>> LoadControlRoots(BridgeQueueState state, IEnumerable<BridgeWorkItem> items, DurableMutationBarrierProof barrier)
        {
            var roots = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            if (barrier == null || barrier.State != UnityCliBridgeQueuePolicy.DurableMutationBarrier.Held) return roots;
            bool leaseOwned;
            if (!state.TryIsLeaseOwned(barrier.RequestId, barrier.RequestDigest, out leaseOwned) || !leaseOwned) return roots;
            foreach (var scope in items.Where(item => item.WorkClass == UnityCliBridgeQueuePolicy.WorkClass.Control)
                .Select(item => ControlRootScope(item.Request.Command)).Where(scope => scope != null).Distinct(StringComparer.Ordinal))
            {
                try { roots[scope] = state.LoadOperation(scope); }
                catch { roots[scope] = null; }
            }
            return roots;
        }

        private static bool ControlRequestMatchesParent(UnityCli.Protocol.BridgeRequest request, DurableMutationBarrierProof barrier, IDictionary<string, Dictionary<string, object>> roots)
        {
            if (request == null || barrier == null || barrier.State != UnityCliBridgeQueuePolicy.DurableMutationBarrier.Held) return false;
            var scope = ControlRootScope(request.Command);
            Dictionary<string, object> root;
            if (scope == null || roots == null || !roots.TryGetValue(scope, out root) || root == null
                || !string.Equals(AdmissionText(root, "requestId"), barrier.RequestId, StringComparison.Ordinal)
                || !string.Equals(AdmissionText(root, "requestDigest"), barrier.RequestDigest, StringComparison.Ordinal)
                || !string.Equals(AdmissionText(root, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) return false;
            var input = request.Input as IDictionary<string, object>;
            if (scope == "package-resolve")
                return string.Equals(ControlInputText(input, "rootRequestId"), barrier.RequestId, StringComparison.Ordinal);
            if (scope == "test-run")
            {
                object rawOperation;
                var operation = root.TryGetValue("testOperation", out rawOperation) ? rawOperation as IDictionary<string, object> : null;
                return operation != null && Guid.TryParse(AdmissionText(operation, "runId"), out _)
                    && string.Equals(ControlInputText(input, "expectedEditorSessionId"), AdmissionText(root, "editorSessionId"), StringComparison.Ordinal);
            }
            return string.Equals(ControlInputText(input, "operationId"), AdmissionText(root, "operationId"), StringComparison.Ordinal);
        }

        private static string ControlRootScope(string command)
        {
            if (command == "internal.test.run.cancel.plan" || command == "internal.test.run.cancel") return "test-run";
            if (command == "internal.package.files.claim" || command == "internal.package.files.commit"
                || command == "internal.package.files.prove" || command == "internal.package.files.fail") return "package-resolve";
            if (command == "internal.navmesh.bake.cancel") return "navmesh-bake";
            if (command == "internal.occlusion.bake.cancel") return "occlusion-bake";
            return null;
        }

        private static string ControlInputText(IDictionary<string, object> input, string key)
        {
            object value;
            return input != null && input.TryGetValue(key, out value) ? value as string : null;
        }

        private static async Task ExecuteWorkItemAsync(BridgeWorkItem item)
        {
            try
            {
                item.QueueState.Sinks.WriteLog("bridge_request_started requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                item.Response = await ExecuteCoordinatedRequest(item.Request, item.RawRequestBytes, item.TryAccept, item.Token);
                item.QueueState.Sinks.WriteLog("bridge_request_finished requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " ok=" + Bool(item.Response != null && item.Response.Contains("\"ok\":true")));
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
            ProductionQueueState.AdmissionPool = admissionPool;
            return EnqueuePrepared(ProductionQueueState, request, rawRequestBytes, admissionListenerId, admissionGeneration, reservation, out created, out queueDepth);
        }

        internal static BridgeWorkItem EnqueuePreparedForTests(UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes, AdmissionQueueReservation reservation, BridgeQueueState state, out bool created, out int queueDepth)
        {
            if (state == null) throw new ArgumentNullException("state");
            return EnqueuePrepared(state, request, rawRequestBytes, state.ListenerId ?? ListenerId, state.ListenerGeneration, reservation, out created, out queueDepth);
        }

        private static BridgeWorkItem EnqueuePrepared(BridgeQueueState state, UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes, string admissionListenerId, long admissionGeneration, AdmissionQueueReservation reservation, out bool created, out int queueDepth)
        {
            var digest = UnityCliRequestDigest.Compute(rawRequestBytes, request.InstanceId);
            var workClass = QueueRequestWorkClass(request);
            if (state == null) throw new ArgumentNullException("state");
            if (reservation == null) throw new ArgumentNullException("reservation");
            BridgeWorkItem item = null;
            try
            {
                lock (state.QueueLock)
                {
                    if (!state.QueuePublicationOpen) throw new BridgeCommandException("E_UNITY_DOMAIN_RELOAD", "Bridge queue admission is closed for the current Unity lifecycle.", null, new[] { "Wait for Unity to finish reloading and retry." }, true, 1000);
                    HitQueueTestBoundary(state, "before-existing-lookup");
                    BridgeWorkItem existing;
                    if (state.ActiveRequestWork.TryGetValue(request.RequestId, out existing))
                    {
                        if (!string.Equals(existing.RequestDigest, digest, StringComparison.Ordinal))
                            throw new BridgeCommandException("E_REQUEST_ID_REUSED", "requestId is already bound to different request bytes.", "Generate a new requestId before retrying this command.");
                        if (!TryAuthorizeCompletionPendingAttach(state, reservation, existing))
                            AuthorizeAdmissionForQueue(state, reservation, request);
                        if (!SameAdmissionOwner(existing.AdmissionOwner, reservation.Owner))
                            throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "requestId is already attached to another admission root.");
                        MarkAdmissionQueuePublished(reservation);
                        created = false;
                        queueDepth = state.WorkQueue.Count;
                        return existing;
                    }
                    AuthorizeAdmissionForQueue(state, reservation, request);
                    var bypass = reservation.IsBypass && reservation.Owner == null;
                    UnityCliBridgeQueuePolicy.BypassAdmissionDecision bypassDecision = null;
                    if (bypass)
                    {
                        var identities = state.ActiveRequestWork.Values.Where(active => active.AdmissionBypass)
                            .Select(active => new UnityCliBridgeQueuePolicy.BypassIdentity(active.Request.RequestId, active.RequestDigest)).ToArray();
                        bypassDecision = UnityCliBridgeQueuePolicy.DecideBypassAdmission(identities, new UnityCliBridgeQueuePolicy.BypassIdentity(request.RequestId, digest));
                        if (bypassDecision.Outcome == UnityCliBridgeQueuePolicy.BypassAdmissionOutcome.RejectCapacity)
                            throw new BridgeCommandException("E_UNITY_BUSY", "Admission-bypass Bridge capacity is full.", "Wait for an active status or package-control request to complete before retrying.");
                        if (bypassDecision.Outcome == UnityCliBridgeQueuePolicy.BypassAdmissionOutcome.RejectIdentityMismatch)
                            throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Admission-bypass requestId identity is inconsistent.");
                        if (bypassDecision.Outcome == UnityCliBridgeQueuePolicy.BypassAdmissionOutcome.Attach)
                            throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Admission-bypass attach target disappeared before publication.");
                    }

                    item = new BridgeWorkItem(state, request, rawRequestBytes, digest, admissionListenerId, admissionGeneration, reservation.Owner, workClass, bypass);
                    HitQueueTestBoundary(state, "before-queue-publication");
                    UnityCliQueuePublication.Publish(
                        () => state.ActiveRequestWork.Add(request.RequestId, item),
                        () => item.QueueNode = state.WorkQueue.AddLast(item),
                        () => { BridgeWorkItem current; if (state.ActiveRequestWork.TryGetValue(request.RequestId, out current) && object.ReferenceEquals(current, item)) state.ActiveRequestWork.Remove(request.RequestId); },
                        () => { if (item.QueueNode != null && item.QueueNode.List == state.WorkQueue) { state.WorkQueue.Remove(item.QueueNode); item.QueueNode = null; } },
                        () => MarkAdmissionQueuePublished(reservation));
                    created = true;
                    queueDepth = state.WorkQueue.Count;
                    return item;
                }
            }
            catch
            {
                if (item != null && !reservation.QueuePublished) item.DisposeUnpublished();
                var compensationRequired = reservation.NewlyConsumed && reservation.Owner != null && !reservation.QueuePublished;
                if (compensationRequired && !PersistAdmissionCompletionIntent(state, reservation.Owner))
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission compensation intent could not be durably recorded before pre-queue cleanup.");
                var compensated = CompensateAdmissionBeforeQueuePublication(state, reservation);
                if (!compensated)
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission compensation could not prove exact pre-queue cleanup.");
                if (compensationRequired)
                {
                    try
                    {
                        HitQueueTestBoundary(state, "before-admission-compensation-persist");
                        if (!PersistAdmissionPoolState(state)) throw new IOException("Admission pool compensation persistence was not confirmed.");
                        if (!ClearAdmissionCompletionIntent(state, reservation.Owner)) throw new IOException("Admission pool compensation intent cleanup was not confirmed.");
                        HitQueueTestBoundary(state, "before-admission-compensation-log");
                        state.Sinks.WriteLog("admission_prequeue_compensated requestId=" + reservation.Owner.RequestId + " active=" + state.AdmissionPool.Capture().Active.ToString(CultureInfo.InvariantCulture));
                    }
                    catch (Exception exception)
                    {
                        state.Sinks.WriteLog("admission_prequeue_compensation_failed requestId=" + reservation.Owner.RequestId + " " + exception.GetType().Name);
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

        /// <summary>completion只作为production算法入口；WorkItem携带其所属state，测试不替换production state。</summary>
        internal static bool CompleteWorkItemForTests(BridgeWorkItem item, string response)
        {
            if (item == null) throw new ArgumentNullException("item");
            item.SetResponseForTests(response);
            return CompleteWorkItem(item);
        }

        /// <summary>按单调时钟和固定退避重试settlement；一个tick不超过准入容量。</summary>
        private static void RetryPendingWorkItemSettlements()
        {
            RetryPendingWorkItemSettlements(ProductionQueueState);
        }

        internal static void RetryPendingWorkItemSettlementsForTests(BridgeQueueState state)
        {
            RetryPendingWorkItemSettlements(state);
        }

        private static void RetryPendingWorkItemSettlements(BridgeQueueState state)
        {
            if (state == null) return;
            var now = state.NowMs();
            var limit = UnityCliBridgeQueuePolicy.MaxStartsPerTick;
            if (state.AdmissionPool != null)
            {
                var snapshot = state.AdmissionPool.Capture();
                limit = Math.Max(1, Math.Min(limit, snapshot.ConfiguredMaxActive));
            }
            BridgeWorkItem[] pending;
            lock (state.QueueLock)
            {
                pending = state.PendingSettlementWork.Where(item => item.NextSettlementRetryAtMs <= now)
                    .OrderBy(item => item.NextSettlementRetryAtMs).ThenBy(item => item.Request.RequestId, StringComparer.Ordinal)
                    .Take(limit).ToArray();
            }
            foreach (var item in pending) CompleteWorkItem(item);
            RetryPendingAdmissionCompletionIntents(state);
        }

        internal static int ActiveRequestWorkCountForTests(BridgeQueueState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            lock (state.QueueLock) return state.ActiveRequestWork.Count;
        }

        internal static bool QueueLockHeldForTests(BridgeQueueState state)
        {
            return state != null && Monitor.IsEntered(state.QueueLock);
        }

        private static bool CompleteWorkItem(BridgeWorkItem item)
        {
            if (item == null) return false;
            var state = item.QueueState;
            if (!item.TryBeginCompletion()) return false;
            var transfer = AdmissionTransferResult.TransferPending;
            Exception settlementException = null;
            Exception boundaryException = null;
            try
            {
                if (item.CompletionIntentReady)
                    transfer = AdmissionTransferResult.NotAccepted;
                else
                    transfer = SettleAdmissionAfterWorkItem(state, item.AdmissionOwner, item.Request.Command, item.Response);
            }
            catch (Exception exception) { settlementException = exception; }

            if (transfer == AdmissionTransferResult.TransferPending)
            {
                HoldPendingSettlement(state, item, settlementException == null ? "recovery-marker-pending" : settlementException.GetType().Name);
                return false;
            }

            // 已被Coordinator/Continuation/Recovery/TerminalCleanup durable接管的owner不再由WorkItem完成。
            if (item.AdmissionOwner == null || transfer == AdmissionTransferResult.Transferred)
            {
                lock (state.QueueLock)
                {
                    state.RunningWork.Remove(item);
                    state.PendingSettlementWork.Remove(item);
                    RemoveActiveWorkItem(state, item);
                }
                CompleteAndLogWorkItem(item);
                TryFinalizeAdmissionPoolShutdown(state);
                return true;
            }

            // 仍由WorkItem负责时，先在QueueLock外落completion intent，再锁内删除pool内存。
            UnityCliAdmissionPool.ConsumedIdentity current;
            AdmissionOwnerIdentity frozen;
            try
            {
                if (item.CompletionIntentReady) frozen = item.AdmissionOwner;
                else
                {
                    frozen = CaptureAdmissionOwner(state, item.AdmissionOwner.RequestId, item.AdmissionOwner.RequestDigest);
                    if (!SameAdmissionOwner(frozen, item.AdmissionOwner)
                        || frozen.OwnerState != UnityCliAdmissionPool.AdmissionOwnerState.WorkItem)
                    {
                        HoldPendingSettlement(state, item, "completion-owner-changed");
                        return false;
                    }
                    if (!PersistAdmissionCompletionIntent(state, frozen))
                    {
                        HoldPendingSettlement(state, item, "completion-intent-pending");
                        return false;
                    }
                    item.CompletionIntentReady = true;
                }
            }
            catch (Exception exception)
            {
                HoldPendingSettlement(state, item, exception.GetType().Name);
                return false;
            }

            UnityCliAdmissionPool.CompletionOutcome completionOutcome;
            try
            {
                lock (state.QueueLock)
                {
                    try { HitQueueTestBoundary(state, "before-completion-memory"); }
                    catch (Exception exception) { boundaryException = exception; }
                    if (boundaryException != null)
                    {
                        completionOutcome = UnityCliAdmissionPool.CompletionOutcome.IdentityMismatchOrBlocked;
                    }
                    else
                    {
                        var pool = state.AdmissionPool;
                        if (pool == null)
                            completionOutcome = UnityCliAdmissionPool.CompletionOutcome.IdentityMismatchOrBlocked;
                        else if (!pool.TryGetConsumedIdentity(item.AdmissionOwner.RequestId, out current))
                            completionOutcome = item.CompletionIntentReady
                                ? UnityCliAdmissionPool.CompletionOutcome.DefinitelyAbsent
                                : UnityCliAdmissionPool.CompletionOutcome.IdentityMismatchOrBlocked;
                        else
                        {
                            frozen = ToAdmissionOwner(current, item.AdmissionOwner.RequestDigest);
                            completionOutcome = SameAdmissionOwner(frozen, item.AdmissionOwner)
                                ? pool.CompleteTrusted(ToPoolIdentity(frozen))
                                : UnityCliAdmissionPool.CompletionOutcome.IdentityMismatchOrBlocked;
                        }
                    }
                    state.RunningWork.Remove(item);
                    state.PendingSettlementWork.Add(item);
                }
            }
            catch (Exception exception)
            {
                if (state.AdmissionPool != null) state.AdmissionPool.BlockRecovery();
                HoldPendingSettlement(state, item, exception.GetType().Name);
                return false;
            }

            if (boundaryException != null)
            {
                if (!item.SettlementFailureLogged)
                {
                    try { HitQueueTestBoundary(state, "before-queue-boundary-fault-log"); } catch { }
                    state.Sinks.WriteLog("queue_test_boundary_failed " + boundaryException.GetType().Name);
                }
                HoldPendingSettlement(state, item, boundaryException.GetType().Name);
                return false;
            }
            if (completionOutcome == UnityCliAdmissionPool.CompletionOutcome.IdentityMismatchOrBlocked)
            {
                HoldPendingSettlement(state, item, "completion-identity-blocked");
                return false;
            }

            try
            {
                HitQueueTestBoundary(state, "before-admission-persist");
                if (!state.PersistPool()) throw new IOException("Admission pool persistence was not confirmed.");
                if (!state.ClearMarkers(item.AdmissionOwner)) throw new IOException("Admission owner marker cleanup was not confirmed.");
                if (!ClearAdmissionCompletionIntent(state, item.AdmissionOwner)) throw new IOException("Admission completion intent cleanup was not confirmed.");
            }
            catch (Exception exception)
            {
                HoldPendingSettlement(state, item, exception.GetType().Name);
                return false;
            }

            lock (state.QueueLock)
            {
                state.PendingSettlementWork.Remove(item);
                RemoveActiveWorkItem(state, item);
            }
            CompleteAndLogWorkItem(item);
            TryFinalizeAdmissionPoolShutdown(state);
            return true;
        }

        private static void HoldPendingSettlement(BridgeQueueState state, BridgeWorkItem item, string reason)
        {
            lock (state.QueueLock)
            {
                state.RunningWork.Remove(item);
                state.PendingSettlementWork.Add(item);
            }
            item.ScheduleSettlementRetry(state.NowMs());
            item.AllowCompletionRetry();
            if (item.SettlementFailureLogged) return;
            item.SettlementFailureLogged = true;
            state.Sinks.WriteLog("admission_settlement_deferred requestId=" + item.Request.RequestId + " reason=" + reason);
        }

        private static void RemoveActiveWorkItem(BridgeQueueState state, BridgeWorkItem item)
        {
            BridgeWorkItem current;
            if (state.ActiveRequestWork.TryGetValue(item.Request.RequestId, out current) && object.ReferenceEquals(current, item))
                state.ActiveRequestWork.Remove(item.Request.RequestId);
        }

        private static string CompletionIntentKey(AdmissionOwnerIdentity identity)
        {
            return identity == null ? string.Empty : identity.RequestId + "\0" + identity.RequestDigest + "\0" + (identity.TopRequestDigest ?? string.Empty)
                + "\0" + identity.RootFingerprint + "\0" + identity.Token + "\0" + identity.SessionId
                + "\0" + identity.ListenerId + "\0" + identity.ListenerGeneration.ToString(CultureInfo.InvariantCulture)
                + "\0" + identity.ExecutionDeadlineMs.ToString(CultureInfo.InvariantCulture) + "\0" + ((int)identity.OwnerState).ToString(CultureInfo.InvariantCulture);
        }

        private static void CompleteAndLogWorkItem(BridgeWorkItem item)
        {
            item.Completed.Set();
            try
            {
                item.QueueState.Sinks.AppendTransaction(item.Request.RequestId, item.Request.Command, Encoding.UTF8.GetString(item.RawRequestBytes), item.Response ?? UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    item.Request.RequestId, "E_BRIDGE_COMMAND_FAILED", "Bridge request completed without a response.")));
            }
            catch (Exception exception) { item.QueueState.Sinks.WriteLog("transaction_log_append_failed requestId=" + item.Request.RequestId + " " + exception.GetType().Name); }
        }

        internal sealed class BridgeWorkItem
        {
            private readonly object StateLock = new object();
            private bool cancelled;
            private bool started;
            private bool completionStarted;
            private readonly UnityCliBridgeWorkItemState lifecycle = new UnityCliBridgeWorkItemState();
            private readonly CancellationTokenSource cancellation;

            public BridgeWorkItem(BridgeQueueState queueState, UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes, string requestDigest, string admissionListenerId, long admissionGeneration, AdmissionOwnerIdentity admissionOwner, UnityCliBridgeQueuePolicy.WorkClass workClass, bool admissionBypass)
            {
                QueueState = queueState;
                Request = request;
                RawRequestBytes = rawRequestBytes;
                RequestDigest = requestDigest;
                AdmissionListenerId = admissionListenerId;
                AdmissionGeneration = admissionGeneration;
                AdmissionOwner = admissionOwner;
                CapturedAdmissionPool = queueState == null ? null : queueState.AdmissionPool;
                WorkClass = workClass;
                AdmissionBypass = admissionBypass;
                PolicyNode = new UnityCliBridgeQueuePolicy.QueueNode(request.RequestId, request.Command, workClass);
                cancellation = CreateExecutionLifetime(request);
            }

            public BridgeQueueState QueueState { get; private set; }
            public UnityCli.Protocol.BridgeRequest Request { get; private set; }
            public byte[] RawRequestBytes { get; private set; }
            public string RequestDigest { get; private set; }
            public string AdmissionListenerId { get; private set; }
            public long AdmissionGeneration { get; private set; }
            public AdmissionOwnerIdentity AdmissionOwner { get; private set; }
            internal UnityCliAdmissionPool CapturedAdmissionPool { get; private set; }
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
            internal long NextSettlementRetryAtMs { get; private set; }
            internal bool SettlementFailureLogged { get; set; }
            internal bool CompletionIntentReady { get; set; }

            internal string RequestDigestValue { get { return RequestDigest; } }
            internal void SetResponseForTests(string response) { Response = response; }

            internal void ScheduleSettlementRetry(long nowMs)
            {
                NextSettlementRetryAtMs = checked(nowMs + 100L);
            }

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
                        QueueState != null && string.Equals(AdmissionListenerId, QueueState.ListenerId, StringComparison.Ordinal) && AdmissionGeneration == QueueState.ListenerGeneration,
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

            /// <summary>StateLock使HTTP接受与主线程启动使用同一内存裁决顺序。</summary>
            public bool TryAccept()
            {
                lock (StateLock) return lifecycle.TryAccept();
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
