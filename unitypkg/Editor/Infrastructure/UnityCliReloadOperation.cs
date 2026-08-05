#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
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
            /// <summary>供带有私有控制面的操作注册 start/status 之外的命令。</summary>
            protected virtual IEnumerable<(string name, bool destructive)> AdditionalCommands { get { yield break; } }
            protected virtual object ExecuteAdditionalCommand(BridgeRequest request)
            {
                throw new BridgeCommandException("E_REQUEST_NOT_FOUND", OperationDisplayName + " command was not found.");
            }
            protected virtual string SessionChangedMessage { get { return OperationDisplayName + " result cannot be confirmed after the Editor session changed; check the business state."; } }
            protected virtual string DeadlineMessage { get { return OperationDisplayName + " did not reach a confirmed terminal state before the deadline; check the business state."; } }

            private DateTime nextTickUtc;
            private string lastTerminalRequestId;
            private object lastTerminalEnvelope;

            internal bool IsCommand(string command)
            {
                if (command == StartCommand || command == StatusCommand) return true;
                foreach (var additional in AdditionalCommands) if (command == additional.name) return true;
                return false;
            }
            internal void Register(BridgeCommandRegistry registry)
            {
                registry.Register<BridgeRequest>(StartCommand, "internal", true, CommandTimeoutMs, (r, c) => { throw new InvalidOperationException(OperationDisplayName + " is coordinator-owned."); });
                registry.Register<BridgeRequest>(StatusCommand, "internal", false, CommandTimeoutMs, (r, c) => { throw new InvalidOperationException(OperationDisplayName + " is coordinator-owned."); });
                foreach (var additional in AdditionalCommands)
                    registry.Register<BridgeRequest>(additional.name, "internal", additional.destructive, CommandTimeoutMs, (r, c) => { throw new InvalidOperationException(OperationDisplayName + " is coordinator-owned."); });
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
                bool acquired;
                var digest = UnityCliRequestDigest.Compute(rawBytes, request.InstanceId);
                if (!UnityCliSessionMutationLease.TryAcquire(request.RequestId, digest, out acquired)) return Busy(request.RequestId);
                try
                {
                    var root = Load();
                    if (root == null)
                    {
                        if (tryAccept == null || !tryAccept()) throw new OperationCanceledException("HTTP timeout cancelled " + OperationDisplayName.ToLowerInvariant() + " before acceptance.");
                        root = CreateRoot(request, digest, args);
                        Save(root);
                    }
                    else if (!string.Equals(Text(root, "requestId"), request.RequestId, StringComparison.Ordinal)) return Busy(request.RequestId);
                }
                catch
                {
                    if (acquired) UnityCliSessionMutationLease.Release(request.RequestId);
                    throw;
                }
                await Task.FromResult(0);
                return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "terminal", false }, { "accepted", true } }));
            }

            /// <summary>status 只读取 active journal 或本 session 的终态缓存，绝不获取 mutation lease。</summary>
            protected virtual object Status(BridgeRequest request)
            {
                var args = request.DeserializeArgs<StatusArgs>();
                if (args == null || string.IsNullOrWhiteSpace(args.RootRequestId)) throw new BridgeCommandException("E_SCHEMA_INVALID", OperationDisplayName + " status requires rootRequestId.");
                var root = Load();
                if (root != null && string.Equals(Text(root, "requestId"), args.RootRequestId, StringComparison.Ordinal))
                {
                    var data = new Dictionary<string, object> { { "terminal", false }, { "phase", Text(root, "phase") } };
                    if (IncludeAcceptedInStatus) data["accepted"] = true;
                    return ProtocolEnvelopeFactory.Success(request.RequestId, data);
                }
                if (lastTerminalEnvelope != null && string.Equals(lastTerminalRequestId, args.RootRequestId, StringComparison.Ordinal))
                    return ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", lastTerminalEnvelope } });
                throw new BridgeCommandException("E_REQUEST_NOT_FOUND", OperationDisplayName + " request was not found.");
            }

            /// <summary>每帧只推进当前 lease owner；session/deadline 不可确认时先持久化失败终态。</summary>
            internal virtual void Tick()
            {
                if (DateTime.UtcNow < nextTickUtc) return;
                nextTickUtc = DateTime.UtcNow.AddMilliseconds(100);
                var root = Load();
                if (root == null || !UnityCliSessionMutationLease.IsOwnedBy(Text(root, "requestId"))) return;
                if (!string.Equals(Text(root, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) { Fail(root, "E_EXECUTION_TIMEOUT", SessionChangedMessage); return; }
                if (DateTimeOffset.UtcNow >= Time(root, "deadline")) { Fail(root, "E_EXECUTION_TIMEOUT", DeadlineMessage); return; }
                OnTick(root);
            }

            /// <summary>终态仅存内存以供同 session status 读取；随后删除 active journal 并释放 lease。</summary>
            protected void Terminal(Dictionary<string, object> root, object envelope)
            {
                lastTerminalRequestId = Text(root, "requestId");
                lastTerminalEnvelope = envelope;
                UnityCliActiveOperation.Clear(Scope);
                UnityCliSessionMutationLease.Release(Text(root, "requestId"));
            }
            protected void Succeed(Dictionary<string, object> root) { Terminal(root, ProtocolEnvelopeFactory.Success(Text(root, "requestId"), BuildSuccessData(root))); }
            protected void Fail(Dictionary<string, object> root, string code, string message)
            {
                WriteLog(Scope + "_failed requestId=" + Text(root, "requestId") + " code=" + code + " message=" + message);
                Terminal(root, ProtocolEnvelopeFactory.Error(Text(root, "requestId"), code, message, null, new[] { "Inspect editor.state before submitting a new request." }));
            }
            protected string Busy(string requestId) { return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(requestId, "E_UNITY_BUSY", "Another mutation owns the selected Unity instance.")); }
            protected Dictionary<string, object> Load()
            {
                try { return UnityCliActiveOperation.Exists(Scope) ? UnityCliActiveOperation.Load(Scope) : null; }
                catch (Exception exception) { WriteLog(Scope + " active state corrupt: " + exception.Message); UnityCliActiveOperation.Clear(Scope); return null; }
            }
            protected void Save(Dictionary<string, object> root) { UnityCliActiveOperation.Save(Scope, root); }
            internal void Update(Action<Dictionary<string, object>> action) { var root = Load(); if (root == null) return; action(root); Save(root); }
            protected static string Text(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item != null ? Convert.ToString(item, CultureInfo.InvariantCulture) : string.Empty; }
            protected static bool Bool(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item is bool && (bool)item; }
            protected static long Long(IDictionary<string, object> value, string key) { long result; return long.TryParse(Text(value, key), out result) ? result : 0L; }
            protected static DateTimeOffset Time(IDictionary<string, object> value, string key) { DateTimeOffset result; return DateTimeOffset.TryParse(Text(value, key), out result) ? result : DateTimeOffset.MinValue; }
            private sealed class StatusArgs { public string RootRequestId { get; set; } }
        }
    }
}
#endif
