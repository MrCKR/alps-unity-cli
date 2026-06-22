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
        private static readonly string[] CameraCommands = { "camera.list", "camera.info", "camera.screenshot", "camera.create", "camera.target", "camera.lens", "camera.multiview" };
        private static readonly string[] SceneCommands = { "scene.active", "scene.hierarchy", "scene.build-settings", "scene.screenshot", "scene.create", "scene.load", "scene.save" };
        private static readonly string[] GameObjectCommands = { "go.find", "go.inspect", "go.create", "go.modify", "go.duplicate", "go.move-relative", "go.look-at", "go.delete", "component.add", "component.remove", "component.set" };
        private static readonly string[] AssetCommands = { "asset.search", "asset.info", "asset.create", "asset.import", "asset.modify", "asset.delete", "asset.duplicate", "asset.move", "asset.rename" };
        private static readonly string[] DiagnoseCommands = { "diagnose.preflight" };
        private static readonly string[] ScreenshotCommands = { "screenshot.capture" };
        private static readonly string[] PrefabCommands = { "prefab.info", "prefab.hierarchy", "prefab.create", "prefab.modify", "prefab.stage-open", "prefab.stage-save", "prefab.stage-close" };
        private static readonly string[] UiCommands = { "ui.canvas.create", "ui.text.create", "ui.button.create", "ui.image.create", "ui.document.info", "ui.document.create", "ui.hierarchy" };
        private static readonly string[] MaterialCommands = { "material.info", "material.create", "material.assign", "material.set" };
        private static readonly string[] GraphicsCommands = { "graphics.quality", "graphics.settings", "graphics.pipeline", "graphics.volume", "graphics.screenshot", "graphics.bake-start", "graphics.bake-cancel", "graphics.bake-status", "graphics.bake-clear", "graphics.bake-reflection-probe", "graphics.bake-settings", "graphics.light-probe-group", "graphics.reflection-probe", "graphics.probe-positions", "graphics.stats", "graphics.stats-counters", "graphics.stats-memory", "graphics.scene-debug", "graphics.skybox-get", "graphics.skybox-set-material", "graphics.skybox-set-properties", "graphics.skybox-set-ambient", "graphics.skybox-set-fog", "graphics.skybox-set-reflection", "graphics.skybox-set-sun", "graphics.pipeline-settings", "graphics.pipeline-quality", "graphics.pipeline-info" };
        private static readonly string[] PhysicsCommands = { "physics.settings", "physics.collision-matrix", "physics.material", "physics.joint", "physics.force", "physics.overlap", "physics.rigidbody", "physics.raycast", "physics.simulation" };
        private static readonly string[] ProfilerCommands = { "profiler.start", "profiler.stop", "profiler.pause", "profiler.counter", "profiler.memory", "profiler.frame", "profiler.capture", "profiler.export", "profiler.status" };
        private static readonly string[] AnimationCommands = { "animation.clip", "animation.state", "animation.play", "animation.stop", "animator.get-info", "animator.get-parameter", "animator.set-parameter", "animator.crossfade", "animator.set-speed", "animator.set-enabled", "controller.create", "controller.add-state", "controller.add-transition", "controller.add-parameter", "controller.get-info", "controller.assign", "controller.add-layer", "controller.remove-layer", "controller.set-layer-weight", "controller.create-blend-tree-1d", "controller.create-blend-tree-2d", "controller.add-blend-tree-child" };
        private static readonly string[] ShaderCommands = { "shader.info", "shader.read", "shader.create", "shader.update" };
        private static readonly string[] TextureCommands = { "texture.info", "texture.create", "texture.sprite", "texture.import-settings" };
        private static readonly string[] EditorCommands = { "editor.state", "editor.play", "editor.pause", "editor.stop", "editor.undo", "editor.redo", "editor.resolution", "editor.menu", "editor.console", "project.tag", "project.layer" };
        private static readonly string[] ScriptCommands = { "script.create", "script.read", "script.hash", "script.edit", "script.apply-edits", "script.validate" };
        private static readonly string[] ScriptableObjectCommands = { "scriptable-object.create", "scriptable-object.info", "scriptable-object.modify" };
        private static readonly string[] CodeCommands = { "code.exec" };
        private static readonly string[] ProjectToolCommands = { "project-tool.list", "project-tool.run" };
        private static readonly string[] RuntimeToolCommands = { "runtime-tool.list", "runtime-tool.run" };
        private static readonly string[] ContextCommands = { "context.list", "context.describe", "context.read" };
        private static readonly string[] ExtensionCommands = { "extension.status" };
        private const string BridgeDiagnosticsFingerprint = "health-fingerprint-play-mode-guard-v2";
        private const string DestructiveCommandFingerprint = "destructive-safety-v1";
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
        private static readonly List<BridgeWorkItem> DeferredItems = new List<BridgeWorkItem>();

        // Cached EditorApplication state — updated on the main thread by ProcessQueuedWork,
        // read by HandleHttpCore on background thread pool threads.
        // volatile ensures the background thread sees the latest write.
        private static volatile bool cachedIsCompiling;
        private static volatile bool cachedIsUpdating;
        private static volatile bool cachedIsPlayingOrWillChangePlaymode;
        private static volatile bool cachedIsPlaying;
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
            CancelDeferredWork();
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
            var w = new UnityCliJsonWriter().BeginObject()
                .Prop("requestId", requestId)
                .Prop("command", command)
                .PropRaw("args", rawArgs);
            if (!string.IsNullOrEmpty(dryRun))
            {
                w.PropRaw("dryRun", dryRun);
            }
            w.EndObject();
            var body = w.ToString();
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

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                // Read-only queries, runtime tool commands, and tool discovery
                // are always allowed in any Play Mode state.
                var alwaysAllowedInPlayMode = request.Command == "editor.state"
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

                if (!alwaysAllowedInPlayMode)
                {
                    WriteLog("execute_play_mode_guard_blocked requestId=" + request.RequestId + " command=" + request.Command + " isPlaying=" + Bool(EditorApplication.isPlaying));
                    if (!EditorApplication.isPlaying)
                    {
                        // Transition phase: isPlayingOrWillChangePlaymode is true but
                        // isPlaying is not yet true (entering) or already false (exiting).
                        // Transient — callers should wait and retry.
                        return TransientErrorJson("E_UNITY_PLAY_MODE", "Unity Editor is entering or exiting Play mode. (execute-guard-v2)", "Wait for Play mode transition to complete and retry.", 3000);
                    }
                    else
                    {
                        // Stable Play Mode: editor-domain write commands are not available.
                        // Non-transient — retrying won't help; the caller must exit Play Mode first.
                        return ErrorJson("E_UNITY_PLAY_MODE", "Unity Editor is in Play Mode; editor-domain write commands are not available. (execute-guard-v2)", "Exit Play Mode first (use editor stop), or use read-only and runtime tool commands.");
                    }
                }
                WriteLog("execute_play_mode_guard_allowed requestId=" + request.RequestId + " command=" + request.Command);
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
                || command == "editor.menu"
                || command == "script.edit"
                || command == "script.apply-edits";
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
                        UnregisterDeferredItem(item);
                        item.Response = onSuccess();
                        item.Completed.Set();
                        WriteLog("deferred_wait_completed requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                        return;
                    }
                    if (DateTime.UtcNow > deadline)
                    {
                        EditorApplication.update -= waiter;
                        UnregisterDeferredItem(item);
                        item.Response = ErrorJson(timeoutErrorCode, timeoutErrorMessage, timeoutNextAction);
                        item.Completed.Set();
                        WriteLog("deferred_wait_timeout requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " errorCode=" + timeoutErrorCode);
                        return;
                    }
                }
                catch (BridgeCommandException exception)
                {
                    EditorApplication.update -= waiter;
                    UnregisterDeferredItem(item);
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
                    UnregisterDeferredItem(item);
                    item.Response = ErrorJson("E_BRIDGE_COMMAND_FAILED", exception.Message, "Check Unity Console and UnityCliBridge.log.");
                    item.Completed.Set();
                    WriteLog("deferred_wait_exception requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " error=" + exception.GetType().Name);
                }
            };
            item.MarkDeferred();
            lock (DeferredItems)
            {
                DeferredItems.Add(item);
            }
            EditorApplication.update += waiter;
            WriteLog("deferred_wait_started requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
            return DeferredResponseMarker;
        }

        private static void UnregisterDeferredItem(BridgeWorkItem item)
        {
            lock (DeferredItems)
            {
                DeferredItems.Remove(item);
            }
        }

        /// <summary>
        /// Called from OnBeforeAssemblyReload to clean up any pending deferred work items.
        /// Removes EditorApplication.update callbacks and signals each deferred item with
        /// a transient domain reload error so the HTTP thread does not hang.
        /// </summary>
        private static void CancelDeferredWork()
        {
            List<BridgeWorkItem> snapshot;
            lock (DeferredItems)
            {
                snapshot = new List<BridgeWorkItem>(DeferredItems);
                DeferredItems.Clear();
            }
            foreach (var item in snapshot)
            {
                if (!item.Completed.IsSet)
                {
                    item.Response = TransientErrorJson("E_UNITY_DOMAIN_RELOAD", "Unity Editor is reloading assemblies (domain reload).", "Wait for domain reload to complete and retry.", 10000);
                    item.Completed.Set();
                    WriteLog("deferred_wait_cancelled_domain_reload requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                }
            }
        }
    }
}
#endif
