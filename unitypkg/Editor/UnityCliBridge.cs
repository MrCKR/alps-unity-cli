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
        private const int PreferredPortBase = 47392;
        private const int PreferredPortRange = 4096;
        private const int PreferredPortRetryCount = 4;
        private const int DeterministicFallbackCount = 64;
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
        private static readonly string[] EditorCommands = { "editor.play", "editor.pause", "editor.stop", "editor.undo", "editor.redo", "editor.resolution", "editor.menu", "editor.console", "project.tag.add", "project.tag.remove", "project.layer.add", "project.layer.remove" };
        private static readonly string[] ScriptableObjectCommands = { "scriptable-object.create", "scriptable-object.info", "scriptable-object.list-types", "scriptable-object.modify" };
        private static readonly string[] CodeCommands = { "code.exec" };
        private static readonly string[] ProjectToolCommands = { "project-tool.list", "project-tool.run" };
        private static readonly string[] RuntimeToolCommands = { "runtime-tool.list", "runtime-tool.run" };
        private static readonly string[] ExtensionCommands = { "extension.status" };
        private const string BridgeDiagnosticsFingerprint = "health-fingerprint-play-mode-guard-v3";
        private static readonly BridgeCommandRegistry CommandRegistry = CreateCommandRegistry();
        private static readonly string ListenerId = "listener-" + Guid.NewGuid().ToString("N");
        private static readonly DateTime BridgeLoadedAtUtc = DateTime.UtcNow;
        private static HttpListener listener;
        private static bool started;
        private static readonly BridgeStartupStateMachine StartupState = new BridgeStartupStateMachine();
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
        private static volatile bool cachedIsPlayModeChanging;
        private static volatile bool playModeChanging = EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode;

        static UnityCliBridge()
        {
            // AssetImportWorker 不是可寻址 Editor 实例，不得启动 listener 或写入 instance registry。
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            WriteRuntimeLoadDiagnostics("static_constructor");
            UnityCliTransactionLog.RotateForNewSession();
            UnityCliSessionMutationLease.Initialize();
            UnityCliCompilationEventBroker.Initialize();
            UnityCliCompileCoordinator.Initialize();
            UnityCliLiveBuildCoordinator.Initialize();
            UnityCliLiveRefreshCoordinator.Initialize();
            UnityCliAssetRefreshCoordinator.Initialize();
            UnityCliPackageEmbedCoordinator.Initialize();
            UnityCliPackageResolveCoordinator.Initialize();
            UnityCliTestRunCoordinator.Initialize();
            UnityCliNavMeshBakeCoordinator.Initialize();
            UnityCliOcclusionBakeCoordinator.Initialize();
            EditorApplication.update += ProcessQueuedWork;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.quitting += UnregisterAndStop;
            EditorApplication.playModeStateChanged += OnBridgePlayModeStateChanged;
            domainReloading = false;
            ScheduleBridgeStartup();
        }

        /// <summary>为当前domain安排唯一启动链；实际推进由Package现有主线程update入口负责。</summary>
        private static void ScheduleBridgeStartup()
        {
            if (!StartupState.Schedule())
            {
                WriteLog("bridge_start_schedule_skipped phase=" + StartupState.Phase);
                return;
            }
            WriteLog("bridge_start_scheduled phase=prime");
        }

        /// <summary>
        /// 每次Editor update只推进一个阶段；busy保持phase，失败后状态机归零且不得重放attempt。
        /// </summary>
        private static void AdvanceBridgeStartup()
        {
            if (domainReloading || AssetDatabase.IsAssetImportWorkerProcess()) return;
            string state = null;
            string error = null;
            var transition = StartupState.Tick(
                EditorApplication.isCompiling || EditorApplication.isUpdating,
                delegate { return UnityCliPipelineHandoff.PrimePipelineStartup(out state, out error); },
                delegate { return CompleteBridgeStartup(out state, out error); });
            if (transition == BridgeStartupTransition.PrimeSucceeded)
            {
                WriteLog("bridge_start_advanced phase=prime pipelinePrime=" + state);
                return;
            }
            if (transition == BridgeStartupTransition.DrainAdvanced)
            {
                WriteLog("bridge_start_advanced phase=drain-pipeline-callbacks");
                return;
            }
            if (transition == BridgeStartupTransition.PrimeFailed)
            {
                DeleteInstanceRegistration();
                StopListener();
                WriteRuntimeLoadDiagnostics("pipeline_prime_failed");
                WriteLog("bridge_start_blocked pipelinePrime=" + state + " error=" + error);
                return;
            }
            if (transition == BridgeStartupTransition.CompleteFailed)
            {
                DeleteInstanceRegistration();
                StopListener();
                WriteRuntimeLoadDiagnostics("pipeline_handoff_failed");
                WriteLog("bridge_start_blocked pipelineHandoff=" + state + " error=" + error);
                return;
            }
            if (transition != BridgeStartupTransition.CompleteSucceeded) return;

            StopStaleBridgeListeners();
            if (!UnityCliProjectSettings.Current.BridgeAutoStart)
            {
                DeleteInstanceRegistration();
                StopListener();
                WriteRuntimeLoadDiagnostics("listener_autostart_disabled");
                WriteLog("bridge_autostart_disabled pipelineHandoff=" + state);
                return;
            }

            WriteLog("bridge_bootstrap pipelineHandoff=" + state + " autoStart=true");
            StartListener();
        }

        /// <summary>状态机调用的handoff attempt；返回后由调用方发布listener或fail closed。</summary>
        private static bool CompleteBridgeStartup(out string handoffState, out string handoffError)
        {
            WriteLog("bridge_pipeline_handoff_started");
            return UnityCliPipelineHandoff.EnsureAppliedOnce(out handoffState, out handoffError);
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
                if (!RegisterInstance("ready")) throw new InvalidOperationException("Initial instance registry publication failed.");
                WriteRuntimeLoadDiagnostics("listener_started");
                WriteLog("bridge_started port=" + port + " listenerId=" + ListenerId + " diagnosticsFingerprint=" + BridgeDiagnosticsFingerprint + " assemblyMvid=" + AssemblyMvid() + " packageName=" + BridgePackageName() + " packageAssetPath=" + BridgePackageAssetPath() + " packageResolvedPath=" + BridgePackageResolvedPath());
            }
            catch (Exception exception)
            {
                StopListener();
                WriteRuntimeLoadDiagnostics("listener_start_failed");
                WriteLog("bridge_start_failed " + exception.GetType().Name + ": " + exception.Message + " listenerId=" + ListenerId + " diagnosticsFingerprint=" + BridgeDiagnosticsFingerprint + " assemblyMvid=" + AssemblyMvid() + " packageName=" + BridgePackageName() + " packageAssetPath=" + BridgePackageAssetPath() + " packageResolvedPath=" + BridgePackageResolvedPath());
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            domainReloading = true;
            ReleaseActiveDirectMutationLease();
            UnityCliCompilationEventBroker.BeforeReload();
            UnityCliCompileCoordinator.BeforeReload();
            UnityCliLiveBuildCoordinator.BeforeReload();
            UnityCliLiveRefreshCoordinator.BeforeReload();
            UnityCliAssetRefreshCoordinator.BeforeReload();
            UnityCliPackageEmbedCoordinator.BeforeReload();
            UnityCliPackageResolveCoordinator.BeforeReload();
            UnityCliTestRunCoordinator.BeforeReload();
            UnityCliNavMeshBakeCoordinator.BeforeReload();
            UnityCliOcclusionBakeCoordinator.BeforeReload();
            BridgeLifetime.Cancel();
            CancelQueuedWork();
            CancelPendingWaits("E_UNITY_DOMAIN_RELOAD", "Unity Editor is reloading assemblies (domain reload).", "Wait for domain reload to complete and retry.", 10000);
            if (!UnityCliProjectSettings.Current.BridgeAutoStart)
            {
                DeleteInstanceRegistration();
                StopListener();
                WriteLog("bridge_before_assembly_reload autostart_disabled");
                return;
            }
            RegisterInstance("transitioning");
            StopListener();
        }

        private static void OnAfterAssemblyReload()
        {
            domainReloading = false;
            UnityCliSessionMutationLease.Initialize();
            UnityCliCompilationEventBroker.Initialize();
            UnityCliCompileCoordinator.Initialize();
            UnityCliLiveBuildCoordinator.Initialize();
            UnityCliLiveRefreshCoordinator.Initialize();
            UnityCliAssetRefreshCoordinator.Initialize();
            UnityCliPackageEmbedCoordinator.Initialize();
            UnityCliPackageResolveCoordinator.Initialize();
            UnityCliTestRunCoordinator.Initialize();
            UnityCliNavMeshBakeCoordinator.Initialize();
            UnityCliOcclusionBakeCoordinator.Initialize();
            StartupState.Reset();
            WriteLog("bridge_after_assembly_reload scheduling bootstrap");
            ScheduleBridgeStartup();

            // After domain reload, static fields are reset. Re-initialize the Runtime Executor
            // and restore session state if we are already in Play Mode.
            ReinitializeRuntimeExecutorAfterDomainReload();
        }

        private static void UnregisterAndStop()
        {
            ReleaseActiveDirectMutationLease();
            StartupState.Reset();
            UnityCliCompilationEventBroker.Shutdown();
            UnityCliCompileCoordinator.Shutdown();
            UnityCliLiveBuildCoordinator.Shutdown();
            UnityCliLiveRefreshCoordinator.Shutdown();
            UnityCliAssetRefreshCoordinator.Shutdown();
            UnityCliPackageEmbedCoordinator.Shutdown();
            UnityCliPackageResolveCoordinator.Shutdown();
            UnityCliTestRunCoordinator.Shutdown();
            UnityCliNavMeshBakeCoordinator.Shutdown();
            UnityCliOcclusionBakeCoordinator.Shutdown();
            BridgeLifetime.Cancel();
            CancelQueuedWork();
            CancelPendingWaits("E_UNITY_QUITTING", "Unity Editor is quitting.", "Restart Unity and retry the request.", 5000);
            DeleteInstanceRegistration();
            StopListener();
        }

        /// <summary>统一记录 PlayMode 过渡；稳定 Play 下 isPlayingOrWillChangePlaymode 仍为 true，不能直接用它判断 changing。</summary>
        private static void OnBridgePlayModeStateChanged(PlayModeStateChange state)
        {
            playModeChanging = state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode;
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                runtimeSession.Cancel();
                runtimeSession.Dispose();
                runtimeSession = new CancellationTokenSource();
            }
            if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.EnteredEditMode)
                UnityCliSessionMutationLease.TryFinalizeOwnedPlayModeRoot();
        }

        internal static bool IsPlayModeChanging() { return playModeChanging; }

        private static CancellationTokenSource CreateExecutionLifetime(UnityCli.Protocol.BridgeRequest request)
        {
            var tokens = new List<CancellationToken> { BridgeLifetime.Token };
            if (request != null && string.Equals(request.Command, "runtime-tool.run", StringComparison.Ordinal)) tokens.Add(runtimeSession.Token);
            return CancellationTokenSource.CreateLinkedTokenSource(tokens.ToArray());
        }

        private static CancellationTokenSource CreateExecutionTimeout(UnityCli.Protocol.BridgeRequest request)
        {
            var source = CreateExecutionLifetime(request);
            int remainingMs;
            if (request != null && request.TryDeadlineRemainingMs(out remainingMs))
                source.CancelAfter(TimeSpan.FromMilliseconds(remainingMs));
            else
                source.Cancel();
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

        private static async ValueTask<string> Execute(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var data = await ExecuteData(request, cancellationToken);
                // handler 返回后先让 coordinator 消费 completion evidence；超时后的已证明 mutation 仍可持久化 canonical terminal。
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
                    "Bridge request execution was cancelled because the immutable deadline elapsed or the Unity session ended.",
                    nextActions: new[] { "Inspect editor.state and retry only after Unity is responsive." }));
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

            var allowedDuringCompileOrImport = UnityCliCompileCoordinator.IsCommand(request.Command) || UnityCliLiveRefreshCoordinator.IsCommand(request.Command) || UnityCliAssetRefreshCoordinator.IsCommand(request.Command) || UnityCliPackageEmbedCoordinator.IsCommand(request.Command) || UnityCliPackageResolveCoordinator.IsCommand(request.Command) || UnityCliTestRunCoordinator.IsCommand(request.Command) || UnityCliNavMeshBakeCoordinator.IsCommand(request.Command) || UnityCliOcclusionBakeCoordinator.IsCommand(request.Command) || IsEditorStateSnapshotCommand(request.Command);
            if (EditorApplication.isCompiling && !allowedDuringCompileOrImport)
            {
                throw new BridgeCommandException("E_UNITY_COMPILING", "Unity Editor is compiling scripts.", null, new[] { "Wait for Unity to finish compiling and retry." }, true, 5000);
            }

            if (EditorApplication.isUpdating && !allowedDuringCompileOrImport)
            {
                throw new BridgeCommandException("E_UNITY_IMPORTING", "Unity Editor is importing assets.", null, new[] { "Wait for Unity to finish importing and retry." }, true, 5000);
            }

            if (IsPlayModeChanging())
            {
                // 只在 PlayMode 切换期限制高风险命令；稳定运行态允许 CLI 工具继续操作 Unity。
                var allowedDuringPlayModeTransition = UnityCliCompileCoordinator.IsCommand(request.Command) || UnityCliLiveRefreshCoordinator.IsCommand(request.Command) || UnityCliAssetRefreshCoordinator.IsCommand(request.Command) || UnityCliPackageEmbedCoordinator.IsCommand(request.Command) || UnityCliPackageResolveCoordinator.IsCommand(request.Command) || UnityCliTestRunCoordinator.IsCommand(request.Command) || UnityCliNavMeshBakeCoordinator.IsCommand(request.Command) || UnityCliOcclusionBakeCoordinator.IsCommand(request.Command)
                    || IsEditorStateSnapshotCommand(request.Command)
                    || request.Command == "editor.console"
                    || request.Command == "editor.pause"
                    || request.Command == "editor.stop"
                    || request.Command == "runtime-tool.list"
                    || request.Command == "runtime-tool.run"
                    || request.Command == "project-tool.list"
                    || request.Command == "diagnose.preflight"
                    || request.Command == "extension.status";

                if (!allowedDuringPlayModeTransition)
                {
                    WriteLog("execute_play_mode_guard_blocked_transition requestId=" + request.RequestId + " command=" + request.Command + " isPlaying=" + Bool(EditorApplication.isPlaying));
                    throw new BridgeCommandException("E_UNITY_PLAY_MODE", "Unity Editor is entering or exiting Play mode. (execute-guard-v3)", null, new[] { "Wait for Play mode transition to complete and retry." }, true, 3000);
                }
                WriteLog("execute_play_mode_guard_allowed requestId=" + request.RequestId + " command=" + request.Command + " isPlaying=" + Bool(EditorApplication.isPlaying));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await CommandRegistry.ExecuteAsync(request, cancellationToken);
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
