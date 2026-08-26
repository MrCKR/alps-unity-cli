#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// Runtime Executor: manages Play Mode lifecycle awareness, ready handshake,
    /// and runtime tool execution routing. All runtime tools (Target=runtime)
    /// are routed through this executor which validates Play Mode session state,
    /// enforces timeout semantics, and provides structured errors with nextActions.
    /// </summary>
    public static partial class UnityCliBridge
    {
        // Play Mode session tracking
        private static string currentPlayModeSessionId = null;
        private static bool playModeReady = false;
        private static int runtimeRegistryVersion = 0;
        private static string runtimeToolCatalogFingerprint = null;
        private static bool runtimeExecutorInitialized = false;

        /// <summary>
        /// Initialize the Runtime Executor: subscribe to Play Mode state change
        /// events and compute the initial tool catalog fingerprint.
        /// </summary>
        private static void InitializeRuntimeExecutor()
        {
            if (runtimeExecutorInitialized) return;
            runtimeExecutorInitialized = true;
            EditorApplication.playModeStateChanged += OnRuntimePlayModeStateChanged;
            RefreshRuntimeToolCatalog();
            WriteLog("runtime_executor_initialized");

            // If we are already in Play Mode at initialization time (e.g. after domain reload),
            // immediately establish the session as ready.
            if (EditorApplication.isPlaying)
            {
                currentPlayModeSessionId = GeneratePlayModeSessionId();
                playModeReady = true;
                runtimeRegistryVersion++;
                RefreshRuntimeToolCatalog();
                WriteLog("runtime_session_ready_after_domain_reload sessionId=" + currentPlayModeSessionId + " registryVersion=" + runtimeRegistryVersion.ToString(CultureInfo.InvariantCulture) + " fingerprint=" + (runtimeToolCatalogFingerprint ?? "none"));
            }
        }

        /// <summary>
        /// Called after domain reload to reinitialize the Runtime Executor.
        /// Domain reload resets all static fields, so the event subscription
        /// and session state are lost. This method re-subscribes to Play Mode
        /// state change events and restores the session state if already in
        /// Play Mode.
        /// </summary>
        private static void ReinitializeRuntimeExecutorAfterDomainReload()
        {
            // Reset the guard so InitializeRuntimeExecutor runs again
            runtimeExecutorInitialized = false;
            InitializeRuntimeExecutor();
        }

        /// <summary>
        /// Handle Play Mode state transitions. Generate a new session ID when
        /// entering Play Mode, mark ready, and invalidate the session when
        /// exiting or during domain reload.
        /// </summary>
        private static void OnRuntimePlayModeStateChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.EnteredEditMode:
                    if (currentPlayModeSessionId != null)
                        WriteLog("runtime_session_ended sessionId=" + currentPlayModeSessionId + " reason=exited_play_mode");
                    currentPlayModeSessionId = null;
                    playModeReady = false;
                    break;

                case PlayModeStateChange.ExitingEditMode:
                    // About to enter Play Mode: pre-generate session ID
                    currentPlayModeSessionId = GeneratePlayModeSessionId();
                    playModeReady = false;
                    runtimeRegistryVersion++;
                    RefreshRuntimeToolCatalog();
                    WriteLog("runtime_session_entering sessionId=" + currentPlayModeSessionId);
                    break;

                case PlayModeStateChange.EnteredPlayMode:
                    // Play Mode is now active: mark ready
                    // After domain reload, currentPlayModeSessionId is reset to null.
                    // Generate a new session ID if the pre-generated one was lost.
                    if (currentPlayModeSessionId == null)
                    {
                        currentPlayModeSessionId = GeneratePlayModeSessionId();
                        runtimeRegistryVersion++;
                        RefreshRuntimeToolCatalog();
                    }
                    playModeReady = true;
                    WriteLog("runtime_session_ready sessionId=" + currentPlayModeSessionId + " registryVersion=" + runtimeRegistryVersion.ToString(CultureInfo.InvariantCulture) + " fingerprint=" + (runtimeToolCatalogFingerprint ?? "none"));
                    break;

                case PlayModeStateChange.ExitingPlayMode:
                    // About to exit Play Mode: mark as not ready
                    playModeReady = false;
                    WriteLog("runtime_session_exiting sessionId=" + currentPlayModeSessionId);
                    break;
            }
        }

        private static string GeneratePlayModeSessionId()
        {
            return "pms-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static void RefreshRuntimeToolCatalog()
        {
            DiscoverProjectTools();
            var count = 0;
            var material = new StringBuilder();
            foreach (var pair in ProjectTools)
            {
                var def = pair.Value;
                if (def.Target == "runtime" || def.Target == "both")
                {
                    count++;
                    material.Append(def.Name).Append('\n')
                        .Append(def.Fingerprint ?? "").Append('\n');
                }
            }
            runtimeToolCatalogFingerprint = Sha256Hex("unity-cli-runtime-tool-catalog-v1\ncount=" + count.ToString(CultureInfo.InvariantCulture) + "\n" + material.ToString());
        }

        /// <summary>
        /// Runtime session readiness report, included in instance registration
        /// and available via editor.state.
        /// </summary>
        private static Dictionary<string, object> RuntimeSessionData()
        {
            var availableTools = new List<object>();
            foreach (var pair in ProjectTools)
            {
                if (pair.Value.Target != "runtime" && pair.Value.Target != "both") continue;
                availableTools.Add(new Dictionary<string, object>
                {
                    { "name", pair.Value.Name }, { "target", pair.Value.Target }, { "availabilityScope", pair.Value.AvailabilityScope }, { "available", playModeReady }
                });
            }
            // 会话数据直接嵌入最终 response，禁止序列化后重新解析为 object。
            return new Dictionary<string, object>
            {
                { "playModeSessionId", currentPlayModeSessionId }, { "ready", playModeReady }, { "registryVersion", runtimeRegistryVersion },
                { "toolCatalogFingerprint", runtimeToolCatalogFingerprint }, { "availableRuntimeTools", availableTools }
            };
        }

        /// <summary>
        /// List runtime tools with their availability status based on current
        /// Play Mode session state.
        /// </summary>
        private static object RuntimeToolList(UnityCli.Protocol.BridgeRequest request)
        {
            DiscoverProjectTools();
            InitializeRuntimeExecutor();
            var tools = new System.Collections.Generic.List<object>();
            foreach (var definition in ProjectTools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal))
            {
                if (definition.Target != "runtime" && definition.Target != "both") continue;
                var descriptor = ProjectToolDescriptorData(definition);
                descriptor["available"] = playModeReady;
                if (!playModeReady)
                    descriptor["unavailableReason"] = "E_RUNTIME_NOT_READY: Enter Play Mode before executing this Tool.";
                tools.Add(descriptor);
            }
            return new System.Collections.Generic.Dictionary<string, object>
            {
                { "tools", tools }, { "session", RuntimeSessionData() }, { "logPath", LogPath() }
            };
        }

        /// <summary>
        /// Execute a runtime tool. Validates Play Mode session state, enforces
        /// timeout, routes to the tool's Execute delegate, and returns structured
        /// errors for non-ready, session stale, and timeout scenarios.
        /// </summary>
        private sealed class RuntimeToolRunArgs
        {
            public string Name { get; set; }
            [UnityCliAnyJson] public object Input { get; set; }
            public string ExpectedDescriptorFingerprint { get; set; }
            public string ArtifactDir { get; set; }
            public string ProjectPath { get; set; }
        }

        private static async ValueTask<object> RuntimeToolRun(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            DiscoverProjectTools();
            InitializeRuntimeExecutor();

            var args = request.DeserializeArgs<RuntimeToolRunArgs>();
            var name = args.Name;
            if (string.IsNullOrEmpty(name))
            {
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Runtime tool name is required.",
                    new Dictionary<string, object> { { "field", "name" } },
                    new[] { "Run unitycli tools search <keyword> --instance <id|projectPath|alias>, then execute a valid runtime Tool with --runtime." });
            }

            if (!playModeReady)
            {
                WriteLog("runtime_tool_not_ready requestId=" + request.RequestId + " name=" + name + " sessionId=" + (currentPlayModeSessionId ?? "null"));
                throw new BridgeCommandException("E_RUNTIME_NOT_READY",
                    "Runtime Tool cannot execute because the selected Editor is not in a ready Play Mode session.",
                    "Run unitycli tools run editor.play, wait until editor.state reports ready Play Mode, then retry.");
            }

            // Validate the tool exists and is a runtime tool
            if (!ProjectTools.ContainsKey(name))
            {
                throw new BridgeCommandException("E_TOOL_NOT_FOUND", "Tool not found: " + name,
                    "Run unitycli tools group --instance <id|projectPath|alias> or tools search <keyword> to inspect the connected instance catalog.");
            }

            var definition = ProjectTools[name];
            if (definition.Target != "runtime" && definition.Target != "both")
            {
                throw new BridgeCommandException("E_RUNTIME_TOOL_TARGET_MISMATCH",
                    "Tool " + name + " has Target=" + definition.Target + " and cannot be executed via the runtime path.",
                    new Dictionary<string, object> { { "toolName", name }, { "toolTarget", definition.Target } },
                    new[] { "Run editor-domain tools without --runtime, or select a Tool whose descriptor declares target=runtime." });
            }
            var dryRun = request.Execution != null && request.Execution.DryRun == true;

            // runtime-tool.run 是内部包装命令；门禁前刷新 live 指纹，防止跨 PlayMode/domain reload 重放旧 proof。
            var fingerprintCheck = RefreshProjectToolFingerprint(definition);
            if (!string.IsNullOrEmpty(args.ExpectedDescriptorFingerprint)
                && !string.Equals(args.ExpectedDescriptorFingerprint, definition.Fingerprint, StringComparison.Ordinal))
            {
                throw new BridgeCommandException(
                    "E_TOOL_DESCRIPTOR_CHANGED",
                    "The runtime Tool descriptor changed after discovery: " + name,
                    new Dictionary<string, object> { { "toolName", name }, { "expectedFingerprint", args.ExpectedDescriptorFingerprint }, { "actualFingerprint", definition.Fingerprint } },
                    new[] { "Run tools describe again in the current Play Mode generation before constructing a new request." });
            }
            // Fingerprint policy 使用同一次 live refresh 结果。
            if (fingerprintCheck.Blocked)
            {
                WriteLog("runtime_tool_fingerprint_blocked requestId=" + request.RequestId + " name=" + name);
                throw new BridgeCommandException("E_TOOL_FINGERPRINT_MISMATCH", fingerprintCheck.Message,
                    ProjectToolFingerprintDetails(definition),
                    ProjectToolFingerprintRePinSteps(definition));
            }

            // timeout 仅来自顶层 execution，业务 input 不参与执行策略。
            var timeoutMs = request.ExecutionTimeoutMs(definition.DefaultTimeoutMs, definition.MaxTimeoutMs);

            // Input schema validation
            var inputJson = request.RawJsonArg("input") ?? "{}";
            var schemaError = ValidateProjectToolInput(definition, inputJson);
            if (schemaError != null) throw SchemaValidationException(schemaError);

            var artifactDir = string.IsNullOrWhiteSpace(args.ArtifactDir) ? null : ResolveArtifactDir(args.ArtifactDir);
            object result;
            UnityCliProjectToolContext executionContext = null;
            try
            {
                WriteLog("runtime_tool_run requestId=" + request.RequestId + " name=" + name + " sessionId=" + currentPlayModeSessionId + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));

                // Check Play Mode is still ready before executing
                if (!EditorApplication.isPlaying || !playModeReady)
                {
                    throw new BridgeCommandException("E_RUNTIME_NOT_READY",
                        "Play Mode session ended before the runtime tool could execute.",
                        "Re-enter Play Mode and retry.");
                }

                using (var toolTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runtimeSession.Token))
                {
                    toolTimeout.CancelAfter(timeoutMs);
                    var typedInput = UnityCliSerializer.Deserialize(inputJson, definition.RequestType);
                    var effectPreparation = PrepareToolEffect(request, definition.CompletionContract, typedInput);
                    executionContext = new UnityCliProjectToolContext
                    {
                        RequestId = request.RequestId,
                        ProjectPath = args.ProjectPath ?? ProjectRoot(),
                        ArtifactDir = artifactDir,
                        LogPath = LogPath(),
                        DryRun = dryRun,
                        CommitEffectAction = () => CommitToolEffect(request, definition.CompletionContract, effectPreparation),
                        TimeoutMs = timeoutMs,
                        Wait = CreateWaiter(request.RequestId, name),
                        CancellationToken = toolTimeout.Token
                    };
                    result = await definition.InvokeAsync(typedInput, executionContext, toolTimeout.Token);
                    if (toolTimeout.IsCancellationRequested && !executionContext.EffectCommitted)
                        throw new OperationCanceledException(toolTimeout.Token);
                }
            }
            catch (BridgeCommandException bce)
            {
                WriteLog("runtime_tool_error requestId=" + request.RequestId + " name=" + name + " error=" + bce.Code);
                if (executionContext != null && executionContext.EffectCommitted && bce.Code != "E_EXECUTION_TIMEOUT")
                    throw PostEffectExecutionTimeout(name, "invoke", bce.Code + ": " + bce.Message);
                throw;
            }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException ?? exception;
                var innerBce = inner as BridgeCommandException;
                if (innerBce != null) throw innerBce;
                if (inner is OperationCanceledException)
                {
                    if (executionContext != null && executionContext.EffectCommitted)
                        throw PostEffectExecutionTimeout(name, "wait", "The immutable request deadline elapsed after the effect barrier.");
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "The immutable request deadline elapsed before the runtime Tool reached its terminal proof.", "Inspect editor.state before submitting a new request.");
                }
                if (executionContext != null && executionContext.EffectCommitted)
                    throw PostEffectExecutionTimeout(name, "invoke", inner.GetType().Name + ": " + inner.Message);
                throw new BridgeCommandException("E_TOOL_EXECUTION_FAILED", inner.GetType().Name + ": " + inner.Message,
                    "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }
            catch (OperationCanceledException)
            {
                if (executionContext != null && executionContext.EffectCommitted)
                    throw PostEffectExecutionTimeout(name, "wait", "The immutable request deadline elapsed after the effect barrier.");
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "The immutable request deadline elapsed before the runtime Tool reached its terminal proof.", "Inspect editor.state before submitting a new request.");
            }
            catch (Exception exception)
            {
                if (executionContext != null && executionContext.EffectCommitted)
                    throw PostEffectExecutionTimeout(name, "invoke", exception.GetType().Name + ": " + exception.Message);
                throw new BridgeCommandException("E_TOOL_EXECUTION_FAILED", exception.GetType().Name + ": " + exception.Message,
                    "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }

            // 终态只能归因到同一 Play Mode session。
            if (!playModeReady || !EditorApplication.isPlaying)
            {
                WriteLog("runtime_tool_session_invalid_after_run requestId=" + request.RequestId + " name=" + name);
                if (executionContext != null && executionContext.EffectCommitted)
                    throw PostEffectExecutionTimeout(name, "readback", "Play Mode session ended before the result could be attributed to its captured runtime session.");
                throw new BridgeCommandException("E_RUNTIME_SESSION_STALE",
                    "Play Mode session ended before the runtime Tool crossed its effect barrier.",
                    new Dictionary<string, object> { { "sessionId", currentPlayModeSessionId ?? "unknown" }, { "toolName", name } },
                    new[] { "Re-enter Play Mode and submit a new request." });
            }

            var outputJson = UnityCliSerializer.Serialize(result);
            var outputError = ValidateTypedInput(definition.ResultType, outputJson);
            if (outputError != null)
            {
                if (executionContext != null && executionContext.EffectCommitted)
                    throw PostEffectExecutionTimeout(name, "postcondition", "Runtime Tool result failed its derived output schema at " + outputError.Path + ".");
                throw new BridgeCommandException(
                    "E_TOOL_COMPLETION_PROOF_INVALID",
                    "Runtime Tool result failed its derived output schema at " + outputError.Path + ".",
                    new Dictionary<string, object> { { "toolName", definition.Name }, { "path", outputError.Path }, { "expected", outputError.Expected }, { "actual", outputError.Actual } },
                    new[] { "Fix the runtime Tool result DTO or completion driver before retrying." });
            }
            var terminalData = CompleteRuntimeToolSuccess(request, definition, executionContext, result, name);
            try { RecordToolCompletionEvidence(request, definition, result, terminalData); }
            catch (BridgeCommandException exception)
            {
                if (executionContext != null && executionContext.EffectCommitted)
                    throw PostEffectExecutionTimeout(name, "postcondition", exception.Code + ": " + exception.Message);
                throw;
            }
            return terminalData;
        }

        private static object CompleteRuntimeToolSuccess(UnityCli.Protocol.BridgeRequest request, RegisteredTool definition, UnityCliProjectToolContext executionContext, object result, string name)
        {
            if (!playModeReady || !cachedIsPlaying)
            {
                WriteLog("runtime_tool_session_invalid_after_run requestId=" + request.RequestId + " name=" + name + " cachedIsPlaying=" + Bool(cachedIsPlaying));
                if (executionContext != null && executionContext.EffectCommitted)
                    throw PostEffectExecutionTimeout(name, "readback", "Play Mode session changed before terminal persistence.");
                throw new BridgeCommandException("E_RUNTIME_SESSION_STALE", "Play Mode session ended before the runtime Tool crossed its effect barrier.", "Re-enter Play Mode and submit a new request.");
            }

            WriteLog("runtime_tool_completed requestId=" + request.RequestId + " name=" + name);
            return new System.Collections.Generic.Dictionary<string, object> { { "result", result } };
        }
    }
}
#endif