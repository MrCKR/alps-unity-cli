#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        private static string lastStalePlayModeSessionId = null;
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
                    // Exited Play Mode: invalidate the runtime session
                    // Preserve the stale sessionId so callers requesting the old session
                    // get E_RUNTIME_SESSION_STALE instead of E_RUNTIME_NOT_READY.
                    if (currentPlayModeSessionId != null)
                    {
                        lastStalePlayModeSessionId = currentPlayModeSessionId;
                        WriteLog("runtime_session_stale sessionId=" + currentPlayModeSessionId + " reason=exited_play_mode");
                    }
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
                        .Append(def.Version ?? "1.0.0").Append('\n')
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
            public string PlayModeSessionId { get; set; }
            public string FingerprintMode { get; set; }
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
                    new[] { "Run tools run <name> --target <instance> with a valid runtime tool name." });
            }

            // Validate Play Mode is ready
            if (!playModeReady)
            {
                // If the caller specified a session ID, check whether it matches a
                // recently-stale session. This gives a more actionable error than
                // E_RUNTIME_NOT_READY when the Play Mode session simply ended.
                var staleRequestedSessionId = args.PlayModeSessionId;
                if (!string.IsNullOrEmpty(staleRequestedSessionId) && string.Equals(staleRequestedSessionId, lastStalePlayModeSessionId, StringComparison.Ordinal))
                {
                    WriteLog("runtime_tool_session_stale_after_exit requestId=" + request.RequestId + " name=" + name + " requested=" + staleRequestedSessionId + " stale=" + lastStalePlayModeSessionId);
                    throw new BridgeCommandException("E_RUNTIME_SESSION_STALE",
                        "Runtime tool session is stale: Play Mode session " + staleRequestedSessionId + " has ended (exited Play Mode).",
                        new Dictionary<string, object> { { "requestedSessionId", staleRequestedSessionId }, { "staleSessionId", lastStalePlayModeSessionId }, { "playModeActive", false } },
                        new[] { "Re-enter Play Mode and retry, or omit playModeSessionId to use the current session." });
                }
                WriteLog("runtime_tool_not_ready requestId=" + request.RequestId + " name=" + name + " sessionId=" + (currentPlayModeSessionId ?? "null"));
                throw new BridgeCommandException("E_RUNTIME_NOT_READY",
                    "Runtime tool cannot execute: Play Mode is not ready. Current Play Mode session is not active.",
                    "Enter Play Mode first (use editor play), then retry the runtime tool call.");
            }

            // Validate the requested session matches the current session
            var requestedSessionId = args.PlayModeSessionId;
            if (!string.IsNullOrEmpty(requestedSessionId) && !string.Equals(requestedSessionId, currentPlayModeSessionId, StringComparison.Ordinal))
            {
                WriteLog("runtime_tool_session_stale requestId=" + request.RequestId + " name=" + name + " requested=" + requestedSessionId + " current=" + currentPlayModeSessionId);
                throw new BridgeCommandException("E_RUNTIME_SESSION_STALE",
                    "Runtime tool session is stale: the Play Mode session has changed. Requested session " + requestedSessionId + " does not match current session " + currentPlayModeSessionId + ".",
                    new Dictionary<string, object> { { "requestedSessionId", requestedSessionId }, { "currentSessionId", currentPlayModeSessionId } },
                    new[] { "Re-enter Play Mode and retry, or omit playModeSessionId to use the current session." });
            }

            // Validate the tool exists and is a runtime tool
            if (!ProjectTools.ContainsKey(name))
            {
                throw new BridgeCommandException("E_TOOL_NOT_FOUND", "Tool not found: " + name,
                    "Run tools list --include-custom to see registered tools.");
            }

            var definition = ProjectTools[name];
            if (definition.Target != "runtime" && definition.Target != "both")
            {
                throw new BridgeCommandException("E_RUNTIME_TOOL_TARGET_MISMATCH",
                    "Tool " + name + " has Target=" + definition.Target + " and cannot be executed via the runtime path.",
                    new Dictionary<string, object> { { "toolName", name }, { "toolTarget", definition.Target } },
                    new[] { "Use tools run <name> without --target runtime for editor-domain tools, or use a tool declared with Target=runtime." });
            }

            // runtime-tool.run 是内部包装命令；门禁前刷新 live 指纹，防止跨 PlayMode/domain reload 重放旧 proof。
            var fingerprintCheck = RefreshProjectToolFingerprint(definition, args.FingerprintMode);
            var dryRun = request.Execution != null && request.Execution.DryRun == true;
            if (definition.DestructiveHint
                && !(dryRun && definition.SupportsDryRun)
                && !request.SecurityConfirmed(name, true, definition.SupportsDryRun, "runtime-tool.run", definition.Fingerprint))
            {
                throw new BridgeCommandException("E_DESTRUCTIVE_CONFIRMATION_REQUIRED",
                    "Destructive runtime tool requires CLI confirmation and fingerprint proof: " + name,
                    "Rerun through unity_cli tools run " + name + " with --yes so the request includes destructive security proof.");
            }

            // Fingerprint policy 与 security proof 使用同一次 live refresh 结果。
            if (fingerprintCheck.Blocked)
            {
                WriteLog("runtime_tool_fingerprint_blocked requestId=" + request.RequestId + " name=" + name);
                throw new BridgeCommandException("E_TOOL_FINGERPRINT_MISMATCH", fingerprintCheck.Message,
                    ProjectToolFingerprintDetails(definition),
                    new[] { "Review the registered custom tool descriptor/handler code, then update the expected fingerprint or rerun with --fingerprint-mode warn." });
            }

            // timeout 仅来自顶层 execution，业务 input 不参与执行策略。
            var timeoutMs = request.ExecutionTimeoutMs(definition.DefaultTimeoutMs, definition.MaxTimeoutMs);

            // Input schema validation
            var inputJson = request.RawJsonArg("input") ?? "{}";
            var schemaError = ValidateProjectToolInput(definition, inputJson);
            if (schemaError != null) throw SchemaValidationException(schemaError);

            // Execute the runtime tool with timeout awareness
            var startedAt = DateTime.UtcNow;
            var artifactDir = ResolveArtifactDir(args.ArtifactDir, request.RequestId);
            try
            {
                Directory.CreateDirectory(artifactDir);
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_ARTIFACT_WRITE_FAILED", "Failed to create artifact directory: " + exception.Message,
                    "Pass --artifact-dir to a writable directory.");
            }

            object result;
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
                    result = await definition.InvokeAsync(typedInput, new UnityCliProjectToolContext
                    {
                        RequestId = request.RequestId,
                        ProjectPath = args.ProjectPath ?? ProjectRoot(),
                        ArtifactDir = artifactDir,
                        LogPath = LogPath(),
                        DryRun = dryRun,
                        TimeoutMs = timeoutMs,
                        Wait = CreateWaiter(request.RequestId, name),
                        CancellationToken = toolTimeout.Token
                    }, toolTimeout.Token);
                    toolTimeout.Token.ThrowIfCancellationRequested();
                }
            }
            catch (BridgeCommandException bce)
            {
                WriteLog("runtime_tool_error requestId=" + request.RequestId + " name=" + name + " error=" + bce.Code);
                throw;
            }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException ?? exception;
                var innerBce = inner as BridgeCommandException;
                if (innerBce != null) throw innerBce;
                throw new BridgeCommandException("E_TOOL_EXECUTION_FAILED", inner.GetType().Name + ": " + inner.Message,
                    "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_TOOL_EXECUTION_FAILED", exception.GetType().Name + ": " + exception.Message,
                    "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Verify Play Mode session is still valid after execution.
            // 这里仍在 Unity 主线程，允许直接读取 EditorApplication.isPlaying。
            if (!playModeReady || !EditorApplication.isPlaying)
            {
                WriteLog("runtime_tool_session_invalid_after_run requestId=" + request.RequestId + " name=" + name);
                throw new BridgeCommandException("E_RUNTIME_SESSION_STALE",
                    "Play Mode session ended during runtime tool execution. The tool may have partially executed.",
                    new Dictionary<string, object> { { "sessionId", currentPlayModeSessionId ?? "unknown" }, { "toolName", name }, { "partialExecution", true } },
                    new[] { "Re-enter Play Mode and verify the tool's side effects before retrying." });
            }

            return CompleteRuntimeToolSuccess(request, definition, artifactDir, timeoutMs, startedAt, result, name);
        }

        private static object CompleteRuntimeToolSuccess(UnityCli.Protocol.BridgeRequest request, RegisteredTool definition, string artifactDir, int timeoutMs, DateTime startedAt, object result, string name)
        {
            if (!playModeReady || !cachedIsPlaying)
            {
                WriteLog("runtime_tool_session_invalid_after_run requestId=" + request.RequestId + " name=" + name + " cachedIsPlaying=" + Bool(cachedIsPlaying));
                throw new BridgeCommandException("E_RUNTIME_SESSION_STALE",
                    "Play Mode session ended during runtime tool execution. The tool may have partially executed.",
                    new Dictionary<string, object> { { "sessionId", currentPlayModeSessionId ?? "unknown" }, { "toolName", name }, { "partialExecution", true } },
                    new[] { "Re-enter Play Mode and verify the tool's side effects before retrying." });
            }

            var endedAt = DateTime.UtcNow;
            var durationMs = (long)(endedAt - startedAt).TotalMilliseconds;
            var manifest = UnityCliSerializer.Serialize(ProjectToolManifestData(request.RequestId, definition, artifactDir, startedAt, endedAt));
            try
            {
                File.WriteAllText(Path.Combine(artifactDir, "manifest.json"), manifest);
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_ARTIFACT_WRITE_FAILED", "Failed to write artifact manifest: " + exception.Message,
                    "Pass --artifact-dir to a writable directory.");
            }

            if (durationMs > timeoutMs)
            {
                WriteLog("runtime_tool_timeout requestId=" + request.RequestId + " name=" + name + " durationMs=" + durationMs.ToString(CultureInfo.InvariantCulture) + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
                throw new BridgeCommandException("E_RUNTIME_TIMEOUT",
                    "Runtime tool exceeded timeoutMs: " + durationMs.ToString(CultureInfo.InvariantCulture) + "ms > " + timeoutMs.ToString(CultureInfo.InvariantCulture) + "ms.",
                    new Dictionary<string, object> { { "durationMs", durationMs }, { "timeoutMs", timeoutMs } },
                    new[] { "Increase the timeout or optimize the runtime tool, then retry." });
            }

            return new System.Collections.Generic.Dictionary<string, object>
            {
                { "command", "runtime-tool.run" },
                { "tool", new System.Collections.Generic.Dictionary<string, object> { { "name", definition.Name }, { "version", definition.Version }, { "target", definition.Target } } },
                { "session", new System.Collections.Generic.Dictionary<string, object> { { "playModeSessionId", currentPlayModeSessionId }, { "ready", playModeReady }, { "registryVersion", runtimeRegistryVersion } } },
                { "execution", new System.Collections.Generic.Dictionary<string, object> { { "channel", "runtime" }, { "startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture) }, { "endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture) }, { "durationMs", durationMs }, { "exitState", "completed" } } },
                { "result", result },
                { "artifacts", new System.Collections.Generic.List<object> { new System.Collections.Generic.Dictionary<string, object> { { "id", "manifest" }, { "kind", "json" }, { "path", Path.Combine(artifactDir, "manifest.json") }, { "description", "runtime-tool artifact manifest" } } } },
                { "logs", new System.Collections.Generic.Dictionary<string, object> { { "bridge", LogPath() } } }
            };
        }
    }
}
#endif