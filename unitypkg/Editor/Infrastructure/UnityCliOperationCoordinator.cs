#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private sealed class CompletionEvidence
        {
            public string RequestId;
            public string ToolName;
            public string CompletionDriverId;
            public string ProofKind;
            public byte[] ProofBytes;
        }

        private static readonly object CompletionEvidenceLock = new object();
        private static readonly Dictionary<string, CompletionEvidence> CompletionEvidenceByRequest = new Dictionary<string, CompletionEvidence>(StringComparer.Ordinal);
        private static readonly HashSet<string> RegisteredCompletionDriverIds = new HashSet<string>(new[]
        {
            "atomic-typed-result-v1",
            "observed-terminal-readback-v1",
            "active-operation-terminal-readback-v1",
            "external-terminal-readback-v1",
            "official-method-return-v1"
        }, StringComparer.Ordinal);
        private const string DirectMutationRequestIdSessionKey = "UnityCli.DirectMutation.RequestId";
        private const string DirectMutationRequestDigestSessionKey = "UnityCli.DirectMutation.RequestDigest";
        private const string DirectMutationCleanupPendingSessionKey = "UnityCli.DirectMutation.CleanupPending";
        private const string DirectMutationEffectCommittedSessionKey = "UnityCli.DirectMutation.EffectCommitted";
        private const string DirectMutationTerminalScope = "direct-mutation";
        private const int MaxDirectMutationTerminalBytes = 1536 * 1024;

        /// <summary>
        /// live HTTP Tool 的唯一完成协调器。mutation 成功只能消费 Tool runner 显式登记的 typed evidence；
        /// 普通 handler envelope、transport success 与中间状态不能跨过 effect_proven barrier。
        /// </summary>
        private static async Task<string> ExecuteCoordinatedRequest(BridgeRequest request, byte[] rawRequestBytes, Func<bool> tryAcceptJournal, CancellationToken cancellationToken)
        {
            // durable status只读active journal或terminal cache，必须绕开通用direct completion路径。
            if (UnityCliDurableStatusCoordinator.IsCommand(request.Command))
                return await UnityCliDurableStatusCoordinator.Execute(request, cancellationToken);
            // 私有 compile start/status 必须绕开通用 completion 路径；start 的 accepted 不是公开 terminal。
            if (UnityCliCompileCoordinator.IsCommand(request.Command))
                return await UnityCliCompileCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            // coordinator-owned roots own their journal and must never enter the direct handler path.
            if (UnityCliLiveBuildCoordinator.IsCommand(request.Command))
                return await UnityCliLiveBuildCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            if (UnityCliLiveRefreshCoordinator.IsCommand(request.Command))
                return await UnityCliLiveRefreshCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            if (UnityCliAssetRefreshCoordinator.IsCommand(request.Command))
                return await UnityCliAssetRefreshCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            if (UnityCliPackageEmbedCoordinator.IsCommand(request.Command))
                return await UnityCliPackageEmbedCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            if (UnityCliPackageResolveCoordinator.IsCommand(request.Command))
                return await UnityCliPackageResolveCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            if (UnityCliTestRunCoordinator.IsCommand(request.Command))
                return await UnityCliTestRunCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            if (UnityCliNavMeshBakeCoordinator.IsCommand(request.Command))
                return await UnityCliNavMeshBakeCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            if (UnityCliOcclusionBakeCoordinator.IsCommand(request.Command))
                return await UnityCliOcclusionBakeCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            var contract = ResolveRequestCompletionContract(request);
            if (contract == null)
            {
                if (IsReadOnlyBridgeCommand(request.Command)) return await Execute(request, cancellationToken);
                return CompletionContractRequiredEnvelope(request);
            }
            if (!string.Equals(contract.DeliveryMode, "direct-terminal", StringComparison.Ordinal))
                return CompletionContractRequiredEnvelope(request);
            if (!IsRegisteredCompletionDriver(contract.ExecutionClass, contract.CompletionDriverId))
                return CompletionDriverRequiredEnvelope(request, contract);

            var mutation = RequestRequiresMutation(request, contract);
            if (!mutation)
            {
                var response = await Execute(request, cancellationToken);
                ValidateCoordinatedTerminal(response, request.RequestId);
                return response;
            }

            // 普通direct mutation没有durable recovery root，只能使用本次新取得的exact lease。
            // 已有同identity owner也可能属于并发副本，不能据此重复跨越effect barrier。
            var requestDigest = UnityCliRequestDigest.Compute(rawRequestBytes, request.InstanceId);
            try
            {
                object durableTerminal;
                if (UnityCliActiveOperation.TryGetTerminal(DirectMutationTerminalScope, request.RequestId, requestDigest, out durableTerminal))
                {
                    CleanupPublishedDirectMutation(request.RequestId, requestDigest);
                    return UnityCliSerializer.Serialize(durableTerminal);
                }
            }
            catch (BridgeCommandException exception) { return SerializeBridgeExceptionEnvelope(request.RequestId, exception); }
            var resumedDirect = TryResumeDirectMutation(request.RequestId, requestDigest);
            if (resumedDirect != null) return resumedDirect;
            bool newlyAcquiredLease;
            if (!UnityCliSessionMutationLease.TryAcquire(request.RequestId, requestDigest, out newlyAcquiredLease) || !newlyAcquiredLease)
                return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(
                    request.RequestId,
                    "E_UNITY_BUSY",
                    "Another mutation owns the selected Unity instance.",
                    new Dictionary<string, object> { { "owner", "session-mutation" } },
                    new[] { "Wait for the current mutation to reach its terminal result, then submit this request again." },
                    transient: true,
                    retryAfterMs: 250));
            BindDirectMutationLease(request.RequestId, requestDigest);

            try
            {
                // 接受后 HTTP 超时不再取消已入队的业务执行；普通 Tool 不创建请求级文件。
                if (tryAcceptJournal == null || !tryAcceptJournal())
                    throw new OperationCanceledException("HTTP timeout cancelled mutation before acceptance.");
            }
            catch (OperationCanceledException)
            {
                if (newlyAcquiredLease) ReleaseDirectMutationLease(request.RequestId, requestDigest);
                return ExecutionTimeoutEnvelope(request.RequestId, "The request was not accepted before the execution deadline elapsed.");
            }

            try { SaveDirectMutationAcceptedRoot(request, requestDigest, contract.ToolName); }
            catch (Exception exception)
            {
                ScheduleDirectMutationPreEffectCleanup(request.RequestId, requestDigest);
                return ExecutionTimeoutEnvelope(request.RequestId, "Direct mutation acceptance could not be persisted before effect: " + exception.GetType().Name + ".");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await Execute(request, cancellationToken);
                var envelope = ValidateCoordinatedTerminal(response, request.RequestId);
                object okValue;
                var ok = envelope.TryGetValue("ok", out okValue) && okValue is bool && (bool)okValue;
                if (!ok)
                {
                    var serialized = UnityCliSerializer.Serialize(envelope);
                    return DirectMutationEffectCommitted() ? PersistDirectMutationTerminal(request.RequestId, requestDigest, serialized) : serialized;
                }

                var evidence = TakeCompletionEvidence(request.RequestId, contract);
                if (evidence == null)
                {
                    var serialized = UnityCliSerializer.Serialize(CompletionProofInvalidEnvelope(request, contract, "No completion-driver evidence was registered."));
                    return DirectMutationEffectCommitted() ? PersistDirectMutationTerminal(request.RequestId, requestDigest, serialized) : serialized;
                }
                var finalized = await FinalizeResult(request, envelope, cancellationToken);
                return DirectMutationEffectCommitted() ? PersistDirectMutationTerminal(request.RequestId, requestDigest, finalized) : finalized;
            }
            catch (OperationCanceledException)
            {
                var serialized = ExecutionTimeoutEnvelope(request.RequestId, "The execution deadline elapsed and the result cannot be confirmed. Check the affected business state.");
                return DirectMutationEffectCommitted() ? PersistDirectMutationTerminal(request.RequestId, requestDigest, serialized) : serialized;
            }
            catch (BridgeCommandException exception)
            {
                var serialized = SerializeBridgeExceptionEnvelope(request.RequestId, exception);
                return DirectMutationEffectCommitted() ? PersistDirectMutationTerminal(request.RequestId, requestDigest, serialized) : serialized;
            }
            catch (Exception exception)
            {
                var serialized = SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException(
                    "E_BRIDGE_COMMAND_FAILED", exception.Message, "Check Unity Console and UnityCliBridge.log."));
                return DirectMutationEffectCommitted() ? PersistDirectMutationTerminal(request.RequestId, requestDigest, serialized) : serialized;
            }
            finally
            {
                RemoveCompletionEvidence(request.RequestId);
                FinalizeDirectMutationExecution(request.RequestId, requestDigest);
            }
        }

        private static bool DirectMutationEffectCommitted()
        {
            return SessionState.GetBool(DirectMutationEffectCommittedSessionKey, false);
        }

        /// <summary>accepted root写入失败也必须先建立exact cleanup marker，不能依赖不可读root驱动释放。</summary>
        private static void ScheduleDirectMutationPreEffectCleanup(string requestId, string requestDigest)
        {
            if (DirectMutationBindingMatches(requestId, requestDigest)) SessionState.SetBool(DirectMutationCleanupPendingSessionKey, true);
            try { UnityCliActiveOperation.BeginExactTerminalCleanup(DirectMutationTerminalScope, requestId, requestDigest); } catch { }
        }

        /// <summary>effect后的direct terminal先冻结first terminal，再由exact cleanup marker驱动root、lease与admission收敛。</summary>
        private static string PersistDirectMutationTerminal(string requestId, string requestDigest, string serializedEnvelope)
        {
            return PublishDirectMutationTerminal(ProductionQueueState, requestId, requestDigest, serializedEnvelope);
        }

        internal static string PublishDirectMutationTerminalForTests(BridgeQueueState state, string requestId, string requestDigest, string serializedEnvelope)
        {
            if (state == null || state.IsolatedExecution == null) throw new ArgumentException("Direct mutation test state must be isolated.", "state");
            return PublishDirectMutationTerminal(state, requestId, requestDigest, serializedEnvelope);
        }

        private static string PublishDirectMutationTerminal(BridgeQueueState state, string requestId, string requestDigest, string serializedEnvelope)
        {
            try
            {
                bool isOwned;
                if (!state.TryIsLeaseOwned(requestId, requestDigest, out isOwned) || !isOwned)
                    serializedEnvelope = ExecutionTimeoutEnvelope(requestId, "The direct mutation lost its exact lease after effect; outcome is unknown. Inspect the affected business state.");
                if (Encoding.UTF8.GetByteCount(serializedEnvelope ?? string.Empty) > MaxDirectMutationTerminalBytes)
                    serializedEnvelope = ExecutionTimeoutEnvelope(requestId, "The direct mutation completed, but its terminal exceeded the durable byte budget; inspect the affected business state.");
                object envelope = UnityCliSerializer.Parse(serializedEnvelope) as Dictionary<string, object>;
                if (envelope == null)
                    envelope = ProtocolEnvelopeFactory.Error(requestId, "E_EXECUTION_TIMEOUT", "The direct mutation terminal could not be decoded after effect; outcome is unknown.");
                var root = LoadDirectMutationRoot(state);
                RequireDirectMutationRoot(root, requestId, requestDigest);
                root["phase"] = "terminal_pending";
                root["terminalEnvelope"] = envelope;
                state.SaveOperation(DirectMutationTerminalScope, root);
                var first = state.StoreTerminalFirst(DirectMutationTerminalScope, requestId, requestDigest, null, root["toolName"] as string, envelope);
                CleanupPublishedDirectMutation(state, requestId, requestDigest);
                return UnityCliSerializer.Serialize(first);
            }
            catch (BridgeCommandException exception) { return SerializeBridgeExceptionEnvelope(requestId, exception); }
            catch (Exception exception) { return SerializeBridgeExceptionEnvelope(requestId, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Direct mutation terminal publication is incomplete; the exact owner is retained: " + exception.GetType().Name + ".")); }
        }

        private static void SaveDirectMutationAcceptedRoot(BridgeRequest request, string requestDigest, string toolName)
        {
            UnityCliActiveOperation.Save(DirectMutationTerminalScope, new Dictionary<string, object>
            {
                { "requestId", request.RequestId }, { "requestDigest", requestDigest }, { "toolName", toolName },
                { "editorSessionId", EditorSessionId() }, { "absoluteDeadlineUtc", request.Execution.AbsoluteDeadlineUtc },
                { "phase", "accepted_before_effect" }
            });
        }

        /// <summary>editor.play-mode.status只读取editor.play-mode.start/editor.play-mode.stop的direct owner或其已发布终态。</summary>
        private static object ReadEditorPlayModeStatus(string requestId)
        {
            string requestDigest;
            string statusDomain;
            object terminal;
            if (UnityCliActiveOperation.TryGetTerminalByRequestId(DirectMutationTerminalScope, requestId, out requestDigest, out statusDomain, out terminal))
            {
                if (statusDomain != "editor.play-mode.start" && statusDomain != "editor.play-mode.stop")
                    throw new BridgeCommandException("E_REQUEST_NOT_FOUND", "The requested Play Mode operation was not found in this status domain.");
                return UnityCliDurableStatusCoordinator.Terminal(requestId, terminal);
            }
            var root = LoadDirectMutationRoot();
            var toolName = root == null ? null : root.ContainsKey("toolName") ? root["toolName"] as string : null;
            if (root == null || !string.Equals(root["requestId"] as string, requestId, StringComparison.Ordinal)
                || (toolName != "editor.play-mode.start" && toolName != "editor.play-mode.stop"))
                throw new BridgeCommandException("E_REQUEST_NOT_FOUND", "The requested Play Mode operation was not found in this Editor session.");
            return UnityCliDurableStatusCoordinator.Running(requestId, root["phase"] as string);
        }

        private static void MarkDirectMutationEffectStarted(string requestId, string requestDigest)
        {
            var root = LoadDirectMutationRoot();
            RequireDirectMutationRoot(root, requestId, requestDigest);
            if (!string.Equals(root["phase"] as string, "accepted_before_effect", StringComparison.Ordinal)
                && !string.Equals(root["phase"] as string, "effect_started", StringComparison.Ordinal))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Direct mutation active state cannot cross its effect barrier.");
            var toolName = root["toolName"] as string;
            if ((string.Equals(toolName, "editor.play-mode.start", StringComparison.Ordinal) || string.Equals(toolName, "editor.play-mode.stop", StringComparison.Ordinal))
                && !root.ContainsKey("playModeWasPlaying"))
                root["playModeWasPlaying"] = EditorApplication.isPlaying;
            root["phase"] = "effect_started";
            UnityCliActiveOperation.Save(DirectMutationTerminalScope, root);
        }

        private static Dictionary<string, object> LoadDirectMutationRoot()
        {
            return LoadDirectMutationRoot(ProductionQueueState);
        }

        private static Dictionary<string, object> LoadDirectMutationRoot(BridgeQueueState state)
        {
            try
            {
                var root = state.LoadOperation(DirectMutationTerminalScope);
                if (root == null) return null;
                object requestId; object requestDigest; object toolName; object editorSessionId; object phase;
                if (!root.TryGetValue("requestId", out requestId) || !root.TryGetValue("requestDigest", out requestDigest)
                    || !root.TryGetValue("toolName", out toolName) || !root.TryGetValue("editorSessionId", out editorSessionId) || !root.TryGetValue("phase", out phase)
                    || !(requestId is string) || !(requestDigest is string) || !(toolName is string) || !(editorSessionId is string) || !(phase is string))
                    throw new InvalidDataException("Direct mutation active state is incomplete.");
                return root;
            }
            catch (BridgeCommandException) { throw; }
            catch (Exception exception) { throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Direct mutation active state cannot be read; outcome is unknown: " + exception.GetType().Name + "."); }
        }

        private static void RequireDirectMutationRoot(Dictionary<string, object> root, string requestId, string requestDigest)
        {
            if (root == null || !string.Equals(root["requestId"] as string, requestId, StringComparison.Ordinal)
                || !string.Equals(root["requestDigest"] as string, requestDigest, StringComparison.Ordinal)
                || !string.Equals(root["editorSessionId"] as string, EditorSessionId(), StringComparison.Ordinal))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Direct mutation active identity cannot be proven; outcome is unknown.");
        }

        internal enum DirectMutationReloadOutcome { NotApplicable, WaitingExactOwner, SettledEffectBefore, IdentityBlocked }

        /// <summary>旧domain不再执行effect；CleanupPending先释放旧lease，再冻结terminal并执行exact cleanup。</summary>
        internal static DirectMutationReloadOutcome ReconcileAcceptedDirectMutationAfterReload(BridgeQueueState state, string requestId, string requestDigest)
        {
            return ReconcileAcceptedDirectMutationAfterReload(state, requestId, requestDigest,
                DirectMutationCleanupPendingForIdentity(requestId, requestDigest));
        }

        internal static DirectMutationReloadOutcome ReconcileAcceptedDirectMutationAfterReload(BridgeQueueState state, string requestId, string requestDigest, bool releaseExactOwnerBeforeSettlement)
        {
            if (state == null) throw new ArgumentNullException("state");
            var root = LoadDirectMutationRoot(state);
            if (root == null) return DirectMutationReloadOutcome.NotApplicable;
            RequireDirectMutationRoot(root, requestId, requestDigest);
            if (!string.Equals(root["phase"] as string, "accepted_before_effect", StringComparison.Ordinal))
                return DirectMutationReloadOutcome.NotApplicable;
            string leaseRequestId;
            string leaseRequestDigest;
            if (!state.TryGetCurrentLeaseOwner(out leaseRequestId, out leaseRequestDigest))
                return DirectMutationReloadOutcome.IdentityBlocked;
            if (string.Equals(leaseRequestId, requestId, StringComparison.Ordinal)
                && string.Equals(leaseRequestDigest, requestDigest, StringComparison.Ordinal))
            {
                if (!releaseExactOwnerBeforeSettlement) return DirectMutationReloadOutcome.WaitingExactOwner;
                if (object.ReferenceEquals(state, ProductionQueueState)) ReleaseDirectMutationLease(requestId, requestDigest);
                else state.ReleaseLease(requestId, requestDigest);
                if (!state.TryGetCurrentLeaseOwner(out leaseRequestId, out leaseRequestDigest))
                    return DirectMutationReloadOutcome.IdentityBlocked;
            }
            if (!string.IsNullOrEmpty(leaseRequestId) || !string.IsNullOrEmpty(leaseRequestDigest))
                return DirectMutationReloadOutcome.IdentityBlocked;
            var owner = CaptureAdmissionOwner(state, requestId, requestDigest);
            if (owner.OwnerState != UnityCliAdmissionPool.AdmissionOwnerState.WorkItem
                && owner.OwnerState != UnityCliAdmissionPool.AdmissionOwnerState.Recovery)
                return DirectMutationReloadOutcome.IdentityBlocked;
            var terminal = ProtocolEnvelopeFactory.Error(requestId, "E_EXECUTION_TIMEOUT", "Direct mutation was accepted before domain reload but never crossed its effect barrier; no effect was executed.", null, new[] { "Submit a new request only after inspecting the exact terminal." });
            root["phase"] = "terminal_pending";
            root["terminalEnvelope"] = terminal;
            state.SaveOperation(DirectMutationTerminalScope, root);
            state.StoreTerminalFirst(DirectMutationTerminalScope, requestId, requestDigest, null, root["toolName"] as string, terminal);
            UnityCliActiveOperation.BeginExactTerminalCleanup(DirectMutationTerminalScope, requestId, requestDigest, state);
            return DirectMutationReloadOutcome.SettledEffectBefore;
        }

        private static string TryResumeDirectMutation(string requestId, string requestDigest)
        {
            Dictionary<string, object> root;
            try { root = LoadDirectMutationRoot(); }
            catch (BridgeCommandException exception) { return SerializeBridgeExceptionEnvelope(requestId, exception); }
            if (root == null) return null;
            if (!string.Equals(root["editorSessionId"] as string, EditorSessionId(), StringComparison.Ordinal))
            {
                try { UnityCliActiveOperation.Clear(DirectMutationTerminalScope); }
                catch (Exception exception) { return SerializeBridgeExceptionEnvelope(requestId, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Stale direct mutation state cannot be cleared: " + exception.GetType().Name + ".")); }
                return null;
            }
            if (!string.Equals(root["requestId"] as string, requestId, StringComparison.Ordinal))
                return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(requestId, "E_UNITY_BUSY", "Another direct mutation is active.", null, null, true, 250));
            if (!string.Equals(root["requestDigest"] as string, requestDigest, StringComparison.Ordinal))
                return SerializeBridgeExceptionEnvelope(requestId, new BridgeCommandException("E_REQUEST_ID_REUSED", "Direct mutation requestId was reused with different input."));
            var phase = root["phase"] as string;
            if (string.Equals(phase, "accepted_before_effect", StringComparison.Ordinal))
                return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(requestId, "E_UNITY_BUSY", "The accepted direct mutation is still active.", null, null, true, 250));
            bool recovered;
            try { if (!UnityCliSessionMutationLease.TryRecoverExact(requestId, requestDigest, out recovered)) return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(requestId, "E_UNITY_BUSY", "Another mutation owns the selected Unity instance.", null, null, true, 250)); }
            catch (BridgeCommandException exception) { return SerializeBridgeExceptionEnvelope(requestId, exception); }
            var toolName = root["toolName"] as string;
            if (string.Equals(phase, "effect_started", StringComparison.Ordinal)
                && (string.Equals(toolName, "editor.play-mode.start", StringComparison.Ordinal) || string.Equals(toolName, "editor.play-mode.stop", StringComparison.Ordinal)))
            {
                if (!TryFinalizeRecoveredPlayModeRoot(
                    ProductionQueueState, requestId, requestDigest,
                    EditorApplication.isPlaying, EditorApplication.isPaused, IsPlayModeChanging(),
                    EditorToolSupport.BuildEditorState, DateTimeOffset.UtcNow))
                    return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(
                        requestId, "E_UNITY_BUSY", "The Play Mode transition is still active for this exact request.",
                        null, new[] { "Run tools run editor.play-mode.status with the same requestId." }, true, 100));
                object recoveredTerminal;
                if (UnityCliActiveOperation.TryGetTerminal(DirectMutationTerminalScope, requestId, requestDigest, out recoveredTerminal))
                    return UnityCliSerializer.Serialize(recoveredTerminal);
                return ExecutionTimeoutEnvelope(requestId, "The recovered Play Mode terminal could not be read after publication.");
            }
            object pending;
            var envelope = string.Equals(phase, "terminal_pending", StringComparison.Ordinal) && root.TryGetValue("terminalEnvelope", out pending) && pending != null
                ? pending
                : (object)ProtocolEnvelopeFactory.Error(requestId, "E_EXECUTION_TIMEOUT", "Direct mutation effect was interrupted before a terminal was frozen; outcome is unknown.");
            var result = PersistDirectMutationTerminal(requestId, requestDigest, UnityCliSerializer.Serialize(envelope));
            CleanupPublishedDirectMutation(requestId, requestDigest);
            return result;
        }

        /// <summary>主循环重试已发布direct terminal的exact cleanup；失败保留固定marker等待下一帧。</summary>
        private static void RetryDirectMutationTerminalCleanup()
        {
            RetryDirectMutationTerminalCleanup(ProductionQueueState);
        }

        internal static void RetryDirectMutationTerminalCleanupForTests(BridgeQueueState state)
        {
            if (state == null || state.IsolatedExecution == null) throw new ArgumentException("Direct mutation test state must be isolated.", "state");
            RetryDirectMutationTerminalCleanup(state);
        }

        private static void RetryDirectMutationTerminalCleanup(BridgeQueueState state)
        {
            try { UnityCliActiveOperation.RetryExactTerminalCleanup(DirectMutationTerminalScope, state); }
            catch (Exception exception) { state.Sinks.WriteLog("direct_mutation_cleanup_retry_failed " + exception.GetType().Name); }
        }

        /// <summary>terminal类型不决定slot释放；exact marker必须在root清理前durable存在。</summary>
        private static void CleanupPublishedDirectMutation(string requestId, string requestDigest)
        {
            CleanupPublishedDirectMutation(ProductionQueueState, requestId, requestDigest);
        }

        private static void CleanupPublishedDirectMutation(BridgeQueueState state, string requestId, string requestDigest)
        {
            try
            {
                object terminal;
                if (!state.TryGetTerminal(DirectMutationTerminalScope, requestId, requestDigest, out terminal)) return;
                AdmissionOwnerIdentity owner;
                var completeAdmission = TryCaptureExactAdmissionOwner(state, requestId, requestDigest, out owner);
                UnityCliActiveOperation.BeginExactTerminalCleanup(DirectMutationTerminalScope, requestId, requestDigest, state, completeAdmission);
            }
            catch { }
        }

        internal static string RecoverPublishedDirectMutationForTests(BridgeQueueState state, string requestId, string requestDigest)
        {
            object terminal;
            if (state == null || state.IsolatedExecution == null || !state.TryGetTerminal(DirectMutationTerminalScope, requestId, requestDigest, out terminal)) return null;
            CleanupPublishedDirectMutation(state, requestId, requestDigest);
            return UnityCliSerializer.Serialize(terminal);
        }

        private static void FinalizeDirectMutationExecution(string requestId, string requestDigest)
        {
            try
            {
                var root = LoadDirectMutationRoot();
                if (!DirectMutationEffectCommitted() && DirectMutationBindingMatches(requestId, requestDigest))
                    SessionState.SetBool(DirectMutationCleanupPendingSessionKey, true);
                if (root != null && !DirectMutationEffectCommitted()
                    && string.Equals(root["requestId"] as string, requestId, StringComparison.Ordinal)
                    && string.Equals(root["requestDigest"] as string, requestDigest, StringComparison.Ordinal)
                    && string.Equals(root["phase"] as string, "accepted_before_effect", StringComparison.Ordinal))
                    UnityCliActiveOperation.Clear(DirectMutationTerminalScope);
                if (UnityCliActiveOperation.Exists(DirectMutationTerminalScope)) return;
            }
            catch
            {
                if (!DirectMutationEffectCommitted() && DirectMutationBindingMatches(requestId, requestDigest))
                {
                    SessionState.SetBool(DirectMutationCleanupPendingSessionKey, true);
                    try { UnityCliActiveOperation.BeginExactTerminalCleanup(DirectMutationTerminalScope, requestId, requestDigest); } catch { }
                }
                return;
            }
            ReleaseDirectMutationLease(requestId, requestDigest);
        }

        /// <summary>主循环只对可直接观察的PlayMode root冻结原类型终态；其余direct effect仍保持unknown。</summary>
        private static bool TryFinalizeRecoveredPlayModeRoot(string requestId, string requestDigest)
        {
            Dictionary<string, object> root;
            try
            {
                root = LoadDirectMutationRoot();
                if (root != null && SessionState.GetBool(DirectMutationCleanupPendingSessionKey, false)
                    && DirectMutationBindingMatches(requestId, requestDigest)
                    && string.Equals(root["requestId"] as string, requestId, StringComparison.Ordinal)
                    && string.Equals(root["requestDigest"] as string, requestDigest, StringComparison.Ordinal)
                    && string.Equals(root["phase"] as string, "accepted_before_effect", StringComparison.Ordinal))
                    UnityCliActiveOperation.Clear(DirectMutationTerminalScope);
            }
            catch { return false; }

            var finalized = TryFinalizeRecoveredPlayModeRoot(
                ProductionQueueState, requestId, requestDigest,
                EditorApplication.isPlaying, EditorApplication.isPaused, IsPlayModeChanging(),
                EditorToolSupport.BuildEditorState, DateTimeOffset.UtcNow);
            if (!finalized)
            {
                try { root = LoadDirectMutationRoot(); }
                catch { root = null; }
                var toolName = root == null ? null : root["toolName"] as string;
                if (toolName != "editor.play-mode.start" && toolName != "editor.play-mode.stop")
                    finalized = TryResumeDirectMutation(requestId, requestDigest) != null;
            }
            if (finalized)
            {
                try { if (!UnityCliActiveOperation.Exists(DirectMutationTerminalScope)) ReleaseDirectMutationLease(requestId, requestDigest); }
                catch { }
            }
            if (!SessionState.GetBool(DirectMutationCleanupPendingSessionKey, false)
                || !DirectMutationBindingMatches(requestId, requestDigest)) return finalized;
            ReleaseDirectMutationLease(requestId, requestDigest);
            bool isOwned;
            return UnityCliSessionMutationLease.TryIsOwnedBy(requestId, requestDigest, out isOwned) && !isOwned;
        }

        internal static bool TryFinalizeRecoveredPlayModeRootForTests(
            BridgeQueueState state, string requestId, string requestDigest,
            bool isPlaying, bool isPaused, bool isChanging, DateTimeOffset utcNow)
        {
            return TryFinalizeRecoveredPlayModeRoot(state, requestId, requestDigest, isPlaying, isPaused, isChanging,
                () => new EditorStateInfo
                {
                    PlayMode = new PlayModeInfo
                    {
                        IsPlaying = isPlaying,
                        IsPaused = isPaused,
                        IsPlayingOrWillChangePlaymode = isPlaying || isChanging
                    },
                    Busy = new BusyInfo { PlayModeChanging = isChanging }
                }, utcNow);
        }

        /// <summary>固定root中的before/deadline与当前主线程状态共同构成跨reload完成证明。</summary>
        private static bool TryFinalizeRecoveredPlayModeRoot(
            BridgeQueueState state, string requestId, string requestDigest,
            bool isPlaying, bool isPaused, bool isChanging,
            Func<EditorStateInfo> readState, DateTimeOffset utcNow)
        {
            Dictionary<string, object> root;
            try
            {
                root = LoadDirectMutationRoot(state);
                RequireDirectMutationRoot(root, requestId, requestDigest);
            }
            catch { return false; }
            if (!string.Equals(root["phase"] as string, "effect_started", StringComparison.Ordinal)) return false;
            var toolName = root["toolName"] as string;
            var enterPlayMode = string.Equals(toolName, "editor.play-mode.start", StringComparison.Ordinal);
            if (!enterPlayMode && !string.Equals(toolName, "editor.play-mode.stop", StringComparison.Ordinal)) return false;

            object beforeValue;
            object deadlineValue;
            DateTimeOffset deadline;
            if (!root.TryGetValue("playModeWasPlaying", out beforeValue) || !(beforeValue is bool)
                || !root.TryGetValue("absoluteDeadlineUtc", out deadlineValue) || !(deadlineValue is string)
                || !DateTimeOffset.TryParse((string)deadlineValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out deadline))
            {
                PublishDirectMutationTerminal(state, requestId, requestDigest,
                    ExecutionTimeoutEnvelope(requestId, "Play Mode recovery state is incomplete; outcome is unknown."));
                object invalidTerminal;
                return state.TryGetTerminal(DirectMutationTerminalScope, requestId, requestDigest, out invalidTerminal);
            }

            object terminal;
            if (!isChanging && isPlaying == enterPlayMode)
            {
                var before = (bool)beforeValue;
                var result = new PlayModeResult
                {
                    Changed = enterPlayMode ? !before : before,
                    IsPlaying = isPlaying,
                    IsPaused = isPaused
                };
                terminal = enterPlayMode
                    ? (object)new EditorPlayModeStartTool.Response { Result = result, State = readState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.play-mode.start" } } }
                    : new EditorPlayModeStopTool.Response { Result = result, State = readState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.play-mode.stop" } } };
                PublishDirectMutationTerminal(state, requestId, requestDigest,
                    UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(requestId, terminal)));
            }
            else
            {
                if (utcNow < deadline) return false;
                var code = enterPlayMode ? "E_PLAY_MODE_TERMINAL_NOT_OBSERVED" : "E_EDIT_MODE_TERMINAL_NOT_OBSERVED";
                var message = enterPlayMode
                    ? "Play Mode did not reach a stable playing state before the immutable deadline."
                    : "The Editor did not reach stable Edit Mode before the immutable deadline.";
                terminal = ProtocolEnvelopeFactory.Error(requestId, code, message, null,
                    new[] { "Run tools run editor.play-mode.status with the same requestId before deciding whether to submit a new request." });
                PublishDirectMutationTerminal(state, requestId, requestDigest, UnityCliSerializer.Serialize(terminal));
            }
            object published;
            return state.TryGetTerminal(DirectMutationTerminalScope, requestId, requestDigest, out published);
        }

        /// <summary>绑定本domain正在执行的direct mutation；SessionState只跨本次Editor启动的domain reload存活。</summary>
        private static void BindDirectMutationLease(string requestId, string requestDigest)
        {
            SessionState.SetString(DirectMutationRequestIdSessionKey, requestId);
            SessionState.SetString(DirectMutationRequestDigestSessionKey, requestDigest);
            SessionState.SetBool(DirectMutationCleanupPendingSessionKey, false);
            SessionState.SetBool(DirectMutationEffectCommittedSessionKey, false);
        }

        /// <summary>释放时先匹配完整owner；I/O不确定时保留marker供后续主循环再次精确清理。</summary>
        private static void ReleaseDirectMutationLease(string requestId, string requestDigest)
        {
            if (DirectMutationBindingMatches(requestId, requestDigest))
                SessionState.SetBool(DirectMutationCleanupPendingSessionKey, true);
            UnityCliSessionMutationLease.Release(requestId, requestDigest);
            bool isOwned;
            if (!UnityCliSessionMutationLease.TryIsOwnedBy(requestId, requestDigest, out isOwned)
                || isOwned || !DirectMutationBindingMatches(requestId, requestDigest)) return;
            SessionState.SetString(DirectMutationRequestIdSessionKey, string.Empty);
            SessionState.SetString(DirectMutationRequestDigestSessionKey, string.Empty);
            SessionState.SetBool(DirectMutationCleanupPendingSessionKey, false);
            SessionState.SetBool(DirectMutationEffectCommittedSessionKey, false);
        }

        /// <summary>domain reload或Editor退出前终止普通direct owner；专用durable coordinator不使用该marker。</summary>
        private static void ReleaseActiveDirectMutationLease()
        {
            var requestId = SessionState.GetString(DirectMutationRequestIdSessionKey, string.Empty);
            var requestDigest = SessionState.GetString(DirectMutationRequestDigestSessionKey, string.Empty);
            if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(requestDigest)) return;
            ReleaseDirectMutationLease(requestId, requestDigest);
        }

        private static bool DirectMutationBindingMatches(string requestId, string requestDigest)
        {
            return string.Equals(SessionState.GetString(DirectMutationRequestIdSessionKey, string.Empty), requestId, StringComparison.Ordinal)
                && string.Equals(SessionState.GetString(DirectMutationRequestDigestSessionKey, string.Empty), requestDigest, StringComparison.Ordinal);
        }

        private static bool DirectMutationCleanupPendingForIdentity(string requestId, string requestDigest)
        {
            return DirectMutationBindingMatches(requestId, requestDigest)
                && SessionState.GetBool(DirectMutationCleanupPendingSessionKey, false);
        }

        /// <summary>普通 Tool 的 effect 提交只保留 runner 协调语义，不写请求级持久状态。</summary>
        private static object PrepareToolEffect(BridgeRequest request, UnityCliToolCompletionContract contract, object typedInput)
        {
            return null;
        }

        private static void CommitToolEffect(BridgeRequest request, UnityCliToolCompletionContract contract, object preparation)
        {
            if (!RequestRequiresMutation(request, contract))
                throw new BridgeCommandException("E_TOOL_EFFECT_BARRIER_INVALID", "The current request is not classified as a mutation.", "Remove ctx.CommitEffect() from read-only and dry-run paths.");
            var requestDigest = SessionState.GetString(DirectMutationRequestDigestSessionKey, string.Empty);
            if (!DirectMutationBindingMatches(request.RequestId, requestDigest)
                || !UnityCliSessionMutationLease.IsOwnedBy(request.RequestId, requestDigest))
                throw new BridgeCommandException("E_UNITY_BUSY", "The direct mutation no longer owns its exact session lease.", "Do not execute the effect; inspect the current owner and business state.");
            MarkDirectMutationEffectStarted(request.RequestId, requestDigest);
            SessionState.SetBool(DirectMutationEffectCommittedSessionKey, true);
        }

        /// <summary>code.execute等内部direct命令在首个用户effect前同样重验完整lease identity。</summary>
        private static void BeginDirectEffect(string requestId)
        {
            var requestDigest = SessionState.GetString(DirectMutationRequestDigestSessionKey, string.Empty);
            if (!DirectMutationBindingMatches(requestId, requestDigest)
                || !UnityCliSessionMutationLease.IsOwnedBy(requestId, requestDigest))
                throw new BridgeCommandException("E_UNITY_BUSY", "The direct command no longer owns its exact session lease.", "Do not execute caller code; inspect the current owner and business state.");
            MarkDirectMutationEffectStarted(requestId, requestDigest);
            SessionState.SetBool(DirectMutationEffectCommittedSessionKey, true);
        }

        private static void RecordToolCompletionEvidence(BridgeRequest request, RegisteredTool definition, object result, object terminalData)
        {
            if (definition == null || definition.CompletionContract == null)
                throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Tool completion contract is unavailable at the runner boundary.", "Fix Tool registration before retrying.");
            var contract = definition.CompletionContract;
            ValidateToolTerminalData(request, definition, result, terminalData);
            if (!RequestRequiresMutation(request, contract))
            {
                if (request.Execution == null || request.Execution.DryRun != true) ValidateDriverReadback(contract, result);
                return;
            }
            ValidateDriverReadback(contract, result);
            RegisterCompletionEvidence(request.RequestId, definition.Name, contract, result);
        }

        /// <summary>内部 code/package 命令在取得命令专属 readback 后登记同一 proof 结构。</summary>
        private static void RecordDirectCompletionEvidence(string requestId, string toolName, UnityCliToolCompletionContract contract, object result)
        {
            if (contract == null || !contract.Mutation) throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Direct completion evidence requires a mutating completion contract.", "Fix the internal command completion registration.");
            ValidateDriverReadback(contract, result);
            RegisterCompletionEvidence(requestId, toolName, contract, result);
        }

        private static void RegisterCompletionEvidence(string requestId, string toolName, UnityCliToolCompletionContract contract, object result)
        {
            var resultJson = UnityCliSerializer.Serialize(result);
            byte[] resultHash;
            using (var sha = SHA256.Create()) resultHash = sha.ComputeHash(Encoding.UTF8.GetBytes(resultJson));
            var proofText = "unity-cli-completion-proof-v1\0" + requestId + "\0" + toolName + "\0" + contract.CompletionDriverId + "\0" + contract.ProofSource + "\0" + Hex(resultHash);
            var evidence = new CompletionEvidence
            {
                RequestId = requestId,
                ToolName = toolName,
                CompletionDriverId = contract.CompletionDriverId,
                ProofKind = contract.ProofSource,
                ProofBytes = Encoding.UTF8.GetBytes(proofText)
            };
            lock (CompletionEvidenceLock)
            {
                if (CompletionEvidenceByRequest.ContainsKey(requestId))
                    throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Duplicate completion evidence was registered for requestId " + requestId + ".", "Ensure exactly one completion driver owns the Tool request.");
                CompletionEvidenceByRequest.Add(requestId, evidence);
            }
        }

        private static CompletionEvidence TakeCompletionEvidence(string requestId, UnityCliToolCompletionContract contract)
        {
            lock (CompletionEvidenceLock)
            {
                CompletionEvidence evidence;
                if (!CompletionEvidenceByRequest.TryGetValue(requestId, out evidence)) return null;
                CompletionEvidenceByRequest.Remove(requestId);
                if (!string.Equals(evidence.ToolName, contract.ToolName, StringComparison.Ordinal)
                    || !string.Equals(evidence.CompletionDriverId, contract.CompletionDriverId, StringComparison.Ordinal)
                    || !string.Equals(evidence.ProofKind, contract.ProofSource, StringComparison.Ordinal))
                    throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Completion evidence does not match the immutable Tool contract.", "Fix the Tool completion driver registration.");
                return evidence;
            }
        }

        private static void RemoveCompletionEvidence(string requestId)
        {
            lock (CompletionEvidenceLock) CompletionEvidenceByRequest.Remove(requestId);
        }

        private static bool IsRegisteredCompletionDriver(string executionClass, string completionDriverId)
        {
            if (!RegisteredCompletionDriverIds.Contains(completionDriverId ?? string.Empty)) return false;
            if (string.Equals(executionClass, "atomic", StringComparison.Ordinal)) return completionDriverId == "atomic-typed-result-v1";
            if (string.Equals(executionClass, "observed-state", StringComparison.Ordinal)) return completionDriverId == "observed-terminal-readback-v1";
            if (string.Equals(executionClass, "resumable", StringComparison.Ordinal)) return completionDriverId == "active-operation-terminal-readback-v1";
            if (string.Equals(executionClass, "external", StringComparison.Ordinal))
                return completionDriverId == "external-terminal-readback-v1" || completionDriverId == "official-method-return-v1";
            return false;
        }

        private static void ValidateToolTerminalData(BridgeRequest request, RegisteredTool definition, object result, object terminalData)
        {
            var data = terminalData as IDictionary<string, object>;
            object resultValue;
            if (data == null
                || data.Count != 1
                || !data.TryGetValue("result", out resultValue)
                || !string.Equals(UnityCliSerializer.Serialize(resultValue), UnityCliSerializer.Serialize(result), StringComparison.Ordinal))
            {
                throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Tool runner did not produce its exact typed business terminal.", "Fix the Tool runner completion boundary before retrying.");
            }
        }

        private static void ValidateDriverReadback(UnityCliToolCompletionContract contract, object result)
        {
            // 官方 attribute 只承诺 attributed method 已返回；null/primitive/对象都属于诚实的弱终态。
            if (string.Equals(contract.CompletionDriverId, "official-method-return-v1", StringComparison.Ordinal)) return;
            if (result == null) throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Completion result is null.", "Return a typed business result with terminal evidence.");
            var normalized = UnityCliSerializer.Parse(UnityCliSerializer.Serialize(result));
            if (normalized == null) throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Completion result is not a JSON value tree.", "Return a serializable typed business result.");
            if (string.Equals(contract.ExecutionClass, "atomic", StringComparison.Ordinal) && !contract.Mutation) return;
            if (ContainsIntermediateStatus(normalized))
                throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Completion result contains an intermediate accepted/requested/queued/started/triggered/running status.", "Wait for the Tool business terminal and return its stable readback.");
            if (string.Equals(contract.ExecutionClass, "atomic", StringComparison.Ordinal)) return;
            var terminalObject = normalized as IDictionary;
            if (terminalObject == null || terminalObject.Count == 0)
                throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Non-atomic Tool result is not a non-empty typed business terminal.", "Return the Tool-specific terminal readback verified by its completion contract.");
        }

        private static bool ContainsIntermediateStatus(object value)
        {
            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                    var text = entry.Value as string;
                    if ((string.Equals(key, "status", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(key, "state", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(key, "phase", StringComparison.OrdinalIgnoreCase)) && IsIntermediateStatus(text)) return true;
                    if (IsIntermediateField(key) && IntermediateFieldIsSet(entry.Value)) return true;
                    if (ContainsIntermediateStatus(entry.Value)) return true;
                }
                return false;
            }
            var sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) foreach (var item in sequence) if (ContainsIntermediateStatus(item)) return true;
            return false;
        }

        private static bool IsIntermediateStatus(string value)
        {
            if (value == null) return false;
            var status = value.Trim().ToLowerInvariant();
            return status == "accepted" || status == "requested" || status == "queued" || status == "started" || status == "triggered" || status == "running" || status == "in_progress";
        }

        private static bool IsIntermediateField(string name)
        {
            return string.Equals(name, "accepted", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "requested", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "queued", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "started", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "triggered", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IntermediateFieldIsSet(object value)
        {
            if (value == null) return false;
            if (value is bool) return (bool)value;
            var text = value as string;
            return text == null || !string.IsNullOrWhiteSpace(text);
        }

        /// <summary>根据 wrapper input 解析真实 Tool；project/runtime wrapper 不能使用泛化 contract。</summary>
        private static UnityCliToolCompletionContract ResolveRequestCompletionContract(BridgeRequest request)
        {
            var toolName = RequestToolName(request);
            if (IsInstanceCleanupCommand(toolName)) return InstanceCleanupCompletionContract();
            UnityCliToolCompletionContract contract;
            if (UnityCliToolCompletionRegistry.TryGet(toolName, out contract)) return contract;
            if (string.Equals(toolName, "code.execute", StringComparison.Ordinal))
                return UnityCliToolCompletionRegistry.CodeExec;
            return ResolveRegisteredCustomToolCompletionContract(toolName);
        }

        private static string RequestToolName(BridgeRequest request)
        {
            if (request != null && (string.Equals(request.Command, "catalog.editor.run", StringComparison.Ordinal)
                || string.Equals(request.Command, "catalog.player.run", StringComparison.Ordinal)))
            {
                var input = request.Input as IDictionary<string, object>;
                object rawName;
                return input != null && input.TryGetValue("name", out rawName) ? rawName as string : null;
            }
            return request == null ? null : request.Command;
        }

        /// <summary>HTTP worker只读取immutable request与已冻结registry；未知或依赖场景现状的分类显式按Mutation关闭。</summary>
        internal static UnityCliBridgeQueuePolicy.WorkClass QueueRequestWorkClass(BridgeRequest request)
        {
            if (request == null) return UnityCliBridgeQueuePolicy.WorkClass.Mutation;
            var toolName = RequestToolName(request);
            if (!string.IsNullOrEmpty(toolName) && toolName.StartsWith("scene.", StringComparison.Ordinal))
                return UnityCliBridgeQueuePolicy.WorkClass.Mutation;
            UnityCliBridgeQueuePolicy.WorkClass registered;
            if (CommandRegistry.TryGetQueueClass(request.Command, out registered)) return registered;
            var contract = ResolveQueueCompletionContract(request);
            if (contract == null)
                return IsReadOnlyBridgeCommand(request.Command)
                    ? UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead
                    : UnityCliBridgeQueuePolicy.WorkClass.Mutation;
            if (request.Execution != null && request.Execution.DryRun == true) return UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead;
            if (!contract.Mutation) return UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead;
            bool requiresSceneState;
            var inputMutation = RequestMutationFromImmutableInput(contract.ToolName, RequestBusinessInput(request), out requiresSceneState);
            return requiresSceneState || inputMutation
                ? UnityCliBridgeQueuePolicy.WorkClass.Mutation
                : UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead;
        }

        /// <summary>worker只读取内置冻结contract；项目自定义Tool未知时由调用方保守归Mutation。</summary>
        private static UnityCliToolCompletionContract ResolveQueueCompletionContract(BridgeRequest request)
        {
            var toolName = RequestToolName(request);
            if (IsInstanceCleanupCommand(toolName)) return InstanceCleanupCompletionContract();
            UnityCliToolCompletionContract contract;
            if (UnityCliToolCompletionRegistry.TryGet(toolName, out contract)) return contract;
            return string.Equals(toolName, "code.execute", StringComparison.Ordinal) ? UnityCliToolCompletionRegistry.CodeExec : null;
        }

        private sealed class DurableMutationBarrierProof
        {
            internal UnityCliBridgeQueuePolicy.DurableMutationBarrier State;
            internal string RequestId;
            internal string RequestDigest;
        }

        /// <summary>durable lease三态同时冻结当前owner identity，供锁外control root proof精确绑定。</summary>
        private static DurableMutationBarrierProof CurrentDurableMutationBarrier(BridgeQueueState state)
        {
            string requestId;
            string requestDigest;
            if (state == null || !state.TryGetCurrentLeaseOwner(out requestId, out requestDigest))
                return new DurableMutationBarrierProof { State = UnityCliBridgeQueuePolicy.DurableMutationBarrier.Unreadable };
            if (string.IsNullOrEmpty(requestId) && string.IsNullOrEmpty(requestDigest))
                return new DurableMutationBarrierProof { State = UnityCliBridgeQueuePolicy.DurableMutationBarrier.Clear };
            if (!IsSafeId(requestId) || !IsSha256(requestDigest))
                return new DurableMutationBarrierProof { State = UnityCliBridgeQueuePolicy.DurableMutationBarrier.Unreadable };
            return new DurableMutationBarrierProof
            {
                State = UnityCliBridgeQueuePolicy.DurableMutationBarrier.Held,
                RequestId = requestId,
                RequestDigest = requestDigest
            };
        }

        /// <summary>主线程在pure input裁决后，仅对真正依赖场景现状的幂等分支读取Unity对象。</summary>
        private static bool RequestRequiresMutation(BridgeRequest request, UnityCliToolCompletionContract contract)
        {
            if (request == null || contract == null || !contract.Mutation || (request.Execution != null && request.Execution.DryRun == true)) return false;
            var input = RequestBusinessInput(request);
            bool requiresSceneState;
            var inputMutation = RequestMutationFromImmutableInput(contract.ToolName, input, out requiresSceneState);
            if (!requiresSceneState) return inputMutation;
            if (contract.ToolName == "physics.rigidbody") return RigidbodyNeedsMutation(input);
            if (contract.ToolName == "lighting.reflection-probe") return CreateProbeNeedsMutation(input, true);
            if (contract.ToolName == "lighting.light-probe-group") return CreateProbeNeedsMutation(input, false);
            return true;
        }

        /// <summary>只按immutable JSON字段判断；需要场景现状的幂等分支通过requiresSceneState显式上报。</summary>
        private static bool RequestMutationFromImmutableInput(string name, IDictionary<string, object> input, out bool requiresSceneState)
        {
            requiresSceneState = false;
            if (name == "lighting.reflection-probe")
            {
                if (HasAnyMeaningfulInput(input, "mode", "resolution", "bounces", "hdr", "boxSize", "boxOffset")) return true;
                requiresSceneState = InputBoolean(input, "create");
                return false;
            }
            if (name == "lighting.light-probe-group")
            {
                if (HasAnyMeaningfulInput(input, "positions")) return true;
                requiresSceneState = InputBoolean(input, "create");
                return false;
            }
            if (name == "lighting.skybox.set-properties") return HasAllMeaningfulInput(input, "property", "value");
            if (name == "build.settings.set") return HasAnyInput(input, "apiCompatibilityLevel", "applicationIdentifier", "bundleVersion", "companyId", "productName", "scriptingBackend");
            if (name == "lighting.bake.settings") return HasAnyInput(input, "bakedGI", "bounces", "directSamples", "environmentSamples", "filtering", "indirectSamples", "lightingMode", "realtimeGI");
            if (name == "rendering.quality") return HasAnyInput(input, "anisotropicFiltering", "antiAliasing", "lodBias", "masterTextureLimit", "maximumLODLevel", "qualityLevel", "realtimeReflectionProbes", "shadowDistance", "shadowResolution", "shadows", "softParticles", "vSyncCount");
            if (name == "rendering.pipeline.settings") return HasAnyInput(input, "renderPipeline");
            if (name == "lighting.skybox.set-ambient") return HasAnyMeaningfulInput(input, "ambientEquatorColor", "ambientGroundColor", "ambientSkyColor", "color", "intensity", "mode");
            if (name == "lighting.skybox.set-fog") return HasAnyMeaningfulInput(input, "color", "density", "enabled", "endDistance", "mode", "startDistance");
            if (name == "lighting.skybox.set-reflection") return HasAnyMeaningfulInput(input, "bounces", "intensity", "mode", "resolution");
            if (name == "rendering.scene-debug") return HasAnyInput(input, "drawMode", "shadingMode");
            if (name == "physics.collision-matrix") return HasAllInput(input, "layerA", "layerB", "ignore");
            if (name == "physics.settings") return HasAnyInput(input, "autoSimulation", "bounceThreshold", "defaultContactOffset", "defaultSolverIterations", "defaultSolverVelocityIterations", "gravity", "gravity2d", "property", "queriesHitBackfaces", "queriesHitTriggers", "queriesStartInColliders", "value");
            if (name == "physics.rigidbody")
            {
                if (InputBoolean(input, "validate")) return false;
                if (HasAnyMeaningfulInput(input, "angularDrag", "drag", "isKinematic", "mass", "useGravity")) return true;
                requiresSceneState = true;
                return false;
            }
            if (name == "physics.simulation") return InputBoolean(input, "simulate");
            if (name == "texture.import-settings") return HasAnyInput(input, "alphaIsTransparency", "compression", "filterMode", "maxTextureSize", "mipmap", "readable", "sRGB", "textureType", "type", "wrapMode");
            if (name == "profiler.deep") return HasAnyInput(input, "enabled");
            if (name == "scene.view-frame") return HasAnyInput(input, "target", "path") || InputBoolean(input, "frameAll");
            if (name == "ui.modify-visual-element") return HasUiVisualElementMutation(input);
            if (name == "cinemachine.lens") return HasAnyInput(input, "dutch", "farClipPlane", "fieldOfView", "nearClipPlane", "orthographicSize");
            if (name == "cinemachine.priority") return HasAnyInput(input, "enabled", "priority");
            if (name == "cinemachine.target") return HasAnyInput(input, "follow", "lookAt");
            if (name == "cinemachine.blend") return HasAnyInput(input, "duration", "style");
            return true;
        }

        private static IDictionary<string, object> RequestBusinessInput(BridgeRequest request)
        {
            var root = request.Input as IDictionary<string, object>;
            if (root == null) return new Dictionary<string, object>();
            if (string.Equals(request.Command, "catalog.editor.run", StringComparison.Ordinal) || string.Equals(request.Command, "catalog.player.run", StringComparison.Ordinal))
            {
                object nested;
                var typed = root.TryGetValue("input", out nested) ? nested as IDictionary<string, object> : null;
                return typed ?? new Dictionary<string, object>();
            }
            return root;
        }

        private static bool HasAnyInput(IDictionary<string, object> input, params string[] names)
        {
            return names.Any(name => input.ContainsKey(name) && input[name] != null);
        }

        private static bool HasAllInput(IDictionary<string, object> input, params string[] names)
        {
            return names.All(name => input.ContainsKey(name) && input[name] != null);
        }

        /// <summary>字符串 setter 与 handler 一致：空字符串表示未提供，数值和布尔值只要非 null 即有效。</summary>
        private static bool HasAnyMeaningfulInput(IDictionary<string, object> input, params string[] names)
        {
            return names.Any(name => HasMeaningfulInput(input, name));
        }

        private static bool HasAllMeaningfulInput(IDictionary<string, object> input, params string[] names)
        {
            return names.All(name => HasMeaningfulInput(input, name));
        }

        private static bool HasMeaningfulInput(IDictionary<string, object> input, string name)
        {
            object value;
            return input.TryGetValue(name, out value) && value != null && (!(value is string) || !string.IsNullOrEmpty((string)value));
        }

        /// <summary>UI text 的空字符串是清空文本；其余字符串 setter 与 handler 一样忽略空字符串。</summary>
        private static bool HasUiVisualElementMutation(IDictionary<string, object> input)
        {
            object text;
            return (input.TryGetValue("text", out text) && text != null)
                || HasAnyMeaningfulInput(input, "addClass", "display", "height", "removeClass", "width");
        }

        private static bool InputBoolean(IDictionary<string, object> input, string name)
        {
            object value;
            return input.TryGetValue(name, out value) && value is bool && (bool)value;
        }

        /// <summary>非 validate 的空配置仅在目标缺少 Rigidbody 时会添加组件，已有组件则保持只读。</summary>
        private static bool RigidbodyNeedsMutation(IDictionary<string, object> input)
        {
            object targetValue;
            var target = input.TryGetValue("target", out targetValue) ? targetValue as string : null;
            if (string.IsNullOrEmpty(target)) return false;
            var targetObject = GameObjectToolSupport.ResolveUniqueTarget(target, "physics.rigidbody requires --target.");
            return targetObject.GetComponent<Rigidbody>() == null;
        }

        /// <summary>create=true 只有缺少组件或未指定目标而需要新建对象时才是 mutation；已有组件的幂等读取不越过 barrier。</summary>
        private static bool CreateProbeNeedsMutation(IDictionary<string, object> input, bool reflectionProbe)
        {
            if (!InputBoolean(input, "create")) return false;
            object targetValue;
            var target = input.TryGetValue("target", out targetValue) ? targetValue as string : null;
            if (string.IsNullOrEmpty(target)) return true;
            var targetObject = GameObjectToolSupport.ResolveUniqueTarget(target, "Probe target could not be resolved.");
            return reflectionProbe ? targetObject.GetComponent<ReflectionProbe>() == null : targetObject.GetComponent<LightProbeGroup>() == null;
        }

        /// <summary>Context/catalog/health 类请求没有 business mutation，不得创建 request journal。</summary>
        private static bool IsReadOnlyBridgeCommand(string command)
        {
            if (IsEditorStateSnapshotCommand(command) || IsDoctorDiagnosticCommand(command)) return true;
            return IsProjectToolCatalogReadCommand(command)
                || string.Equals(command, "catalog.player.groups", StringComparison.Ordinal)
                || string.Equals(command, "catalog.player.summary", StringComparison.Ordinal)
                || string.Equals(command, "catalog.player.describe", StringComparison.Ordinal)
                || string.Equals(command, "catalog.player.list", StringComparison.Ordinal);
        }

        private static Dictionary<string, object> ValidateCoordinatedTerminal(string response, string requestId)
        {
            var envelope = UnityCliSerializer.Parse(response) as Dictionary<string, object>;
            object ok;
            object actualRequestId;
            if (envelope == null
                || !envelope.TryGetValue("ok", out ok) || !(ok is bool)
                || !envelope.TryGetValue("requestId", out actualRequestId)
                || !string.Equals(actualRequestId as string, requestId, StringComparison.Ordinal))
                throw new BridgeCommandException(
                    "E_TOOL_COMPLETION_PROOF_INVALID",
                    "Tool completion did not produce a terminal envelope bound to the root requestId.",
                    "Inspect the Tool completion driver and output schema before retrying.");
            return envelope;
        }

        /// <summary>普通 Tool 只在当前异步窗口等待稳定状态，超时后由调用方检查业务状态。</summary>
        private static async Task<string> FinalizeResult(BridgeRequest request, object terminalEnvelope, CancellationToken cancellationToken)
        {
            int remainingMs;
            if (!request.TryDeadlineRemainingMs(out remainingMs))
                return ExecutionTimeoutEnvelope(request.RequestId, "The execution result cannot be confirmed before the deadline. Check the affected business state.");
            try
            {
                if (!await CreateWaiter(request.RequestId, "completion.finalize").UntilEditorReady(TimeSpan.FromMilliseconds(remainingMs), cancellationToken))
                    return ExecutionTimeoutEnvelope(request.RequestId, "The execution result cannot be confirmed before the deadline. Check the affected business state.");
            }
            catch (OperationCanceledException)
            {
                return ExecutionTimeoutEnvelope(request.RequestId, "The execution result cannot be confirmed before the deadline. Check the affected business state.");
            }
            return UnityCliSerializer.Serialize(terminalEnvelope);
        }

        private static string ExecutionTimeoutEnvelope(string requestId, string reason)
        {
            return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(
                requestId,
                "E_EXECUTION_TIMEOUT",
                reason,
                null,
                new[] { "Check the affected business state before submitting another request." }));
        }


        private static object CompletionProofInvalidEnvelope(BridgeRequest request, UnityCliToolCompletionContract contract, string reason)
        {
            return ProtocolEnvelopeFactory.Error(
                request == null ? string.Empty : request.RequestId,
                "E_TOOL_COMPLETION_PROOF_INVALID",
                "The Tool did not satisfy its registered terminal completion proof.",
                new Dictionary<string, object>
                {
                    { "toolName", contract == null ? string.Empty : contract.ToolName },
                    { "completionDriverId", contract == null ? string.Empty : contract.CompletionDriverId },
                    { "reason", reason }
                },
                new[] { "Fix the Tool completion driver or business readback before retrying." });
        }

        private static string CompletionContractRequiredEnvelope(BridgeRequest request)
        {
            return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(
                request == null ? string.Empty : request.RequestId,
                "E_TOOL_COMPLETION_CONTRACT_REQUIRED",
                "The requested Tool has no explicit completion contract: " + RequestToolName(request) + ".",
                new Dictionary<string, object> { { "toolName", RequestToolName(request) ?? string.Empty } },
                new[] { "Declare execution class, completion predicate, proof source, reload policy, timeout error, and outcome-unknown policy before registering this Tool." }));
        }

        private static string CompletionDriverRequiredEnvelope(BridgeRequest request, UnityCliToolCompletionContract contract)
        {
            return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(
                request == null ? string.Empty : request.RequestId,
                "E_TOOL_COMPLETION_CONTRACT_REQUIRED",
                "The Tool completion driver is not registered for its execution class: " + (contract == null ? string.Empty : contract.ToolName) + ".",
                new Dictionary<string, object> { { "toolName", contract == null ? string.Empty : contract.ToolName }, { "completionDriverId", contract == null ? string.Empty : contract.CompletionDriverId } },
                new[] { "Register one sealed completion driver before exposing this Tool." }));
        }
    }
}
#endif
