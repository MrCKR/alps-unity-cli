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
                    WriteLog("stale_bridge_listener_stopped assembly=" + assembly.GetName().Name);
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
                WriteJson(context, "{\"ok\":true,\"data\":{\"bridge\":\"unity_cli\",\"port\":" + port.ToString(CultureInfo.InvariantCulture) + ",\"route\":\"POST /bridge/execute\",\"buildStamp\":" + Quote(BridgeBuildStamp()) + ",\"capabilities\":" + CapabilitiesJson() + "}}", 200);
                return;
            }

            if (context.Request.HttpMethod != "POST" || context.Request.Url.AbsolutePath != "/bridge/execute")
            {
                WriteJson(context, "{\"ok\":false,\"error\":{\"code\":\"E_BRIDGE_ROUTE_NOT_FOUND\",\"message\":\"Expected POST /bridge/execute.\"}}", 404);
                return;
            }

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
            if (cachedIsPlayingOrWillChangePlaymode
                && request.Command != "editor.pause"
                && request.Command != "editor.stop")
            {
                WriteJson(context, TransientErrorJson("E_UNITY_PLAY_MODE", "Unity Editor is entering or exiting Play mode.", "Wait for Play mode transition to complete and retry.", 3000), 200);
                return;
            }

            var item = new BridgeWorkItem(request);
            lock (QueueLock)
            {
                WorkQueue.Enqueue(item);
            }

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
                    WriteJson(context, SuccessJson("{\"status\":\"running\",\"requestId\":" + Quote(request.RequestId) + ",\"command\":" + Quote(request.Command) + ",\"timeoutSeconds\":60,\"sideEffectSemantics\":\"started-before-http-timeout\",\"message\":\"Unity started this mutating request before the HTTP wait timeout; it may still complete. This is not a failure envelope. Inspect logPath and requestId before retrying.\",\"logPath\":" + Quote(LogPath()) + "}"), 200);
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
