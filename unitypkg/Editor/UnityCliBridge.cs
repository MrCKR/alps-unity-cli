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
        private static readonly string[] ProfilerCommands = { "profiler.start", "profiler.stop", "profiler.capture", "profiler.status" };
        private static readonly string[] ProBuilderCommands = { "probuilder.shape", "probuilder.create", "probuilder.csg", "probuilder.edit" };
        private static readonly string[] AnimationCommands = { "animation.clip", "animation.state", "animation.play", "animation.stop" };
        private static readonly string[] VfxCommands = { "vfx.graph", "vfx.effect" };
        private static readonly string[] ShaderCommands = { "shader.info", "shader.read", "shader.create", "shader.update" };
        private static readonly string[] TextureCommands = { "texture.info", "texture.create", "texture.sprite", "texture.import-settings" };
        private static readonly string[] EditorCommands = { "editor.state", "editor.play", "editor.pause", "editor.stop", "editor.menu", "editor.console" };
        private static readonly string[] ScriptCommands = { "script.create", "script.read", "script.edit", "script.validate" };
        private static readonly string[] CodeCommands = { "code.exec" };
        private static readonly string[] CustomToolCommands = { "custom-tool.list", "custom-tool.run" };
        private static readonly BridgeCommandRegistry CommandRegistry = CreateCommandRegistry();
        private static HttpListener listener;
        private static bool started;
        private static bool customToolsDiscovered;
        private static volatile bool domainReloading;
        private static int port;
        private static double nextHeartbeatAt;

        // Cached EditorApplication state — updated on the main thread by ProcessQueuedWork,
        // read by HandleHttpCore on background thread pool threads.
        // volatile ensures the background thread sees the latest write.
        private static volatile bool cachedIsCompiling;
        private static volatile bool cachedIsUpdating;
        private static volatile bool cachedIsPlayingOrWillChangePlaymode;
        private static volatile bool cachedIsBuilding;

        static UnityCliBridge()
        {
            EditorApplication.update += ProcessQueuedWork;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.quitting += MarkStaleAndStop;
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
                WriteLog("bridge_started port=" + port);
            }
            catch (Exception exception)
            {
                WriteLog("bridge_start_failed " + exception.GetType().Name + ": " + exception.Message);
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
        }

        private static void MarkStaleAndStop()
        {
            RegisterInstance("stale");
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
            var body = "{\"requestId\":" + Quote(requestId) + ",\"command\":" + Quote(command) + ",\"args\":" + (string.IsNullOrEmpty(argsJson) ? "{}" : argsJson) + "}";
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
                && request.Command != "editor.stop")
            {
                return TransientErrorJson("E_UNITY_PLAY_MODE", "Unity Editor is entering or exiting Play mode.", "Wait for Play mode transition to complete and retry.", 3000);
            }

            var response = CommandRegistry.Execute(request);
            if (response != null)
            {
                return response;
            }

            return ErrorJson("E_BRIDGE_COMMAND_UNSUPPORTED", "Unsupported bridge command: " + request.Command, "Upgrade the Unity CLI Bridge package or use a registered CLI command.");
        }
    }
}
#endif
