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
        private static void StopStaleBridgeListeners()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("UnityCli.UnityCliBridge", false);
                if (type == null || type == typeof(UnityCliBridge))
                {
                    continue;
                }
                var method = type.GetMethod("StopListener", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (method == null)
                {
                    continue;
                }
                try
                {
                    method.Invoke(null, null);
                    WriteLog("stale_bridge_listener_stopped assembly=" + assembly.GetName().Name + " mvid=" + AssemblyMvid(assembly));
                }
                catch (Exception exception)
                {
                    WriteLog("stale_bridge_listener_stop_failed assembly=" + assembly.GetName().Name + " " + exception.GetType().Name + ": " + exception.Message);
                }
            }
        }

        private static string BridgeBuildStamp()
        {
            return typeof(UnityCliBridgeBuildInfo).GetField("BuildStamp", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null) as string ?? "unknown";
        }

        /// <summary>health v3 同时返回严格transport identity与只读主线程pulse；两者不得互相替代。</summary>
        private static string BridgeHealthJson()
        {
            var data = new Dictionary<string, object>
            {
                { "version", 3L },
                { "instanceId", InstanceId() },
                { "canonicalProjectPath", ProjectIdentity().CanonicalProjectPath },
                { "canonicalizationVersion", (long)UnityCliCanonicalProjectPath.CanonicalizationVersion },
                { "target", "editor" },
                { "sessionId", EditorSessionId() },
                { "editorSessionId", EditorSessionId() },
                { "listenerId", ListenerId },
                { "listenerGeneration", CurrentListenerGeneration() },
                { "actualPort", (long)port },
                { "buildStamp", BridgeBuildStamp() },
                { "mainThread", MainThreadHealthData() }
            };
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Success(string.Empty, data));
        }

        /// <summary>使用同一monotonic clock计算pulse age；后台线程不调用Unity API。</summary>
        private static Dictionary<string, object> MainThreadHealthData()
        {
            long before;
            long after;
            long timestamp;
            do
            {
                before = Interlocked.Read(ref mainThreadPulseSequence);
                timestamp = Interlocked.Read(ref mainThreadPulseTimestamp);
                after = Interlocked.Read(ref mainThreadPulseSequence);
            }
            while (before != after);

            object ageMs = null;
            if (timestamp > 0)
            {
                var elapsed = Math.Max(0L, System.Diagnostics.Stopwatch.GetTimestamp() - timestamp);
                ageMs = (long)(elapsed * 1000d / System.Diagnostics.Stopwatch.Frequency);
            }
            var lifecycle = domainReloading ? "domain-reloading"
                : cachedIsCompiling ? "compiling"
                : cachedIsUpdating ? "importing"
                : cachedIsBuilding ? "building"
                : cachedIsPlayModeChanging ? "playmode-changing"
                : "ready";
            return new Dictionary<string, object>
            {
                { "observed", timestamp > 0 },
                { "sequence", after },
                { "ageMs", ageMs },
                { "listenerGeneration", CurrentListenerGeneration() },
                { "lifecycle", lifecycle }
            };
        }

        private static string AssemblyMvid()
        {
            return AssemblyMvid(typeof(UnityCliBridge).Assembly);
        }

        private static string AssemblyMvid(Assembly assembly)
        {
            try
            {
                return assembly.ManifestModule.ModuleVersionId.ToString("N");
            }
            catch
            {
                return "unknown";
            }
        }

        private static string AssemblyLocation(Assembly assembly)
        {
            try
            {
                return assembly.Location;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string BridgePackageAssetPath()
        {
            return "Packages/com.alpsckr.unitycli/Editor/UnityCliBridge.cs";
        }

        private static string BridgePackageName()
        {
            var package = BridgePackageInfo();
            return package == null ? "unknown" : package.name;
        }

        private static string BridgePackageResolvedPath()
        {
            var package = BridgePackageInfo();
            return package == null ? string.Empty : package.resolvedPath;
        }

        private static UnityEditor.PackageManager.PackageInfo BridgePackageInfo()
        {
            try
            {
                return UnityEditor.PackageManager.PackageInfo.FindForAssetPath(BridgePackageAssetPath());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>按完整 project identity 计算稳定 preferred port。</summary>
        private static int PreferredPort()
        {
            var seed = ulong.Parse(ProjectHash().Substring(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return PreferredPortBase + (int)(seed % (ulong)PreferredPortRange);
        }

        /// <summary>preferred -> previous fallback -> deterministic probe；每次 reload 都重新评估 preferred。</summary>
        private static int StartHttpListener(HttpListener httpListener)
        {
            Exception lastException = null;
            var preferred = PreferredPort();
            var previous = PreviousActualPort();
            var candidates = new List<int>();
            for (var retry = 0; retry < PreferredPortRetryCount; retry++) candidates.Add(preferred);
            if (previous.HasValue && previous.Value != preferred) candidates.Add(previous.Value);

            var seed = ulong.Parse(ProjectHash().Substring(16, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            for (var index = 1; index <= DeterministicFallbackCount; index++)
            {
                var candidate = PreferredPortBase + (int)((seed + (ulong)(index * 7919)) % (ulong)PreferredPortRange);
                if (!candidates.Contains(candidate)) candidates.Add(candidate);
            }

            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                try
                {
                    httpListener.Prefixes.Clear();
                    httpListener.Prefixes.Add("http://127.0.0.1:" + candidate.ToString(CultureInfo.InvariantCulture) + "/");
                    httpListener.Start();
                    return candidate;
                }
                catch (Exception exception)
                {
                    lastException = exception;
                    if (candidate == preferred && index + 1 < PreferredPortRetryCount) Thread.Sleep(50);
                }
            }
            throw lastException ?? new InvalidOperationException("No deterministic loopback port is available.");
        }

        private static async Task ListenLoop()
        {
            while (listener != null && listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    _ = Task.Run(() => HandleHttp(context));
                }
                catch (Exception exception)
                {
                    WriteLog("bridge_listen_failed " + exception.GetType().Name + ": " + exception.Message);
                }
            }
        }

        private static void HandleHttp(HttpListenerContext context)
        {
            try
            {
                HandleHttpCore(context);
            }
            catch (Exception exception)
            {
                WriteLog("handle_http_unhandled " + exception.GetType().Name + ": " + exception.Message);
                TryWriteSerializeErrorEnvelope(context, "E_BRIDGE_INTERNAL_ERROR", "Unhandled Bridge error.", "Run unitycli instances list and inspect editor.state before retrying.");
            }
        }

        private static void HandleHttpCore(HttpListenerContext context)
        {
            if (context.Request.HttpMethod == "GET" && context.Request.Url.AbsolutePath == "/health")
            {
                var expectedInstanceId = context.Request.Headers["x-unity-cli-expected-instance-id"];
                var expectedSessionId = context.Request.Headers["x-unity-cli-expected-session-id"];
                var expectedTarget = context.Request.Headers["x-unity-cli-expected-target"];
                var expectedListenerId = context.Request.Headers["x-unity-cli-expected-listener-id"];
                var expectedGeneration = context.Request.Headers["x-unity-cli-expected-listener-generation"];
                if (!string.Equals(expectedInstanceId, InstanceId(), StringComparison.Ordinal)
                    || !string.Equals(expectedSessionId, EditorSessionId(), StringComparison.Ordinal)
                    || !string.Equals(expectedTarget, "editor", StringComparison.Ordinal)
                    || !string.Equals(expectedListenerId, ListenerId, StringComparison.Ordinal)
                    || !string.Equals(expectedGeneration, CurrentListenerGeneration().ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    WriteJson(context, UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                        string.Empty,
                        "E_INSTANCE_IDENTITY_MISMATCH",
                        "Bridge health identity does not match the requested Unity project session.",
                        nextActions: new[] { "Resolve the target again through --instance or the current Unity project CWD." })), 409);
                    return;
                }
                WriteJson(context, BridgeHealthJson(), 200);
                return;
            }

            if (context.Request.HttpMethod == "POST" && (context.Request.Url.AbsolutePath == "/bridge/admission/acquire" || context.Request.Url.AbsolutePath == "/bridge/admission/revoke"))
            {
                var expectedInstanceId = context.Request.Headers["x-unity-cli-expected-instance-id"];
                var expectedSessionId = context.Request.Headers["x-unity-cli-expected-session-id"];
                var expectedTarget = context.Request.Headers["x-unity-cli-expected-target"];
                var expectedListenerId = context.Request.Headers["x-unity-cli-expected-listener-id"];
                var expectedGeneration = context.Request.Headers["x-unity-cli-expected-listener-generation"];
                if (!string.Equals(expectedInstanceId, InstanceId(), StringComparison.Ordinal)
                    || !string.Equals(expectedSessionId, EditorSessionId(), StringComparison.Ordinal)
                    || !string.Equals(expectedTarget, "editor", StringComparison.Ordinal)
                    || !string.Equals(expectedListenerId, ListenerId, StringComparison.Ordinal)
                    || !string.Equals(expectedGeneration, CurrentListenerGeneration().ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    WriteJson(context, UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                        string.Empty, "E_INSTANCE_IDENTITY_MISMATCH", "Admission identity does not match the selected Editor listener generation.",
                        nextActions: new[] { "Resolve the selected Unity target again before waiting for admission." })), 409);
                    return;
                }
                try
                {
                    var body = ReadBodyBytes(context.Request);
                    string result;
                    if (context.Request.Url.AbsolutePath == "/bridge/admission/acquire")
                    {
                        string token;
                        result = ExecuteAdmissionAcquire(body, out token);
                        if (!string.IsNullOrEmpty(token)) context.Response.Headers["x-unity-cli-admission-token"] = token;
                    }
                    else result = ExecuteAdmissionRevoke(body, context.Request);
                    WriteJson(context, result, 200);
                }
                catch (BridgeCommandException exception) { WriteJson(context, SerializeBridgeExceptionEnvelope(string.Empty, exception), 200); }
                catch (Exception exception) { WriteJson(context, SerializeErrorEnvelope("E_BRIDGE_INTERNAL_ERROR", "Admission request failed: " + exception.GetType().Name + ".", "Inspect the selected Unity session before retrying."), 200); }
                return;
            }

            if (context.Request.HttpMethod != "POST" || context.Request.Url.AbsolutePath != "/bridge/execute")
            {
                WriteJson(context, UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    string.Empty,
                    "E_BRIDGE_ROUTE_NOT_FOUND",
                    "Expected POST /bridge/execute.",
                    nextActions: new[] { "Use POST /bridge/execute or GET /health." })), 404);
                return;
            }

            var expectedExecuteInstanceId = context.Request.Headers["x-unity-cli-expected-instance-id"];
            var expectedExecuteSessionId = context.Request.Headers["x-unity-cli-expected-session-id"];
            var expectedExecuteTarget = context.Request.Headers["x-unity-cli-expected-target"];
            var expectedExecuteListenerId = context.Request.Headers["x-unity-cli-expected-listener-id"];
            var expectedExecuteGeneration = context.Request.Headers["x-unity-cli-expected-listener-generation"];
            if (string.IsNullOrEmpty(expectedExecuteInstanceId)
                || string.IsNullOrEmpty(expectedExecuteSessionId)
                || string.IsNullOrEmpty(expectedExecuteTarget)
                || string.IsNullOrEmpty(expectedExecuteListenerId)
                || string.IsNullOrEmpty(expectedExecuteGeneration)
                || !string.Equals(expectedExecuteInstanceId, InstanceId(), StringComparison.Ordinal)
                || !string.Equals(expectedExecuteSessionId, EditorSessionId(), StringComparison.Ordinal)
                || !string.Equals(expectedExecuteTarget, "editor", StringComparison.Ordinal)
                || !string.Equals(expectedExecuteListenerId, ListenerId, StringComparison.Ordinal)
                || !string.Equals(expectedExecuteGeneration, CurrentListenerGeneration().ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                WriteJson(context, UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    string.Empty,
                    "E_INSTANCE_IDENTITY_MISMATCH",
                    "Bridge execute attestation does not match the selected Unity listener generation.",
                    nextActions: new[] { "Resolve the target again through --instance and retry after the selected generation is stable." })), 409);
                return;
            }

            WriteLog("bridge_execute_accepted method=" + context.Request.HttpMethod + " path=" + context.Request.Url.AbsolutePath + " contentLength=" + context.Request.ContentLength64.ToString(CultureInfo.InvariantCulture));

            byte[] rawBody;
            try
            {
                rawBody = ReadBodyBytes(context.Request);
            }
            catch (Exception exception)
            {
                WriteLog("read_body_failed " + exception.GetType().Name + ": " + exception.Message);
                WriteJson(context, SerializeErrorEnvelope("E_BRIDGE_READ_BODY_FAILED", "Failed to read the Bridge request body.", "Inspect editor.state and retry only after the selected Editor is stable."), 200);
                return;
            }
            UnityCli.Protocol.BridgeRequest request;
            try
            {
                request = UnityCli.Protocol.BridgeRequest.DeserializeStrict(DecodeRequestBody(rawBody));
            }
            catch (Exception exception) when (exception is FormatException || exception is OverflowException || exception is InvalidCastException || exception is DecoderFallbackException)
            {
                var details = new Dictionary<string, object>
                {
                    { "path", "$" },
                    { "expected", "BridgeRequest" },
                    { "actual", exception.Message }
                };
                WriteJson(context, UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    string.Empty,
                    "E_SCHEMA_INVALID",
                    "Bridge request JSON does not match the protocol schema.",
                    details,
                    new[] { "Send command, requestId, input, execution, and security using their declared JSON types." })), 200);
                return;
            }
            if (!string.Equals(request.InstanceId, InstanceId(), StringComparison.Ordinal))
            {
                WriteJson(context, UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    request.RequestId,
                    "E_INSTANCE_IDENTITY_MISMATCH",
                    "Bridge request instanceId does not match this canonical Unity project.",
                    new Dictionary<string, object> { { "expectedInstanceId", InstanceId() }, { "actualInstanceId", request.InstanceId } },
                    new[] { "Resolve the target again through --instance or the current Unity project CWD." })), 409);
                return;
            }
            try
            {
                var recoveredDirect = AdmissionRecoveredDirectTerminal(request, rawBody);
                if (recoveredDirect != null) { WriteJson(context, recoveredDirect, 200); return; }
            }
            catch (BridgeCommandException exception) { WriteJson(context, SerializeBridgeExceptionEnvelope(request.RequestId, exception), 200); return; }

            int initialRemainingMs;
            if (!request.TryDeadlineRemainingMs(out initialRemainingMs))
            {
                WriteJson(context, UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    request.RequestId,
                    "E_EXECUTION_TIMEOUT",
                    "The immutable request deadline elapsed before the request reached the Unity queue.",
                    nextActions: new[] { "Inspect editor.state and retry only after Unity is responsive." })), 200);
                return;
            }
            AdmissionOwnerIdentity admissionOwner;
            try { admissionOwner = AuthorizeAdmission(context.Request, request, rawBody); }
            catch (BridgeCommandException exception)
            {
                WriteJson(context, SerializeBridgeExceptionEnvelope(request.RequestId, exception), 200);
                return;
            }
            WriteLog("bridge_request_body_read requestId=" + request.RequestId + " command=" + request.Command + " bytes=" + rawBody.Length.ToString(CultureInfo.InvariantCulture));

            // Check for transient Unity state before queuing the request.
            // During compile/import/domain reload, immediately return a transient error
            // so the CLI can retry-after instead of waiting 60s for a timeout.
            if (domainReloading)
            {
                WriteJson(context, SerializeTransientErrorEnvelope(request.RequestId, "E_UNITY_DOMAIN_RELOAD", "Unity Editor is reloading assemblies (domain reload).", "Wait for domain reload to complete and retry.", 10000), 200);
                return;
            }
            var allowedDuringCompileOrImport = UnityCliCompileCoordinator.IsCommand(request.Command) || UnityCliLiveRefreshCoordinator.IsCommand(request.Command) || UnityCliAssetRefreshCoordinator.IsCommand(request.Command) || UnityCliPackageEmbedCoordinator.IsCommand(request.Command) || UnityCliPackageResolveCoordinator.IsCommand(request.Command) || UnityCliTestRunCoordinator.IsCommand(request.Command) || UnityCliNavMeshBakeCoordinator.IsCommand(request.Command) || UnityCliOcclusionBakeCoordinator.IsCommand(request.Command) || IsEditorStateSnapshotCommand(request.Command) || IsProjectToolCatalogReadCommand(request.Command);
            if (cachedIsCompiling && !allowedDuringCompileOrImport)
            {
                WriteJson(context, SerializeTransientErrorEnvelope(request.RequestId, "E_UNITY_COMPILING", "Unity Editor is compiling scripts.", "Wait for Unity to finish compiling and retry.", 5000), 200);
                return;
            }
            if (cachedIsUpdating && !allowedDuringCompileOrImport)
            {
                WriteJson(context, SerializeTransientErrorEnvelope(request.RequestId, "E_UNITY_IMPORTING", "Unity Editor is importing assets.", "Wait for Unity to finish importing and retry.", 5000), 200);
                return;
            }
            if (cachedIsPlayModeChanging)
            {
                // 只在 PlayMode 切换期限制高风险命令；稳定运行态允许 CLI 工具继续入队执行。
                // 这里必须与 UnityCliBridge.Execute() 的切换期 allowlist 保持一致。
                var allowedDuringPlayModeTransition = UnityCliCompileCoordinator.IsCommand(request.Command) || UnityCliLiveRefreshCoordinator.IsCommand(request.Command) || UnityCliAssetRefreshCoordinator.IsCommand(request.Command) || UnityCliPackageEmbedCoordinator.IsCommand(request.Command) || UnityCliPackageResolveCoordinator.IsCommand(request.Command) || UnityCliTestRunCoordinator.IsCommand(request.Command) || UnityCliNavMeshBakeCoordinator.IsCommand(request.Command) || UnityCliOcclusionBakeCoordinator.IsCommand(request.Command)
                    || IsEditorStateSnapshotCommand(request.Command)
                    || request.Command == "editor.console"
                    || request.Command == "editor.pause"
                    || request.Command == "editor.stop"
                    || IsProjectToolCatalogReadCommand(request.Command)
                    || request.Command == "runtime-tool.run"
                    || request.Command == "diagnose.preflight";

                WriteLog("http_play_mode_guard requestId=" + request.RequestId + " command=" + request.Command + " cachedIsPlayingOrWillChange=" + Bool(cachedIsPlayingOrWillChangePlaymode) + " cachedIsPlaying=" + Bool(cachedIsPlaying) + " transitionAllowed=" + Bool(allowedDuringPlayModeTransition));

                if (!allowedDuringPlayModeTransition)
                {
                    WriteLog("http_play_mode_guard_blocked_transition requestId=" + request.RequestId + " command=" + request.Command);
                    WriteJson(context, SerializeTransientErrorEnvelope(request.RequestId, "E_UNITY_PLAY_MODE", "Unity Editor is entering or exiting Play mode. (http-guard-v3)", "Wait for Play mode transition to complete and retry.", 3000), 200);
                    return;
                }
                WriteLog("http_play_mode_guard_allowed requestId=" + request.RequestId + " command=" + request.Command + " cachedIsPlaying=" + Bool(cachedIsPlaying));
            }

            BridgeWorkItem item;
            bool created;
            int queueDepth;
            try
            {
                item = EnqueueOrAttach(request, rawBody, ListenerId, CurrentListenerGeneration(), admissionOwner, out created, out queueDepth);
            }
            catch (BridgeCommandException exception)
            {
                WriteJson(context, SerializeBridgeExceptionEnvelope(request.RequestId, exception), 200);
                return;
            }
            WriteLog((created ? "bridge_request_enqueued" : "bridge_request_attached") + " requestId=" + request.RequestId + " command=" + request.Command + " queueDepth=" + queueDepth.ToString(CultureInfo.InvariantCulture));

            // queue 与 handler 共用 immutable absolute deadline；HTTP client 断开不得取消已接收的业务请求。
            int queueRemainingMs;
            if (!request.TryDeadlineRemainingMs(out queueRemainingMs)) queueRemainingMs = 0;
            var queueSignal = WaitHandle.WaitAny(new[] { item.Started.WaitHandle, item.Completed.WaitHandle }, queueRemainingMs);
            if (queueSignal == WaitHandle.WaitTimeout)
            {
                if (item.CancelBeforeStart())
                {
                    WriteLog("bridge_queue_start_timeout requestId=" + request.RequestId + " command=" + request.Command);
                    WriteJson(context, UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                        request.RequestId,
                        "E_EXECUTION_TIMEOUT",
                        "Unity Editor did not start the bridge request before the immutable deadline.",
                        nextActions: new[] { "Inspect editor.state and retry only after Unity is responsive." })), 200);
                    return;
                }
                WriteLog("bridge_queue_start_timeout requestId=" + request.RequestId + " command=" + request.Command);
                WriteJson(context, ResolveStartedTimeoutResponse(item, request,
                    "The immutable deadline elapsed after Unity started the request."), 200);
                return;
            }
            if (queueSignal == 1)
            {
                WriteJson(context, item.Response ?? SerializeTransientErrorEnvelope(request.RequestId, "E_EXECUTION_CANCELLED", "Bridge request was cancelled before execution.", "Retry after Unity is ready.", 1000), 200);
                return;
            }

            int executionRemainingMs;
            if (!request.TryDeadlineRemainingMs(out executionRemainingMs)) executionRemainingMs = 0;
            if (!item.Completed.Wait(TimeSpan.FromMilliseconds(executionRemainingMs)))
            {
                item.Cancel();
                WriteLog("bridge_execution_timeout requestId=" + request.RequestId + " command=" + request.Command);
                WriteJson(context, ResolveStartedTimeoutResponse(item, request,
                    "The immutable deadline elapsed after Unity started the request."), 200);
                return;
            }

            WriteJson(context, item.Response ?? SerializeTransientErrorEnvelope(request.RequestId, "E_EXECUTION_CANCELLED", "Bridge request completed without a response.", "Inspect editor.state before deciding whether to submit a new request.", 1000), 200);
        }

        /// <summary>已开始请求的 timeout 由内存 response 与接受状态裁决，不创建请求级持久记录。</summary>
        private static string ResolveStartedTimeoutResponse(BridgeWorkItem item, UnityCli.Protocol.BridgeRequest request, string reason)
        {
            var response = item.Response;
            if (response != null) return response;

            // CompleteWorkItem/Response 可能恰好在上述读取后发生；再读一次避免丢失已完成结果。
            if (item.Completed.Wait(0) && item.Response != null) return item.Response;

            // 接受后的请求继续执行；只有 timeout 在接受前获胜时才取消执行 token。
            item.CancelBeforeAcceptance();
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                request.RequestId,
                "E_EXECUTION_TIMEOUT",
                reason + " The result cannot be confirmed; inspect the business state before deciding whether to retry.",
                nextActions: new[] { "Inspect editor.state and retry only after Unity is responsive." }));
        }
    }
}
#endif
