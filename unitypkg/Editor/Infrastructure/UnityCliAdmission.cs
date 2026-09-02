#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using UnityEditor;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private const string AdmissionSessionStateKey = "UnityCli.AdmissionPool.State";
        private const string AdmissionSettlementScope = "admission-settlement";
        private static readonly string[] AdmissionDurableScopes = { "compile", "package-resolve", "refresh", "asset-refresh", "package-embed", "test-run", "test-cancel", "build", "navmesh-bake", "occlusion-bake", "direct-mutation" };
        private static UnityCliAdmissionPool admissionPool
        {
            get { return ProductionQueueState.AdmissionPool; }
            set { ProductionQueueState.AdmissionPool = value; }
        }

        internal sealed class AdmissionOwnerIdentity
        {
            internal string RequestId;
            internal string RequestDigest;
            internal string TopRequestDigest;
            internal string RootFingerprint;
            internal string Token;
            internal string SessionId;
            internal string ListenerId;
            internal long ListenerGeneration;
            internal long ExecutionDeadlineMs;
            internal string EditorSessionId;
            internal UnityCliAdmissionPool.AdmissionOwnerState OwnerState;
        }

        private enum AdmissionTransferResult
        {
            NotAccepted,
            Transferred,
            TransferPending
        }

        /// <summary>队列publication失败时只补偿本次新消费；attach不拥有撤销权。</summary>
        internal sealed class AdmissionQueueReservation
        {
            internal string RequestId;
            internal string RequestDigest;
            internal string RootFingerprint;
            internal string Token;
            internal long ExecutionDeadlineMs;
            internal bool HasAdmissionHeaders;
            internal bool HasExactTerminal;
            internal bool IsBypass;
            internal AdmissionOwnerIdentity Owner;
            internal bool NewlyConsumed;
            internal bool QueuePublished;
        }

        /// <summary>按当前Editor session与项目配置建立唯一准入池，并恢复同启动内的consumed roots。</summary>
        private static void InitializeAdmissionPool()
        {
            if (admissionPool != null) return;
            var settings = UnityCliProjectSettings.Current;
            admissionPool = new UnityCliAdmissionPool(InstanceId(), EditorSessionId(), settings.TaskPoolMaxActive, settings.TaskPoolReopenAt);
            ProductionQueueState.ListenerId = ListenerId;
            ProductionQueueState.ListenerGeneration = Math.Max(1, CurrentListenerGeneration());
            var serialized = SessionState.GetString(AdmissionSessionStateKey, string.Empty);
            if (string.IsNullOrEmpty(serialized))
            {
                ReconcileOrphanAdmissionOwnerMarkers();
                ReconcilePendingAdmissionSettlements();
                return;
            }
            try
            {
                var state = AsDictionary(UnityCliSerializer.Parse(serialized));
                if (state == null || AdmissionText(state, "editorSessionId") != EditorSessionId())
                {
                    SessionState.EraseString(AdmissionSessionStateKey);
                    ReconcileOrphanAdmissionOwnerMarkers();
                    ReconcilePendingAdmissionSettlements();
                    return;
                }
                var records = new List<UnityCliAdmissionPool.AdmissionRecord>();
                object rawRecords;
                var values = state.TryGetValue("records", out rawRecords) ? rawRecords as IEnumerable : null;
                if (values == null) throw new InvalidOperationException("Admission SessionState records are missing.");
                foreach (var value in values)
                {
                    var record = AsDictionary(value);
                    if (record == null) throw new InvalidOperationException("Admission SessionState record is invalid.");
                    records.Add(new UnityCliAdmissionPool.AdmissionRecord
                    {
                        InstanceId = AdmissionText(record, "instanceId"),
                        SessionId = AdmissionText(record, "sessionId"),
                        RequestId = AdmissionText(record, "requestId"),
                        RootFingerprint = AdmissionText(record, "rootFingerprint"),
                        RequestDigest = AdmissionText(record, "requestDigest"),
                        Token = AdmissionText(record, "token"),
                        ListenerId = AdmissionText(record, "listenerId"),
                        ListenerGeneration = AdmissionLong(record, "listenerGeneration"),
                        GrantExpiresAtMs = AdmissionLong(record, "grantExpiresAtMs"),
                        ExecutionDeadlineMs = AdmissionLong(record, "executionDeadlineMs"),
                        OwnerState = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(record, "ownerState"),
                        IntendedOwnerState = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(record, "intendedOwnerState"),
                        TransferCommitted = AdmissionBool(record, "transferCommitted")
                    });
                }
                admissionPool.Restore(records, checked((int)AdmissionLong(state, "activeCount")), AdmissionBool(state, "gateClosed"), RequiredText(state, "recoveryDigest", 64), checked((int)AdmissionLong(state, "configuredMaxActive")), checked((int)AdmissionLong(state, "configuredReopenAt")));
                ReconcileOrphanAdmissionOwnerMarkers();
                ReconcilePendingAdmissionSettlements();
            }
            catch (Exception exception)
            {
                admissionPool.BlockRecovery();
                SessionState.EraseString(AdmissionSessionStateKey);
                WriteLog("admission_restore_failed " + exception.GetType().Name + ": " + exception.Message);
                ReconcileOrphanAdmissionOwnerMarkers();
                ReconcilePendingAdmissionSettlements();
            }
        }

        /// <summary>reload前撤销未消费grant，只持久化同Editor启动内的有界owner state。</summary>
        private static void SaveAdmissionPoolBeforeReload()
        {
            if (admissionPool == null) return;
            admissionPool.RevokeUnconsumed(ListenerId, CurrentListenerGeneration());
            PersistAdmissionPoolState();
        }

        /// <summary>单个SessionState键保存最多maxActive条；新Editor启动因session不匹配整体作废。</summary>
        private static bool PersistAdmissionPoolState(BridgeQueueState state = null)
        {
            state = state ?? ProductionQueueState;
            if (state == null || state.AdmissionPool == null) return true;
            if (!object.ReferenceEquals(state, ProductionQueueState)) return state.PersistPool();
            return PersistProductionAdmissionPoolState();
        }

        private static bool PersistProductionAdmissionPoolState()
        {
            if (admissionPool == null) return true;
            var snapshot = admissionPool.CaptureRecovery();
            var records = snapshot.Records.Select(record => (object)new Dictionary<string, object>
            {
                { "instanceId", record.InstanceId }, { "sessionId", record.SessionId },
                { "requestId", record.RequestId }, { "rootFingerprint", record.RootFingerprint }, { "requestDigest", record.RequestDigest }, { "token", record.Token },
                { "listenerId", record.ListenerId }, { "listenerGeneration", record.ListenerGeneration },
                { "grantExpiresAtMs", record.GrantExpiresAtMs }, { "executionDeadlineMs", record.ExecutionDeadlineMs },
                { "ownerState", (long)record.OwnerState }, { "intendedOwnerState", (long)record.IntendedOwnerState },
                { "transferCommitted", record.TransferCommitted }
            }).ToList();
            SessionState.SetString(AdmissionSessionStateKey, UnityCliSerializer.Serialize(new Dictionary<string, object>
            {
                { "editorSessionId", EditorSessionId() }, { "gateClosed", snapshot.GateClosed }, { "records", records },
                { "activeCount", (long)records.Count }, { "recoveryDigest", snapshot.RecoveryDigest },
                { "configuredMaxActive", (long)snapshot.ConfiguredMaxActive }, { "configuredReopenAt", (long)snapshot.ReopenAt }
            }));
            return true;
        }

        /// <summary>重启后只重放已落盘completion intent；该路径只完成准入，不重新执行WorkItem effect。</summary>
        private static void ReconcilePendingAdmissionSettlements()
        {
            if (admissionPool == null) return;
            Dictionary<string, object> root;
            try { root = UnityCliActiveOperation.Load(AdmissionSettlementScope); }
            catch { admissionPool.BlockRecovery(); return; }
            if (root == null) return;
            var entries = root.ContainsKey("entries") ? root["entries"] as List<object> : null;
            if (!string.Equals(AdmissionText(root, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal) || entries == null)
            {
                admissionPool.BlockRecovery();
                return;
            }
            foreach (var raw in entries.ToArray())
            {
                var entry = AsDictionary(raw);
                var requestId = AdmissionText(entry, "requestId");
                var owner = ReadCompletionIntentOwner(entry);
                if (owner == null || !HasAdmissionCompletionIntent(ProductionQueueState, owner)) { admissionPool.BlockRecovery(); return; }
                var exact = admissionPool.InspectExactIdentityFields(ToPoolIdentity(owner));
                if (exact == UnityCliAdmissionPool.ExactIdentityOutcome.IdentityMismatchOrBlocked) { admissionPool.BlockRecovery(); return; }
                var outcome = exact == UnityCliAdmissionPool.ExactIdentityOutcome.ExactCurrent
                    ? admissionPool.CompleteTrusted(ToPoolIdentity(owner))
                    : UnityCliAdmissionPool.CompletionOutcome.DefinitelyAbsent;
                if (outcome != UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted && outcome != UnityCliAdmissionPool.CompletionOutcome.DefinitelyAbsent)
                { admissionPool.BlockRecovery(); return; }
                try
                {
                    if (!PersistProductionAdmissionPoolState() || !ClearProductionAdmissionOwnerMarkers(owner) || !ClearProductionAdmissionCompletionIntent(owner))
                    { admissionPool.BlockRecovery(); return; }
                }
                catch { admissionPool.BlockRecovery(); return; }
                WriteLog("admission_settlement_replayed requestId=" + requestId);
            }
        }

        private static AdmissionOwnerIdentity ReadCompletionIntentOwner(IDictionary<string, object> entry)
        {
            if (entry == null || !AdmissionDigestIsValid(AdmissionText(entry, "requestDigest"))) return null;
            var source = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(entry, "admissionOwnerState");
            if (!Enum.IsDefined(typeof(UnityCliAdmissionPool.AdmissionOwnerState), source) || source == UnityCliAdmissionPool.AdmissionOwnerState.Granted) return null;
            return new AdmissionOwnerIdentity
            {
                RequestId = AdmissionText(entry, "requestId"), RequestDigest = AdmissionText(entry, "admissionRequestDigest"), TopRequestDigest = AdmissionText(entry, "requestDigest"),
                RootFingerprint = AdmissionText(entry, "admissionRootFingerprint"), Token = AdmissionText(entry, "admissionToken"), SessionId = AdmissionText(entry, "admissionSessionId"),
                ListenerId = AdmissionText(entry, "admissionListenerId"), ListenerGeneration = AdmissionLong(entry, "admissionListenerGeneration"),
                ExecutionDeadlineMs = AdmissionLong(entry, "admissionExecutionDeadlineMs"), EditorSessionId = AdmissionText(entry, "editorSessionId"), OwnerState = source
            };
        }

        private static void ShutdownAdmissionPool()
        {
            if (admissionPool != null) admissionPool.Reset();
            admissionPool = null;
            SessionState.EraseString(AdmissionSessionStateKey);
        }

        /// <summary>HTTP后台线程原子发grant；不进入主线程或transaction log。</summary>
        private static string ExecuteAdmissionAcquire(byte[] rawBody, out string token)
        {
            token = null;
            var value = StrictAdmissionObject(rawBody, "acquire", "protocolVersion", "requestId", "instanceId", "target", "command", "rootFingerprint");
            var requestId = RequiredText(value, "requestId", 200);
            if (AdmissionLong(value, "protocolVersion") != 1L || AdmissionText(value, "instanceId") != InstanceId() || AdmissionText(value, "target") != "editor")
                throw new BridgeCommandException("E_INSTANCE_IDENTITY_MISMATCH", "Admission acquire identity does not match this Editor session.");
            RequiredText(value, "command", 256);
            var fingerprint = RequiredText(value, "rootFingerprint", 64);
            EnsureAdmissionPool();
            var result = admissionPool.Acquire(requestId, fingerprint, ListenerId, CurrentListenerGeneration());
            if (result.Outcome == UnityCliAdmissionPool.AcquireOutcome.Invalid)
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Admission acquire fields are invalid.");
            if (result.Outcome == UnityCliAdmissionPool.AcquireOutcome.IdentityMismatch)
                throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Admission requestId is already bound to different business input.");
            if (result.Outcome == UnityCliAdmissionPool.AcquireOutcome.AlreadyConsumed)
                throw new BridgeCommandException("E_ADMISSION_ALREADY_CONSUMED", "Admission root is already consumed and must use exact attach or terminal recovery.");
            var granted = result.Outcome == UnityCliAdmissionPool.AcquireOutcome.Granted;
            if (granted) token = result.Token;
            if (granted) WriteLog((result.NewlyGranted ? "admission_granted" : "admission_attached") + " requestId=" + requestId + " active=" + admissionPool.Capture().Active.ToString(CultureInfo.InvariantCulture));
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Success(requestId, new Dictionary<string, object>
            {
                { "protocolVersion", 1L }, { "granted", granted },
                { "grantExpiresInMs", (long)(granted ? result.GrantExpiresInMs : 0) },
                { "retryAfterMs", (long)result.RetryAfterMs }
            }));
        }

        private static string ExecuteAdmissionRevoke(byte[] rawBody, HttpListenerRequest httpRequest)
        {
            var value = StrictAdmissionObject(rawBody, "revoke", "protocolVersion", "requestId", "instanceId", "target", "rootFingerprint");
            var requestId = RequiredText(value, "requestId", 200);
            if (AdmissionLong(value, "protocolVersion") != 1L || AdmissionText(value, "instanceId") != InstanceId() || AdmissionText(value, "target") != "editor")
                throw new BridgeCommandException("E_INSTANCE_IDENTITY_MISMATCH", "Admission revoke identity does not match this Editor session.");
            EnsureAdmissionPool();
            var fingerprint = RequiredText(value, "rootFingerprint", 64);
            var revoked = admissionPool.Revoke(requestId, fingerprint, RequiredReleaseToken(httpRequest, requestId, fingerprint));
            if (!revoked) throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission revoke identity does not match an unconsumed root.");
            WriteLog("admission_revoked requestId=" + requestId + " active=" + admissionPool.Capture().Active.ToString(CultureInfo.InvariantCulture));
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Success(requestId, new Dictionary<string, object> { { "revoked", true } }));
        }

        /// <summary>只解析HTTP immutable admission输入；调用方在QueueLock内调用AuthorizeAdmissionForQueue完成pool线性化。</summary>
        internal static AdmissionQueueReservation PrepareAdmissionForQueue(HttpListenerRequest httpRequest, UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes)
        {
            var rootId = httpRequest.Headers["x-unity-cli-admission-root-id"];
            var fingerprint = httpRequest.Headers["x-unity-cli-admission-fingerprint"];
            var token = httpRequest.Headers["x-unity-cli-admission-token"];
            var deadlineText = httpRequest.Headers["x-unity-cli-admission-deadline-utc"];
            var reservation = new AdmissionQueueReservation
            {
                RequestId = request.RequestId,
                RequestDigest = UnityCliRequestDigest.Compute(rawRequestBytes, request.InstanceId),
                RootFingerprint = fingerprint,
                Token = token,
                HasAdmissionHeaders = !string.IsNullOrEmpty(rootId) || !string.IsNullOrEmpty(fingerprint) || !string.IsNullOrEmpty(token) || !string.IsNullOrEmpty(deadlineText),
                IsBypass = AdmissionBypassCommand(request.Command),
                HasExactTerminal = AdmissionHasExactTerminal(request, rawRequestBytes)
            };
            if (!reservation.HasAdmissionHeaders) return reservation;
            if (string.IsNullOrEmpty(rootId) || string.IsNullOrEmpty(fingerprint) || string.IsNullOrEmpty(deadlineText))
                throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission root headers are incomplete.");
            if (!string.Equals(rootId, request.RequestId, StringComparison.Ordinal))
                throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission root requestId does not match the Bridge request body.");
            DateTimeOffset deadline;
            if (!DateTimeOffset.TryParse(deadlineText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out deadline))
                throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission execution deadline is invalid.");
            reservation.ExecutionDeadlineMs = deadline.ToUnixTimeMilliseconds();
            if (reservation.ExecutionDeadlineMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission execution deadline has elapsed.");
            reservation.RootFingerprint = fingerprint;
            reservation.Token = token;
            return reservation;
        }

        /// <summary>
        /// QueueLock是外层锁，admission pool锁是内层锁；本方法只操作内存pool，不读取文件或SessionState。
        /// </summary>
        internal static void AuthorizeAdmissionForQueue(AdmissionQueueReservation reservation, UnityCli.Protocol.BridgeRequest request)
        {
            AuthorizeAdmissionForQueue(ProductionQueueState, reservation, request);
        }

        internal static void AuthorizeAdmissionForQueue(BridgeQueueState state, AdmissionQueueReservation reservation, UnityCli.Protocol.BridgeRequest request)
        {
            if (state == null || reservation == null || request == null) throw new ArgumentNullException("reservation");
            if (!reservation.HasAdmissionHeaders)
            {
                if (reservation.IsBypass) return;
                throw new BridgeCommandException("E_ADMISSION_REQUIRED", "A valid Unity task-pool admission is required before this business request can enter the queue.");
            }
            var pool = state.AdmissionPool;
            if (pool == null) throw new BridgeCommandException("E_RUNTIME_SESSION_STALE", "The Unity session ended before admission completed.");
            if (!string.IsNullOrEmpty(reservation.Token))
            {
                var result = pool.AuthorizeAndCapture(reservation.RequestId, reservation.RootFingerprint, reservation.RequestDigest, reservation.Token, state.ListenerId ?? ListenerId, state.ListenerGeneration, reservation.ExecutionDeadlineMs);
                if (result.Outcome == UnityCliAdmissionPool.AuthorizeOutcome.Authorized
                    || result.Outcome == UnityCliAdmissionPool.AuthorizeOutcome.AlreadyConsumed)
                {
                    reservation.Owner = ToAdmissionOwner(result.Identity, reservation.RequestDigest);
                    reservation.NewlyConsumed = result.NewlyConsumed;
                    return;
                }
                throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission token is missing, expired, reused, or bound to another root.");
            }
            if (pool.AttachConsumed(reservation.RequestId, reservation.RootFingerprint, reservation.RequestDigest, state.ListenerId ?? ListenerId, state.ListenerGeneration, reservation.ExecutionDeadlineMs))
            {
                reservation.Owner = CaptureAdmissionOwner(state, reservation.RequestId, reservation.RequestDigest);
                return;
            }
            if (AdmissionRecoveryCommand(request.Command) && pool.RecoverConsumed(reservation.RequestId, reservation.RootFingerprint, reservation.RequestDigest, state.ListenerId ?? ListenerId, state.ListenerGeneration, reservation.ExecutionDeadlineMs))
            {
                reservation.Owner = CaptureAdmissionOwner(state, reservation.RequestId, reservation.RequestDigest);
                return;
            }
            if (reservation.HasExactTerminal) return;
            throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Consumed admission root cannot attach to this listener generation.");
        }

        /// <summary>pool memory已完成但durable settlement尚未清理时，同字节duplicate只附着原WorkItem。</summary>
        private static bool TryAuthorizeCompletionPendingAttach(BridgeQueueState state, AdmissionQueueReservation reservation, BridgeWorkItem existing)
        {
            var owner = existing == null ? null : existing.AdmissionOwner;
            if (state == null || reservation == null || owner == null || !existing.CompletionIntentReady
                || !HasAdmissionCompletionIntent(state, owner) || !reservation.HasAdmissionHeaders
                || !string.Equals(reservation.RequestId, owner.RequestId, StringComparison.Ordinal)
                || !string.Equals(reservation.RequestDigest, owner.RequestDigest, StringComparison.Ordinal)
                || !string.Equals(reservation.RootFingerprint, owner.RootFingerprint, StringComparison.Ordinal)
                || reservation.ExecutionDeadlineMs != owner.ExecutionDeadlineMs
                || (!string.IsNullOrEmpty(reservation.Token) && !string.Equals(reservation.Token, owner.Token, StringComparison.Ordinal))) return false;
            reservation.Owner = owner;
            reservation.NewlyConsumed = false;
            return true;
        }

        internal static void MarkAdmissionQueuePublished(AdmissionQueueReservation reservation)
        {
            if (reservation != null) reservation.QueuePublished = true;
        }

        /// <summary>publication前补偿只修改pool内存；持久化与日志由QueueLock外调用方完成。</summary>
        internal static bool CompensateAdmissionBeforeQueuePublication(AdmissionQueueReservation reservation)
        {
            return CompensateAdmissionBeforeQueuePublication(ProductionQueueState, reservation);
        }

        internal static bool CompensateAdmissionBeforeQueuePublication(BridgeQueueState state, AdmissionQueueReservation reservation)
        {
            if (state == null || reservation == null || reservation.QueuePublished || !reservation.NewlyConsumed || reservation.Owner == null) return true;
            var pool = state.AdmissionPool;
            var compensated = pool != null && pool.CompensateAuthorization(ToPoolIdentity(reservation.Owner));
            if (compensated) reservation.NewlyConsumed = false;
            return compensated;
        }

        /// <summary>只按当前pool中的完整digest冻结exact owner；缺失与identity冲突保持不同结果。</summary>
        internal static bool TryCaptureExactAdmissionOwner(string requestId, string requestDigest, out AdmissionOwnerIdentity identity)
        {
            return TryCaptureExactAdmissionOwner(ProductionQueueState, requestId, requestDigest, out identity);
        }

        internal static bool TryCaptureExactAdmissionOwner(BridgeQueueState state, string requestId, string requestDigest, out AdmissionOwnerIdentity identity)
        {
            identity = null;
            UnityCliAdmissionPool.ConsumedIdentity current;
            var pool = state == null ? null : state.AdmissionPool;
            if (pool == null || !pool.TryGetConsumedIdentity(requestId, out current)) return false;
            identity = ToAdmissionOwner(current, requestDigest);
            return true;
        }

        internal static AdmissionOwnerIdentity CaptureAdmissionOwner(string requestId, string requestDigest = null)
        {
            return CaptureAdmissionOwner(ProductionQueueState, requestId, requestDigest);
        }

        internal static AdmissionOwnerIdentity CaptureAdmissionOwner(BridgeQueueState state, string requestId, string requestDigest = null)
        {
            UnityCliAdmissionPool.ConsumedIdentity identity;
            var pool = state == null ? null : state.AdmissionPool;
            if (pool == null || !pool.TryGetConsumedIdentity(requestId, out identity))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner identity cannot be frozen after consumption.");
            return ToAdmissionOwner(identity, requestDigest);
        }

        /// <summary>先落exact completion intent，再在state锁内完成pool memory与map裁决。</summary>
        internal static bool CompleteAdmissionRoot(AdmissionOwnerIdentity identity)
        {
            return CompleteAdmissionRoot(ProductionQueueState, identity);
        }

        internal static bool CompleteAdmissionRoot(BridgeQueueState state, AdmissionOwnerIdentity identity)
        {
            if (state == null || identity == null) return false;
            if (!HasAdmissionCompletionIntent(state, identity) && !PersistAdmissionCompletionIntent(state, identity)) return false;
            var pool = state.AdmissionPool;
            if (pool == null) return false;
            UnityCliAdmissionPool.CompletionOutcome outcome;
            lock (state.QueueLock)
            {
                BridgeWorkItem existing;
                if (state.ActiveRequestWork.TryGetValue(identity.RequestId, out existing)
                    && existing.AdmissionOwner != null
                    && SameAdmissionOwner(existing.AdmissionOwner, identity)) return false;
                outcome = pool.CompleteTrusted(ToPoolIdentity(identity));
            }
            if (outcome != UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted
                && outcome != UnityCliAdmissionPool.CompletionOutcome.DefinitelyAbsent) return false;
            try
            {
                if (!state.PersistPool()) return false;
                if (!state.ClearMarkers(identity)) return false;
                if (!ClearAdmissionCompletionIntent(state, identity)) return false;
            }
            catch { return false; }
            state.Sinks.WriteLog("admission_terminal requestId=" + identity.RequestId + " outcome=" + outcome.ToString() + " active=" + pool.Capture().Active.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>仅为将随父root落盘的continuation写入完整intent；pool transfer由队列terminal owner提交。</summary>
        internal static void AddAdmissionOwnerFields(IDictionary<string, object> state, string requestId, UnityCliAdmissionPool.AdmissionOwnerState intendedOwner = UnityCliAdmissionPool.AdmissionOwnerState.Continuation) { AddAdmissionOwnerFields(state, CaptureAdmissionOwner(requestId), intendedOwner); }

        internal static void AddAdmissionOwnerFields(IDictionary<string, object> state, AdmissionOwnerIdentity identity, UnityCliAdmissionPool.AdmissionOwnerState intendedOwner = UnityCliAdmissionPool.AdmissionOwnerState.Continuation)
        {
            if (state == null || identity == null) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner marker identity is unavailable.");
            WriteAdmissionOwnerFields(state, identity, intendedOwner, identity.OwnerState == intendedOwner);
        }

        internal static AdmissionOwnerIdentity ReadAdmissionOwnerFields(IDictionary<string, object> state, string requestId)
        {
            if (state == null || string.IsNullOrEmpty(requestId)) return null;
            var committed = AdmissionBool(state, "admissionTransferCommitted");
            var source = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(state, "admissionOwnerState");
            var intended = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(state, "admissionIntendedOwnerState");
            var topDigest = AdmissionText(state, "requestDigest");
            if (!AdmissionDigestIsValid(topDigest)) topDigest = AdmissionText(state, "cancelRootRequestDigest");
            if (!AdmissionDigestIsValid(topDigest)) topDigest = AdmissionText(state, "admissionRequestDigest");
            return new AdmissionOwnerIdentity
            {
                RequestId = requestId,
                RequestDigest = AdmissionText(state, "admissionRequestDigest"),
                TopRequestDigest = topDigest,
                RootFingerprint = AdmissionText(state, "admissionRootFingerprint"),
                Token = AdmissionText(state, "admissionToken"),
                SessionId = AdmissionText(state, "admissionSessionId"),
                ListenerId = AdmissionText(state, "admissionListenerId"),
                ListenerGeneration = AdmissionLong(state, "admissionListenerGeneration"),
                ExecutionDeadlineMs = AdmissionLong(state, "admissionExecutionDeadlineMs"),
                EditorSessionId = AdmissionText(state, "editorSessionId"),
                OwnerState = committed ? intended : source
            };
        }

        /// <summary>三段式owner transfer：先落完整intent，再exact转pool，最后落committed marker。</summary>
        internal static AdmissionOwnerIdentity PersistAdmissionOwnerTransfer(string markerScope, IDictionary<string, object> marker, AdmissionOwnerIdentity identity, UnityCliAdmissionPool.AdmissionOwnerState intendedOwner)
        {
            return PersistAdmissionOwnerTransfer(ProductionQueueState, markerScope, marker, identity, intendedOwner);
        }

        internal static AdmissionOwnerIdentity PersistAdmissionOwnerTransfer(BridgeQueueState state, string markerScope, IDictionary<string, object> marker, AdmissionOwnerIdentity identity, UnityCliAdmissionPool.AdmissionOwnerState intendedOwner)
        {
            if (state == null || marker == null || identity == null || string.IsNullOrEmpty(markerScope)) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission transfer marker is unavailable.");
            var pool = state.AdmissionPool;
            if (pool == null) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission pool is unavailable during owner transfer.");
            var existing = state.LoadOperation(markerScope);
            if (existing != null && !string.Equals(AdmissionText(existing, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal))
            {
                state.ClearOperation(markerScope);
                existing = null;
            }
            if (existing != null && !AdmissionMarkerMatchesIdentity(existing, identity) && !AdmissionMarkerCanAcquireOwner(existing, identity))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission transfer marker is owned by another exact request.");
            marker["requestId"] = identity.RequestId;
            marker["requestDigest"] = AdmissionTopDigest(identity);
            marker["editorSessionId"] = identity.EditorSessionId ?? EditorSessionId();
            WriteAdmissionOwnerFields(marker, identity, intendedOwner, false);
            state.SaveOperation(markerScope, new Dictionary<string, object>(marker));
            if (!pool.PrepareOwnerTransfer(ToPoolIdentity(identity), intendedOwner))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner transfer intent does not match the current exact owner.");
            if (!PersistAdmissionPoolState(state)) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner transfer intent could not be durably persisted.");
            UnityCliAdmissionPool.ConsumedIdentity successor;
            if (!pool.CommitOwnerTransfer(ToPoolIdentity(identity), intendedOwner, out successor))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner transfer could not be committed.");
            var updated = ToAdmissionOwner(successor, identity.RequestDigest);
            updated.TopRequestDigest = AdmissionText(marker, "requestDigest");
            updated.EditorSessionId = AdmissionText(marker, "editorSessionId");
            marker["admissionTransferCommitted"] = true;
            state.SaveOperation(markerScope, new Dictionary<string, object>(marker));
            if (!PersistAdmissionPoolState(state)) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner transfer commit could not be durably persisted.");
            return updated;
        }

        /// <summary>重试修复transfer窗口；pool确定缺失时仍先持久化当前SessionState再交给fixed marker清理。</summary>
        internal static AdmissionOwnerIdentity ReconcileAdmissionTransferForLiveMarker(string markerScope, Dictionary<string, object> marker, string requestId, string requestDigest)
        {
            return ReconcileAdmissionTransferForLiveMarker(ProductionQueueState, markerScope, marker, requestId, requestDigest);
        }

        internal static AdmissionOwnerIdentity ReconcileAdmissionTransferForLiveMarker(BridgeQueueState state, string markerScope, Dictionary<string, object> marker, string requestId, string requestDigest)
        {
            if (string.Equals(markerScope, "test-cancel-admission-owner", StringComparison.Ordinal)
                || string.Equals(markerScope, "test-cancel-terminal-cleanup", StringComparison.Ordinal))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Test cancellation admission markers require dedicated parent and cancel identity reconciliation.");
            var markerOwner = ReadAdmissionOwnerFields(marker, requestId);
            var pool = state == null ? null : state.AdmissionPool;
            if (!AdmissionMarkerHasImmutableIdentity(marker, markerOwner, AdmissionTopDigest(markerOwner)) || pool == null)
            {
                if (pool != null) pool.BlockRecovery();
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission cleanup marker identity is invalid.");
            }
            var exact = pool.InspectExactIdentityFields(ToPoolIdentity(markerOwner));
            if (exact == UnityCliAdmissionPool.ExactIdentityOutcome.DefinitelyAbsent)
            {
                try
                {
                    if (!RestoreAbsentAdmissionOwner(state, markerScope, marker, markerOwner)) return null;
                    exact = UnityCliAdmissionPool.ExactIdentityOutcome.ExactCurrent;
                }
                catch
                {
                    pool.BlockRecovery();
                    throw;
                }
            }
            if (exact != UnityCliAdmissionPool.ExactIdentityOutcome.ExactCurrent)
            {
                pool.BlockRecovery();
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission cleanup marker conflicts with the current requestId owner.");
            }
            var current = CaptureAdmissionOwner(state, requestId, markerOwner.RequestDigest);
            current.TopRequestDigest = AdmissionText(marker, "requestDigest");
            current.EditorSessionId = AdmissionText(marker, "editorSessionId");
            var intended = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionIntendedOwnerState");
            if (current.OwnerState != intended) return PersistAdmissionOwnerTransfer(state, markerScope, marker, current, intended);
            WriteAdmissionOwnerFields(marker, current, intended, true);
            state.SaveOperation(markerScope, marker);
            if (!PersistAdmissionPoolState(state)) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission marker reconciliation persistence was not confirmed.");
            return current;
        }

        /// <summary>启动时按marker、对应root与lease三者重建或清理owner；pool缺失不能单独证明终态。</summary>
        private static void ReconcileOrphanAdmissionOwnerMarkers()
        {
            var pool = admissionPool;
            if (pool == null) return;
            foreach (var scope in AdmissionDurableScopes)
            {
                if (scope == "test-cancel") continue;
                var markerScope = scope + "-admission-owner";
                if (!UnityCliActiveOperation.Exists(markerScope)) continue;
                Dictionary<string, object> marker;
                try { marker = UnityCliActiveOperation.Load(markerScope); }
                catch { pool.BlockRecovery(); return; }
                if (marker == null) continue;
                if (!string.Equals(AdmissionText(marker, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal))
                {
                    try { UnityCliActiveOperation.Clear(markerScope); } catch { pool.BlockRecovery(); return; }
                    continue;
                }
                AdmissionOwnerIdentity markerOwner;
                try { markerOwner = ReadAdmissionOwnerFields(marker, AdmissionText(marker, "requestId")); }
                catch { pool.BlockRecovery(); return; }
                if (!AdmissionMarkerHasImmutableIdentity(marker, markerOwner, AdmissionTopDigest(markerOwner))) { pool.BlockRecovery(); return; }
                var exact = pool.InspectExactIdentityFields(ToPoolIdentity(markerOwner));
                if (exact == UnityCliAdmissionPool.ExactIdentityOutcome.IdentityMismatchOrBlocked) { pool.BlockRecovery(); return; }
                if (exact != UnityCliAdmissionPool.ExactIdentityOutcome.DefinitelyAbsent) continue;
                try { RestoreAbsentAdmissionOwner(markerScope, marker, markerOwner); }
                catch { pool.BlockRecovery(); return; }
            }
        }

        /// <summary>pool缺失时，cleanup marker可覆盖root/lease清理窗口；普通owner marker只接受完整root+lease重建。</summary>
        private static bool RestoreAbsentAdmissionOwner(string markerScope, Dictionary<string, object> marker, AdmissionOwnerIdentity markerOwner)
        {
            return RestoreAbsentAdmissionOwner(ProductionQueueState, markerScope, marker, markerOwner);
        }

        private static bool RestoreAbsentAdmissionOwner(BridgeQueueState state, string markerScope, Dictionary<string, object> marker, AdmissionOwnerIdentity markerOwner)
        {
            bool rootReadable;
            var rootExists = HasExactAdmissionMarkerRoot(state, markerScope, markerOwner, out rootReadable);
            if (!rootReadable) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission cleanup marker root cannot be verified.");
            bool leaseOwned;
            if (!state.TryIsLeaseOwned(markerOwner.RequestId, markerOwner.RequestDigest, out leaseOwned))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission cleanup marker lease ownership cannot be verified.");
            var cleanupMarker = markerScope.EndsWith("-terminal-cleanup", StringComparison.Ordinal)
                || markerScope.EndsWith("-pre-effect-cleanup", StringComparison.Ordinal);
            if (rootExists || leaseOwned)
            {
                if (!cleanupMarker && (!rootExists || !leaseOwned))
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner marker has only a partial root and lease proof.");
                var intended = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionIntendedOwnerState");
                var pool = state == null ? null : state.AdmissionPool;
                if (pool == null || !pool.RestoreConsumed(ToPoolIdentity(markerOwner), intended, AdmissionBool(marker, "admissionTransferCommitted")))
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission cleanup marker could not rebuild its exact consumed owner.");
                if (!PersistAdmissionPoolState(state)) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission cleanup marker restore was not durably persisted.");
                return true;
            }
            if (!cleanupMarker && !AdmissionOwnerTerminalExists(state, markerScope, markerOwner))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner marker has no exact cleanup completion proof.");
            state.ClearOperation(markerScope);
            if (state.OperationExists(markerScope))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Completed admission cleanup marker could not be cleared.");
            return false;
        }

        private static bool AdmissionOwnerTerminalExists(BridgeQueueState state, string markerScope, AdmissionOwnerIdentity owner)
        {
            try
            {
                var terminalScope = markerScope.EndsWith("-admission-owner", StringComparison.Ordinal)
                    ? markerScope.Substring(0, markerScope.Length - "-admission-owner".Length)
                    : markerScope;
                object terminal;
                return state.TryGetTerminal(terminalScope, owner.RequestId, AdmissionTopDigest(owner), out terminal);
            }
            catch { return false; }
        }

        private static bool HasExactAdmissionMarkerRoot(BridgeQueueState state, string markerScope, AdmissionOwnerIdentity owner, out bool readable)
        {
            readable = true;
            try
            {
                var rootScope = markerScope.EndsWith("-admission-owner", StringComparison.Ordinal)
                    ? markerScope.Substring(0, markerScope.Length - "-admission-owner".Length)
                    : markerScope;
                if (rootScope.EndsWith("-terminal-cleanup", StringComparison.Ordinal)) rootScope = rootScope.Substring(0, rootScope.Length - "-terminal-cleanup".Length);
                if (rootScope.EndsWith("-pre-effect-cleanup", StringComparison.Ordinal)) rootScope = rootScope.Substring(0, rootScope.Length - "-pre-effect-cleanup".Length);
                var root = state.LoadOperation(rootScope);
                if (root == null || !string.Equals(AdmissionText(root, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) return false;
                return string.Equals(AdmissionText(root, "requestId"), owner.RequestId, StringComparison.Ordinal)
                    && string.Equals(AdmissionText(root, "requestDigest"), owner.RequestDigest, StringComparison.Ordinal);
            }
            catch { readable = false; return false; }
        }

        /// <summary>reload只按完整marker identity修复intent/commit；requestId单字段从不构成owner证明。</summary>
        private static void ReconcileRestoredAdmissionRoots()
        {
            if (admissionPool == null) return;
            foreach (var captured in admissionPool.Capture().Records)
            {
                var record = captured;
                if (DedicatedCancelMarkerOwnsRecord(record)) continue;
                Dictionary<string, object> marker;
                string markerScope;
                try
                {
                    if (TryFindExactAdmissionMarker(record, out markerScope, out marker))
                    {
                        ReconcileAdmissionMarker(record, markerScope, marker);
                        continue;
                    }
                    if (record.OwnerState != UnityCliAdmissionPool.AdmissionOwnerState.WorkItem
                        && record.OwnerState != UnityCliAdmissionPool.AdmissionOwnerState.Recovery)
                    {
                        admissionPool.BlockRecovery();
                        return;
                    }
                    var owner = RecordIdentity(record);
                    string durableScope;
                    UnityCliAdmissionPool.AdmissionOwnerState durableOwner;
                    if (TryFindExactDurableBusinessRoot(record, out durableScope, out durableOwner))
                    {
                        var markerState = new Dictionary<string, object>
                        {
                            { "requestId", record.RequestId }, { "requestDigest", record.RequestDigest }, { "editorSessionId", EditorSessionId() }
                        };
                        PersistAdmissionOwnerTransfer(durableScope + "-admission-owner", markerState, owner,
                            owner.OwnerState == UnityCliAdmissionPool.AdmissionOwnerState.WorkItem ? durableOwner : UnityCliAdmissionPool.AdmissionOwnerState.Recovery);
                        continue;
                    }
                    if (owner.OwnerState == UnityCliAdmissionPool.AdmissionOwnerState.WorkItem)
                        owner = TransitionWithoutMarker(owner, UnityCliAdmissionPool.AdmissionOwnerState.Recovery);
                    owner = TransitionWithoutMarker(owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
                    if (!CompleteAdmissionRoot(owner)) { admissionPool.BlockRecovery(); return; }
                }
                catch (Exception exception)
                {
                    admissionPool.BlockRecovery();
                    WriteLog("admission_reconcile_failed requestId=" + record.RequestId + " " + exception.GetType().Name + ": " + exception.Message);
                    return;
                }
            }
        }

        private static bool DedicatedCancelMarkerOwnsRecord(UnityCliAdmissionPool.AdmissionRecord record)
        {
            const string markerScope = "test-cancel-admission-owner";
            if (record == null || !UnityCliActiveOperation.Exists(markerScope)) return false;
            try
            {
                var marker = UnityCliActiveOperation.Load(markerScope);
                return marker != null && string.Equals(AdmissionText(marker, "requestId"), record.RequestId, StringComparison.Ordinal);
            }
            catch { return true; }
        }

        private static AdmissionOwnerIdentity TransitionWithoutMarker(AdmissionOwnerIdentity identity, UnityCliAdmissionPool.AdmissionOwnerState target)
        {
            if (!admissionPool.PrepareOwnerTransfer(ToPoolIdentity(identity), target)) throw new InvalidOperationException("Admission recovery transfer intent failed.");
            PersistAdmissionPoolState();
            UnityCliAdmissionPool.ConsumedIdentity successor;
            if (!admissionPool.CommitOwnerTransfer(ToPoolIdentity(identity), target, out successor)) throw new InvalidOperationException("Admission recovery transfer commit failed.");
            PersistAdmissionPoolState();
            return ToAdmissionOwner(successor, identity.RequestDigest);
        }

        private static bool TryFindExactDurableBusinessRoot(UnityCliAdmissionPool.AdmissionRecord record, out string markerScope, out UnityCliAdmissionPool.AdmissionOwnerState intendedOwner)
        {
            markerScope = null;
            intendedOwner = UnityCliAdmissionPool.AdmissionOwnerState.Recovery;
            foreach (var scope in AdmissionDurableScopes)
            {
                if (scope == "test-cancel") continue;
                var state = UnityCliActiveOperation.Load(scope);
                if (state == null || !string.Equals(AdmissionText(state, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) continue;
                if (string.Equals(AdmissionText(state, "requestId"), record.RequestId, StringComparison.Ordinal)
                    && string.Equals(AdmissionText(state, "requestDigest"), record.RequestDigest, StringComparison.Ordinal))
                {
                    markerScope = scope;
                    intendedOwner = scope == "direct-mutation" ? UnityCliAdmissionPool.AdmissionOwnerState.Recovery : UnityCliAdmissionPool.AdmissionOwnerState.Coordinator;
                    return true;
                }
            }
            return false;
        }

        private static bool TryFindExactAdmissionMarker(UnityCliAdmissionPool.AdmissionRecord record, out string markerScope, out Dictionary<string, object> marker)
        {
            markerScope = null;
            marker = null;
            foreach (var scope in AdmissionDurableScopes)
            {
                if (scope == "test-cancel") continue;
                foreach (var candidate in new[] { scope + "-terminal-cleanup", scope + "-pre-effect-cleanup", scope + "-admission-owner" })
                {
                    if (!UnityCliActiveOperation.Exists(candidate)) continue;
                    var value = UnityCliActiveOperation.Load(candidate);
                    if (!AdmissionMarkerMatchesRecord(value, record)) continue;
                    markerScope = candidate;
                    marker = value;
                    return true;
                }
            }
            return false;
        }

        private static void ReconcileAdmissionMarker(UnityCliAdmissionPool.AdmissionRecord record, string markerScope, Dictionary<string, object> marker)
        {
            var source = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionOwnerState");
            var intended = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionIntendedOwnerState");
            var current = RecordIdentity(record);
            if (current.OwnerState != intended)
            {
                if (current.OwnerState != source || !admissionPool.PrepareOwnerTransfer(ToPoolIdentity(current), intended))
                    throw new InvalidOperationException("Admission marker source does not match the restored owner.");
                PersistAdmissionPoolState();
                UnityCliAdmissionPool.ConsumedIdentity successor;
                if (!admissionPool.CommitOwnerTransfer(ToPoolIdentity(current), intended, out successor))
                    throw new InvalidOperationException("Admission marker transfer cannot be repaired.");
                current = ToAdmissionOwner(successor, AdmissionText(marker, "admissionRequestDigest"));
            }
            current.TopRequestDigest = AdmissionText(marker, "requestDigest");
            current.EditorSessionId = AdmissionText(marker, "editorSessionId");
            WriteAdmissionOwnerFields(marker, current, intended, true);
            UnityCliActiveOperation.Save(markerScope, marker);
            PersistAdmissionPoolState();
        }

        private static bool AdmissionMarkerCanAcquireOwner(IDictionary<string, object> marker, AdmissionOwnerIdentity identity)
        {
            return marker != null && identity != null && string.IsNullOrEmpty(AdmissionText(marker, "admissionRootFingerprint"))
                && string.Equals(AdmissionText(marker, "requestId"), identity.RequestId, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "requestDigest"), AdmissionTopDigest(identity), StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal);
        }

        private static bool AdmissionMarkerMatchesIdentity(IDictionary<string, object> marker, AdmissionOwnerIdentity identity)
        {
            return AdmissionMarkerIdentityIsValid(marker, identity, AdmissionTopDigest(identity));
        }

        /// <summary>marker与pool snapshot只按完整immutable identity及合法三阶段状态匹配。</summary>
        private static bool AdmissionMarkerMatchesRecord(IDictionary<string, object> marker, UnityCliAdmissionPool.AdmissionRecord record)
        {
            if (marker == null || record == null || !AdmissionDigestIsValid(AdmissionText(marker, "requestDigest"))) return false;
            var source = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionOwnerState");
            var intended = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionIntendedOwnerState");
            object committedValue;
            if (!marker.TryGetValue("admissionTransferCommitted", out committedValue) || !(committedValue is bool)
                || source == UnityCliAdmissionPool.AdmissionOwnerState.Granted || intended == UnityCliAdmissionPool.AdmissionOwnerState.Granted
                || !Enum.IsDefined(typeof(UnityCliAdmissionPool.AdmissionOwnerState), source)
                || !Enum.IsDefined(typeof(UnityCliAdmissionPool.AdmissionOwnerState), intended)
                || !UnityCliAdmissionPool.IsLegalOwnerTransfer(source, intended)) return false;
            if (!string.Equals(AdmissionText(marker, "requestId"), record.RequestId, StringComparison.Ordinal)
                || !string.Equals(AdmissionText(marker, "admissionRequestDigest"), record.RequestDigest, StringComparison.Ordinal)
                || !string.Equals(AdmissionText(marker, "admissionRootFingerprint"), record.RootFingerprint, StringComparison.Ordinal)
                || !string.Equals(AdmissionText(marker, "admissionToken"), record.Token, StringComparison.Ordinal)
                || !string.Equals(AdmissionText(marker, "admissionSessionId"), record.SessionId, StringComparison.Ordinal)
                || AdmissionLong(marker, "admissionExecutionDeadlineMs") != record.ExecutionDeadlineMs
                || !string.Equals(AdmissionText(marker, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) return false;
            var sourceCommitted = record.TransferCommitted && record.OwnerState == source && record.IntendedOwnerState == source;
            var sourcePending = !record.TransferCommitted && record.OwnerState == source && record.IntendedOwnerState == intended;
            var intendedCommitted = record.TransferCommitted && record.OwnerState == intended && record.IntendedOwnerState == intended;
            return sourceCommitted || sourcePending || intendedCommitted;
        }

        private static bool AdmissionMarkerIdentityIsValid(IDictionary<string, object> marker, AdmissionOwnerIdentity identity, string topRequestDigest)
        {
            if (!AdmissionMarkerHasImmutableIdentity(marker, identity, topRequestDigest)) return false;
            var source = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionOwnerState");
            var intended = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionIntendedOwnerState");
            return identity.OwnerState == source || identity.OwnerState == intended;
        }

        private static bool AdmissionMarkerHasImmutableIdentity(IDictionary<string, object> marker, AdmissionOwnerIdentity identity, string topRequestDigest)
        {
            if (marker == null || identity == null || !AdmissionDigestIsValid(topRequestDigest)
                || !AdmissionDigestIsValid(identity.RequestDigest)
                || !Enum.IsDefined(typeof(UnityCliAdmissionPool.AdmissionOwnerState), identity.OwnerState)) return false;
            var source = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionOwnerState");
            var intended = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionIntendedOwnerState");
            object committed;
            return marker.TryGetValue("admissionTransferCommitted", out committed) && committed is bool
                && source != UnityCliAdmissionPool.AdmissionOwnerState.Granted
                && intended != UnityCliAdmissionPool.AdmissionOwnerState.Granted
                && Enum.IsDefined(typeof(UnityCliAdmissionPool.AdmissionOwnerState), source)
                && Enum.IsDefined(typeof(UnityCliAdmissionPool.AdmissionOwnerState), intended)
                && UnityCliAdmissionPool.IsLegalOwnerTransfer(source, intended)
                && string.Equals(AdmissionText(marker, "requestId"), identity.RequestId, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "requestDigest"), topRequestDigest, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "admissionRequestDigest"), identity.RequestDigest, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "admissionRootFingerprint"), identity.RootFingerprint, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "admissionToken"), identity.Token, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "admissionSessionId"), identity.SessionId, StringComparison.Ordinal)
                && AdmissionLong(marker, "admissionExecutionDeadlineMs") == identity.ExecutionDeadlineMs
                && string.Equals(AdmissionText(marker, "editorSessionId"), identity.EditorSessionId ?? EditorSessionId(), StringComparison.Ordinal);
        }

        /// <summary>pool删除前先落固定有界completion intent；该intent只记录identity，不重新执行业务effect。</summary>
        internal static bool PersistAdmissionCompletionIntent(BridgeQueueState state, AdmissionOwnerIdentity identity)
        {
            if (state == null || identity == null) return false;
            return state.PersistIntent(identity);
        }

        internal static bool HasAdmissionCompletionIntent(BridgeQueueState state, AdmissionOwnerIdentity identity)
        {
            if (state == null || identity == null) return false;
            return state.HasIntent(identity);
        }

        internal static bool ClearAdmissionCompletionIntent(BridgeQueueState state, AdmissionOwnerIdentity identity)
        {
            if (state == null || identity == null) return false;
            return state.ClearIntent(identity);
        }

        private static bool PersistProductionAdmissionCompletionIntent(AdmissionOwnerIdentity identity, string kind)
        {
            try
            {
                var root = UnityCliActiveOperation.Load(AdmissionSettlementScope);
                if (root == null) root = new Dictionary<string, object> { { "editorSessionId", EditorSessionId() }, { "entries", new List<object>() } };
                if (!string.Equals(AdmissionText(root, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal))
                {
                    root = new Dictionary<string, object> { { "editorSessionId", EditorSessionId() }, { "entries", new List<object>() } };
                }
                var entries = root["entries"] as List<object>;
                if (entries == null || entries.Count > 256) return false;
                if (entries.Cast<object>().Select(AsDictionary).Any(value => value == null)) return false;
                if (entries.Cast<object>().Select(AsDictionary).Any(value => CompletionIntentMatches(value, identity))) return true;
                entries.Add(CompletionIntent(identity, kind));
                UnityCliActiveOperation.Save(AdmissionSettlementScope, root);
                return true;
            }
            catch { return false; }
        }

        private static bool HasProductionAdmissionCompletionIntent(AdmissionOwnerIdentity identity)
        {
            try
            {
                var root = UnityCliActiveOperation.Load(AdmissionSettlementScope);
                var entries = root == null ? null : root.ContainsKey("entries") ? root["entries"] as List<object> : null;
                return string.Equals(AdmissionText(root, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)
                    && entries != null && entries.Cast<object>().Select(AsDictionary).Any(value => value != null && CompletionIntentMatches(value, identity));
            }
            catch { return false; }
        }

        private static bool ClearProductionAdmissionCompletionIntent(AdmissionOwnerIdentity identity)
        {
            try
            {
                var root = UnityCliActiveOperation.Load(AdmissionSettlementScope);
                if (root == null) return true;
                var entries = root["entries"] as List<object>;
                if (entries == null) return false;
                entries.RemoveAll(value => { var entry = AsDictionary(value); return entry != null && CompletionIntentMatches(entry, identity); });
                if (entries.Count == 0) UnityCliActiveOperation.Clear(AdmissionSettlementScope);
                else UnityCliActiveOperation.Save(AdmissionSettlementScope, root);
                return !HasProductionAdmissionCompletionIntent(identity);
            }
            catch { return false; }
        }

        private static Dictionary<string, object> CompletionIntent(AdmissionOwnerIdentity identity, string kind)
        {
            return new Dictionary<string, object>
            {
                { "requestId", identity.RequestId }, { "requestDigest", AdmissionTopDigest(identity) },
                { "kind", kind ?? "admission-completion" }, { "admissionRequestDigest", identity.RequestDigest },
                { "admissionRootFingerprint", identity.RootFingerprint }, { "admissionToken", identity.Token },
                { "admissionSessionId", identity.SessionId }, { "admissionListenerId", identity.ListenerId },
                { "admissionListenerGeneration", identity.ListenerGeneration }, { "admissionExecutionDeadlineMs", identity.ExecutionDeadlineMs },
                { "admissionOwnerState", (long)identity.OwnerState }, { "editorSessionId", identity.EditorSessionId ?? EditorSessionId() }
            };
        }

        private static bool CompletionIntentMatches(IDictionary<string, object> entry, AdmissionOwnerIdentity identity)
        {
            return entry != null && identity != null
                && string.Equals(AdmissionText(entry, "requestId"), identity.RequestId, StringComparison.Ordinal)
                && string.Equals(AdmissionText(entry, "requestDigest"), AdmissionTopDigest(identity), StringComparison.Ordinal)
                && string.Equals(AdmissionText(entry, "admissionRequestDigest"), identity.RequestDigest, StringComparison.Ordinal)
                && string.Equals(AdmissionText(entry, "admissionRootFingerprint"), identity.RootFingerprint, StringComparison.Ordinal)
                && string.Equals(AdmissionText(entry, "admissionToken"), identity.Token, StringComparison.Ordinal)
                && string.Equals(AdmissionText(entry, "admissionSessionId"), identity.SessionId, StringComparison.Ordinal)
                && string.Equals(AdmissionText(entry, "admissionListenerId"), identity.ListenerId, StringComparison.Ordinal)
                && AdmissionLong(entry, "admissionListenerGeneration") == identity.ListenerGeneration
                && AdmissionLong(entry, "admissionExecutionDeadlineMs") == identity.ExecutionDeadlineMs
                && string.Equals(AdmissionText(entry, "editorSessionId"), identity.EditorSessionId ?? EditorSessionId(), StringComparison.Ordinal);
        }

        /// <summary>accepted durable transfer失败时先用WorkItem冻结身份建立Recovery marker；marker失败则保留WorkItem等待重试。</summary>
        private static AdmissionTransferResult SettleAdmissionAfterWorkItem(AdmissionOwnerIdentity identity, string command, string response)
        {
            return SettleAdmissionAfterWorkItem(ProductionQueueState, identity, command, response);
        }

        private static AdmissionTransferResult SettleAdmissionAfterWorkItem(BridgeQueueState state, AdmissionOwnerIdentity identity, string command, string response)
        {
            if (identity == null || string.IsNullOrEmpty(identity.RequestId)) return AdmissionTransferResult.NotAccepted;
            var transfer = TransferAdmissionAfterAcceptedWork(state, identity, command, response);
            if (transfer == AdmissionTransferResult.TransferPending)
                transfer = PersistWorkItemRecoveryAfterTransferFailure(state, identity, command, response);
            if (transfer == AdmissionTransferResult.TransferPending) return transfer;
            var pool = state.AdmissionPool;
            UnityCliAdmissionPool.ConsumedIdentity current;
            if (pool == null || !pool.TryGetConsumedIdentity(identity.RequestId, out current)) return transfer;
            AdmissionOwnerIdentity frozen;
            try { frozen = ToAdmissionOwner(current, identity.RequestDigest); }
            catch (BridgeCommandException) { return AdmissionTransferResult.TransferPending; }
            if (!SameAdmissionImmutableIdentity(frozen, identity)) return AdmissionTransferResult.TransferPending;
            identity.OwnerState = frozen.OwnerState;
            return transfer;
        }

        /// <summary>root观察不确定时用队列冻结身份建立可执行Recovery marker，不从Unity场景或半字段重建。</summary>
        private static AdmissionTransferResult PersistWorkItemRecoveryAfterTransferFailure(BridgeQueueState state, AdmissionOwnerIdentity identity, string command, string response)
        {
            if (identity == null || (!AdmissionDurableRootCommand(command) && command != "project-tool.run" && command != "code.exec"))
                return AdmissionTransferResult.TransferPending;
            try
            {
                var current = CaptureAdmissionOwner(state, identity.RequestId, identity.RequestDigest);
                if (current.OwnerState != UnityCliAdmissionPool.AdmissionOwnerState.WorkItem)
                    return TryConfirmDurableTransferredOwner(state, identity, command, current)
                        ? AdmissionTransferResult.Transferred
                        : AdmissionTransferResult.TransferPending;
                string markerScope;
                string rootScope;
                AdmissionOwnerScopes(command, out markerScope, out rootScope);
                current.TopRequestDigest = AdmissionAcceptedTopDigest(state, response, current, command);
                var marker = new Dictionary<string, object>
                {
                    { "requestId", current.RequestId }, { "requestDigest", AdmissionTopDigest(current) }, { "editorSessionId", EditorSessionId() }
                };
                if (command == "internal.test.run.cancel" && !TryAddCancelAdmissionRootIdentity(state, marker, current))
                    return AdmissionTransferResult.TransferPending;
                var recovered = PersistAdmissionOwnerTransfer(state, markerScope + "-admission-owner", marker, current, UnityCliAdmissionPool.AdmissionOwnerState.Recovery);
                identity.OwnerState = recovered.OwnerState;
                identity.TopRequestDigest = recovered.TopRequestDigest;
                return AdmissionTransferResult.Transferred;
            }
            catch (Exception exception)
            {
                state.Sinks.WriteLog("admission_workitem_recovery_pending requestId=" + identity.RequestId + " " + exception.GetType().Name + ": " + exception.Message);
                return AdmissionTransferResult.TransferPending;
            }
        }

        /// <summary>accepted coordinator/cancel及仍持有exact direct durable root的请求只通过固定marker移交owner。</summary>
        private static AdmissionTransferResult TransferAdmissionAfterAcceptedWork(BridgeQueueState state, AdmissionOwnerIdentity identity, string command, string response)
        {
            if (identity == null) return AdmissionTransferResult.NotAccepted;
            var directMutation = command == "project-tool.run" || command == "code.exec";
            if (!AdmissionDurableRootCommand(command) && !directMutation) return AdmissionTransferResult.NotAccepted;
            string markerScope;
            string rootScope;
            AdmissionOwnerScopes(command, out markerScope, out rootScope);
            try
            {
                var current = CaptureAdmissionOwner(state, identity.RequestId, identity.RequestDigest);
                if (current.OwnerState != UnityCliAdmissionPool.AdmissionOwnerState.WorkItem)
                    return TryConfirmDurableTransferredOwner(state, identity, command, current)
                        ? AdmissionTransferResult.Transferred
                        : AdmissionTransferResult.TransferPending;
                bool durable;
                var readable = TryHasExactDurableBusinessRoot(state, rootScope, command, identity, out durable);
                if (!readable) return AdmissionTransferResult.TransferPending;
                var accepted = AdmissionResponseIsCoordinatorAccepted(response) || durable;
                if (!accepted) return AdmissionTransferResult.NotAccepted;
                identity.TopRequestDigest = AdmissionAcceptedTopDigest(state, response, identity, command);
                var target = command == "internal.test.run.cancel"
                    ? UnityCliAdmissionPool.AdmissionOwnerState.Continuation
                    : UnityCliAdmissionPool.AdmissionOwnerState.Coordinator;
                var intended = durable ? target : UnityCliAdmissionPool.AdmissionOwnerState.Recovery;
                if (directMutation) intended = UnityCliAdmissionPool.AdmissionOwnerState.Recovery;
                var marker = new Dictionary<string, object>
                {
                    { "requestId", identity.RequestId }, { "requestDigest", AdmissionTopDigest(identity) }, { "editorSessionId", EditorSessionId() }
                };
                if (command == "internal.test.run.cancel" && !TryAddCancelAdmissionRootIdentity(state, marker, identity))
                    return AdmissionTransferResult.TransferPending;
                var updated = PersistAdmissionOwnerTransfer(state, markerScope + "-admission-owner", marker, identity, intended);
                identity.OwnerState = updated.OwnerState;
                identity.TopRequestDigest = updated.TopRequestDigest;
                return AdmissionTransferResult.Transferred;
            }
            catch (Exception exception)
            {
                state.Sinks.WriteLog("admission_durable_transfer_pending requestId=" + identity.RequestId + " " + exception.GetType().Name + ": " + exception.Message);
                return AdmissionTransferResult.TransferPending;
            }
        }

        /// <summary>pool owner已变化时仍须由exact marker reconcile证明commit与pool persistence均已完成。</summary>
        private static bool TryConfirmDurableTransferredOwner(BridgeQueueState state, AdmissionOwnerIdentity identity, string command, AdmissionOwnerIdentity current)
        {
            if (command == "internal.test.run.cancel")
                return TryConfirmCancelTransferredOwner(state, identity, current);
            string markerScope;
            string rootScope;
            AdmissionOwnerScopes(command, out markerScope, out rootScope);
            foreach (var candidate in new[]
            {
                markerScope + "-admission-owner",
                rootScope + "-terminal-cleanup",
                rootScope + "-pre-effect-cleanup"
            }.Distinct(StringComparer.Ordinal))
            {
                Dictionary<string, object> marker;
                try { marker = state.LoadOperation(candidate); }
                catch { return false; }
                if (marker == null) continue;
                current.TopRequestDigest = AdmissionText(marker, "requestDigest");
                current.EditorSessionId = AdmissionText(marker, "editorSessionId");
                if (!AdmissionMarkerMatchesIdentity(marker, current)) continue;
                AdmissionOwnerIdentity reconciled;
                try { reconciled = ReconcileAdmissionTransferForLiveMarker(state, candidate, marker, current.RequestId, current.RequestDigest); }
                catch { return false; }
                if (reconciled == null || reconciled.OwnerState != current.OwnerState) return false;
                identity.OwnerState = reconciled.OwnerState;
                identity.TopRequestDigest = reconciled.TopRequestDigest;
                return true;
            }
            return false;
        }

        /// <summary>cancel queue settlement只确认cancel owner marker，不进入通用root/lease reconciliation。</summary>
        private static bool TryConfirmCancelTransferredOwner(BridgeQueueState state, AdmissionOwnerIdentity identity, AdmissionOwnerIdentity current)
        {
            const string markerScope = "test-cancel-admission-owner";
            Dictionary<string, object> marker;
            try { marker = state.LoadOperation(markerScope); }
            catch { return false; }
            var rootRequestId = AdmissionText(marker, "rootRequestId");
            var rootRequestDigest = AdmissionText(marker, "rootRequestDigest");
            var runId = AdmissionText(marker, "runId");
            if (!IsSafeId(rootRequestId) || !AdmissionDigestIsValid(rootRequestDigest) || !Guid.TryParse(runId, out _)
                || !string.Equals(AdmissionText(marker, "requestId"), current.RequestId, StringComparison.Ordinal)
                || !string.Equals(AdmissionText(marker, "requestDigest"), rootRequestDigest, StringComparison.Ordinal)) return false;
            current.TopRequestDigest = rootRequestDigest;
            current.EditorSessionId = AdmissionText(marker, "editorSessionId");
            if (!AdmissionMarkerMatchesIdentity(marker, current)) return false;
            var intended = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionIntendedOwnerState");
            try
            {
                var reconciled = current.OwnerState == intended
                    ? current
                    : PersistAdmissionOwnerTransfer(state, markerScope, marker, current, intended);
                if (current.OwnerState == intended)
                {
                    WriteAdmissionOwnerFields(marker, current, intended, true);
                    state.SaveOperation(markerScope, marker);
                    if (!state.PersistPool()) return false;
                }
                identity.OwnerState = reconciled.OwnerState;
                identity.TopRequestDigest = rootRequestDigest;
                return true;
            }
            catch { return false; }
        }

        /// <summary>cancel marker首次转移即冻结parent root与run identity，reload不依赖finalizer补写。</summary>
        private static bool TryAddCancelAdmissionRootIdentity(BridgeQueueState state, IDictionary<string, object> marker, AdmissionOwnerIdentity identity)
        {
            try
            {
                var root = state.LoadOperation("test-run");
                object rawOperation;
                var operation = root != null && root.TryGetValue("testOperation", out rawOperation) ? AsDictionary(rawOperation) : null;
                var rootRequestId = AdmissionText(root, "requestId");
                var rootRequestDigest = AdmissionText(root, "requestDigest");
                var runId = AdmissionText(operation, "cancelRunId");
                if (!IsSafeId(rootRequestId) || !AdmissionDigestIsValid(rootRequestDigest) || !Guid.TryParse(runId, out _)
                    || !string.Equals(AdmissionText(operation, "cancelRequestId"), identity.RequestId, StringComparison.Ordinal)
                    || !string.Equals(AdmissionText(operation, "cancelRequestDigest"), identity.RequestDigest, StringComparison.Ordinal)
                    || !string.Equals(AdmissionText(operation, "cancelRootRequestDigest"), rootRequestDigest, StringComparison.Ordinal)
                    || !string.Equals(AdmissionTopDigest(identity), rootRequestDigest, StringComparison.Ordinal)) return false;
                marker["rootRequestId"] = rootRequestId;
                marker["rootRequestDigest"] = rootRequestDigest;
                marker["runId"] = runId;
                return true;
            }
            catch { return false; }
        }

        private static bool TryHasExactDurableBusinessRoot(BridgeQueueState queueState, string rootScope, string command, AdmissionOwnerIdentity identity, out bool durable)
        {
            durable = false;
            try
            {
                var operationRoot = queueState.LoadOperation(rootScope);
                if (operationRoot == null)
                {
                    if (command == "internal.test.run.cancel") return false;
                    bool ownsLease;
                    if (!queueState.TryIsLeaseOwned(identity.RequestId, identity.RequestDigest, out ownsLease)) return false;
                    if (command == "project-tool.run" || command == "code.exec")
                    {
                        durable = ownsLease;
                        return true;
                    }
                    // durable coordinator无root但仍持有exact lease时，不能把错误响应归约为未接受。
                    return !ownsLease;
                }
                if (!string.Equals(AdmissionText(operationRoot, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) return true;
                if (command == "internal.test.run.cancel")
                {
                    object rawOperation;
                    var operation = operationRoot.TryGetValue("testOperation", out rawOperation) ? AsDictionary(rawOperation) : null;
                    var exactCancel = operation != null && string.Equals(AdmissionText(operation, "cancelRequestId"), identity.RequestId, StringComparison.Ordinal)
                        && string.Equals(AdmissionText(operation, "cancelRequestDigest"), identity.RequestDigest, StringComparison.Ordinal);
                    durable = exactCancel && (AdmissionBool(operation, "cancelNeedsContinuation")
                        || new[] { "dispatching", "dispatched", "outcome_unknown", "terminal_cleanup" }.Contains(AdmissionText(operation, "cancelEffectState")));
                    return true;
                }
                durable = string.Equals(AdmissionText(operationRoot, "requestId"), identity.RequestId, StringComparison.Ordinal)
                    && string.Equals(AdmissionText(operationRoot, "requestDigest"), identity.RequestDigest, StringComparison.Ordinal);
                return true;
            }
            catch { return false; }
        }

        private static void AdmissionOwnerScopes(string command, out string markerScope, out string rootScope)
        {
            markerScope = AdmissionTerminalScope(command);
            rootScope = markerScope == "test-cancel" ? "test-run" : markerScope;
            if (string.IsNullOrEmpty(markerScope)) throw new InvalidOperationException("Admission durable command has no owner scope.");
        }

        /// <summary>deadline之后仍允许同字节direct request读取已发布terminal，不入队也不重新占slot。</summary>
        private static string AdmissionRecoveredDirectTerminal(UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes)
        {
            if (request == null || rawRequestBytes == null || (request.Command != "code.exec" && request.Command != "project-tool.run")) return null;
            var digest = UnityCliRequestDigest.Compute(rawRequestBytes, request.InstanceId);
            object terminal;
            if (!UnityCliActiveOperation.TryGetTerminal("direct-mutation", request.RequestId, digest, out terminal)) return null;
            CleanupPublishedDirectMutation(request.RequestId, digest);
            return UnityCliSerializer.Serialize(terminal);
        }

        /// <summary>active admission已收敛后，只允许同字节请求读取现有exact durable terminal。</summary>
        private static bool AdmissionHasExactTerminal(UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes)
        {
            var scope = AdmissionTerminalScope(request == null ? null : request.Command);
            if (scope == null || request == null || rawRequestBytes == null) return false;
            var digest = UnityCliRequestDigest.Compute(rawRequestBytes, request.InstanceId);
            object terminal;
            return UnityCliActiveOperation.TryGetTerminal(scope, request.RequestId, digest, out terminal);
        }

        private static string AdmissionTerminalScope(string command)
        {
            switch (command)
            {
                case "internal.build.run.start": return "build";
                case "internal.editor.compile.start": return "compile";
                case "internal.editor.refresh.start": return "refresh";
                case "internal.asset.refresh.start": return "asset-refresh";
                case "internal.package.embed.start": return "package-embed";
                case "internal.package.resolve.start": return "package-resolve";
                case "internal.test.run.start": return "test-run";
                case "internal.test.run.cancel": return "test-cancel";
                case "internal.navmesh.bake.start": return "navmesh-bake";
                case "internal.occlusion.bake.start": return "occlusion-bake";
                case "project-tool.run":
                case "code.exec":
                    return "direct-mutation";
                default:
                    return null;
            }
        }

        private static bool AdmissionResponseIsCoordinatorAccepted(string response)
        {
            try
            {
                var envelope = AsDictionary(UnityCliSerializer.Parse(response));
                object ok;
                object rawData;
                var data = envelope != null && envelope.TryGetValue("data", out rawData) ? AsDictionary(rawData) : null;
                object terminal;
                object accepted;
                return envelope != null && envelope.TryGetValue("ok", out ok) && ok is bool && (bool)ok
                    && data != null && data.TryGetValue("terminal", out terminal) && terminal is bool && !(bool)terminal
                    && data.TryGetValue("accepted", out accepted) && accepted is bool && (bool)accepted;
            }
            catch { return false; }
        }

        private static bool AdmissionDurableRootCommand(string command)
        {
            return command == "internal.build.run.start" || command == "internal.editor.compile.start"
                || command == "internal.editor.refresh.start" || command == "internal.asset.refresh.start"
                || command == "internal.package.embed.start" || command == "internal.package.resolve.start"
                || command == "internal.test.run.start" || command == "internal.test.run.cancel"
                || command == "internal.navmesh.bake.start" || command == "internal.occlusion.bake.start"
                || command == "project-tool.run" || command == "code.exec";
        }

        private static string AdmissionAcceptedTopDigest(BridgeQueueState state, string response, AdmissionOwnerIdentity identity, string command)
        {
            try
            {
                var envelope = AsDictionary(UnityCliSerializer.Parse(response));
                object rawData;
                var data = envelope != null && envelope.TryGetValue("data", out rawData) ? AsDictionary(rawData) : null;
                var digest = AdmissionText(data, "rootRequestDigest");
                if (AdmissionDigestIsValid(digest)) return digest;
                if (command == "internal.test.run.cancel")
                {
                    var root = state.LoadOperation("test-run");
                    object rawOperation;
                    var operation = root != null && root.TryGetValue("testOperation", out rawOperation) ? AsDictionary(rawOperation) : null;
                    digest = AdmissionText(operation, "cancelRootRequestDigest");
                    if (AdmissionDigestIsValid(digest)) return digest;
                }
                return identity.RequestDigest;
            }
            catch { return identity.RequestDigest; }
        }

        private static bool AdmissionRecoveryCommand(string command)
        {
            return command == "project-tool.list" || command == "internal.editor.state.snapshot"
                || UnityCliCompileCoordinator.IsCommand(command) || UnityCliLiveBuildCoordinator.IsCommand(command)
                || UnityCliLiveRefreshCoordinator.IsCommand(command) || UnityCliAssetRefreshCoordinator.IsCommand(command)
                || UnityCliPackageEmbedCoordinator.IsCommand(command) || UnityCliPackageResolveCoordinator.IsCommand(command)
                || UnityCliTestRunCoordinator.IsCommand(command) || UnityCliNavMeshBakeCoordinator.IsCommand(command)
                || UnityCliOcclusionBakeCoordinator.IsCommand(command)
                || command == "project-tool.run" || command == "code.exec";
        }

        private static bool AdmissionBypassCommand(string command)
        {
            switch (command)
            {
                case "project-tool.list":
                case "diagnose.preflight":
                case "internal.editor.state.snapshot":
                case "internal.editor.compile.status":
                case "internal.editor.refresh.status":
                case "internal.asset.refresh.status":
                case "internal.package.embed.status":
                case "internal.package.resolve.status":
                case "internal.package.files.claim":
                case "internal.package.files.commit":
                case "internal.package.files.prove":
                case "internal.package.files.fail":
                case "internal.test.run.status":
                case "internal.test.run.cancel.plan":
                case "internal.test.run.cancel.status":
                case "internal.navmesh.bake.status":
                case "internal.navmesh.bake.plan":
                case "internal.navmesh.bake.snapshot":
                case "internal.navmesh.bake.cancel":
                case "internal.occlusion.bake.status":
                case "internal.occlusion.bake.plan":
                case "internal.occlusion.bake.snapshot":
                case "internal.occlusion.bake.cancel":
                    return true;
                default:
                    return false;
            }
        }

        private static void WriteAdmissionOwnerFields(IDictionary<string, object> state, AdmissionOwnerIdentity identity, UnityCliAdmissionPool.AdmissionOwnerState intendedOwner, bool committed)
        {
            state["admissionRootFingerprint"] = identity.RootFingerprint;
            state["admissionToken"] = identity.Token;
            state["admissionSessionId"] = identity.SessionId;
            state["admissionListenerId"] = identity.ListenerId;
            state["admissionListenerGeneration"] = identity.ListenerGeneration;
            state["admissionExecutionDeadlineMs"] = identity.ExecutionDeadlineMs;
            state["admissionRequestDigest"] = identity.RequestDigest;
            state["admissionOwnerState"] = (long)identity.OwnerState;
            state["admissionIntendedOwnerState"] = (long)intendedOwner;
            state["admissionTransferCommitted"] = committed;
        }

        private static AdmissionOwnerIdentity ToAdmissionOwner(UnityCliAdmissionPool.ConsumedIdentity identity, string requestDigest)
        {
            if (identity == null) return null;
            if (!string.IsNullOrEmpty(requestDigest) && !string.Equals(identity.RequestDigest, requestDigest, StringComparison.Ordinal))
                throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission owner request digest does not match the frozen WorkItem.");
            return new AdmissionOwnerIdentity { RequestId = identity.RequestId, RequestDigest = identity.RequestDigest, TopRequestDigest = identity.RequestDigest, RootFingerprint = identity.RootFingerprint, Token = identity.Token, SessionId = identity.SessionId, ListenerId = identity.ListenerId, ListenerGeneration = identity.ListenerGeneration, ExecutionDeadlineMs = identity.ExecutionDeadlineMs, EditorSessionId = EditorSessionId(), OwnerState = identity.OwnerState };
        }

        private static UnityCliAdmissionPool.ConsumedIdentity ToPoolIdentity(AdmissionOwnerIdentity identity)
        {
            return identity == null ? null : UnityCliAdmissionPool.CreateConsumedIdentity(identity.RequestId, identity.RootFingerprint, identity.RequestDigest, identity.Token, identity.SessionId, identity.OwnerState, identity.ListenerId, identity.ListenerGeneration, identity.ExecutionDeadlineMs);
        }

        private static AdmissionOwnerIdentity RecordIdentity(UnityCliAdmissionPool.AdmissionRecord record)
        {
            return new AdmissionOwnerIdentity { RequestId = record.RequestId, RequestDigest = record.RequestDigest, TopRequestDigest = record.RequestDigest, RootFingerprint = record.RootFingerprint, Token = record.Token, SessionId = record.SessionId, ListenerId = record.ListenerId, ListenerGeneration = record.ListenerGeneration, ExecutionDeadlineMs = record.ExecutionDeadlineMs, EditorSessionId = EditorSessionId(), OwnerState = record.OwnerState };
        }

        private static bool SameAdmissionImmutableIdentity(AdmissionOwnerIdentity left, AdmissionOwnerIdentity right)
        {
            return left != null && right != null
                && string.Equals(left.RequestId, right.RequestId, StringComparison.Ordinal)
                && string.Equals(left.RequestDigest, right.RequestDigest, StringComparison.Ordinal)
                && string.Equals(left.RootFingerprint, right.RootFingerprint, StringComparison.Ordinal)
                && string.Equals(left.Token, right.Token, StringComparison.Ordinal)
                && string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
                && string.Equals(left.ListenerId, right.ListenerId, StringComparison.Ordinal)
                && left.ListenerGeneration == right.ListenerGeneration
                && left.ExecutionDeadlineMs == right.ExecutionDeadlineMs
                && string.Equals(left.EditorSessionId ?? EditorSessionId(), right.EditorSessionId ?? EditorSessionId(), StringComparison.Ordinal);
        }

        private static string AdmissionTopDigest(AdmissionOwnerIdentity identity)
        {
            return identity == null || !AdmissionDigestIsValid(identity.TopRequestDigest) ? identity == null ? string.Empty : identity.RequestDigest : identity.TopRequestDigest;
        }

        private static bool AdmissionDigestIsValid(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (var character in value) if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
            return true;
        }

        internal static bool ClearExactAdmissionOwnerMarkers(AdmissionOwnerIdentity identity)
        {
            return ClearExactAdmissionOwnerMarkers(ProductionQueueState, identity);
        }

        internal static bool ClearExactAdmissionOwnerMarkers(BridgeQueueState state, AdmissionOwnerIdentity identity)
        {
            return ClearAdmissionOwnerMarkersForState(state, identity);
        }

        internal static bool ClearAdmissionOwnerMarkersForState(BridgeQueueState state, AdmissionOwnerIdentity identity)
        {
            if (state == null || identity == null) return false;
            return ClearAdmissionOwnerMarkers(state, identity);
        }

        private static bool ClearProductionAdmissionOwnerMarkers(AdmissionOwnerIdentity identity)
        {
            return ClearAdmissionOwnerMarkers(ProductionQueueState, identity);
        }

        private static bool ClearAdmissionOwnerMarkers(BridgeQueueState state, AdmissionOwnerIdentity identity)
        {
            if (state == null || identity == null) return false;
            var cleared = true;
            foreach (var scope in AdmissionDurableScopes)
            {
                var markerScope = scope + "-admission-owner";
                try
                {
                    var marker = state.LoadOperation(markerScope);
                    if (marker == null || !AdmissionMarkerHasImmutableIdentity(marker, identity, AdmissionTopDigest(identity))) continue;
                    state.ClearOperation(markerScope);
                    if (state.OperationExists(markerScope)) cleared = false;
                }
                catch (Exception exception)
                {
                    cleared = false;
                    state.Sinks.WriteLog("admission_owner_marker_cleanup_pending requestId=" + identity.RequestId + " scope=" + markerScope + " " + exception.GetType().Name);
                }
            }
            return cleared;
        }

        private static void EnsureAdmissionPool()
        {
            if (admissionPool == null) InitializeAdmissionPool();
        }

        private static Dictionary<string, object> StrictAdmissionObject(byte[] rawBody, string operation, params string[] allowed)
        {
            Dictionary<string, object> value;
            try { value = AsDictionary(UnityCliSerializer.Parse(DecodeRequestBody(rawBody))); }
            catch (Exception exception) { throw new BridgeCommandException("E_SCHEMA_INVALID", "Admission " + operation + " body is invalid: " + exception.Message); }
            if (value == null) throw new BridgeCommandException("E_SCHEMA_INVALID", "Admission " + operation + " body must be an object.");
            var keys = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var key in value.Keys) if (!keys.Contains(key)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Admission " + operation + " contains unknown field " + key + ".");
            return value;
        }

        private static Dictionary<string, object> AsDictionary(object value)
        {
            var dictionary = value as IDictionary<string, object>;
            return dictionary == null ? null : dictionary.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        private static string RequiredReleaseToken(HttpListenerRequest request, string requestId, string fingerprint)
        {
            if (!string.Equals(RequiredHeader(request, "x-unity-cli-admission-root-id", 200), requestId, StringComparison.Ordinal)
                || !string.Equals(RequiredHeader(request, "x-unity-cli-admission-fingerprint", 64), fingerprint, StringComparison.Ordinal))
                throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission release headers do not match the body identity.");
            return RequiredHeader(request, "x-unity-cli-admission-token", 256);
        }

        private static string RequiredHeader(HttpListenerRequest request, string key, int maximum)
        {
            var result = request == null ? null : request.Headers[key];
            if (string.IsNullOrEmpty(result) || result.Length > maximum) throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission header " + key + " is missing or invalid.");
            return result;
        }

        private static string RequiredText(IDictionary<string, object> value, string key, int maximum)
        {
            var result = AdmissionText(value, key);
            if (string.IsNullOrEmpty(result) || result.Length > maximum) throw new BridgeCommandException("E_SCHEMA_INVALID", "Admission field " + key + " is missing or invalid.");
            return result;
        }

        private static string AdmissionText(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) ? item as string : null; }
        private static long AdmissionLong(IDictionary<string, object> value, string key)
        {
            object item;
            if (value == null || !value.TryGetValue(key, out item)) return 0L;
            if (item is long) return (long)item;
            if (item is double)
            {
                var number = (double)item;
                return !double.IsNaN(number) && !double.IsInfinity(number) && Math.Floor(number) == number && number >= long.MinValue && number <= long.MaxValue ? (long)number : 0L;
            }
            return 0L;
        }
        private static bool AdmissionBool(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item is bool && (bool)item; }
    }
}
#endif
