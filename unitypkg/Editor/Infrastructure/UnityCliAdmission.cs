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
        private static UnityCliAdmissionPool admissionPool;

        internal sealed class AdmissionOwnerIdentity
        {
            internal string RequestId;
            internal string RootFingerprint;
            internal string Token;
            internal string SessionId;
            internal UnityCliAdmissionPool.AdmissionOwnerKind OwnerKind;
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
                            Token = AdmissionText(record, "token"),
                            ListenerId = AdmissionText(record, "listenerId"),
                            ListenerGeneration = AdmissionLong(record, "listenerGeneration"),
                            GrantExpiresAtMs = AdmissionLong(record, "grantExpiresAtMs"),
                            ExecutionDeadlineMs = AdmissionLong(record, "executionDeadlineMs"),
                            State = AdmissionText(record, "state") == "consumed" ? UnityCliAdmissionPool.AdmissionState.Consumed : UnityCliAdmissionPool.AdmissionState.Granted,
                            OwnerKind = (UnityCliAdmissionPool.AdmissionOwnerKind)AdmissionLong(record, "ownerKind")
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

        /// <summary>reload前撤销未消费grant，只持久化同Editor启动内的有界consumed identity。</summary>
        private static void SaveAdmissionPoolBeforeReload()
        {
            if (admissionPool == null) return;
            admissionPool.RevokeUnconsumed(ListenerId, CurrentListenerGeneration());
            var snapshot = admissionPool.Capture();
            var records = snapshot.Records.Where(record => record.Consumed).Select(record => (object)new Dictionary<string, object>
            {
                { "instanceId", record.InstanceId }, { "sessionId", record.SessionId },
                { "requestId", record.RequestId }, { "rootFingerprint", record.RootFingerprint }, { "token", record.Token },
                { "listenerId", record.ListenerId }, { "listenerGeneration", record.ListenerGeneration },
                { "grantExpiresAtMs", record.GrantExpiresAtMs }, { "executionDeadlineMs", record.ExecutionDeadlineMs }, { "state", "consumed" }, { "ownerKind", (long)record.OwnerKind }
            }).ToList();
            SessionState.SetString(AdmissionSessionStateKey, UnityCliSerializer.Serialize(new Dictionary<string, object>
            {
                { "editorSessionId", EditorSessionId() }, { "gateClosed", snapshot.GateClosed }, { "records", records },
                { "activeCount", (long)snapshot.Active }, { "recoveryDigest", snapshot.RecoveryDigest },
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

        /// <summary>任何带admission headers的请求都必须授权；无headers只允许内部控制面。</summary>
        private static AdmissionOwnerIdentity AuthorizeAdmission(HttpListenerRequest httpRequest, UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes)
        {
            var rootId = httpRequest.Headers["x-unity-cli-admission-root-id"];
            var fingerprint = httpRequest.Headers["x-unity-cli-admission-fingerprint"];
            var token = httpRequest.Headers["x-unity-cli-admission-token"];
            var deadlineText = httpRequest.Headers["x-unity-cli-admission-deadline-utc"];
            var any = !string.IsNullOrEmpty(rootId) || !string.IsNullOrEmpty(fingerprint) || !string.IsNullOrEmpty(token) || !string.IsNullOrEmpty(deadlineText);
            if (!any)
            {
                if (AdmissionBypassCommand(request.Command)) return null;
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
            if (!string.IsNullOrEmpty(token))
            {
                var outcome = admissionPool.Authorize(rootId, fingerprint, token, ListenerId, CurrentListenerGeneration(), deadlineMs);
                if (outcome == UnityCliAdmissionPool.AuthorizeOutcome.Authorized) return CaptureAdmissionOwner(rootId);
                throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Admission token is missing, expired, reused, or bound to another root.");
            }
            if (admissionPool.AttachConsumed(rootId, fingerprint, ListenerId, CurrentListenerGeneration(), deadlineMs)) return CaptureAdmissionOwner(rootId);
            if (AdmissionRecoveryCommand(request.Command) && admissionPool.RecoverConsumed(rootId, fingerprint, ListenerId, CurrentListenerGeneration(), deadlineMs)) return CaptureAdmissionOwner(rootId);
            if (AdmissionHasExactTerminal(request, rawRequestBytes)) return null;
            throw new BridgeCommandException("E_ADMISSION_IDENTITY_MISMATCH", "Consumed admission root cannot attach to this listener generation.");
        }

        private static AdmissionOwnerIdentity CaptureAdmissionOwner(string requestId)
        {
            UnityCliAdmissionPool.ConsumedIdentity identity;
            if (admissionPool == null || !admissionPool.TryGetConsumedIdentity(requestId, out identity))
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner identity cannot be frozen after consumption.");
            return new AdmissionOwnerIdentity { RequestId = identity.RequestId, RootFingerprint = identity.RootFingerprint, Token = identity.Token, SessionId = identity.SessionId, OwnerKind = identity.OwnerKind };
        }

        internal static bool CompleteAdmissionRoot(AdmissionOwnerIdentity identity)
        {
            if (admissionPool == null || identity == null) return false;
            UnityCliAdmissionPool.ConsumedIdentity current;
            var existed = admissionPool.TryGetConsumedIdentity(identity.RequestId, out current);
            var completed = admissionPool.CompleteTrusted(identity.RequestId, identity.RootFingerprint, identity.Token, identity.SessionId, identity.OwnerKind);
            if (existed && completed) WriteLog("admission_terminal requestId=" + identity.RequestId + " active=" + admissionPool.Capture().Active.ToString(CultureInfo.InvariantCulture));
            return completed;
        }

        internal static void AddAdmissionOwnerFields(IDictionary<string, object> state, string requestId, UnityCliAdmissionPool.AdmissionOwnerKind ownerKind = UnityCliAdmissionPool.AdmissionOwnerKind.Coordinator) { AddAdmissionOwnerFields(state, CaptureAdmissionOwner(requestId), ownerKind); }

        internal static void AddAdmissionOwnerFields(IDictionary<string, object> state, AdmissionOwnerIdentity identity, UnityCliAdmissionPool.AdmissionOwnerKind ownerKind = UnityCliAdmissionPool.AdmissionOwnerKind.Coordinator)
        {
            if (state == null || identity == null) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner marker identity is unavailable.");
            if (identity.OwnerKind != ownerKind)
            {
                if (admissionPool == null || !admissionPool.TransferOwner(identity.RequestId, identity.RootFingerprint, identity.Token, identity.SessionId, ownerKind))
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Admission owner transfer could not be proven.");
                identity.OwnerKind = ownerKind;
            }
            state["admissionRootFingerprint"] = identity.RootFingerprint;
            state["admissionToken"] = identity.Token;
            state["admissionSessionId"] = identity.SessionId;
            state["admissionOwnerKind"] = (long)identity.OwnerKind;
        }

        internal static AdmissionOwnerIdentity ReadAdmissionOwnerFields(IDictionary<string, object> state, string requestId)
        {
            if (state == null || string.IsNullOrEmpty(requestId)) return null;
            return new AdmissionOwnerIdentity
            {
                RequestId = requestId,
                RootFingerprint = AdmissionText(state, "admissionRootFingerprint"),
                Token = AdmissionText(state, "admissionToken"),
                SessionId = AdmissionText(state, "admissionSessionId"),
                OwnerKind = (UnityCliAdmissionPool.AdmissionOwnerKind)AdmissionLong(state, "admissionOwnerKind")
            };
        }

        /// <summary>reload恢复后只保留由固定active root证明仍在执行的consumed admission。</summary>
        private static void ReconcileRestoredAdmissionRoots()
        {
            if (admissionPool == null) return;
            foreach (var record in admissionPool.Capture().Records)
            {
                bool durable;
                if (!TryHasDurableAdmissionRoot(record.RequestId, out durable)) { admissionPool.BlockRecovery(); return; }
                if (!durable)
                {
                    var identity = new AdmissionOwnerIdentity { RequestId = record.RequestId, RootFingerprint = record.RootFingerprint, Token = record.Token, SessionId = record.SessionId, OwnerKind = record.OwnerKind };
                    if (identity.OwnerKind == UnityCliAdmissionPool.AdmissionOwnerKind.WorkItem)
                    {
                        if (!admissionPool.TransferOwner(identity.RequestId, identity.RootFingerprint, identity.Token, identity.SessionId, UnityCliAdmissionPool.AdmissionOwnerKind.Recovery)) { admissionPool.BlockRecovery(); return; }
                        identity.OwnerKind = UnityCliAdmissionPool.AdmissionOwnerKind.Recovery;
                    }
                    CompleteAdmissionRoot(identity);
                }
            }
        }

        private static bool TryHasDurableAdmissionRoot(string requestId, out bool durable)
        {
            durable = false;
            string leaseRequestId;
            string leaseDigest;
            if (!UnityCliSessionMutationLease.TryGetCurrentOwner(out leaseRequestId, out leaseDigest)) return false;
            if (string.Equals(leaseRequestId, requestId, StringComparison.Ordinal)) { durable = true; return true; }

            foreach (var scope in new[] { "compile", "package-resolve", "refresh", "asset-refresh", "package-embed", "test-run", "build", "navmesh-bake", "occlusion-bake", "direct-mutation" })
            {
                foreach (var stateScope in new[] { scope, scope + "-pre-effect-cleanup", scope + "-terminal-cleanup" })
                {
                    try
                    {
                        if (!UnityCliActiveOperation.Exists(stateScope)) continue;
                        var state = UnityCliActiveOperation.Load(stateScope);
                        object rootSession;
                        if (state == null || !state.TryGetValue("editorSessionId", out rootSession)) return false;
                        if (!string.Equals(rootSession as string, EditorSessionId(), StringComparison.Ordinal)) continue;
                        if (AdmissionStateOwnsRequest(state, requestId)) { durable = true; return true; }
                    }
                    catch { return false; }
                }
            }
            return true;
        }

        private static bool AdmissionStateOwnsRequest(IDictionary<string, object> state, string requestId)
        {
            if (string.Equals(AdmissionText(state, "requestId"), requestId, StringComparison.Ordinal)) return true;
            object rawOperation;
            var operation = state != null && state.TryGetValue("testOperation", out rawOperation) ? AsDictionary(rawOperation) : null;
            return operation != null && string.Equals(AdmissionText(operation, "cancelRequestId"), requestId, StringComparison.Ordinal);
        }

        private static bool AdmissionCoordinatorOwns(AdmissionOwnerIdentity identity, string command, string response)
        {
            if (identity == null || !AdmissionDurableRootCommand(command)) return false;
            UnityCliAdmissionPool.ConsumedIdentity current;
            if (admissionPool == null || !admissionPool.TryGetConsumedIdentity(identity.RequestId, out current)) return true;
            if (!string.Equals(current.RootFingerprint, identity.RootFingerprint, StringComparison.Ordinal)
                || !string.Equals(current.Token, identity.Token, StringComparison.Ordinal)
                || !string.Equals(current.SessionId, identity.SessionId, StringComparison.Ordinal)) return true;
            if (current.OwnerKind == UnityCliAdmissionPool.AdmissionOwnerKind.Coordinator) return true;
            bool durable;
            if (!TryHasDurableAdmissionRoot(identity.RequestId, out durable)) return true;
            if (!durable && !AdmissionResponseIsCoordinatorAccepted(response)) return false;
            if (!admissionPool.TransferOwner(identity.RequestId, identity.RootFingerprint, identity.Token, identity.SessionId, UnityCliAdmissionPool.AdmissionOwnerKind.Coordinator)) return true;
            return true;
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
            return IsProjectToolCatalogReadCommand(command) || command == "internal.editor.state.snapshot"
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
                case "project-tool.groups":
                case "project-tool.summary":
                case "project-tool.describe":
                case "project-tool.list":
                case "runtime-tool.groups":
                case "runtime-tool.summary":
                case "runtime-tool.describe":
                case "runtime-tool.list":
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
