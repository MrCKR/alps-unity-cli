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
        private static readonly string[] SceneCommands = { "scene.active", "scene.hierarchy", "scene.build-settings", "scene.create", "scene.load", "scene.save" };
        private static readonly string[] GameObjectCommands = { "go.find", "go.inspect", "go.create", "go.modify", "go.duplicate", "go.move-relative", "go.look-at", "go.delete", "component.add", "component.remove", "component.set" };
        private static readonly string[] AssetCommands = { "asset.search", "asset.info", "asset.create", "asset.import", "asset.modify", "asset.delete", "asset.duplicate", "asset.move", "asset.rename", "asset.refresh" };
        private static readonly string[] DiagnoseCommands = { "diagnose.preflight" };
        private static readonly string[] ScreenshotCommands = { "screenshot.game-view", "screenshot.scene-view", "screenshot.isolated-object", "screenshot.orbit", "screenshot.surround" };
        private static readonly string[] PrefabCommands = { "prefab.info", "prefab.hierarchy", "prefab.create", "prefab.modify", "prefab.stage-open", "prefab.stage-save", "prefab.stage-close" };
        private static readonly string[] UiCommands = { "ui.canvas.create", "ui.text.create", "ui.button.create", "ui.image.create", "ui.document.info", "ui.document.create", "ui.hierarchy" };
        private static readonly string[] MaterialCommands = { "material.info", "material.create", "material.assign", "material.set" };
        private static readonly string[] GraphicsCommands = { "graphics.quality", "graphics.settings", "graphics.volume", "graphics.bake-start", "graphics.bake-cancel", "graphics.bake-status", "graphics.bake-clear", "graphics.bake-reflection-probe", "graphics.bake-settings", "graphics.light-probe-group", "graphics.reflection-probe", "graphics.probe-positions", "graphics.stats", "graphics.stats-counters", "graphics.stats-memory", "graphics.scene-debug", "graphics.skybox-get", "graphics.skybox-set-material", "graphics.skybox-set-properties", "graphics.skybox-set-ambient", "graphics.skybox-set-fog", "graphics.skybox-set-reflection", "graphics.skybox-set-sun", "graphics.pipeline-settings", "graphics.pipeline-quality", "graphics.pipeline-info" };
        private static readonly string[] PhysicsCommands = { "physics.settings", "physics.collision-matrix", "physics.material", "physics.assign-material", "physics.joint.add", "physics.joint.remove", "physics.force", "physics.overlap", "physics.rigidbody", "physics.raycast", "physics.simulation", "physics.raycast-all", "physics.linecast", "physics.shapecast", "physics.validate" };
        private static readonly string[] ProfilerCommands = { "profiler.start", "profiler.stop", "profiler.pause", "profiler.counter", "profiler.memory", "profiler.frame", "profiler.capture", "profiler.export", "profiler.status" };
        private static readonly string[] AnimationCommands = { "animation.clip", "animation.clip.create", "animation.clip.modify", "animation.state", "animation.play", "animation.stop", "animator.get-info", "animator.get-parameter", "animator.set-parameter", "animator.crossfade", "animator.set-speed", "animator.set-enabled", "controller.create", "controller.add-state", "controller.add-transition", "controller.add-parameter", "controller.get-info", "controller.assign", "controller.add-layer", "controller.remove-layer", "controller.set-layer-weight", "controller.create-blend-tree-1d", "controller.create-blend-tree-2d", "controller.add-blend-tree-child" };
        private static readonly string[] ShaderCommands = { "shader.info", "shader.read", "shader.create", "shader.update" };
        private static readonly string[] TextureCommands = { "texture.info", "texture.create", "texture.sprite", "texture.import-settings" };
        private static readonly string[] EditorCommands = { "editor.state", "editor.play", "editor.pause", "editor.stop", "editor.undo", "editor.redo", "editor.resolution", "editor.menu", "editor.compile", "editor.console", "project.tag.add", "project.tag.remove", "project.layer.add", "project.layer.remove" };
        private static readonly string[] ScriptableObjectCommands = { "scriptable-object.create", "scriptable-object.info", "scriptable-object.list-types", "scriptable-object.modify" };
        private static readonly string[] CodeCommands = { "code.exec" };
        private static readonly string[] ProjectToolCommands = { "project-tool.list", "project-tool.run" };
        private static readonly string[] RuntimeToolCommands = { "runtime-tool.list", "runtime-tool.run" };
        private static readonly string[] ContextCommands = { "context.list", "context.describe", "context.read" };
        private static readonly string[] ExtensionCommands = { "extension.status" };
        private const string BridgeDiagnosticsFingerprint = "health-fingerprint-play-mode-guard-v3";
        private static readonly BridgeCommandRegistry CommandRegistry = CreateCommandRegistry();
        private static readonly string ListenerId = Guid.NewGuid().ToString("N");
        private static readonly DateTime BridgeLoadedAtUtc = DateTime.UtcNow;
        private static HttpListener listener;
        private static bool started;
        private static bool projectToolsDiscovered;
        private static volatile bool domainReloading;
        private static int port;
        private static double nextHeartbeatAt;
        private static readonly CancellationTokenSource BridgeLifetime = new CancellationTokenSource();
        private static CancellationTokenSource runtimeSession = new CancellationTokenSource();

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
            // AssetImportWorker 不是可寻址 Editor 实例，不得启动 listener 或写入 instance registry。
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            WriteRuntimeLoadDiagnostics("static_constructor");
            EditorApplication.update += ProcessQueuedWork;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.quitting += UnregisterAndStop;
            EditorApplication.playModeStateChanged += OnBridgePlayModeStateChanged;
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
            BridgeLifetime.Cancel();
            CancelQueuedWork();
            CancelPendingWaits("E_UNITY_DOMAIN_RELOAD", "Unity Editor is reloading assemblies (domain reload).", "Wait for domain reload to complete and retry.", 10000);
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
            BridgeLifetime.Cancel();
            CancelQueuedWork();
            CancelPendingWaits("E_UNITY_QUITTING", "Unity Editor is quitting.", "Restart Unity and retry the request.", 5000);
            DeleteInstanceRegistration();
            StopListener();
        }

        private static void OnBridgePlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                runtimeSession.Cancel();
                runtimeSession.Dispose();
                runtimeSession = new CancellationTokenSource();
            }
        }

        private static CancellationTokenSource CreateExecutionLifetime(UnityCli.Protocol.BridgeRequest request, CancellationToken clientAbort = default)
        {
            var tokens = new List<CancellationToken> { BridgeLifetime.Token };
            if (request != null && string.Equals(request.Command, "runtime-tool.run", StringComparison.Ordinal)) tokens.Add(runtimeSession.Token);
            if (clientAbort.CanBeCanceled) tokens.Add(clientAbort);
            return CancellationTokenSource.CreateLinkedTokenSource(tokens.ToArray());
        }

        private static CancellationTokenSource CreateExecutionTimeout(UnityCli.Protocol.BridgeRequest request)
        {
            var source = CreateExecutionLifetime(request);
            var timeoutMs = request == null || request.Execution == null ? null : request.Execution.TimeoutMs;
            if (timeoutMs.HasValue && timeoutMs.Value > 0) source.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(timeoutMs.Value, int.MaxValue)));
            return source;
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

        /// <summary>
        /// 在 batchmode 主线程上执行最终 Bridge 协议，并允许工具通过中央 scheduler 跨帧等待。
        /// </summary>
        public static async Task<int> ExecuteBatchCommandAsync(string requestJson)
        {
            string response;
            try
            {
                var request = UnityCli.Protocol.BridgeRequest.DeserializeStrict(requestJson);
                using (var timeout = CreateExecutionTimeout(request))
                {
                    response = await Execute(request, timeout.Token);
                }
            }
            catch (FormatException exception)
            {
                response = UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    string.Empty,
                    "E_SCHEMA_INVALID",
                    "Bridge request JSON does not match the protocol schema.",
                    new Dictionary<string, object> { { "path", "$" }, { "expected", "BridgeRequest" }, { "actual", exception.Message } },
                    new[] { "Send a complete final BridgeRequest JSON value." }));
            }
            catch (BridgeCommandException exception)
            {
                response = SerializeBridgeExceptionEnvelope(string.Empty, exception);
            }
            catch (Exception exception)
            {
                response = UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    string.Empty, "E_BRIDGE_COMMAND_FAILED", exception.Message, nextActions: new[] { "Check Unity Console and UnityCliBridge.log." }));
            }
            Console.WriteLine("UNITY_CLI_BRIDGE_RESULT " + response);
            return response.Contains("\"ok\":true") ? 0 : 1;
        }

        /// <summary>
        /// 确保 batchmode 已安装中央主线程同步上下文；update 驱动仅由 Bridge 注册一次。
        /// </summary>
        internal static void EnsureScheduler()
        {
            UnityCliWaitScheduler.EnsureMainThreadSynchronizationContext();
        }

        private static async ValueTask<string> Execute(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var data = await ExecuteData(request, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Success(request.RequestId, data));
            }
            catch (BridgeCommandException exception)
            {
                return SerializeBridgeExceptionEnvelope(request.RequestId, exception);
            }
            catch (OperationCanceledException)
            {
                return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    request.RequestId,
                    "E_EXECUTION_TIMEOUT",
                    "Bridge request execution was cancelled or timed out.",
                    nextActions: new[] { "Retry with a larger execution.timeoutMs after Unity is responsive." },
                    transient: true));
            }
            catch (Exception exception)
            {
                return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    request.RequestId,
                    "E_BRIDGE_COMMAND_FAILED",
                    exception.Message,
                    nextActions: new[] { "Check Unity Console and UnityCliBridge.log." }));
            }
        }

        private static async ValueTask<object> ExecuteData(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            WriteLog("execute requestId=" + request.RequestId + " command=" + request.Command);

            if (domainReloading)
            {
                throw new BridgeCommandException("E_UNITY_DOMAIN_RELOAD", "Unity Editor is reloading assemblies (domain reload).", null, new[] { "Wait for domain reload to complete and retry." }, true, 10000);
            }

            var allowedDuringCompileOrImport = request.Command == "editor.compile";
            if (EditorApplication.isCompiling && !allowedDuringCompileOrImport)
            {
                throw new BridgeCommandException("E_UNITY_COMPILING", "Unity Editor is compiling scripts.", null, new[] { "Wait for Unity to finish compiling and retry." }, true, 5000);
            }

            if (EditorApplication.isUpdating && !allowedDuringCompileOrImport)
            {
                throw new BridgeCommandException("E_UNITY_IMPORTING", "Unity Editor is importing assets.", null, new[] { "Wait for Unity to finish importing and retry." }, true, 5000);
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                // 只在 Play Mode 切换期限制高风险命令；稳定运行态允许 CLI 工具继续操作 Unity。
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

                if (!EditorApplication.isPlaying && !allowedDuringPlayModeTransition)
                {
                    WriteLog("execute_play_mode_guard_blocked_transition requestId=" + request.RequestId + " command=" + request.Command + " isPlaying=" + Bool(EditorApplication.isPlaying));
                    throw new BridgeCommandException("E_UNITY_PLAY_MODE", "Unity Editor is entering or exiting Play mode. (execute-guard-v3)", null, new[] { "Wait for Play mode transition to complete and retry." }, true, 3000);
                }
                WriteLog("execute_play_mode_guard_allowed requestId=" + request.RequestId + " command=" + request.Command + " isPlaying=" + Bool(EditorApplication.isPlaying));
            }

            // 动态 Tool 必须先解析 live descriptor，再按实际 tool name 与 wrapper command 校验证明。
            // 此处仅处理直接任意代码入口，wrapper 的确认由已注册 descriptor 二次门禁。
            if (RequiresDestructiveSecurityProof(request.Command)
                && !(request.Execution != null && request.Execution.DryRun == true)
                && !request.SecurityConfirmed(request.Command, true, true, request.Command))
            {
                throw new BridgeCommandException("E_DESTRUCTIVE_CONFIRMATION_REQUIRED", "Destructive command requires CLI confirmation and fingerprint proof: " + request.Command, "Rerun the same tools run command with --yes so the request includes destructive security proof.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await CommandRegistry.ExecuteAsync(request, cancellationToken);
        }

        private static bool RequiresDestructiveSecurityProof(string command)
        {
            return string.Equals(command, "code.exec", StringComparison.Ordinal);
        }

        private static void CancelPendingWaits(string code, string message, string nextAction, int retryAfterMs)
        {
            UnityCliWaitScheduler.CancelAll(new BridgeCommandException(
                code,
                message,
                null,
                new[] { nextAction },
                transient: true,
                retryAfterMs: retryAfterMs));
        }
    }
}
#endif
