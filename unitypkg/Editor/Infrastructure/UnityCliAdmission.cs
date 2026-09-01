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
            internal string RootFingerprint;
            internal string Token;
            internal string SessionId;
            internal UnityCliAdmissionPool.AdmissionOwnerState OwnerState;
        }

        /// <summary>队列publication失败时只补偿本次新消费；attach不拥有撤销权。</summary>
        internal sealed class AdmissionQueueReservation
        {
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
            if (string.IsNullOrEmpty(serialized)) return;
            try
            {
                var state = AsDictionary(UnityCliSerializer.Parse(serialized));
                if (state == null || AdmissionText(state, "editorSessionId") != EditorSessionId()) { SessionState.EraseString(AdmissionSessionStateKey); return; }
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
            }
            catch (Exception exception)
            {
                admissionPool.BlockRecovery();
                SessionState.EraseString(AdmissionSessionStateKey);
                WriteLog("admission_restore_failed " + exception.GetType().Name + ": " + exception.Message);
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

        /// <summary>
        /// QueueLock是外层锁，admission pool锁是内层锁；队列分支必须在同一QueueLock临界区调用本API并发布WorkItem。
        /// publication前异常只能调用CompensateAdmissionBeforeQueuePublication，且不得在持有pool锁时反向获取QueueLock。
        /// </summary>
        internal static AdmissionQueueReservation AuthorizeAdmissionForQueue(HttpListenerRequest httpRequest, UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes)
        {
            var rootId = httpRequest.Headers["x-unity-cli-admission-root-id"];
            var fingerprint = httpRequest.Headers["x-unity-cli-admission-fingerprint"];
            var token = httpRequest.Headers["x-unity-cli-admission-token"];
            var deadlineText = httpRequest.Headers["x-unity-cli-admission-deadline-utc"];
            var any = !string.IsNullOrEmpty(rootId) || !string.IsNullOrEmpty(fingerprint) || !string.IsNullOrEmpty(token) || !string.IsNullOrEmpty(deadlineText);
            if (!any)
            {
                if (AdmissionBypassCommand(request.Command)) return new AdmissionQueueReservation();
                throw new BridgeCommandException("E_ADMISSION_REQUIRED", "A valid Unity task-pool admission is required before this business request can enter the queue.");
            }
            if (string.IsNullOrEmpty(rootId) || string.IsNullOrEmpty(fingerprint) || string.IsNullOrEmpty(deadlineText))
                throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission root headers are incomplete.");
            if (!string.Equals(rootId, request.RequestId, StringComparison.Ordinal))
                throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission root requestId does not match the Bridge request body.");
            DateTimeOffset deadline;
            if (!DateTimeOffset.TryParse(deadlineText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out deadline))
                throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission execution deadline is invalid.");
            EnsureAdmissionPool();
            var deadlineMs = deadline.ToUnixTimeMilliseconds();
            var requestDigest = UnityCliRequestDigest.Compute(rawRequestBytes, request.InstanceId);
            if (!string.IsNullOrEmpty(token))
            {
                var result = admissionPool.AuthorizeAndCapture(rootId, fingerprint, requestDigest, token, ListenerId, CurrentListenerGeneration(), deadlineMs);
                if (result.Outcome == UnityCliAdmissionPool.AuthorizeOutcome.Authorized)
                    return new AdmissionQueueReservation { Owner = ToAdmissionOwner(result.Identity, requestDigest), NewlyConsumed = result.NewlyConsumed };
                throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission token is missing, expired, reused, or bound to another root.");
            }
            if (admissionPool.AttachConsumed(rootId, fingerprint, requestDigest, ListenerId, CurrentListenerGeneration(), deadlineMs))
                return new AdmissionQueueReservation { Owner = CaptureAdmissionOwner(rootId, requestDigest) };
            if (AdmissionRecoveryCommand(request.Command) && admissionPool.RecoverConsumed(rootId, fingerprint, requestDigest, ListenerId, CurrentListenerGeneration(), deadlineMs))
                return new AdmissionQueueReservation { Owner = CaptureAdmissionOwner(rootId, requestDigest) };
            if (AdmissionHasExactTerminal(request, rawRequestBytes)) return new AdmissionQueueReservation();
            throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Consumed admission root cannot attach to this listener generation.");
        }

        internal static void MarkAdmissionQueuePublished(AdmissionQueueReservation reservation)
        {
            if (reservation != null) reservation.QueuePublished = true;
        }

        internal static bool CompensateAdmissionBeforeQueuePublication(AdmissionQueueReservation reservation)
        {
            if (reservation == null || reservation.QueuePublished || !reservation.NewlyConsumed || reservation.Owner == null) return true;
            var compensated = admissionPool != null && admissionPool.CompensateAuthorization(ToPoolIdentity(reservation.Owner));
            if (compensated)
            {
                reservation.NewlyConsumed = false;
                WriteLog("admission_prequeue_compensated requestId=" + reservation.Owner.RequestId + " active=" + admissionPool.Capture().Active.ToString(CultureInfo.InvariantCulture));
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

        internal static bool CompleteAdmissionRoot(AdmissionOwnerIdentity identity)
        {
            if (admissionPool == null || identity == null) return false;
            var completed = admissionPool.CompleteTrusted(ToPoolIdentity(identity));
            if (completed)
            {
                WriteLog("admission_terminal requestId=" + identity.RequestId + " active=" + admissionPool.Capture().Active.ToString(CultureInfo.InvariantCulture));
                PersistAdmissionPoolState();
                ClearExactAdmissionOwnerMarker(identity);
            }
            return completed;
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
            var intended = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(state, "admissionIntendedOwnerState");
            return new AdmissionOwnerIdentity
            {
                RequestId = requestId,
                RequestDigest = AdmissionText(state, "admissionRequestDigest"),
                RootFingerprint = AdmissionText(state, "admissionRootFingerprint"),
                Token = AdmissionText(state, "admissionToken"),
                SessionId = AdmissionText(state, "admissionSessionId"),
                OwnerState = intended
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
            WriteAdmissionOwnerFields(marker, identity, intendedOwner, false);
            UnityCliActiveOperation.Save(markerScope, new Dictionary<string, object>(marker));
            if (!admissionPool.PrepareOwnerTransfer(ToPoolIdentity(identity), intendedOwner))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner transfer intent does not match the current exact owner.");
            PersistAdmissionPoolState();
            UnityCliAdmissionPool.ConsumedIdentity successor;
            if (!admissionPool.CommitOwnerTransfer(ToPoolIdentity(identity), intendedOwner, out successor))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner transfer could not be committed.");
            var updated = ToAdmissionOwner(successor, identity.RequestDigest);
            marker["admissionTransferCommitted"] = true;
            UnityCliActiveOperation.Save(markerScope, new Dictionary<string, object>(marker));
            PersistAdmissionPoolState();
            return updated;
        }

        /// <summary>同domain重试修复intent或pool已转移但marker未commit的窗口。</summary>
        internal static AdmissionOwnerIdentity ReconcileAdmissionTransferForLiveMarker(string markerScope, Dictionary<string, object> marker, string requestId, string requestDigest)
        {
            if (marker == null || !string.Equals(AdmissionText(marker, "requestId"), requestId, StringComparison.Ordinal)
                || !string.Equals(AdmissionText(marker, "requestDigest"), requestDigest, StringComparison.Ordinal)
                || !string.Equals(AdmissionText(marker, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission cleanup marker identity is invalid.");
            var current = CaptureAdmissionOwner(requestId, requestDigest);
            if (!string.Equals(AdmissionText(marker, "admissionRootFingerprint"), current.RootFingerprint, StringComparison.Ordinal)
                || !string.Equals(AdmissionText(marker, "admissionToken"), current.Token, StringComparison.Ordinal)
                || !string.Equals(AdmissionText(marker, "admissionSessionId"), current.SessionId, StringComparison.Ordinal))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission cleanup marker does not match the exact current owner.");
            var intended = (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionIntendedOwnerState");
            if (current.OwnerState != intended) return PersistAdmissionOwnerTransfer(markerScope, marker, current, intended);
            marker["admissionTransferCommitted"] = true;
            UnityCliActiveOperation.Save(markerScope, marker);
            PersistAdmissionPoolState();
            return current;
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
            else current.RequestDigest = AdmissionText(marker, "admissionRequestDigest");
            marker["admissionTransferCommitted"] = true;
            UnityCliActiveOperation.Save(markerScope, marker);
            PersistAdmissionPoolState();
        }

        private static bool AdmissionMarkerCanAcquireOwner(IDictionary<string, object> marker, AdmissionOwnerIdentity identity)
        {
            return marker != null && identity != null && string.IsNullOrEmpty(AdmissionText(marker, "admissionRootFingerprint"))
                && string.Equals(AdmissionText(marker, "requestId"), identity.RequestId, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "requestDigest"), identity.RequestDigest, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal);
        }

        private static bool AdmissionMarkerMatchesIdentity(IDictionary<string, object> marker, AdmissionOwnerIdentity identity)
        {
            return marker != null && identity != null && string.Equals(AdmissionText(marker, "requestId"), identity.RequestId, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "requestDigest"), identity.RequestDigest, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "admissionRootFingerprint"), identity.RootFingerprint, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "admissionToken"), identity.Token, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "admissionSessionId"), identity.SessionId, StringComparison.Ordinal);
        }

        private static bool AdmissionMarkerMatchesRecord(IDictionary<string, object> marker, UnityCliAdmissionPool.AdmissionRecord record)
        {
            return marker != null && string.Equals(AdmissionText(marker, "requestId"), record.RequestId, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "admissionRootFingerprint"), record.RootFingerprint, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "admissionToken"), record.Token, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "admissionSessionId"), record.SessionId, StringComparison.Ordinal)
                && string.Equals(AdmissionText(marker, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal);
        }

        /// <summary>accepted durable root先写固定scope owner intent；不确定时保留WorkItem而不误释放active。</summary>
        private static bool AdmissionCoordinatorOwns(AdmissionOwnerIdentity identity, string command, string response)
        {
            if (identity == null || !AdmissionDurableRootCommand(command)) return false;
            var target = command == "internal.test.run.cancel" ? UnityCliAdmissionPool.AdmissionOwnerState.Continuation : UnityCliAdmissionPool.AdmissionOwnerState.Coordinator;
            if (identity.OwnerState == target || identity.OwnerState == UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup) return true;
            if (!AdmissionResponseIsCoordinatorAccepted(response)) return false;
            string markerScope;
            string rootScope;
            AdmissionOwnerScopes(command, out markerScope, out rootScope);
            try
            {
                var marker = new Dictionary<string, object>
                {
                    { "requestId", identity.RequestId }, { "requestDigest", identity.RequestDigest }, { "editorSessionId", EditorSessionId() }
                };
                bool durable;
                var readable = TryHasExactDurableBusinessRoot(rootScope, command, identity, out durable);
                var intended = readable && durable ? target : UnityCliAdmissionPool.AdmissionOwnerState.Recovery;
                var updated = PersistAdmissionOwnerTransfer(markerScope + "-admission-owner", marker, identity, intended);
                identity.OwnerState = updated.OwnerState;
                return true;
            }
            catch (Exception exception)
            {
                WriteLog("admission_durable_transfer_pending requestId=" + identity.RequestId + " " + exception.GetType().Name + ": " + exception.Message);
                return true;
            }
        }

        private static bool TryHasExactDurableBusinessRoot(string rootScope, string command, AdmissionOwnerIdentity identity, out bool durable)
        {
            durable = false;
            try
            {
                var state = UnityCliActiveOperation.Load(rootScope);
                if (state == null) return true;
                if (!string.Equals(AdmissionText(state, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) return true;
                if (command == "internal.test.run.cancel")
                {
                    object rawOperation;
                    var operation = state.TryGetValue("testOperation", out rawOperation) ? AsDictionary(rawOperation) : null;
                    durable = operation != null && string.Equals(AdmissionText(operation, "cancelRequestId"), identity.RequestId, StringComparison.Ordinal)
                        && string.Equals(AdmissionText(operation, "cancelRequestDigest"), identity.RequestDigest, StringComparison.Ordinal);
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

        private static bool AdmissionResponseHasTrustedTerminal(string response)
        {
            try
            {
                var envelope = AsDictionary(UnityCliSerializer.Parse(response));
                object ok;
                if (envelope == null || !envelope.TryGetValue("ok", out ok) || !(ok is bool)) return false;
                if ((bool)ok) return true;
                object rawError;
                var error = envelope.TryGetValue("error", out rawError) ? AsDictionary(rawError) : null;
                var code = AdmissionText(error, "code");
                return error != null && code != "E_EXECUTION_TIMEOUT" && code != "E_UNITY_DOMAIN_RELOAD"
                    && code != "E_UNITY_COMPILING" && code != "E_UNITY_IMPORTING" && code != "E_UNITY_PLAY_MODE"
                    && code != "E_QUEUE_START_TIMEOUT";
            }
            catch { return false; }
        }

        private static bool AdmissionDurableRootCommand(string command)
        {
            return command == "internal.build.run.start" || command == "internal.editor.compile.start"
                || command == "internal.editor.refresh.start" || command == "internal.asset.refresh.start"
                || command == "internal.package.embed.start" || command == "internal.package.resolve.start"
                || command == "internal.test.run.start" || command == "internal.test.run.cancel"
                || command == "internal.navmesh.bake.start" || command == "internal.occlusion.bake.start";
        }

        private static bool AdmissionRecoveryCommand(string command)
        {
            return command == "project-tool.list" || command == "internal.editor.state.snapshot"
                || UnityCliCompileCoordinator.IsCommand(command) || UnityCliLiveBuildCoordinator.IsCommand(command)
                || UnityCliLiveRefreshCoordinator.IsCommand(command) || UnityCliAssetRefreshCoordinator.IsCommand(command)
                || UnityCliPackageEmbedCoordinator.IsCommand(command) || UnityCliPackageResolveCoordinator.IsCommand(command)
                || UnityCliTestRunCoordinator.IsCommand(command) || UnityCliNavMeshBakeCoordinator.IsCommand(command)
                || UnityCliOcclusionBakeCoordinator.IsCommand(command);
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
            return new AdmissionOwnerIdentity { RequestId = identity.RequestId, RequestDigest = identity.RequestDigest, RootFingerprint = identity.RootFingerprint, Token = identity.Token, SessionId = identity.SessionId, OwnerState = identity.OwnerState };
        }

        private static UnityCliAdmissionPool.ConsumedIdentity ToPoolIdentity(AdmissionOwnerIdentity identity)
        {
            return identity == null ? null : UnityCliAdmissionPool.CreateConsumedIdentity(identity.RequestId, identity.RootFingerprint, identity.RequestDigest, identity.Token, identity.SessionId, identity.OwnerState);
        }

        private static AdmissionOwnerIdentity RecordIdentity(UnityCliAdmissionPool.AdmissionRecord record)
        {
            return new AdmissionOwnerIdentity { RequestId = record.RequestId, RequestDigest = record.RequestDigest, RootFingerprint = record.RootFingerprint, Token = record.Token, SessionId = record.SessionId, OwnerState = record.OwnerState };
        }

        private static void ClearExactAdmissionOwnerMarker(AdmissionOwnerIdentity identity)
        {
            if (identity == null) return;
            foreach (var scope in AdmissionDurableScopes)
            {
                var markerScope = scope + "-admission-owner";
                try
                {
                    var marker = UnityCliActiveOperation.Load(markerScope);
                    if (marker == null || !string.Equals(AdmissionText(marker, "requestId"), identity.RequestId, StringComparison.Ordinal)
                        || !string.Equals(AdmissionText(marker, "admissionRootFingerprint"), identity.RootFingerprint, StringComparison.Ordinal)
                        || !string.Equals(AdmissionText(marker, "admissionToken"), identity.Token, StringComparison.Ordinal)
                        || !string.Equals(AdmissionText(marker, "admissionSessionId"), identity.SessionId, StringComparison.Ordinal)) continue;
                    UnityCliActiveOperation.Clear(markerScope);
                }
                catch { }
            }
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
