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
        private const string BridgeDiagnosticsFingerprint = "health-fingerprint-play-mode-guard-v3";
        private static readonly BridgeCommandRegistry CommandRegistry = CreateCommandRegistry();
        private static readonly string ListenerId = "listener-" + Guid.NewGuid().ToString("N");
        private static readonly DateTime BridgeLoadedAtUtc = DateTime.UtcNow;
        private static HttpListener listener;
        private static bool started;
        private static readonly BridgeStartupStateMachine StartupState = new BridgeStartupStateMachine();
        private static readonly BridgeInitialPublicationGate InitialPublicationGate = new BridgeInitialPublicationGate();
        private static volatile bool domainReloading;
        private const double RegistryRefreshSeconds = 30.0;
        private static int port;
        private static double nextRegistryRefreshAt;
        private static bool readyPublicationPending;
        private static readonly CancellationTokenSource BridgeLifetime = new CancellationTokenSource();
        private static CancellationTokenSource runtimeSession = new CancellationTokenSource();

        // 主线程只更新内存快照；HTTP后台线程读取时不得调用Unity API或写入Registry。
        private static long mainThreadPulseSequence;
        private static long mainThreadPulseTimestamp;
        private static volatile bool cachedIsCompiling;
        private static volatile bool cachedIsUpdating;
        private static volatile bool cachedIsPlayingOrWillChangePlaymode;
        private static volatile bool cachedIsPlaying;
        private static volatile bool cachedIsBuilding;
        private static volatile bool cachedIsPlayModeChanging;
        private static volatile bool playModeChanging = EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode;
        internal static void HitQueueTestBoundary(BridgeQueueState state, string name)
        {
            if (state == null) throw new ArgumentNullException("state");
            state.TestHooks.Hit(name);
        }

        static UnityCliBridge()
        {
            // AssetImportWorker 不是可寻址 Editor 实例，不得启动 listener 或写入 instance registry。
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            WriteRuntimeLoadDiagnostics("static_constructor");
            UnityCliTransactionLog.RotateForNewSession();
            InitializeProjectToolCatalog();
            if (ProjectToolCatalogReady()) InitializeRuntimeExecutor();
            InitializeAdmissionPool();
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
            MethodHookHost.InitializeAfterReload();
            ReconcileRestoredAdmissionRoots();
            EditorApplication.update += ProcessQueuedWork;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.quitting += UnregisterAndStop;
            EditorApplication.playModeStateChanged += OnBridgePlayModeStateChanged;
            domainReloading = false;
            lock (ProductionQueueState.QueueLock)
            {
                ProductionQueueState.QueuePublicationOpen = false;
                ProductionQueueState.QueueSessionStopping = false;
            }
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

        /// <summary>
        /// handoff 完成后即可发布基础 listener。目录局部隔离或整体不可用改由 catalog health 单独表达，
        /// 不再阻断状态观察与诊断；只有 handoff 自身失败才继续 fail closed。
        /// </summary>
        private static bool CompleteBridgeStartup(out string handoffState, out string handoffError)
        {
            WriteLog("bridge_pipeline_handoff_started");
            if (!UnityCliPipelineHandoff.EnsureAppliedOnce(out handoffState, out handoffError)) return false;
            if (!ProjectToolCatalogReady())
            {
                var health = ProjectToolCatalogHealthSnapshotReader.Read();
                WriteLog("bridge_startup_catalog_unavailable degraded=true failure=" + BoundedCatalogDiagnostic(health.Failure));
            }
            return true;
        }

        private static void StartListener()
        {
            if (started)
            {
                return;
            }

            try
            {
                PrepareListenerGenerationForStart();
                // 控制通道必须在任何 pause 资源安装之前发布；否则现场一旦停住就没有可达的解除路径。
                DebugPauseControl.Activate();
                // 控制通道会在后台线程上读取 session 状态；这里在主线程上先发布 SessionState 恢复事实，
                // 使 off-main 服务不必（也不得）直接访问该主线程专属状态。
                DebugSessionStore.PublishRecoveryFacts();
                if (string.IsNullOrWhiteSpace(ListenerId)) throw new InvalidOperationException("Listener identity is unavailable.");
                listener = new HttpListener();
                port = StartHttpListener(listener);
                started = true;
                lock (ProductionQueueState.QueueLock)
                {
                    ProductionQueueState.QueueSessionStopping = false;
                    ProductionQueueState.QueuePublicationOpen = false;
                }
                StartupState.BeginRegistryPublication();
                InitialPublicationGate.Start();
                HandleInitialPublicationOutcome(RegisterInstance("transitioning"));
            }
            catch (Exception exception)
            {
                StopListener();
                WriteRuntimeLoadDiagnostics("listener_start_failed");
                WriteLog("bridge_start_failed " + exception.GetType().Name + ": " + exception.Message + " listenerId=" + ListenerId + " diagnosticsFingerprint=" + BridgeDiagnosticsFingerprint + " assemblyMvid=" + AssemblyMvid() + " packageName=" + BridgePackageName() + " packageAssetPath=" + BridgePackageAssetPath() + " packageResolvedPath=" + BridgePackageResolvedPath());
            }
        }

        /// <summary>首次registry成功后只开放一次queue并启动一次ListenLoop；永久失败才停止listener。</summary>
        private static void HandleInitialPublicationOutcome(InstancePublicationRetryOutcome outcome)
        {
            if (outcome == InstancePublicationRetryOutcome.None) return;
            var action = InitialPublicationGate.Apply(outcome);
            if (action == BridgeInitialPublicationAction.Wait)
            {
                WriteRuntimeLoadDiagnostics("listener_registry_pending");
                return;
            }
            if (action == BridgeInitialPublicationAction.StopListener)
            {
                StartupState.CompleteRegistryPublication();
                StopListener();
                WriteRuntimeLoadDiagnostics("listener_registry_failed");
                WriteLog("bridge_start_failed initial registry publication exhausted listenerId=" + ListenerId);
                return;
            }
            if (action != BridgeInitialPublicationAction.OpenQueueAndListen) return;
            StartupState.CompleteRegistryPublication();
            ProductionQueueState.ListenerId = ListenerId;
            ProductionQueueState.ListenerGeneration = Math.Max(1, CurrentListenerGeneration());
            lock (ProductionQueueState.QueueLock)
            {
                ProductionQueueState.QueueSessionStopping = false;
                ProductionQueueState.QueuePublicationOpen = true;
            }
            Task.Run(ListenLoop);
            readyPublicationPending = true;
            nextRegistryRefreshAt = EditorApplication.timeSinceStartup + RegistryRefreshSeconds;
            WriteRuntimeLoadDiagnostics("listener_started");
            WriteLog("bridge_started port=" + port + " listenerId=" + ListenerId + " diagnosticsFingerprint=" + BridgeDiagnosticsFingerprint + " assemblyMvid=" + AssemblyMvid() + " packageName=" + BridgePackageName() + " packageAssetPath=" + BridgePackageAssetPath() + " packageResolvedPath=" + BridgePackageResolvedPath());
        }

        private static void OnBeforeAssemblyReload()
        {
            domainReloading = true;
            var queuedBeforeReload = CloseQueuePublicationAndTakeQueuedWork();
            SaveAdmissionPoolBeforeReload();
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
            MethodHookHost.BeforeReload();
            BridgeLifetime.Cancel();
            CancelQueuedWork(queuedBeforeReload);
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
            lock (ProductionQueueState.QueueLock)
            {
                ProductionQueueState.QueuePublicationOpen = false;
                ProductionQueueState.QueueSessionStopping = false;
            }
            InitializeAdmissionPool();
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
            ReconcileRestoredAdmissionRoots();
            StartupState.Reset();
            WriteLog("bridge_after_assembly_reload scheduling bootstrap");
            ScheduleBridgeStartup();
        }

        private static void UnregisterAndStop()
        {
            var queuedBeforeShutdown = CloseQueuePublicationAndTakeQueuedWork(true);
            CancelQueuedWorkForShutdown(queuedBeforeShutdown);
            ShutdownAdmissionPool();
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
            MethodHookHost.Shutdown();
            BridgeLifetime.Cancel();
            CancelPendingWaits("E_UNITY_QUITTING", "Unity Editor is quitting.", "Restart Unity and retry the request.", 5000);
            DeleteInstanceRegistration();
            StopListener();
        }

        /// <summary>记录 PlayMode 过渡并立即发布实例状态；稳定状态下只保留低频保险刷新。</summary>
        private static void OnBridgePlayModeStateChanged(PlayModeStateChange state)
        {
            playModeChanging = state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode;
            MethodHookHost.OnPlayModeStateChanged(state);
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                runtimeSession.Cancel();
                runtimeSession.Dispose();
                runtimeSession = new CancellationTokenSource();
            }
            if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.EnteredEditMode)
                UnityCliSessionMutationLease.TryFinalizeOwnedPlayModeRoot();
            if (started && InitialPublicationGate.Published)
            {
                RegisterInstance(playModeChanging ? "transitioning" : "ready");
                nextRegistryRefreshAt = EditorApplication.timeSinceStartup + RegistryRefreshSeconds;
            }
        }

        internal static bool IsPlayModeChanging() { return playModeChanging; }

        private static CancellationTokenSource CreateExecutionLifetime(UnityCli.Protocol.BridgeRequest request)
        {
            var tokens = new List<CancellationToken> { BridgeLifetime.Token };
            if (request != null && string.Equals(request.Command, "catalog.player.run", StringComparison.Ordinal)) tokens.Add(runtimeSession.Token);
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
            lock (ProductionQueueState.QueueLock) ProductionQueueState.QueuePublicationOpen = false;
            readyPublicationPending = false;
            StartupState.CompleteRegistryPublication();
            InitialPublicationGate.Reset();
            CancelInstancePublicationRetry();
        }

        private static async ValueTask<string> Execute(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var data = await ExecuteData(request, cancellationToken);
                // Project Tool 的成功 envelope 已在其同一 deadline 与请求预算内完成；其他内部命令保持统一包装。
                var serializedEnvelope = data as SerializedBridgeEnvelope;
                if (serializedEnvelope != null) return serializedEnvelope.Json;
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

            var allowedDuringCompileOrImport = UnityCliDurableStatusCoordinator.IsCommand(request.Command) || UnityCliCompileCoordinator.IsCommand(request.Command) || UnityCliLiveRefreshCoordinator.IsCommand(request.Command) || UnityCliAssetRefreshCoordinator.IsCommand(request.Command) || UnityCliPackageEmbedCoordinator.IsCommand(request.Command) || UnityCliPackageResolveCoordinator.IsCommand(request.Command) || UnityCliTestRunCoordinator.IsCommand(request.Command) || UnityCliNavMeshBakeCoordinator.IsCommand(request.Command) || UnityCliOcclusionBakeCoordinator.IsCommand(request.Command) || IsEditorStateSnapshotCommand(request.Command) || IsProjectToolCatalogReadCommand(request.Command) || IsDoctorAdmissionBypassCommand(request.Command);
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
                var allowedDuringPlayModeTransition = UnityCliDurableStatusCoordinator.IsCommand(request.Command) || UnityCliCompileCoordinator.IsCommand(request.Command) || UnityCliLiveRefreshCoordinator.IsCommand(request.Command) || UnityCliAssetRefreshCoordinator.IsCommand(request.Command) || UnityCliPackageEmbedCoordinator.IsCommand(request.Command) || UnityCliPackageResolveCoordinator.IsCommand(request.Command) || UnityCliTestRunCoordinator.IsCommand(request.Command) || UnityCliNavMeshBakeCoordinator.IsCommand(request.Command) || UnityCliOcclusionBakeCoordinator.IsCommand(request.Command)
                    || IsEditorStateSnapshotCommand(request.Command)
                    || request.Command == "editor.pause"
                    || request.Command == "editor.play-mode.stop"
                    || IsProjectToolCatalogReadCommand(request.Command)
                    || request.Command == "catalog.player.run"
                    || IsDoctorAdmissionBypassCommand(request.Command);

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
