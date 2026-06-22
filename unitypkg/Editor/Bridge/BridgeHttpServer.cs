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
            var data = new UnityCliJsonWriter().BeginObject()
                .Prop("bridge", "unity_cli")
                .PropRaw("target", TargetContextJson())
                .Prop("port", port)
                .Prop("route", "POST /bridge/execute")
                .Prop("buildStamp", BridgeBuildStamp())
                .Prop("diagnosticsFingerprint", BridgeDiagnosticsFingerprint)
                .Prop("listenerId", ListenerId)
                .Prop("loadedAt", BridgeLoadedAtUtc.ToString("O", CultureInfo.InvariantCulture))
                .PropObject("assembly", assembly =>
                {
                    assembly.Prop("fullName", typeof(UnityCliBridge).Assembly.FullName)
                        .Prop("location", AssemblyLocation(typeof(UnityCliBridge).Assembly))
                        .Prop("moduleVersionId", AssemblyMvid());
                })
                .PropObject("package", package =>
                {
                    package.Prop("name", BridgePackageName())
                        .Prop("assetPath", BridgePackageAssetPath())
                        .Prop("resolvedPath", BridgePackageResolvedPath());
                })
                .PropObject("diagnostics", diagnostics =>
                {
                    diagnostics.Prop("executeStageLogs", true)
                        .Prop("contentLengthBoundedRead", true)
                        .Prop("runtimeLoadFile", RuntimeDiagnosticsPath());
                })
                .PropRaw("capabilities", CapabilitiesJson())
                .EndObject()
                .ToString();
            return UnityCliEnvelope.Success(data);
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
                TryWriteErrorJson(context, "E_BRIDGE_INTERNAL_ERROR", "Unhandled bridge error: " + exception.Message, "Check UnityCliBridge.log for details.");
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
                WriteJson(context, UnityCliEnvelope.Error("E_BRIDGE_ROUTE_NOT_FOUND", "Expected POST /bridge/execute.", "Use POST /bridge/execute or GET /health."), 404);
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
                WriteJson(context, ErrorJson("E_BRIDGE_READ_BODY_FAILED", "Failed to read request body: " + exception.Message, "The request stream may have been closed due to a domain reload. Retry the request."), 200);
                return;
            }
            var request = BridgeRequest.Parse(body);
            WriteLog("bridge_request_body_read requestId=" + request.RequestId + " command=" + request.Command + " bytes=" + Encoding.UTF8.GetByteCount(body ?? string.Empty).ToString(CultureInfo.InvariantCulture));

            // Check for transient Unity state before queuing the request.
            // During compile/import/domain reload, immediately return a transient error
            // so the CLI can retry-after instead of waiting 60s for a timeout.
            if (domainReloading)
            {
                WriteJson(context, TransientErrorJson("E_UNITY_DOMAIN_RELOAD", "Unity Editor is reloading assemblies (domain reload).", "Wait for domain reload to complete and retry.", 10000), 200);
                return;
            }
            if (cachedIsCompiling)
            {
                WriteJson(context, TransientErrorJson("E_UNITY_COMPILING", "Unity Editor is compiling scripts.", "Wait for Unity to finish compiling and retry.", 5000), 200);
                return;
            }
            if (cachedIsUpdating)
            {
                WriteJson(context, TransientErrorJson("E_UNITY_IMPORTING", "Unity Editor is importing assets.", "Wait for Unity to finish importing and retry.", 5000), 200);
                return;
            }
            if (cachedIsPlayingOrWillChangePlaymode)
            {
                // Read-only queries, runtime tool commands, and tool discovery
                // are always allowed in any Play Mode state.
                // This must match the Execute() allowlist in UnityCliBridge.cs.
                var alwaysAllowedInPlayMode = request.Command == "editor.state"
                    || request.Command == "editor.console"
                    || request.Command == "editor.pause"
                    || request.Command == "editor.stop"
                    || request.Command == "runtime-tool.list"
                    || request.Command == "runtime-tool.run"
                    || request.Command == "project-tool.list"
                    || request.Command == "diagnose.preflight";

                WriteLog("http_play_mode_guard requestId=" + request.RequestId + " command=" + request.Command + " cachedIsPlayingOrWillChange=" + Bool(cachedIsPlayingOrWillChangePlaymode) + " cachedIsPlaying=" + Bool(cachedIsPlaying) + " alwaysAllowed=" + Bool(alwaysAllowedInPlayMode));

                if (!alwaysAllowedInPlayMode)
                {
                    if (!cachedIsPlaying)
                    {
                        // During Play Mode state changes, cachedIsPlayingOrWillChangePlaymode is true
                        // while cachedIsPlaying may still be false on enter or exit.
                        // Transient — callers should wait and retry.
                        WriteLog("http_play_mode_guard_blocked_transition requestId=" + request.RequestId + " command=" + request.Command);
                        WriteJson(context, TransientErrorJson("E_UNITY_PLAY_MODE", "Unity Editor is entering or exiting Play mode. (http-guard-v2)", "Wait for Play mode transition to complete and retry.", 3000), 200);
                        return;
                    }
                    else
                    {
                        // Stable Play Mode: editor-domain write commands are not available.
                        // Non-transient — retrying won't help; the caller must exit Play Mode first.
                        WriteLog("http_play_mode_guard_blocked_stable requestId=" + request.RequestId + " command=" + request.Command);
                        WriteJson(context, ErrorJson("E_UNITY_PLAY_MODE", "Unity Editor is in Play Mode; editor-domain write commands are not available. (http-guard-v2)", "Exit Play Mode first (use editor stop), or use read-only and runtime tool commands."), 200);
                        return;
                    }
                }
                WriteLog("http_play_mode_guard_allowed requestId=" + request.RequestId + " command=" + request.Command);
            }

            var item = new BridgeWorkItem(request);
            var queueDepth = 0;
            lock (QueueLock)
            {
                WorkQueue.Enqueue(item);
                queueDepth = WorkQueue.Count;
            }
            WriteLog("bridge_request_enqueued requestId=" + request.RequestId + " command=" + request.Command + " queueDepth=" + queueDepth.ToString(CultureInfo.InvariantCulture));

            var timeout = TimeSpan.FromSeconds(60);
            if (!item.Completed.Wait(timeout))
            {
                if (item.CancelBeforeStart())
                {
                    WriteLog("bridge_request_timeout_cancelled requestId=" + request.RequestId + " command=" + request.Command);
                    WriteJson(context, TransientErrorJson("E_UNITY_BUSY", "Unity Editor did not start the bridge request within 60 seconds; the request was cancelled before execution.", "No Unity side effects were applied. Wait for Unity to finish compiling/importing and retry.", 5000), 200);
                    return;
                }

                if (item.Completed.IsSet)
                {
                    WriteJson(context, item.Response ?? TransientErrorJson("E_UNITY_BUSY", "Unity Editor did not finish the bridge request within 60 seconds.", "Wait for Unity to finish compiling/importing and retry.", 5000), 200);
                    return;
                }

                item.MarkTimedOut();
                WriteLog("bridge_request_timeout_started requestId=" + request.RequestId + " command=" + request.Command + " mutating=" + IsMutatingCommand(request.Command));
                if (IsMutatingCommand(request.Command))
                {
                    var running = new UnityCliJsonWriter().BeginObject()
                        .Prop("status", "running")
                        .Prop("requestId", request.RequestId)
                        .Prop("command", request.Command)
                        .Prop("timeoutSeconds", 60)
                        .Prop("sideEffectSemantics", "started-before-http-timeout")
                        .Prop("message", "Unity started this mutating request before the HTTP wait timeout; it may still complete. This is not a failure envelope. Inspect logPath and requestId before retrying.")
                        .Prop("logPath", LogPath())
                        .EndObject()
                        .ToString();
                    WriteJson(context, SuccessJson(running), 200);
                    return;
                }

                WriteJson(context, TransientErrorJson("E_UNITY_BUSY", "Unity Editor started the bridge request but did not finish within 60 seconds.", "Wait for Unity to finish compiling/importing and retry.", 5000), 200);
                return;
            }

            WriteJson(context, item.Response ?? TransientErrorJson("E_UNITY_BUSY", "Unity Editor did not finish the bridge request within 60 seconds.", "Wait for Unity to finish compiling/importing and retry.", 5000), 200);
        }
    }
}
#endif
