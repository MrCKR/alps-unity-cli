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

        private static string BridgeHealthJson()
        {
            var data = new Dictionary<string, object>
            {
                { "bridge", "unity_cli" }, { "target", TargetContextData() }, { "port", port }, { "route", "POST /bridge/execute" }, { "buildStamp", BridgeBuildStamp() },
                { "diagnosticsFingerprint", BridgeDiagnosticsFingerprint }, { "listenerId", ListenerId }, { "loadedAt", BridgeLoadedAtUtc.ToString("O", CultureInfo.InvariantCulture) },
                { "assembly", new Dictionary<string, object> { { "fullName", typeof(UnityCliBridge).Assembly.FullName }, { "location", AssemblyLocation(typeof(UnityCliBridge).Assembly) }, { "moduleVersionId", AssemblyMvid() } } },
                { "package", new Dictionary<string, object> { { "name", BridgePackageName() }, { "assetPath", BridgePackageAssetPath() }, { "resolvedPath", BridgePackageResolvedPath() } } },
                { "diagnostics", new Dictionary<string, object> { { "executeStageLogs", true }, { "contentLengthBoundedRead", true }, { "runtimeLoadFile", RuntimeDiagnosticsPath() } } },
                { "capabilities", CapabilitiesData() }
            };
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Success(string.Empty, data));
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
            return "Packages/com.unity_cli/Editor/UnityCliBridge.cs";
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

        private static int StartHttpListener(HttpListener httpListener)
        {
            Exception lastException = null;
            for (var candidate = Port; candidate < Port + 20; candidate++)
            {
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
                }
            }
            throw lastException ?? new InvalidOperationException("No loopback port available.");
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
                TryWriteSerializeErrorEnvelope(context, "E_BRIDGE_INTERNAL_ERROR", "Unhandled bridge error: " + exception.Message, "Check UnityCliBridge.log for details.");
            }
        }

        private static async Task WatchClientDisconnect(HttpListenerContext context, BridgeWorkItem item, CancellationToken stop)
        {
            // HttpListener 不暴露连接 abort token；响应流关闭/不可写是可观测的断连信号。
            while (!stop.IsCancellationRequested && !item.Completed.IsSet)
            {
                try
                {
                    if (!context.Response.OutputStream.CanWrite)
                    {
                        WriteLog("bridge_client_disconnected requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                        item.CancelClientDisconnected();
                        return;
                    }
                }
                catch (ObjectDisposedException)
                {
                    item.CancelClientDisconnected();
                    return;
                }
                catch (HttpListenerException)
                {
                    item.CancelClientDisconnected();
                    return;
                }
                try { await Task.Delay(100, stop); }
                catch (OperationCanceledException) { return; }
            }
        }

        private static void HandleHttpCore(HttpListenerContext context)
        {
            if (context.Request.HttpMethod == "GET" && context.Request.Url.AbsolutePath == "/health")
            {
                WriteJson(context, BridgeHealthJson(), 200);
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

            WriteLog("bridge_execute_accepted method=" + context.Request.HttpMethod + " path=" + context.Request.Url.AbsolutePath + " contentLength=" + context.Request.ContentLength64.ToString(CultureInfo.InvariantCulture));

            string body;
            try
            {
                body = ReadBody(context.Request);
            }
            catch (Exception exception)
            {
                WriteLog("read_body_failed " + exception.GetType().Name + ": " + exception.Message);
                WriteJson(context, SerializeErrorEnvelope("E_BRIDGE_READ_BODY_FAILED", "Failed to read request body: " + exception.Message, "The request stream may have been closed due to a domain reload. Retry the request."), 200);
                return;
            }
            UnityCli.Protocol.BridgeRequest request;
            try
            {
                request = UnityCli.Protocol.BridgeRequest.DeserializeStrict(body);
            }
            catch (Exception exception) when (exception is FormatException || exception is OverflowException || exception is InvalidCastException)
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
            WriteLog("bridge_request_body_read requestId=" + request.RequestId + " command=" + request.Command + " bytes=" + Encoding.UTF8.GetByteCount(body ?? string.Empty).ToString(CultureInfo.InvariantCulture));

            // Check for transient Unity state before queuing the request.
            // During compile/import/domain reload, immediately return a transient error
            // so the CLI can retry-after instead of waiting 60s for a timeout.
            if (domainReloading)
            {
                WriteJson(context, SerializeTransientErrorEnvelope(request.RequestId, "E_UNITY_DOMAIN_RELOAD", "Unity Editor is reloading assemblies (domain reload).", "Wait for domain reload to complete and retry.", 10000), 200);
                return;
            }
            var allowedDuringCompileOrImport = request.Command == "editor.compile";
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
            if (cachedIsPlayingOrWillChangePlaymode)
            {
                // 只在 Play Mode 切换期限制高风险命令；稳定运行态允许 CLI 工具继续入队执行。
                // 这里必须与 UnityCliBridge.Execute() 的切换期 allowlist 保持一致。
                var allowedDuringPlayModeTransition = request.Command == "editor.state"
                    || request.Command == "editor.console"
                    || request.Command == "editor.pause"
                    || request.Command == "editor.stop"
                    || request.Command == "runtime-tool.list"
                    || request.Command == "runtime-tool.run"
                    || request.Command == "project-tool.list"
                    || request.Command == "diagnose.preflight"
                    || request.Command == "context.list"
                    || request.Command == "context.describe"
                    || request.Command == "context.read"
                    || request.Command == "extension.status";

                WriteLog("http_play_mode_guard requestId=" + request.RequestId + " command=" + request.Command + " cachedIsPlayingOrWillChange=" + Bool(cachedIsPlayingOrWillChangePlaymode) + " cachedIsPlaying=" + Bool(cachedIsPlaying) + " transitionAllowed=" + Bool(allowedDuringPlayModeTransition));

                if (!cachedIsPlaying && !allowedDuringPlayModeTransition)
                {
                    WriteLog("http_play_mode_guard_blocked_transition requestId=" + request.RequestId + " command=" + request.Command);
                    WriteJson(context, SerializeTransientErrorEnvelope(request.RequestId, "E_UNITY_PLAY_MODE", "Unity Editor is entering or exiting Play mode. (http-guard-v3)", "Wait for Play mode transition to complete and retry.", 3000), 200);
                    return;
                }
                WriteLog("http_play_mode_guard_allowed requestId=" + request.RequestId + " command=" + request.Command + " cachedIsPlaying=" + Bool(cachedIsPlaying));
            }

            var item = new BridgeWorkItem(request);
            var queueDepth = 0;
            lock (QueueLock)
            {
                WorkQueue.Enqueue(item);
                queueDepth = WorkQueue.Count;
            }
            WriteLog("bridge_request_enqueued requestId=" + request.RequestId + " command=" + request.Command + " queueDepth=" + queueDepth.ToString(CultureInfo.InvariantCulture));

            using (var monitorStop = new CancellationTokenSource())
            {
                _ = WatchClientDisconnect(context, item, monitorStop.Token);
                try
                {
                    // queue start deadline 与 handler execution timeout 独立，排队时间不消耗工具执行预算。
                    var queueSignal = WaitHandle.WaitAny(new[] { item.Started.WaitHandle, item.Completed.WaitHandle }, 60000);
                    if (queueSignal == WaitHandle.WaitTimeout)
                    {
                        item.CancelBeforeStart();
                        WriteLog("bridge_queue_start_timeout requestId=" + request.RequestId + " command=" + request.Command);
                        WriteJson(context, SerializeTransientErrorEnvelope(request.RequestId, "E_QUEUE_START_TIMEOUT", "Unity Editor did not start the bridge request before the queue deadline.", "No Unity side effects were applied. Wait for Unity to become responsive and retry.", 5000), 200);
                        return;
                    }
                    if (queueSignal == 1)
                    {
                        WriteJson(context, item.Response ?? SerializeTransientErrorEnvelope(request.RequestId, "E_EXECUTION_CANCELLED", "Bridge request was cancelled before execution.", "Retry after Unity is ready.", 1000), 200);
                        return;
                    }

                    var requestedTimeoutMs = request.Execution == null ? null : request.Execution.TimeoutMs;
                    var executionHttpDeadline = TimeSpan.FromMilliseconds(requestedTimeoutMs.HasValue && requestedTimeoutMs.Value > 0
                        ? Math.Min(requestedTimeoutMs.Value + 5000, int.MaxValue)
                        : 65000);
                    if (!item.Completed.Wait(executionHttpDeadline))
                    {
                        item.Cancel();
                        WriteLog("bridge_execution_timeout requestId=" + request.RequestId + " command=" + request.Command);
                        WriteJson(context, UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                            request.RequestId,
                            "E_EXECUTION_TIMEOUT",
                            "Unity Editor did not finish the bridge request before the execution deadline; execution was cancelled.",
                            nextActions: new[] { "Retry with a larger execution.timeoutMs after Unity is responsive." },
                            transient: true)), 200);
                        return;
                    }

                    WriteJson(context, item.Response ?? SerializeTransientErrorEnvelope(request.RequestId, "E_EXECUTION_CANCELLED", "Bridge request completed without a response.", "Check UnityCliBridge.log before retrying.", 1000), 200);
                }
                finally
                {
                    monitorStop.Cancel();
                }
            }
        }
    }
}
#endif
