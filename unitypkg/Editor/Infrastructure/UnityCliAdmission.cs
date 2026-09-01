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
        private static readonly string[] AdmissionDurableScopes = { "compile", "package-resolve", "refresh", "asset-refresh", "package-embed", "test-run", "test-cancel", "build", "navmesh-bake", "occlusion-bake", "direct-mutation" };
        private static UnityCliAdmissionPool admissionPool;

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
            var serialized = SessionState.GetString(AdmissionSessionStateKey, string.Empty);
            if (string.IsNullOrEmpty(serialized))
            {
                ReconcileOrphanAdmissionOwnerMarkers();
                return;
            }
            try
            {
                var state = AsDictionary(UnityCliSerializer.Parse(serialized));
                if (state == null || AdmissionText(state, "editorSessionId") != EditorSessionId())
                {
                    SessionState.EraseString(AdmissionSessionStateKey);
                    ReconcileOrphanAdmissionOwnerMarkers();
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
            }
            catch (Exception exception)
            {
                admissionPool.BlockRecovery();
                SessionState.EraseString(AdmissionSessionStateKey);
                WriteLog("admission_restore_failed " + exception.GetType().Name + ": " + exception.Message);
                ReconcileOrphanAdmissionOwnerMarkers();
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
        private static void PersistAdmissionPoolState()
        {
            if (admissionPool == null) return;
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
            if (reservation == null || request == null) throw new ArgumentNullException("reservation");
            if (!reservation.HasAdmissionHeaders)
            {
                if (reservation.IsBypass) return;
                throw new BridgeCommandException("E_ADMISSION_REQUIRED", "A valid Unity task-pool admission is required before this business request can enter the queue.");
            }
            if (admissionPool == null) throw new BridgeCommandException("E_RUNTIME_SESSION_STALE", "The Unity session ended before admission completed.");
            if (!string.IsNullOrEmpty(reservation.Token))
            {
                var result = admissionPool.AuthorizeAndCapture(reservation.RequestId, reservation.RootFingerprint, reservation.RequestDigest, reservation.Token, ListenerId, CurrentListenerGeneration(), reservation.ExecutionDeadlineMs);
                if (result.Outcome == UnityCliAdmissionPool.AuthorizeOutcome.Authorized
                    || result.Outcome == UnityCliAdmissionPool.AuthorizeOutcome.AlreadyConsumed)
                {
                    reservation.Owner = ToAdmissionOwner(result.Identity, reservation.RequestDigest);
                    reservation.NewlyConsumed = result.NewlyConsumed;
                    return;
                }
                throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission token is missing, expired, reused, or bound to another root.");
            }
            if (admissionPool.AttachConsumed(reservation.RequestId, reservation.RootFingerprint, reservation.RequestDigest, ListenerId, CurrentListenerGeneration(), reservation.ExecutionDeadlineMs))
            {
                reservation.Owner = CaptureAdmissionOwner(reservation.RequestId, reservation.RequestDigest);
                return;
            }
            if (AdmissionRecoveryCommand(request.Command) && admissionPool.RecoverConsumed(reservation.RequestId, reservation.RootFingerprint, reservation.RequestDigest, ListenerId, CurrentListenerGeneration(), reservation.ExecutionDeadlineMs))
            {
                reservation.Owner = CaptureAdmissionOwner(reservation.RequestId, reservation.RequestDigest);
                return;
            }
            if (reservation.HasExactTerminal) return;
            throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Consumed admission root cannot attach to this listener generation.");
        }

        internal static void MarkAdmissionQueuePublished(AdmissionQueueReservation reservation)
        {
            if (reservation != null) reservation.QueuePublished = true;
        }

        internal static bool CompensateAdmissionBeforeQueuePublication(AdmissionQueueReservation reservation)
        {
            if (reservation == null || reservation.QueuePublished || !reservation.NewlyConsumed || reservation.Owner == null) return true;
            var pool = admissionPool;
            var compensated = pool != null && pool.CompensateAuthorization(ToPoolIdentity(reservation.Owner));
            if (compensated)
            {
                reservation.NewlyConsumed = false;
                WriteLog("admission_prequeue_compensated requestId=" + reservation.Owner.RequestId + " active=" + pool.Capture().Active.ToString(CultureInfo.InvariantCulture));
            }
            return compensated;
        }

        internal static AdmissionOwnerIdentity CaptureAdmissionOwner(string requestId, string requestDigest = null)
        {
            UnityCliAdmissionPool.ConsumedIdentity identity;
            if (admissionPool == null || !admissionPool.TryGetConsumedIdentity(requestId, out identity))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner identity cannot be frozen after consumption.");
            return ToAdmissionOwner(identity, requestDigest);
        }

        /// <summary>QueueLock内完成pool memory裁决；map/running存在时延迟完成避免exact attach误拒绝。</summary>
        internal static bool CompleteAdmissionRoot(AdmissionOwnerIdentity identity)
        {
            if (identity == null) return false;
            UnityCliAdmissionPool.CompletionOutcome outcome;
            UnityCliAdmissionPool pool;
            lock (QueueLock)
            {
                pool = admissionPool;
                if (pool == null) return false;
                BridgeWorkItem existing;
                if (ActiveRequestWork.TryGetValue(identity.RequestId, out existing)
                    && existing.AdmissionOwner != null
                    && SameAdmissionOwner(existing.AdmissionOwner, identity)) return false;
                outcome = pool.CompleteTrusted(ToPoolIdentity(identity));
            }
            if (outcome != UnityCliAdmissionPool.CompletionOutcome.ExactCurrentCompleted) return false;
            PersistAdmissionPoolState();
            if (!ClearExactAdmissionOwnerMarkers(identity)) return false;
            WriteLog("admission_terminal requestId=" + identity.RequestId + " outcome=" + outcome.ToString() + " active=" + pool.Capture().Active.ToString(CultureInfo.InvariantCulture));
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
            if (marker == null || identity == null || string.IsNullOrEmpty(markerScope)) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission transfer marker is unavailable.");
            var existing = UnityCliActiveOperation.Load(markerScope);
            if (existing != null && !string.Equals(AdmissionText(existing, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal))
            {
                UnityCliActiveOperation.Clear(markerScope);
                existing = null;
            }
            if (existing != null && !AdmissionMarkerMatchesIdentity(existing, identity) && !AdmissionMarkerCanAcquireOwner(existing, identity))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission transfer marker is owned by another exact request.");
            marker["requestId"] = identity.RequestId;
            marker["requestDigest"] = AdmissionTopDigest(identity);
            marker["editorSessionId"] = identity.EditorSessionId ?? EditorSessionId();
            WriteAdmissionOwnerFields(marker, identity, intendedOwner, false);
            UnityCliActiveOperation.Save(markerScope, new Dictionary<string, object>(marker));
            if (!admissionPool.PrepareOwnerTransfer(ToPoolIdentity(identity), intendedOwner))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner transfer intent does not match the current exact owner.");
            PersistAdmissionPoolState();
            UnityCliAdmissionPool.ConsumedIdentity successor;
            if (!admissionPool.CommitOwnerTransfer(ToPoolIdentity(identity), intendedOwner, out successor))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner transfer could not be committed.");
            var updated = ToAdmissionOwner(successor, identity.RequestDigest);
            updated.TopRequestDigest = AdmissionText(marker, "requestDigest");
            updated.EditorSessionId = AdmissionText(marker, "editorSessionId");
            marker["admissionTransferCommitted"] = true;
            UnityCliActiveOperation.Save(markerScope, new Dictionary<string, object>(marker));
            PersistAdmissionPoolState();
            return updated;
        }

        /// <summary>重试修复transfer窗口；pool确定缺失时仍先持久化当前SessionState再交给fixed marker清理。</summary>
        internal static AdmissionOwnerIdentity ReconcileAdmissionTransferForLiveMarker(string markerScope, Dictionary<string, object> marker, string requestId, string requestDigest)
        {
            var markerOwner = ReadAdmissionOwnerFields(marker, requestId);
            if (!AdmissionMarkerHasImmutableIdentity(marker, markerOwner, AdmissionTopDigest(markerOwner)))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission cleanup marker identity is invalid.");
            var exact = admissionPool.InspectExactIdentityFields(ToPoolIdentity(markerOwner));
            if (exact == UnityCliAdmissionPool.ExactIdentityOutcome.DefinitelyAbsent)
            {
                bool rootReadable;
                var rootExists = HasExactAdmissionMarkerRoot(markerScope, markerOwner, out rootReadable);
                if (!rootReadable) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission cleanup marker root cannot be verified.");
                bool leaseOwned;
                if (!UnityCliSessionMutationLease.TryIsOwnedBy(markerOwner.RequestId, markerOwner.RequestDigest, out leaseOwned))
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission cleanup marker lease ownership cannot be verified.");
                if (rootExists || leaseOwned)
                {
                    if (!rootExists || !leaseOwned) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission cleanup marker root and lease are not an exact pair.");
                    var intended = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionIntendedOwnerState");
                    if (!admissionPool.RestoreConsumed(ToPoolIdentity(markerOwner), intended, AdmissionBool(marker, "admissionTransferCommitted")))
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission cleanup marker could not rebuild its exact consumed owner.");
                    PersistAdmissionPoolState();
                    exact = UnityCliAdmissionPool.ExactIdentityOutcome.ExactCurrent;
                }
                else
                {
                    UnityCliActiveOperation.Clear(markerScope);
                    if (UnityCliActiveOperation.Exists(markerScope)) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Completed admission cleanup marker could not be cleared.");
                    return null;
                }
            }
            if (exact != UnityCliAdmissionPool.ExactIdentityOutcome.ExactCurrent)
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission cleanup marker conflicts with the current requestId owner.");
            var current = CaptureAdmissionOwner(requestId, markerOwner.RequestDigest);
            current.TopRequestDigest = AdmissionText(marker, "requestDigest");
            current.EditorSessionId = AdmissionText(marker, "editorSessionId");
            var intended = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionIntendedOwnerState");
            if (current.OwnerState != intended) return PersistAdmissionOwnerTransfer(markerScope, marker, current, intended);
            marker["admissionTransferCommitted"] = true;
            UnityCliActiveOperation.Save(markerScope, marker);
            PersistAdmissionPoolState();
            return current;
        }

        /// <summary>启动时按marker、对应root与lease三者重建或清理owner；pool缺失不能单独证明终态。</summary>
        private static void ReconcileOrphanAdmissionOwnerMarkers()
        {
            var pool = admissionPool;
            if (pool == null) return;
            foreach (var scope in AdmissionDurableScopes)
            {
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
                bool rootReadable;
                var rootExists = HasExactAdmissionMarkerRoot(markerScope, markerOwner, out rootReadable);
                if (!rootReadable) { pool.BlockRecovery(); return; }
                bool leaseOwned;
                if (!UnityCliSessionMutationLease.TryIsOwnedBy(markerOwner.RequestId, markerOwner.RequestDigest, out leaseOwned)) { pool.BlockRecovery(); return; }
                try
                {
                    if (rootExists || leaseOwned)
                    {
                        if (!rootExists || !leaseOwned) { pool.BlockRecovery(); return; }
                        var intended = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionIntendedOwnerState");
                        if (!pool.RestoreConsumed(ToPoolIdentity(markerOwner), intended, AdmissionBool(marker, "admissionTransferCommitted"))) { pool.BlockRecovery(); return; }
                        PersistAdmissionPoolState();
                        continue;
                    }
                    UnityCliActiveOperation.Clear(markerScope);
                    if (UnityCliActiveOperation.Exists(markerScope)) { pool.BlockRecovery(); return; }
                }
                catch { pool.BlockRecovery(); return; }
            }
        }

        private static bool HasExactAdmissionMarkerRoot(string markerScope, AdmissionOwnerIdentity owner, out bool readable)
        {
            readable = true;
            try
            {
                var rootScope = markerScope.EndsWith("-admission-owner", StringComparison.Ordinal)
                    ? markerScope.Substring(0, markerScope.Length - "-admission-owner".Length)
                    : markerScope;
                if (rootScope.EndsWith("-terminal-cleanup", StringComparison.Ordinal)) rootScope = rootScope.Substring(0, rootScope.Length - "-terminal-cleanup".Length);
                if (rootScope.EndsWith("-pre-effect-cleanup", StringComparison.Ordinal)) rootScope = rootScope.Substring(0, rootScope.Length - "-pre-effect-cleanup".Length);
                if (rootScope == "test-cancel") rootScope = "test-run";
                var root = UnityCliActiveOperation.Load(rootScope);
                if (root == null || !string.Equals(AdmissionText(root, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) return false;
                if (rootScope == "test-run" && owner.RequestId != AdmissionText(root, "requestId"))
                {
                    object rawOperation;
                    var operation = root.TryGetValue("testOperation", out rawOperation) ? AsDictionary(rawOperation) : null;
                    return operation != null && string.Equals(AdmissionText(operation, "cancelRequestId"), owner.RequestId, StringComparison.Ordinal)
                        && string.Equals(AdmissionText(operation, "cancelRequestDigest"), owner.RequestDigest, StringComparison.Ordinal)
                        && string.Equals(AdmissionText(operation, "cancelRootRequestDigest"), root.RequestDigest, StringComparison.Ordinal);
                }
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
                if (scope != "test-run") continue;
                object rawOperation;
                var operation = state.TryGetValue("testOperation", out rawOperation) ? AsDictionary(rawOperation) : null;
                if (operation != null && string.Equals(AdmissionText(operation, "cancelRequestId"), record.RequestId, StringComparison.Ordinal)
                    && string.Equals(AdmissionText(operation, "cancelRequestDigest"), record.RequestDigest, StringComparison.Ordinal))
                {
                    markerScope = "test-cancel";
                    intendedOwner = UnityCliAdmissionPool.AdmissionOwnerState.Continuation;
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
            marker["admissionTransferCommitted"] = true;
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
                || !string.Equals(AdmissionText(marker, "admissionListenerId"), record.ListenerId, StringComparison.Ordinal)
                || AdmissionLong(marker, "admissionListenerGeneration") != record.ListenerGeneration
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
                && string.Equals(AdmissionText(marker, "admissionListenerId"), identity.ListenerId, StringComparison.Ordinal)
                && AdmissionLong(marker, "admissionListenerGeneration") == identity.ListenerGeneration
                && AdmissionLong(marker, "admissionExecutionDeadlineMs") == identity.ExecutionDeadlineMs
                && string.Equals(AdmissionText(marker, "editorSessionId"), identity.EditorSessionId ?? EditorSessionId(), StringComparison.Ordinal);
        }

        /// <summary>accepted durable transfer失败时返回Pending；普通WorkItem的pool completion留给QueueLock边界。</summary>
        private static AdmissionTransferResult SettleAdmissionAfterWorkItem(AdmissionOwnerIdentity identity, string command, string response)
        {
            if (identity == null || string.IsNullOrEmpty(identity.RequestId)) return AdmissionTransferResult.NotAccepted;
            var transfer = TransferAdmissionAfterAcceptedWork(identity, command, response);
            if (transfer == AdmissionTransferResult.TransferPending) return transfer;
            var pool = admissionPool;
            UnityCliAdmissionPool.ConsumedIdentity current;
            if (pool == null || !pool.TryGetConsumedIdentity(identity.RequestId, out current)) return transfer;
            AdmissionOwnerIdentity frozen;
            try { frozen = ToAdmissionOwner(current, identity.RequestDigest); }
            catch (BridgeCommandException) { return AdmissionTransferResult.TransferPending; }
            if (!SameAdmissionImmutableIdentity(frozen, identity)) return AdmissionTransferResult.TransferPending;
            identity.OwnerState = frozen.OwnerState;
            return transfer;
        }

        /// <summary>accepted coordinator/cancel及仍持有exact direct durable root的请求只通过固定marker移交owner。</summary>
        private static AdmissionTransferResult TransferAdmissionAfterAcceptedWork(AdmissionOwnerIdentity identity, string command, string response)
        {
            if (identity == null) return AdmissionTransferResult.NotAccepted;
            var directMutation = command == "project-tool.run" || command == "code.exec";
            var durableCancelRoot = false;
            var cancelRootReadable = command != "internal.test.run.cancel" || TryHasExactDurableBusinessRoot("test-run", command, identity, out durableCancelRoot);
            if (command == "internal.test.run.cancel" && !cancelRootReadable) return AdmissionTransferResult.TransferPending;
            var cancelAccepted = command == "internal.test.run.cancel" && durableCancelRoot;
            var coordinatorAccepted = AdmissionDurableRootCommand(command) && (AdmissionResponseIsCoordinatorAccepted(response) || cancelAccepted);
            if (!coordinatorAccepted && !directMutation) return AdmissionTransferResult.NotAccepted;
            string markerScope;
            string rootScope;
            AdmissionOwnerScopes(command, out markerScope, out rootScope);
            identity.TopRequestDigest = coordinatorAccepted ? AdmissionAcceptedTopDigest(response, identity, command) : identity.RequestDigest;
            try
            {
                bool durable;
                var readable = TryHasExactDurableBusinessRoot(rootScope, command, identity, out durable);
                if (directMutation && readable && !durable) return AdmissionTransferResult.NotAccepted;
                if (command == "internal.test.run.cancel")
                {
                    var current = CaptureAdmissionOwner(identity.RequestId, identity.RequestDigest);
                    if (current.OwnerState == UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup)
                    {
                        identity.OwnerState = current.OwnerState;
                        return AdmissionTransferResult.Transferred;
                    }
                }
                var target = command == "internal.test.run.cancel"
                    ? UnityCliAdmissionPool.AdmissionOwnerState.Continuation
                    : UnityCliAdmissionPool.AdmissionOwnerState.Coordinator;
                var intended = readable && durable ? target : UnityCliAdmissionPool.AdmissionOwnerState.Recovery;
                if (directMutation) intended = UnityCliAdmissionPool.AdmissionOwnerState.Recovery;
                var marker = new Dictionary<string, object>
                {
                    { "requestId", identity.RequestId }, { "requestDigest", AdmissionTopDigest(identity) }, { "editorSessionId", EditorSessionId() }
                };
                var updated = PersistAdmissionOwnerTransfer(markerScope + "-admission-owner", marker, identity, intended);
                identity.OwnerState = updated.OwnerState;
                identity.TopRequestDigest = updated.TopRequestDigest;
                return AdmissionTransferResult.Transferred;
            }
            catch (Exception exception)
            {
                WriteLog("admission_durable_transfer_pending requestId=" + identity.RequestId + " " + exception.GetType().Name + ": " + exception.Message);
                return AdmissionTransferResult.TransferPending;
            }
        }

        private static bool TryHasExactDurableBusinessRoot(string rootScope, string command, AdmissionOwnerIdentity identity, out bool durable)
        {
            durable = false;
            try
            {
                var state = UnityCliActiveOperation.Load(rootScope);
                if (state == null)
                {
                    if (command == "internal.test.run.cancel") return false;
                    if (command == "project-tool.run" || command == "code.exec")
                    {
                        bool ownsLease;
                        if (!UnityCliSessionMutationLease.TryIsOwnedBy(identity.RequestId, identity.RequestDigest, out ownsLease)) return false;
                        durable = ownsLease;
                    }
                    return true;
                }
                if (!string.Equals(AdmissionText(state, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) return true;
                if (command == "internal.test.run.cancel")
                {
                    object rawOperation;
                    var operation = state.TryGetValue("testOperation", out rawOperation) ? AsDictionary(rawOperation) : null;
                    var exactCancel = operation != null && string.Equals(AdmissionText(operation, "cancelRequestId"), identity.RequestId, StringComparison.Ordinal)
                        && string.Equals(AdmissionText(operation, "cancelRequestDigest"), identity.RequestDigest, StringComparison.Ordinal);
                    durable = exactCancel && (AdmissionBool(operation, "cancelNeedsContinuation")
                        || new[] { "dispatching", "dispatched", "outcome_unknown", "terminal_cleanup" }.Contains(AdmissionText(operation, "cancelEffectState")));
                    return true;
                }
                durable = string.Equals(AdmissionText(state, "requestId"), identity.RequestId, StringComparison.Ordinal)
                    && string.Equals(AdmissionText(state, "requestDigest"), identity.RequestDigest, StringComparison.Ordinal);
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
            return UnityCliActiveOperation.TryGetTerminal("direct-mutation", request.RequestId, digest, out terminal) ? UnityCliSerializer.Serialize(terminal) : null;
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

        private static string AdmissionAcceptedTopDigest(string response, AdmissionOwnerIdentity identity, string command)
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
                    var root = UnityCliActiveOperation.Load("test-run");
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
                case "internal.test.run.cancel":
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

        private static bool ClearExactAdmissionOwnerMarkers(AdmissionOwnerIdentity identity)
        {
            if (identity == null) return false;
            var cleared = true;
            foreach (var scope in AdmissionDurableScopes)
            {
                var markerScope = scope + "-admission-owner";
                try
                {
                    var marker = UnityCliActiveOperation.Load(markerScope);
                    if (marker == null || !AdmissionMarkerHasImmutableIdentity(marker, identity, AdmissionTopDigest(identity))) continue;
                    UnityCliActiveOperation.Clear(markerScope);
                    if (UnityCliActiveOperation.Exists(markerScope)) cleared = false;
                }
                catch (Exception exception)
                {
                    cleared = false;
                    WriteLog("admission_owner_marker_cleanup_pending requestId=" + identity.RequestId + " scope=" + markerScope + " " + exception.GetType().Name);
                }
            }
            return cleared;
        }

        private static UnityCliAdmissionPool queueTestPreviousPool;
        private static long queueTestPreviousListenerGeneration;

        internal static void BeginQueueTestScope(UnityCliAdmissionPool pool)
        {
            if (pool == null) throw new ArgumentNullException("pool");
            lock (QueueLock)
            {
                if (WorkQueue.Count != 0 || RunningWork.Count != 0 || ActiveRequestWork.Count != 0)
                    throw new InvalidOperationException("Queue test scope requires an idle production queue.");
                queueTestPreviousPool = admissionPool;
                queueTestPreviousListenerGeneration = listenerGeneration;
                if (listenerGeneration < 1) listenerGeneration = 1;
                admissionPool = pool;
                queuePublicationOpen = true;
                QueueTestBoundary = null;
            }
        }

        internal static void EndQueueTestScope()
        {
            lock (QueueLock)
            {
                WorkQueue.Clear();
                RunningWork.Clear();
                ActiveRequestWork.Clear();
                QueueTestBoundary = null;
                admissionPool = queueTestPreviousPool;
                queueTestPreviousPool = null;
                listenerGeneration = queueTestPreviousListenerGeneration;
                queueTestPreviousListenerGeneration = 0;
                queuePublicationOpen = started;
            }
        }

        internal static void ReconcileAdmissionMarkersForTests()
        {
            ReconcileOrphanAdmissionOwnerMarkers();
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
