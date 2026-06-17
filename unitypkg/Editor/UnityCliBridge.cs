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
    [InitializeOnLoad]
    public static partial class UnityCliBridge
    {
        private const int Port = 47392;
        private static readonly object QueueLock = new object();
        private static readonly string[] CameraCommands = { "camera.list", "camera.info", "camera.screenshot" };
        private static readonly string[] SceneCommands = { "scene.active", "scene.hierarchy", "scene.build-settings", "scene.screenshot", "scene.create", "scene.load", "scene.save" };
        private static readonly string[] GameObjectCommands = { "go.find", "go.inspect", "go.create", "go.modify", "go.delete", "component.add", "component.remove", "component.set" };
        private static readonly string[] AssetCommands = { "asset.search", "asset.info", "asset.create", "asset.import", "asset.modify", "asset.delete", "asset.duplicate", "asset.move", "asset.rename" };
        private static readonly string[] DiagnoseCommands = { "diagnose.preflight" };
        private static readonly string[] ScreenshotCommands = { "screenshot.capture" };
        private static readonly string[] PrefabCommands = { "prefab.info", "prefab.hierarchy", "prefab.create", "prefab.modify", "prefab.stage-open", "prefab.stage-save", "prefab.stage-close" };
        private static readonly string[] UiCommands = { "ui.canvas.create", "ui.text.create", "ui.button.create", "ui.image.create", "ui.document.info", "ui.document.create", "ui.hierarchy" };
        private static readonly string[] MaterialCommands = { "material.info", "material.create", "material.assign", "material.set" };
        private static readonly string[] GraphicsCommands = { "graphics.quality", "graphics.settings", "graphics.pipeline", "graphics.volume", "graphics.screenshot" };
        private static readonly string[] PhysicsCommands = { "physics.settings", "physics.raycast", "physics.simulation" };
        private static readonly string[] ProfilerCommands = { "profiler.start", "profiler.stop", "profiler.pause", "profiler.counter", "profiler.memory", "profiler.frame", "profiler.capture", "profiler.export", "profiler.status" };
        private static readonly string[] ProBuilderCommands = { "probuilder.shape", "probuilder.create", "probuilder.csg", "probuilder.edit" };
        private static readonly string[] AnimationCommands = { "animation.clip", "animation.state", "animation.play", "animation.stop" };
        private static readonly string[] VfxCommands = { "vfx.graph", "vfx.effect" };
        private static readonly string[] ShaderCommands = { "shader.info", "shader.read", "shader.create", "shader.update" };
        private static readonly string[] TextureCommands = { "texture.info", "texture.create", "texture.sprite", "texture.import-settings" };
        private static readonly string[] EditorCommands = { "editor.state", "editor.play", "editor.pause", "editor.stop", "editor.menu", "editor.console" };
        private static readonly string[] ScriptCommands = { "script.create", "script.read", "script.edit", "script.validate" };
        private static readonly string[] CodeCommands = { "code.exec" };
        private static readonly string[] ProjectToolCommands = { "project-tool.list", "project-tool.run" };
        private static readonly string[] RuntimeToolCommands = { "runtime-tool.list", "runtime-tool.run" };
        private const string BridgeDiagnosticsFingerprint = "alp276-health-fingerprint-20260615-execute-stage-v1";
        private const string DestructiveCommandFingerprint = "alp332-destructive-safety-v1";
        private static readonly BridgeCommandRegistry CommandRegistry = CreateCommandRegistry();
        private static readonly string ListenerId = Guid.NewGuid().ToString("N");
        private static readonly string InstanceSuffix = Guid.NewGuid().ToString("N").Substring(0, 6);
        private static readonly DateTime BridgeLoadedAtUtc = DateTime.UtcNow;
        private static HttpListener listener;
        private static bool started;
        private static bool projectToolsDiscovered;
        private static volatile bool domainReloading;
        private static int port;
        private static double nextHeartbeatAt;
        private static BridgeWorkItem currentWorkItem;
        private static readonly string DeferredResponseMarker = "\0DEFERRED";

        // Cached EditorApplication state — updated on the main thread by ProcessQueuedWork,
        // read by HandleHttpCore on background thread pool threads.
        // volatile ensures the background thread sees the latest write.
        private static volatile bool cachedIsCompiling;
        private static volatile bool cachedIsUpdating;
        private static volatile bool cachedIsPlayingOrWillChangePlaymode;
        private static volatile bool cachedIsBuilding;

        static UnityCliBridge()
        {
            WriteRuntimeLoadDiagnostics("static_constructor");
            EditorApplication.update += ProcessQueuedWork;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.quitting += UnregisterAndStop;
            domainReloading = false;
            StopStaleBridgeListeners();
            StartListener();
        }

        private static void StartListener()
        {
            if (started)
            {
                return;
            }

            try
            {
                listener = new HttpListener();
                port = StartHttpListener(listener);
                started = true;
                Task.Run(ListenLoop);
                RegisterInstance("healthy");
                WriteRuntimeLoadDiagnostics("listener_started");
                WriteLog("bridge_started port=" + port + " listenerId=" + ListenerId + " diagnosticsFingerprint=" + BridgeDiagnosticsFingerprint + " assemblyMvid=" + AssemblyMvid() + " packageName=" + BridgePackageName() + " packageAssetPath=" + BridgePackageAssetPath() + " packageResolvedPath=" + BridgePackageResolvedPath());
            }
            catch (Exception exception)
            {
                WriteRuntimeLoadDiagnostics("listener_start_failed");
                WriteLog("bridge_start_failed " + exception.GetType().Name + ": " + exception.Message + " listenerId=" + ListenerId + " diagnosticsFingerprint=" + BridgeDiagnosticsFingerprint + " assemblyMvid=" + AssemblyMvid() + " packageName=" + BridgePackageName() + " packageAssetPath=" + BridgePackageAssetPath() + " packageResolvedPath=" + BridgePackageResolvedPath());
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            domainReloading = true;
            RegisterInstance("stale");
            StopListener();
        }

        private static void OnAfterAssemblyReload()
        {
            domainReloading = false;
            WriteLog("bridge_after_assembly_reload restarting listener");
            StopStaleBridgeListeners();
            StartListener();

            // After domain reload, static fields are reset. Re-initialize the Runtime Executor
            // and restore session state if we are already in Play Mode.
            ReinitializeRuntimeExecutorAfterDomainReload();
        }

        private static void UnregisterAndStop()
        {
            DeleteInstanceRegistration();
            StopListener();
        }

        private static void StopListener()
        {
            try
            {
                if (listener != null)
                {
                    listener.Stop();
                    listener.Close();
                    listener = null;
                }
            }
            catch (Exception exception)
            {
                WriteLog("bridge_stop_failed " + exception.GetType().Name + ": " + exception.Message);
            }
            started = false;
        }

        public static int ExecuteBatchCommand(string requestId, string command, string argsJson)
        {
            var rawArgs = string.IsNullOrEmpty(argsJson) ? "{}" : argsJson;
            var dryRun = JsonRawProperty(rawArgs, "dryRun");
            var body = "{\"requestId\":" + Quote(requestId) + ",\"command\":" + Quote(command) + ",\"args\":" + rawArgs + (string.IsNullOrEmpty(dryRun) ? string.Empty : ",\"dryRun\":" + dryRun) + "}";
            var response = string.Empty;
            try
            {
                response = Execute(BridgeRequest.Parse(body));
            }
            catch (BridgeCommandException exception)
            {
                if (exception.Transient)
                {
                    response = TransientErrorJson(exception.Code, exception.Message, exception.NextAction ?? "Check output path, package availability and UnityCliBridge.log.", exception.RetryAfterMs > 0 ? exception.RetryAfterMs : 5000);
                }
                else
                {
                    response = ErrorJson(exception.Code, exception.Message, exception.NextAction ?? "Check output path, package availability and UnityCliBridge.log.");
                }
            }
            catch (Exception exception)
            {
                response = ErrorJson("E_BRIDGE_COMMAND_FAILED", exception.Message, "Check Unity Console and UnityCliBridge.log.");
            }
            Console.WriteLine("UNITY_CLI_BRIDGE_RESULT " + response);
            return response.Contains("\"ok\":true") ? 0 : 1;
        }

        private static string Execute(BridgeRequest request)
        {
            WriteLog("execute requestId=" + request.RequestId + " command=" + request.Command);

            if (domainReloading)
            {
                return TransientErrorJson("E_UNITY_DOMAIN_RELOAD", "Unity Editor is reloading assemblies (domain reload).", "Wait for domain reload to complete and retry.", 10000);
            }

            if (EditorApplication.isCompiling)
            {
                return TransientErrorJson("E_UNITY_COMPILING", "Unity Editor is compiling scripts.", "Wait for Unity to finish compiling and retry.", 5000);
            }

            if (EditorApplication.isUpdating)
            {
                return TransientErrorJson("E_UNITY_IMPORTING", "Unity Editor is importing assets.", "Wait for Unity to finish importing and retry.", 5000);
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode
                && request.Command != "editor.pause"
                && request.Command != "editor.stop"
                && request.Command != "runtime-tool.list"
                && request.Command != "runtime-tool.run")
            {
                return TransientErrorJson("E_UNITY_PLAY_MODE", "Unity Editor is entering or exiting Play mode.", "Wait for Play mode transition to complete and retry.", 3000);
            }

            if (RequiresDestructiveSecurityProof(request.Command) && !request.DryRun && !request.SecurityConfirmed(DestructiveCommandFingerprint))
            {
                return ErrorJson("E_DESTRUCTIVE_CONFIRMATION_REQUIRED", "Destructive command requires CLI confirmation and fingerprint proof: " + request.Command, "Rerun through unity_cli with --yes so the request includes destructive security proof.");
            }

            var response = CommandRegistry.Execute(request);
            if (response != null)
            {
                return response;
            }

            return ErrorJson("E_BRIDGE_COMMAND_UNSUPPORTED", "Unsupported bridge command: " + request.Command, "Upgrade the Unity CLI Bridge package or use a registered CLI command.");
        }

        private static bool RequiresDestructiveSecurityProof(string command)
        {
            return command == "asset.delete"
                || command == "go.delete"
                || command == "scene.save"
                || command == "code.exec"
                || command == "component.remove"
                || command == "probuilder.csg"
                || command == "editor.menu"
                || command == "script.edit";
        }

        /// <summary>
        /// Starts a deferred wait that checks a condition each frame via EditorApplication.update.
        /// The BridgeWorkItem is completed when the condition is met or the timeout elapses.
        /// The calling command handler should return DeferredResponseMarker.
        /// This replaces Thread.Sleep-based polling loops with non-blocking frame-based waits
        /// that do not block the Unity main thread.
        /// </summary>
        private static string StartDeferredWait(
            BridgeWorkItem item,
            Func<bool> condition,
            int timeoutMs,
            Func<string> onSuccess,
            string timeoutErrorCode,
            string timeoutErrorMessage,
            string timeoutNextAction)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            EditorApplication.CallbackFunction waiter = null;
            waiter = () =>
            {
                try
                {
                    if (condition())
                    {
                        EditorApplication.update -= waiter;
                        item.Response = onSuccess();
                        item.Completed.Set();
                        WriteLog("deferred_wait_completed requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                        return;
                    }
                    if (DateTime.UtcNow > deadline)
                    {
                        EditorApplication.update -= waiter;
                        item.Response = ErrorJson(timeoutErrorCode, timeoutErrorMessage, timeoutNextAction);
                        item.Completed.Set();
                        WriteLog("deferred_wait_timeout requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " errorCode=" + timeoutErrorCode);
                        return;
                    }
                }
                catch (BridgeCommandException exception)
                {
                    EditorApplication.update -= waiter;
                    if (exception.Transient)
                    {
                        item.Response = TransientErrorJson(exception.Code, exception.Message, exception.NextAction ?? "Check Unity Console and UnityCliBridge.log.", exception.RetryAfterMs > 0 ? exception.RetryAfterMs : 5000);
                    }
                    else
                    {
                        item.Response = ErrorJson(exception.Code, exception.Message, exception.NextAction ?? "Check Unity Console and UnityCliBridge.log.");
                    }
                    item.Completed.Set();
                    WriteLog("deferred_wait_exception requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " error=" + exception.Code);
                }
                catch (Exception exception)
                {
                    EditorApplication.update -= waiter;
                    item.Response = ErrorJson("E_BRIDGE_COMMAND_FAILED", exception.Message, "Check Unity Console and UnityCliBridge.log.");
                    item.Completed.Set();
                    WriteLog("deferred_wait_exception requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " error=" + exception.GetType().Name);
                }
            };
            item.MarkDeferred();
            EditorApplication.update += waiter;
            WriteLog("deferred_wait_started requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
            return DeferredResponseMarker;
        }
    }
}
#endif
