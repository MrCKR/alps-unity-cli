#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>跨 domain reload 的 dictionary journal 操作公共协调骨架。</summary>
        internal abstract class UnityCliReloadOperation
        {
            protected abstract string Scope { get; }
            protected abstract string StartCommand { get; }
            protected abstract string StatusCommand { get; }
            protected abstract string OperationDisplayName { get; }
            protected virtual int CommandTimeoutMs { get { return 30000; } }
            protected virtual bool IncludeAcceptedInStatus { get { return false; } }
            protected abstract object DeserializeStartArgs(BridgeRequest request);
            protected abstract void ValidateStart(object args);
            protected abstract object Planned(object args);
            protected abstract Dictionary<string, object> CreateRoot(BridgeRequest request, string digest, object args);
            protected abstract void OnTick(Dictionary<string, object> root);
            protected abstract object BuildSuccessData(Dictionary<string, object> root);
            /// <summary>业务可拒绝语法可读但语义损坏的active root；异常统一进入corrupt recovery。</summary>
            protected virtual void ValidateLoadedRoot(Dictionary<string, object> root) { }

            internal static void WriteEffectFence(string scope, Dictionary<string, object> root, string effectKind)
            {
                UnityCliActiveOperation.Save(scope + "-effect-fence-" + effectKind, new Dictionary<string, object>
                {
                    { "requestId", Text(root, "requestId") }, { "requestDigest", Text(root, "requestDigest") },
                    { "editorSessionId", Text(root, "editorSessionId") }, { "rootContractDigest", Text(root, "rootContractDigest") },
                    { "effectKind", effectKind }
                });
            }

            internal static bool HasExactEffectFence(string scope, Dictionary<string, object> root, string effectKind)
            {
                var fence = UnityCliActiveOperation.Load(scope + "-effect-fence-" + effectKind);
                if (fence == null) return false;
                if (!string.Equals(Text(fence, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) return false;
                return string.Equals(Text(fence, "requestId"), Text(root, "requestId"), StringComparison.Ordinal)
                    && string.Equals(Text(fence, "requestDigest"), Text(root, "requestDigest"), StringComparison.Ordinal)
                    && string.Equals(Text(fence, "rootContractDigest"), Text(root, "rootContractDigest"), StringComparison.Ordinal)
                    && string.Equals(Text(fence, "effectKind"), effectKind, StringComparison.Ordinal);
            }

            /// <summary>fence写入后仅按exact raw identity推进post phase，再用完整validator重读确认。</summary>
            protected Dictionary<string, object> CrossEffectFence(Dictionary<string, object> root, string effectKind, string postPhase)
            {
                WriteEffectFence(Scope, root, effectKind);
                var raw = UnityCliActiveOperation.Load(Scope);
                if (raw == null || !string.Equals(Text(raw, "requestId"), Text(root, "requestId"), StringComparison.Ordinal)
                    || !string.Equals(Text(raw, "requestDigest"), Text(root, "requestDigest"), StringComparison.Ordinal)
                    || !string.Equals(Text(raw, "rootContractDigest"), Text(root, "rootContractDigest"), StringComparison.Ordinal)
                    || !HasExactEffectFence(Scope, raw, effectKind)
                    || !UnityCliSessionMutationLease.IsOwnedBy(Text(raw, "requestId"), Text(raw, "requestDigest")))
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", OperationDisplayName + " effect fence transition cannot be confirmed.");
                raw["phase"] = postPhase;
                UnityCliActiveOperation.Save(Scope, raw);
                var confirmed = Load();
                if (confirmed == null || !string.Equals(Text(confirmed, "phase"), postPhase, StringComparison.Ordinal))
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", OperationDisplayName + " post-fence phase cannot be confirmed.");
                return confirmed;
            }

            protected static void ValidateRootContract(Dictionary<string, object> root, IEnumerable<string> domainKeys, params string[] phases)
            {
                if (root == null) throw new InvalidDataException("Active root is missing.");
                var allowed = new HashSet<string>(new[] { "requestId", "requestDigest", "editorSessionId", "deadline", "phase", "unityCliTerminalEnvelope" }, StringComparer.Ordinal);
                foreach (var key in domainKeys ?? new string[0]) allowed.Add(key);
                foreach (var key in root.Keys) if (!allowed.Contains(key)) throw new InvalidDataException("Active root contains an unknown field: " + key + ".");
                var requestId = Text(root, "requestId");
                var requestDigest = Text(root, "requestDigest");
                var editorSessionId = Text(root, "editorSessionId");
                DateTimeOffset deadline;
                if (!IsSafeId(requestId) || !IsSha256(requestDigest) || string.IsNullOrWhiteSpace(editorSessionId)
                    || !DateTimeOffset.TryParse(Text(root, "deadline"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out deadline)
                    || Array.IndexOf(phases ?? new string[0], Text(root, "phase")) < 0)
                    throw new InvalidDataException("Active root common fields are invalid.");
            }
            /// <summary>供带有私有控制面的操作注册 start/status 之外的命令与唯一queue分类。</summary>
            protected virtual IEnumerable<(string name, bool destructive, UnityCliBridgeQueuePolicy.WorkClass queueClass)> AdditionalCommands { get { yield break; } }
            protected virtual object ExecuteAdditionalCommand(BridgeRequest request)
            {
                throw new BridgeCommandException("E_REQUEST_NOT_FOUND", OperationDisplayName + " command was not found.");
            }
            protected virtual string SessionChangedMessage { get { return OperationDisplayName + " result cannot be confirmed after the Editor session changed; check the business state."; } }
            protected virtual string DeadlineMessage { get { return OperationDisplayName + " did not reach a confirmed terminal state before the deadline; check the business state."; } }
            /// <summary>子类可在 active journal 清除前写入本业务自己的有界终态快照。</summary>
            protected virtual void OnTerminal(Dictionary<string, object> root, object envelope) { }
            /// <summary>root不可读时默认不能把当前global owner归因给本scope；只有业务独立绑定后才可收敛。</summary>
            protected virtual bool OnActiveStateCorrupt(Exception exception)
            {
                string ownerRequestId;
                string ownerRequestDigest;
                if (!UnityCliSessionMutationLease.TryGetOnlyActiveScopeOwner(Scope, out ownerRequestId, out ownerRequestDigest)
                    || string.IsNullOrEmpty(ownerRequestId) || string.IsNullOrEmpty(ownerRequestDigest)) return false;
                var envelope = ProtocolEnvelopeFactory.Error(ownerRequestId, "E_EXECUTION_TIMEOUT", OperationDisplayName + " active state is corrupt or unreadable; outcome is unknown.", null, new[] { "Inspect the affected business state before submitting another mutation." });
                var first = UnityCliActiveOperation.StoreTerminalFirst(Scope, ownerRequestId, ownerRequestDigest, envelope);
                RememberTerminal(ownerRequestId, ownerRequestDigest, first);
                BeginTerminalCleanup(ownerRequestId, ownerRequestDigest);
                return true;
            }
            /// <summary>lease仍存在但active journal丢失时，业务可从自己的durable status保守收敛。</summary>
            protected virtual bool OnActiveStateMissing() { return false; }
            /// <summary>terminal cleanup必须区分可清理、暂时不可验证和确定拒绝。</summary>
            protected enum TerminalCleanupDecision { Cleanup, Retry, Reject }
            /// <summary>marker清理可读root前，业务必须确认同identity terminal已durable发布。</summary>
            protected virtual TerminalCleanupDecision EvaluateTerminalCleanupRoot(Dictionary<string, object> root, string requestId, string requestDigest) { return TerminalCleanupDecision.Cleanup; }
            /// <summary>root不可读时，业务必须仅凭独立durable terminal决定清理、重试或拒绝。</summary>
            protected virtual TerminalCleanupDecision EvaluateUnreadableTerminalCleanup(string requestId, string requestDigest) { return TerminalCleanupDecision.Cleanup; }

            /// <summary>每tick先消费scope准入marker；只有root与lease的exact状态都可证明时才推进或清理owner。</summary>
            protected bool RetryAdmissionOwnerRecoveryMarker()
            {
                var markerScope = Scope + "-admission-owner";
                if (UnityCliActiveOperation.Exists(Scope + "-terminal-cleanup"))
                {
                    try
                    {
                        if (UnityCliActiveOperation.RetryExactTerminalCleanup(Scope)) return true;
                    }
                    catch { return true; }
                }
                if (!UnityCliActiveOperation.Exists(markerScope)) return false;
                Dictionary<string, object> marker;
                try { marker = UnityCliActiveOperation.Load(markerScope); }
                catch { return true; }
                if (marker == null) return true;
                var requestId = Text(marker, "requestId");
                var ownerRequestDigest = Text(marker, "admissionRequestDigest");
                var topRequestDigest = Text(marker, "requestDigest");
                if (!IsSafeId(requestId) || !IsSha256(ownerRequestDigest) || !IsSha256(topRequestDigest)) return true;
                AdmissionOwnerIdentity owner;
                try { owner = ReconcileAdmissionTransferForLiveMarker(markerScope, marker, requestId, ownerRequestDigest); }
                catch { return true; }
                if (owner == null || !string.Equals(owner.TopRequestDigest, topRequestDigest, StringComparison.Ordinal)) return true;

                Dictionary<string, object> root;
                try { root = UnityCliActiveOperation.Load(Scope); }
                catch { return true; }
                if (root != null)
                {
                    var rootMatches = string.Equals(Text(root, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)
                        && string.Equals(Text(root, "requestId"), requestId, StringComparison.Ordinal)
                        && string.Equals(Text(root, "requestDigest"), topRequestDigest, StringComparison.Ordinal);
                    return !rootMatches;
                }

                bool leaseOwned;
                if (!UnityCliSessionMutationLease.TryIsOwnedBy(owner.RequestId, owner.RequestDigest, out leaseOwned)) return true;
                if (leaseOwned)
                {
                    try
                    {
                        var terminal = ProtocolEnvelopeFactory.Error(requestId, "E_EXECUTION_TIMEOUT", OperationDisplayName + " active root disappeared while its admission owner was retained; outcome is unknown.", null, new[] { "Inspect the affected business state before submitting another mutation." });
                        UnityCliActiveOperation.StoreTerminalFirst(Scope, requestId, topRequestDigest, terminal);
                        UnityCliActiveOperation.BeginExactTerminalCleanup(Scope, requestId, topRequestDigest);
                    }
                    catch { return true; }
                    return true;
                }

                try
                {
                    if (owner.OwnerState != UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup)
                    {
                        var cleanupMarker = new Dictionary<string, object>(marker);
                        owner = PersistAdmissionOwnerTransfer(markerScope, cleanupMarker, owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
                    }
                    return !CompleteAdmissionRoot(owner);
                }
                catch { return true; }
            }
            /// <summary>effect前放弃只能清理同identity且由业务证明从未越过effect barrier的root。</summary>
            protected virtual bool CanCleanupAbandonedPreEffectRoot(Dictionary<string, object> root, string requestId, string requestDigest) { return false; }
            /// <summary>root删除后、lease释放前，业务可清理同identity且未dispatch的附属status；失败必须重试。</summary>
            protected virtual bool FinalizeAbandonedPreEffectState(string requestId, string requestDigest) { return true; }
            /// <summary>deadline/session检查前恢复公共durable terminal；业务专用status可覆盖此实现。</summary>
            protected virtual bool TryResumePublishedTerminal(Dictionary<string, object> root)
            {
                object envelope;
                if (root == null || !root.TryGetValue("unityCliTerminalEnvelope", out envelope) || envelope == null) return false;
                var requestId = Text(root, "requestId");
                var requestDigest = Text(root, "requestDigest");
                var first = UnityCliActiveOperation.StoreTerminalFirst(Scope, requestId, requestDigest, envelope);
                RememberTerminal(requestId, requestDigest, first);
                BeginTerminalCleanup(requestId, requestDigest);
                return true;
            }

            private string TerminalCleanupScope { get { return Scope + "-terminal-cleanup"; } }
            private string PreEffectCleanupScope { get { return Scope + "-pre-effect-cleanup"; } }
            private DateTime nextTickUtc;
            private string lastTerminalRequestId;
            private string lastTerminalRequestDigest;
            private object lastTerminalEnvelope;
            private string pendingTerminalCleanupRequestId;
            private string pendingTerminalCleanupRequestDigest;
            private string pendingPreEffectCleanupRequestId;
            private string pendingPreEffectCleanupRequestDigest;
            private AdmissionOwnerIdentity pendingPreEffectAdmissionOwner;
            private AdmissionOwnerIdentity pendingTerminalAdmissionOwner;
            protected bool LastLoadWasCorrupt { get; private set; }
            protected bool LastLoadWasUnreadable { get; private set; }
            protected bool LastLoadWasMissing { get; private set; }
            protected bool LastLoadRecoveredTerminal { get; private set; }

            internal bool IsCommand(string command)
            {
                if (command == StartCommand || command == StatusCommand) return true;
                foreach (var additional in AdditionalCommands) if (command == additional.name) return true;
                return false;
            }
            internal void Register(BridgeCommandRegistry registry)
            {
                registry.Register<BridgeRequest>(StartCommand, "internal", true, CommandTimeoutMs, (r, c) => { throw new InvalidOperationException(OperationDisplayName + " is coordinator-owned."); }, UnityCliBridgeQueuePolicy.WorkClass.Mutation);
                registry.Register<BridgeRequest>(StatusCommand, "internal", false, CommandTimeoutMs, (r, c) => { throw new InvalidOperationException(OperationDisplayName + " is coordinator-owned."); }, UnityCliBridgeQueuePolicy.WorkClass.SafeStatus);
                foreach (var additional in AdditionalCommands)
                    registry.Register<BridgeRequest>(additional.name, "internal", additional.destructive, CommandTimeoutMs, (r, c) => { throw new InvalidOperationException(OperationDisplayName + " is coordinator-owned."); }, additional.queueClass);
            }

            /// <summary>HTTP 接受后建立或续接唯一 journal；lease 成功后任意异常都必须释放它。</summary>
            internal virtual async Task<string> Execute(BridgeRequest request, byte[] rawBytes, Func<bool> tryAccept, CancellationToken cancellationToken)
            {
                if (request.Command == StatusCommand) return UnityCliSerializer.Serialize(Status(request));
                foreach (var additional in AdditionalCommands)
                    if (request.Command == additional.name) return UnityCliSerializer.Serialize(ExecuteAdditionalCommand(request));
                var args = DeserializeStartArgs(request);
                ValidateStart(args);
                if (request.Execution != null && request.Execution.DryRun == true)
                    return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, Planned(args)));
                var digest = UnityCliRequestDigest.Compute(rawBytes, request.InstanceId);
                var recovered = RecoveredTerminal(request.RequestId, digest);
                if (recovered != null)
                    return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, recovered));
                string currentOwnerRequestId;
                string currentOwnerRequestDigest;
                if (!UnityCliSessionMutationLease.TryGetCurrentOwner(out currentOwnerRequestId, out currentOwnerRequestDigest))
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", OperationDisplayName + " lease ownership cannot be read; outcome is unknown.");
                if (string.Equals(currentOwnerRequestId, request.RequestId, StringComparison.Ordinal)
                    && !string.Equals(currentOwnerRequestDigest, digest, StringComparison.Ordinal))
                    throw new BridgeCommandException("E_REQUEST_ID_REUSED", OperationDisplayName + " requestId was reused with different input.");

                var root = Load();
                if (root != null)
                {
                    if (!string.Equals(Text(root, "requestId"), request.RequestId, StringComparison.Ordinal)) return Busy(request.RequestId);
                    if (!string.Equals(Text(root, "requestDigest"), digest, StringComparison.Ordinal))
                        throw new BridgeCommandException("E_REQUEST_ID_REUSED", OperationDisplayName + " requestId was reused with different input.");
                    if (TryResumePublishedTerminal(root))
                        return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, RecoveredTerminal(request.RequestId, digest)));
                    bool recoveredLease;
                    if (!UnityCliSessionMutationLease.TryRecoverExact(request.RequestId, digest, out recoveredLease)) return Busy(request.RequestId);
                    await Task.FromResult(0);
                    return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "terminal", false }, { "accepted", true }, { "rootRequestDigest", digest } }));
                }
                if (LastLoadWasCorrupt || LastLoadWasUnreadable || LastLoadRecoveredTerminal
                    || (LastLoadWasMissing && !string.IsNullOrEmpty(currentOwnerRequestId)))
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", OperationDisplayName + " active state is unavailable; outcome is unknown.");

                bool acquired;
                if (!UnityCliSessionMutationLease.TryAcquire(request.RequestId, digest, out acquired) || !acquired) return Busy(request.RequestId);
                try
                {
                    if (tryAccept == null || !tryAccept()) throw new OperationCanceledException("HTTP timeout cancelled " + OperationDisplayName.ToLowerInvariant() + " before acceptance.");
                    root = CreateRoot(request, digest, args);
                    Save(root);
                }
                catch
                {
                    UnityCliSessionMutationLease.Release(request.RequestId, digest);
                    throw;
                }
                await Task.FromResult(0);
                return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "terminal", false }, { "accepted", true }, { "rootRequestDigest", digest } }));
            }

            /// <summary>status 只读取 active journal 或本 session 的终态缓存，绝不获取 mutation lease。</summary>
            protected virtual object Status(BridgeRequest request)
            {
                var args = request.DeserializeArgs<StatusArgs>();
                if (args == null || string.IsNullOrWhiteSpace(args.RootRequestId) || !IsSha256(args.RootRequestDigest)) throw new BridgeCommandException("E_SCHEMA_INVALID", OperationDisplayName + " status requires rootRequestId and rootRequestDigest.");
                object durable;
                if (UnityCliActiveOperation.TryGetTerminal(Scope, args.RootRequestId, args.RootRequestDigest, out durable))
                    return ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", durable } });
                var root = Load();
                if (root != null && string.Equals(Text(root, "requestId"), args.RootRequestId, StringComparison.Ordinal))
                {
                    if (!string.Equals(Text(root, "requestDigest"), args.RootRequestDigest, StringComparison.Ordinal))
                        throw new BridgeCommandException("E_REQUEST_ID_REUSED", OperationDisplayName + " requestId was reused with different input.");
                    var data = new Dictionary<string, object> { { "terminal", false }, { "phase", Text(root, "phase") }, { "rootRequestDigest", args.RootRequestDigest } };
                    if (IncludeAcceptedInStatus) data["accepted"] = true;
                    return ProtocolEnvelopeFactory.Success(request.RequestId, data);
                }
                if (lastTerminalEnvelope != null && string.Equals(lastTerminalRequestId, args.RootRequestId, StringComparison.Ordinal))
                {
                    if (!string.Equals(lastTerminalRequestDigest, args.RootRequestDigest, StringComparison.Ordinal))
                        throw new BridgeCommandException("E_REQUEST_ID_REUSED", OperationDisplayName + " requestId was reused with different input.");
                    return ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", lastTerminalEnvelope } });
                }
                throw new BridgeCommandException("E_REQUEST_NOT_FOUND", OperationDisplayName + " request was not found.");
            }

            /// <summary>每帧只推进当前 lease owner；session/deadline 不可确认时先持久化失败终态。</summary>
            internal virtual void Tick()
            {
                if (RetryAdmissionOwnerRecoveryMarker()) return;
                if (DateTime.UtcNow < nextTickUtc) return;
                nextTickUtc = DateTime.UtcNow.AddMilliseconds(100);
                if (RetryPreEffectCleanup()) return;
                if (RetryTerminalCleanup()) return;
                var root = Load();
                if (root == null || !UnityCliSessionMutationLease.IsOwnedBy(Text(root, "requestId"), Text(root, "requestDigest"))) return;
                if (TryResumePublishedTerminal(root)) return;
                if (!string.Equals(Text(root, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) { Fail(root, "E_EXECUTION_TIMEOUT", SessionChangedMessage); return; }
                if (DateTimeOffset.UtcNow >= Time(root, "deadline")) { Fail(root, "E_EXECUTION_TIMEOUT", DeadlineMessage); return; }
                OnTick(root);
            }

            /// <summary>业务终态先写active与固定terminal cache，再由cleanup marker驱动root与lease收敛。</summary>
            protected void Terminal(Dictionary<string, object> root, object envelope)
            {
                var requestId = Text(root, "requestId");
                var requestDigest = Text(root, "requestDigest");
                bool isOwned;
                if (!UnityCliSessionMutationLease.TryIsOwnedBy(requestId, requestDigest, out isOwned) || !isOwned)
                    envelope = ProtocolEnvelopeFactory.Error(requestId, "E_EXECUTION_TIMEOUT", OperationDisplayName + " lost its exact mutation lease after effect; outcome is unknown.", null, new[] { "Inspect the affected business state before submitting another mutation." });
                OnTerminal(root, envelope);
                root["unityCliTerminalEnvelope"] = envelope;
                Save(root);
                var first = UnityCliActiveOperation.StoreTerminalFirst(Scope, requestId, requestDigest, envelope);
                if (!string.Equals(UnityCliSerializer.Serialize(first), UnityCliSerializer.Serialize(envelope), StringComparison.Ordinal))
                {
                    root["unityCliTerminalEnvelope"] = first;
                    Save(root);
                }
                RememberTerminal(requestId, requestDigest, first);
                BeginTerminalCleanup(requestId, requestDigest);
            }

            /// <summary>业务终态已durable时只恢复cleanup，不得再次调用OnTerminal改写结果。</summary>
            protected void ResumeTerminalCleanup(Dictionary<string, object> root)
            {
                BeginTerminalCleanup(Text(root, "requestId"), Text(root, "requestDigest"));
            }
            /// <summary>同一 Editor session 的重复 root 只返回exact durable terminal，不能重放 effect。</summary>
            protected Dictionary<string, object> RecoveredTerminal(string requestId, string requestDigest)
            {
                if (string.IsNullOrEmpty(requestDigest))
                {
                    if (lastTerminalEnvelope == null || !string.Equals(lastTerminalRequestId, requestId, StringComparison.Ordinal)) return null;
                    return new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", lastTerminalEnvelope } };
                }
                object envelope;
                if (!UnityCliActiveOperation.TryGetTerminal(Scope, requestId, requestDigest, out envelope)) return null;
                RememberTerminal(requestId, requestDigest, envelope);
                return new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", envelope } };
            }

            private void RememberTerminal(string requestId, string requestDigest, object envelope)
            {
                lastTerminalRequestId = requestId;
                lastTerminalRequestDigest = requestDigest;
                lastTerminalEnvelope = envelope;
            }
            protected void Succeed(Dictionary<string, object> root) { Terminal(root, ProtocolEnvelopeFactory.Success(Text(root, "requestId"), BuildSuccessData(root))); }
            protected void Fail(Dictionary<string, object> root, string code, string message)
            {
                Fail(root, code, message, null, new[] { "Inspect editor.state before submitting a new request." });
            }

            /// <summary>业务失败可附带有界公开证明；terminal持久化与cleanup仍由统一骨架负责。</summary>
            protected void Fail(Dictionary<string, object> root, string code, string message, object details, string[] nextActions)
            {
                WriteLog(Scope + "_failed requestId=" + Text(root, "requestId") + " code=" + code + " message=" + message);
                Terminal(root, ProtocolEnvelopeFactory.Error(Text(root, "requestId"), code, message, details, nextActions));
            }
            protected string Busy(string requestId) { return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(requestId, "E_UNITY_BUSY", "Another mutation owns the selected Unity instance.")); }
            /// <summary>
            /// 读取唯一active journal。文件I/O不确定时保持root/owner原状等待下一轮；只有成功读取后的结构或语义错误
            /// 才进入corruption终态。owner身份始终从同一次snapshot取得，禁止拼接跨代requestId与digest。
            /// </summary>
            protected Dictionary<string, object> Load()
            {
                LastLoadWasCorrupt = false;
                LastLoadWasUnreadable = false;
                LastLoadWasMissing = false;
                LastLoadRecoveredTerminal = false;
                try
                {
                    var root = UnityCliActiveOperation.Load(Scope);
                    if (root != null)
                    {
                        ValidateLoadedRoot(root);
                        return root;
                    }
                }
                catch (IOException exception)
                {
                    LastLoadWasUnreadable = true;
                    WriteLog(Scope + " active state cannot be read yet: " + exception.Message);
                    return null;
                }
                catch (UnauthorizedAccessException exception)
                {
                    LastLoadWasUnreadable = true;
                    WriteLog(Scope + " active state cannot be read yet: " + exception.Message);
                    return null;
                }
                catch (Exception exception)
                {
                    LastLoadWasCorrupt = true;
                    WriteLog(Scope + " active state corrupt: " + exception.Message);
                    string ownerRequestId;
                    string ownerRequestDigest;
                    var ownerReadable = UnityCliSessionMutationLease.TryGetCurrentOwner(out ownerRequestId, out ownerRequestDigest);
                    var terminalStored = false;
                    try { terminalStored = OnActiveStateCorrupt(exception); }
                    catch (Exception recoveryException) { WriteLog(Scope + " corrupt state recovery failed: " + recoveryException.Message); }
                    if (terminalStored && ownerReadable) BeginTerminalCleanup(ownerRequestId, ownerRequestDigest);
                    else if (terminalStored) WriteLog(Scope + " corrupt terminal cleanup waits for readable lease ownership.");
                    return null;
                }

                LastLoadWasMissing = true;
                string missingOwnerRequestId;
                string missingOwnerRequestDigest;
                if (!UnityCliSessionMutationLease.TryGetCurrentOwner(out missingOwnerRequestId, out missingOwnerRequestDigest))
                {
                    WriteLog(Scope + " missing active state recovery waits for readable lease ownership.");
                    return null;
                }
                if (!string.IsNullOrEmpty(missingOwnerRequestId) && !string.IsNullOrEmpty(missingOwnerRequestDigest) && OnActiveStateMissing())
                {
                    LastLoadRecoveredTerminal = true;
                    BeginTerminalCleanup(missingOwnerRequestId, missingOwnerRequestDigest);
                }
                return null;
            }
            protected void Save(Dictionary<string, object> root) { UnityCliActiveOperation.Save(Scope, root); }

            /// <summary>effect前失败先发布fixed marker，再精确删除root并释放owner；失败重试不能重新dispatch。</summary>
            protected void BeginPreEffectCleanup(Dictionary<string, object> root)
            {
                var requestId = Text(root, "requestId");
                var requestDigest = Text(root, "requestDigest");
                if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(requestDigest))
                {
                    WriteLog(Scope + " pre-effect cleanup identity is missing.");
                    return;
                }
                pendingPreEffectCleanupRequestId = requestId;
                pendingPreEffectCleanupRequestDigest = requestDigest;
                try { pendingPreEffectAdmissionOwner = CaptureAdmissionOwner(requestId, requestDigest); }
                catch (Exception exception) { WriteLog(Scope + " pre-effect admission owner freeze failed: " + exception.Message); return; }
                var marker = new Dictionary<string, object>
                {
                    { "requestId", requestId }, { "requestDigest", requestDigest }, { "editorSessionId", EditorSessionId() }
                };
                try { pendingPreEffectAdmissionOwner = PersistAdmissionOwnerTransfer(PreEffectCleanupScope, marker, pendingPreEffectAdmissionOwner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup); }
                catch (Exception exception) { WriteLog(Scope + " pre-effect cleanup owner transfer failed: " + exception.Message); return; }
                RetryPreEffectCleanup();
            }

            /// <summary>domain reload初始化只恢复fixed pre-effect cleanup，不推进业务root。</summary>
            protected bool ResumePreEffectCleanup() { return RetryPreEffectCleanup(); }

            private bool RetryPreEffectCleanup()
            {
                Dictionary<string, object> cleanup = null;
                var markerReadFailed = false;
                if (UnityCliActiveOperation.Exists(PreEffectCleanupScope))
                {
                    try { cleanup = UnityCliActiveOperation.Load(PreEffectCleanupScope); }
                    catch (Exception exception)
                    {
                        markerReadFailed = true;
                        WriteLog(Scope + " pre-effect cleanup marker read failed: " + exception.Message);
                    }
                }
                if (cleanup == null && !string.IsNullOrEmpty(pendingPreEffectCleanupRequestId))
                {
                    cleanup = new Dictionary<string, object>
                    {
                        { "requestId", pendingPreEffectCleanupRequestId },
                        { "requestDigest", pendingPreEffectCleanupRequestDigest },
                        { "editorSessionId", EditorSessionId() }
                    };
                    AddAdmissionOwnerFields(cleanup, pendingPreEffectAdmissionOwner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
                }
                if (cleanup == null) return markerReadFailed;
                if (markerReadFailed || !UnityCliActiveOperation.Exists(PreEffectCleanupScope))
                {
                    try { UnityCliActiveOperation.Save(PreEffectCleanupScope, cleanup); }
                    catch (Exception exception) { WriteLog(Scope + " pre-effect cleanup marker retry failed: " + exception.Message); return true; }
                }
                var requestId = Text(cleanup, "requestId");
                var requestDigest = Text(cleanup, "requestDigest");
                if (!string.Equals(Text(cleanup, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal))
                {
                    try { UnityCliActiveOperation.Clear(PreEffectCleanupScope); } catch { return true; }
                    ClearPendingPreEffectCleanup(requestId, requestDigest);
                    return true;
                }
                if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(requestDigest)) return true;
                AdmissionOwnerIdentity admissionOwner;
                try { admissionOwner = ReconcileAdmissionTransferForLiveMarker(PreEffectCleanupScope, cleanup, requestId, requestDigest); }
                catch (Exception exception) { WriteLog(Scope + " pre-effect admission transfer retry failed: " + exception.Message); return true; }
                if (admissionOwner == null) { ClearPendingPreEffectCleanup(requestId, requestDigest); return true; }

                Dictionary<string, object> active = null;
                if (UnityCliActiveOperation.Exists(Scope))
                {
                    try { active = UnityCliActiveOperation.Load(Scope); }
                    catch (Exception exception) { WriteLog(Scope + " abandoned pre-effect root read failed: " + exception.Message); return true; }
                }
                if (active != null)
                {
                    var markerMatchesActive = string.Equals(Text(active, "requestId"), requestId, StringComparison.Ordinal)
                        && string.Equals(Text(active, "requestDigest"), requestDigest, StringComparison.Ordinal);
                    if (!markerMatchesActive)
                    {
                        if (!LeaseDefinitelyNotOwned(requestId, requestDigest)) return true;
                        if (!CompleteAdmissionRoot(admissionOwner)) return true;
                        try { UnityCliActiveOperation.Clear(PreEffectCleanupScope); } catch { return true; }
                        ClearPendingPreEffectCleanup(requestId, requestDigest);
                        return true;
                    }
                    if (!CanCleanupAbandonedPreEffectRoot(active, requestId, requestDigest)) return true;
                    try { UnityCliActiveOperation.Clear(Scope); }
                    catch (Exception exception) { WriteLog(Scope + " abandoned pre-effect root cleanup failed: " + exception.Message); return true; }
                    if (UnityCliActiveOperation.Exists(Scope)) return true;
                }
                try
                {
                    if (!FinalizeAbandonedPreEffectState(requestId, requestDigest)) return true;
                }
                catch (Exception exception) { WriteLog(Scope + " abandoned pre-effect state cleanup failed: " + exception.Message); return true; }
                try { UnityCliSessionMutationLease.Release(requestId, requestDigest); }
                catch (Exception exception) { WriteLog(Scope + " pre-effect lease release failed: " + exception.Message); return true; }
                if (!LeaseDefinitelyNotOwned(requestId, requestDigest)) return true;
                if (!CompleteAdmissionRoot(admissionOwner)) return true;
                try { UnityCliActiveOperation.Clear(PreEffectCleanupScope); }
                catch (Exception exception) { WriteLog(Scope + " pre-effect cleanup marker clear failed: " + exception.Message); return true; }
                ClearPendingPreEffectCleanup(requestId, requestDigest);
                return true;
            }

            private void ClearPendingPreEffectCleanup(string requestId, string requestDigest)
            {
                if (!string.Equals(pendingPreEffectCleanupRequestId, requestId, StringComparison.Ordinal)
                    || !string.Equals(pendingPreEffectCleanupRequestDigest, requestDigest, StringComparison.Ordinal)) return;
                pendingPreEffectCleanupRequestId = null;
                pendingPreEffectCleanupRequestDigest = null;
                pendingPreEffectAdmissionOwner = null;
            }

            /// <summary>固定marker使active root已删后的lease release失败仍可由后续Tick重试。</summary>
            private void BeginTerminalCleanup(string requestId, string requestDigest)
            {
                if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(requestDigest))
                {
                    WriteLog(Scope + " terminal cleanup identity is missing.");
                    return;
                }
                pendingTerminalCleanupRequestId = requestId;
                pendingTerminalCleanupRequestDigest = requestDigest;
                try { pendingTerminalAdmissionOwner = CaptureAdmissionOwner(requestId, requestDigest); }
                catch (Exception exception) { WriteLog(Scope + " terminal admission owner freeze failed: " + exception.Message); return; }
                var marker = new Dictionary<string, object>
                {
                    { "requestId", requestId }, { "requestDigest", requestDigest }, { "editorSessionId", EditorSessionId() }
                };
                try { pendingTerminalAdmissionOwner = PersistAdmissionOwnerTransfer(TerminalCleanupScope, marker, pendingTerminalAdmissionOwner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup); }
                catch (Exception exception) { WriteLog(Scope + " terminal cleanup owner transfer failed: " + exception.Message); return; }
                RetryTerminalCleanup();
            }

            /// <summary>marker只能清理同identity root；旧marker绝不能删除后继request的active root。</summary>
            private bool RetryTerminalCleanup()
            {
                Dictionary<string, object> cleanup = null;
                if (UnityCliActiveOperation.Exists(TerminalCleanupScope))
                {
                    try { cleanup = UnityCliActiveOperation.Load(TerminalCleanupScope); }
                    catch (Exception exception)
                    {
                        WriteLog(Scope + " terminal cleanup marker read failed: " + exception.Message);
                        return true;
                    }
                }
                if (cleanup == null && !string.IsNullOrEmpty(pendingTerminalCleanupRequestId))
                {
                    cleanup = new Dictionary<string, object>
                    {
                        { "requestId", pendingTerminalCleanupRequestId },
                        { "requestDigest", pendingTerminalCleanupRequestDigest },
                        { "editorSessionId", EditorSessionId() }
                    };
                    AddAdmissionOwnerFields(cleanup, pendingTerminalAdmissionOwner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
                }
                if (cleanup == null) return false;
                if (!UnityCliActiveOperation.Exists(TerminalCleanupScope))
                {
                    try { UnityCliActiveOperation.Save(TerminalCleanupScope, cleanup); }
                    catch (Exception exception) { WriteLog(Scope + " terminal cleanup marker retry failed: " + exception.Message); return true; }
                    if (!UnityCliActiveOperation.Exists(TerminalCleanupScope)) return true;
                }
                if (!string.Equals(Text(cleanup, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal))
                {
                    try { UnityCliActiveOperation.Clear(TerminalCleanupScope); } catch { }
                    ClearPendingTerminalCleanup(Text(cleanup, "requestId"), Text(cleanup, "requestDigest"));
                    return true;
                }
                var requestId = Text(cleanup, "requestId");
                var requestDigest = Text(cleanup, "requestDigest");
                if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(requestDigest)) return true;
                AdmissionOwnerIdentity admissionOwner;
                try { admissionOwner = ReconcileAdmissionTransferForLiveMarker(TerminalCleanupScope, cleanup, requestId, requestDigest); }
                catch (Exception exception) { WriteLog(Scope + " terminal admission transfer retry failed: " + exception.Message); return true; }
                if (admissionOwner == null) { ClearPendingTerminalCleanup(requestId, requestDigest); return true; }

                Dictionary<string, object> active = null;
                if (UnityCliActiveOperation.Exists(Scope))
                {
                    try { active = UnityCliActiveOperation.Load(Scope); }
                    catch (Exception exception)
                    {
                        WriteLog(Scope + " terminal active root read failed: " + exception.Message);
                        if (!UnityCliSessionMutationLease.IsOwnedBy(requestId, requestDigest))
                        {
                            if (!CompleteAdmissionRoot(admissionOwner)) return true;
                            try { UnityCliActiveOperation.Clear(TerminalCleanupScope); } catch { return true; }
                            ClearPendingTerminalCleanup(requestId, requestDigest);
                            return true;
                        }
                        var unreadableDecision = EvaluateUnreadableTerminalCleanup(requestId, requestDigest);
                        if (unreadableDecision == TerminalCleanupDecision.Retry) return true;
                        if (unreadableDecision == TerminalCleanupDecision.Reject) return true;
                        try { UnityCliActiveOperation.Clear(Scope); }
                        catch (Exception clearException) { WriteLog(Scope + " corrupt terminal root cleanup failed: " + clearException.Message); return true; }
                        if (UnityCliActiveOperation.Exists(Scope)) return true;
                    }
                }
                if (active != null)
                {
                    var activeRequestId = Text(active, "requestId");
                    var activeRequestDigest = Text(active, "requestDigest");
                    var markerMatchesActive = string.Equals(activeRequestId, requestId, StringComparison.Ordinal)
                        && string.Equals(activeRequestDigest, requestDigest, StringComparison.Ordinal);
                    if (!markerMatchesActive)
                    {
                        if (!LeaseDefinitelyNotOwned(requestId, requestDigest)) return true;
                        if (!CompleteAdmissionRoot(admissionOwner)) return true;
                        try { UnityCliActiveOperation.Clear(TerminalCleanupScope); } catch { return true; }
                        ClearPendingTerminalCleanup(requestId, requestDigest);
                        return true;
                    }
                    var cleanupDecision = EvaluateTerminalCleanupRoot(active, requestId, requestDigest);
                    if (cleanupDecision == TerminalCleanupDecision.Retry) return true;
                    if (cleanupDecision == TerminalCleanupDecision.Reject) return true;
                    try { UnityCliActiveOperation.Clear(Scope); }
                    catch (Exception exception) { WriteLog(Scope + " terminal active root cleanup failed: " + exception.Message); return true; }
                    if (UnityCliActiveOperation.Exists(Scope)) return true;
                }
                try { UnityCliSessionMutationLease.Release(requestId, requestDigest); }
                catch (Exception exception) { WriteLog(Scope + " terminal lease release failed: " + exception.Message); return true; }
                if (!LeaseDefinitelyNotOwned(requestId, requestDigest)) return true;
                if (!CompleteAdmissionRoot(admissionOwner)) return true;
                try { UnityCliActiveOperation.Clear(TerminalCleanupScope); }
                catch (Exception exception) { WriteLog(Scope + " terminal cleanup marker clear failed: " + exception.Message); return true; }
                ClearPendingTerminalCleanup(requestId, requestDigest);
                return true;
            }

            /// <summary>只有ownership读取成功且exact owner不存在时才允许删除cleanup marker。</summary>
            private static bool LeaseDefinitelyNotOwned(string requestId, string requestDigest)
            {
                bool isOwned;
                return UnityCliSessionMutationLease.TryIsOwnedBy(requestId, requestDigest, out isOwned) && !isOwned;
            }

            /// <summary>只清除同identity的进程内retry，避免旧marker完成时覆盖新terminal cleanup。</summary>
            private void ClearPendingTerminalCleanup(string requestId, string requestDigest)
            {
                if (!string.Equals(pendingTerminalCleanupRequestId, requestId, StringComparison.Ordinal)
                    || !string.Equals(pendingTerminalCleanupRequestDigest, requestDigest, StringComparison.Ordinal)) return;
                pendingTerminalCleanupRequestId = null;
                pendingTerminalCleanupRequestDigest = null;
                pendingTerminalAdmissionOwner = null;
            }

            /// <summary>只在fresh active root成功读取并durable保存时返回true；调用方不得回退到旧内存root。</summary>
            internal bool Update(Action<Dictionary<string, object>> action)
            {
                var root = Load();
                if (root == null) return false;
                action(root);
                Save(root);
                return true;
            }
            protected static string Text(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item != null ? Convert.ToString(item, CultureInfo.InvariantCulture) : string.Empty; }
            protected static bool Bool(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item is bool && (bool)item; }
            protected static long Long(IDictionary<string, object> value, string key) { long result; return long.TryParse(Text(value, key), out result) ? result : 0L; }
            protected static DateTimeOffset Time(IDictionary<string, object> value, string key) { DateTimeOffset result; return DateTimeOffset.TryParse(Text(value, key), out result) ? result : DateTimeOffset.MinValue; }
            private sealed class StatusArgs { public string RootRequestId { get; set; } public string RootRequestDigest { get; set; } }
        }
    }
}
#endif
