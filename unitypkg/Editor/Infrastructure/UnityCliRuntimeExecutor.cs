#if UNITY_EDITOR
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
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
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
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
        private static void OnPlayModeStateChanged(PlayModeStateChange change)
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
        /// Runtime session readiness report JSON, included in instance registration
        /// and available via editor.state.
        /// </summary>
        private static string RuntimeSessionJson()
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("playModeSessionId", currentPlayModeSessionId ?? (string)null)
                .Prop("ready", playModeReady)
                .Prop("registryVersion", runtimeRegistryVersion)
                .Prop("toolCatalogFingerprint", runtimeToolCatalogFingerprint ?? (string)null)
                .PropArray("availableRuntimeTools", array =>
                {
                    foreach (var pair in ProjectTools)
                    {
                        if (pair.Value.Target == "runtime" || pair.Value.Target == "both")
                        {
                            array.BeginObject()
                                .Prop("name", pair.Value.Name)
                                .Prop("target", pair.Value.Target)
                                .Prop("availabilityScope", pair.Value.AvailabilityScope)
                                .Prop("available", playModeReady)
                                .EndObject();
                        }
                    }
                })
                .EndObject()
                .ToString();
        }

        /// <summary>
        /// List runtime tools with their availability status based on current
        /// Play Mode session state.
        /// </summary>
        private static string RuntimeToolList(BridgeRequest request)
        {
            DiscoverProjectTools();
            InitializeRuntimeExecutor();

            var tools = new System.Collections.Generic.List<string>();
            foreach (var pair in ProjectTools)
            {
                var def = pair.Value;
                if (def.Target == "runtime" || def.Target == "both")
                {
                    tools.Add(RuntimeToolDescriptorJson(def));
                }
            }
            tools.Sort(StringComparer.Ordinal);

            var data = new UnityCliJsonWriter().BeginObject()
                .PropArray("tools", arr => { foreach (var tool in tools) { arr.RawValue(tool); } })
                .PropRaw("session", RuntimeSessionJson())
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
            return SuccessJson(data);
        }

        /// <summary>
        /// Execute a runtime tool. Validates Play Mode session state, enforces
        /// timeout, routes to the tool's Execute delegate, and returns structured
        /// errors for non-ready, session stale, and timeout scenarios.
        /// </summary>
        private static string RuntimeToolRun(BridgeRequest request)
        {
            DiscoverProjectTools();
            InitializeRuntimeExecutor();

            var name = request.StringArg("name");
            if (string.IsNullOrEmpty(name))
            {
                return ErrorJsonWithDetails("E_SCHEMA_INVALID", "Runtime tool name is required.",
                    new UnityCliJsonWriter().BeginObject()
                        .Prop("field", "name")
                        .EndObject().ToString(),
                    "Run tools run <name> --target <instance> with a valid runtime tool name.");
            }

            // Validate Play Mode is ready
            if (!playModeReady)
            {
                // If the caller specified a session ID, check whether it matches a
                // recently-stale session. This gives a more actionable error than
                // E_RUNTIME_NOT_READY when the Play Mode session simply ended.
                var staleRequestedSessionId = request.StringArg("playModeSessionId");
                if (!string.IsNullOrEmpty(staleRequestedSessionId) && string.Equals(staleRequestedSessionId, lastStalePlayModeSessionId, StringComparison.Ordinal))
                {
                    WriteLog("runtime_tool_session_stale_after_exit requestId=" + request.RequestId + " name=" + name + " requested=" + staleRequestedSessionId + " stale=" + lastStalePlayModeSessionId);
                    return ErrorJsonWithDetails("E_RUNTIME_SESSION_STALE",
                        "Runtime tool session is stale: Play Mode session " + staleRequestedSessionId + " has ended (exited Play Mode).",
                        new UnityCliJsonWriter().BeginObject()
                            .Prop("requestedSessionId", staleRequestedSessionId)
                            .Prop("staleSessionId", lastStalePlayModeSessionId)
                            .Prop("playModeActive", false)
                            .EndObject().ToString(),
                        "Re-enter Play Mode and retry, or omit playModeSessionId to use the current session.");
                }
                WriteLog("runtime_tool_not_ready requestId=" + request.RequestId + " name=" + name + " sessionId=" + (currentPlayModeSessionId ?? "null"));
                return ErrorJson("E_RUNTIME_NOT_READY",
                    "Runtime tool cannot execute: Play Mode is not ready. Current Play Mode session is not active.",
                    "Enter Play Mode first (use editor play), then retry the runtime tool call.");
            }

            // Validate the requested session matches the current session
            var requestedSessionId = request.StringArg("playModeSessionId");
            if (!string.IsNullOrEmpty(requestedSessionId) && !string.Equals(requestedSessionId, currentPlayModeSessionId, StringComparison.Ordinal))
            {
                WriteLog("runtime_tool_session_stale requestId=" + request.RequestId + " name=" + name + " requested=" + requestedSessionId + " current=" + currentPlayModeSessionId);
                return ErrorJsonWithDetails("E_RUNTIME_SESSION_STALE",
                    "Runtime tool session is stale: the Play Mode session has changed. Requested session " + requestedSessionId + " does not match current session " + currentPlayModeSessionId + ".",
                    new UnityCliJsonWriter().BeginObject()
                        .Prop("requestedSessionId", requestedSessionId)
                        .Prop("currentSessionId", currentPlayModeSessionId)
                        .EndObject().ToString(),
                    "Re-enter Play Mode and retry, or omit playModeSessionId to use the current session.");
            }

            // Validate the tool exists and is a runtime tool
            if (!ProjectTools.ContainsKey(name))
            {
                return ErrorJson("E_TOOL_NOT_FOUND", "Tool not found: " + name,
                    "Run tools list --include-custom to see registered tools.");
            }

            var definition = ProjectTools[name];
            if (definition.Target != "runtime" && definition.Target != "both")
            {
                return ErrorJsonWithDetails("E_RUNTIME_TOOL_TARGET_MISMATCH",
                    "Tool " + name + " has Target=" + definition.Target + " and cannot be executed via the runtime path.",
                    new UnityCliJsonWriter().BeginObject()
                        .Prop("toolName", name)
                        .Prop("toolTarget", definition.Target)
                        .EndObject().ToString(),
                    "Use tools run <name> without --target runtime for editor-domain tools, or use a tool declared with Target=runtime.");
            }

            // Fingerprint check
            var fingerprintCheck = RefreshProjectToolFingerprint(definition, request.StringArg("fingerprintMode"));
            if (fingerprintCheck.Blocked)
            {
                WriteLog("runtime_tool_fingerprint_blocked requestId=" + request.RequestId + " name=" + name);
                return ErrorJsonWithDetails("E_TOOL_FINGERPRINT_MISMATCH", fingerprintCheck.Message,
                    ProjectToolFingerprintDetailsJson(definition),
                    "Review the registered custom tool descriptor/handler code, then update the expected fingerprint or rerun with --fingerprint-mode warn.");
            }

            // Timeout validation
            var timeoutMs = request.IntArg("timeoutMs", definition.DefaultTimeoutMs);
            if (timeoutMs <= 0 || timeoutMs > definition.MaxTimeoutMs)
            {
                return ErrorJson("E_SCHEMA_INVALID", "timeoutMs exceeds the registered tool limit.",
                    "Use a timeout between 1 and " + definition.MaxTimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms.");
            }

            // Input schema validation
            var inputJson = request.RawJsonArg("input") ?? "{}";
            var schemaError = ValidateProjectToolInput(definition, inputJson);
            if (!string.IsNullOrEmpty(schemaError))
            {
                return ErrorJson("E_TOOL_SCHEMA_INVALID", schemaError,
                    "Run tools list --include-custom to inspect the tool inputSchema.");
            }

            // Execute the runtime tool with timeout awareness
            var startedAt = DateTime.UtcNow;
            var artifactDir = ResolveArtifactDir(request.StringArg("artifactDir"), request.RequestId);
            try
            {
                Directory.CreateDirectory(artifactDir);
            }
            catch (Exception exception)
            {
                return ErrorJson("E_ARTIFACT_WRITE_FAILED", "Failed to create artifact directory: " + exception.Message,
                    "Pass --artifact-dir to a writable directory.");
            }

            string resultJson;
            try
            {
                WriteLog("runtime_tool_run requestId=" + request.RequestId + " name=" + name + " sessionId=" + currentPlayModeSessionId + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));

                // Check Play Mode is still ready before executing
                if (!EditorApplication.isPlaying || !playModeReady)
                {
                    return ErrorJson("E_RUNTIME_NOT_READY",
                        "Play Mode session ended before the runtime tool could execute.",
                        "Re-enter Play Mode and retry.");
                }

                resultJson = definition.Execute(new UnityCliProjectToolContext
                {
                    RequestId = request.RequestId,
                    ProjectPath = request.StringArg("projectPath") ?? ProjectRoot(),
                    InputJson = inputJson,
                    ArtifactDir = artifactDir,
                    LogPath = LogPath()
                });
            }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException ?? exception;
                return ErrorJson("E_TOOL_EXECUTION_FAILED", inner.GetType().Name + ": " + inner.Message,
                    "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }
            catch (Exception exception)
            {
                return ErrorJson("E_TOOL_EXECUTION_FAILED", exception.GetType().Name + ": " + exception.Message,
                    "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }

            // If the runtime tool returned the deferred wait marker (RuntimeWaitTool),
            // set up a deferred EditorApplication.update callback-based wait that
            // does not block the Unity main thread, and returns the response wrapped
            // in the runtime tool envelope.
            if (resultJson == RuntimeWaitTool.DeferredWaitMarker && RuntimeWaitTool.LastWaitRequest != null)
            {
                var waitReq = RuntimeWaitTool.LastWaitRequest;
                RuntimeWaitTool.LastWaitRequest = null; // consume
                var item = currentWorkItem;
                var waitTimeoutMs = (int)Math.Min(waitReq.RequestedDurationMs + 5000, timeoutMs);

                WriteLog("runtime_tool_deferred_wait requestId=" + request.RequestId + " name=" + name + " condition=" + waitReq.Condition + " framesToWait=" + waitReq.FramesToWait.ToString(CultureInfo.InvariantCulture));

                return StartDeferredWait(
                    item,
                    () => RuntimeWaitCondition(waitReq),
                    waitTimeoutMs,
                    () => RuntimeWaitResponse(waitReq, name, definition, startedAt, artifactDir, request, timeoutMs),
                    "E_RUNTIME_TIMEOUT",
                    "Runtime wait did not complete before the timeout.",
                    "Reduce the requested frames or seconds, or increase the timeout.");
            }

            // Verify Play Mode session is still valid after execution
            if (!playModeReady || !EditorApplication.isPlaying)
            {
                WriteLog("runtime_tool_session_invalid_after_run requestId=" + request.RequestId + " name=" + name);
                return ErrorJsonWithDetails("E_RUNTIME_SESSION_STALE",
                    "Play Mode session ended during runtime tool execution. The tool may have partially executed.",
                    new UnityCliJsonWriter().BeginObject()
                        .Prop("sessionId", currentPlayModeSessionId ?? "unknown")
                        .Prop("toolName", name)
                        .Prop("partialExecution", true)
                        .EndObject().ToString(),
                    "Re-enter Play Mode and verify the tool's side effects before retrying.");
            }

            var endedAt = DateTime.UtcNow;
            var durationMs = (long)(endedAt - startedAt).TotalMilliseconds;
            var manifest = ProjectToolManifestJson(request.RequestId, definition, artifactDir, startedAt, endedAt);
            try
            {
                File.WriteAllText(Path.Combine(artifactDir, "manifest.json"), manifest);
            }
            catch (Exception exception)
            {
                return ErrorJson("E_ARTIFACT_WRITE_FAILED", "Failed to write artifact manifest: " + exception.Message,
                    "Pass --artifact-dir to a writable directory.");
            }

            if (durationMs > timeoutMs)
            {
                WriteLog("runtime_tool_timeout requestId=" + request.RequestId + " name=" + name + " durationMs=" + durationMs.ToString(CultureInfo.InvariantCulture) + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
                return ErrorJsonWithDetails("E_RUNTIME_TIMEOUT",
                    "Runtime tool exceeded timeoutMs: " + durationMs.ToString(CultureInfo.InvariantCulture) + "ms > " + timeoutMs.ToString(CultureInfo.InvariantCulture) + "ms.",
                    new UnityCliJsonWriter().BeginObject()
                        .Prop("durationMs", durationMs)
                        .Prop("timeoutMs", timeoutMs)
                        .EndObject().ToString(),
                    "Increase the timeout or optimize the runtime tool, then retry.");
            }

            var data = new UnityCliJsonWriter().BeginObject()
                .Prop("command", "runtime-tool.run")
                .PropObject("tool", tool =>
                {
                    tool.Prop("name", definition.Name)
                        .Prop("version", definition.Version)
                        .Prop("target", definition.Target);
                })
                .PropObject("session", session =>
                {
                    session.Prop("playModeSessionId", currentPlayModeSessionId)
                        .Prop("ready", playModeReady)
                        .Prop("registryVersion", runtimeRegistryVersion);
                })
                .PropObject("execution", execution =>
                {
                    execution.Prop("channel", "runtime")
                        .Prop("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture))
                        .Prop("endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture))
                        .Prop("durationMs", durationMs)
                        .Prop("exitState", "completed");
                })
                .PropRaw("result", resultJson)
                .PropArray("artifacts", artifacts =>
                {
                    artifacts.BeginObject()
                        .Prop("id", "manifest")
                        .Prop("kind", "json")
                        .Prop("path", Path.Combine(artifactDir, "manifest.json"))
                        .Prop("description", "runtime-tool artifact manifest")
                        .EndObject();
                })
                .PropObject("logs", logs => logs.Prop("bridge", LogPath()))
                .EndObject()
                .ToString();
            return SuccessJson(data);
        }

        /// <summary>
        /// Check whether the runtime wait condition is met.
        /// Returns true when the requested frames/duration have elapsed or Play Mode ends.
        /// </summary>
        private static bool RuntimeWaitCondition(RuntimeWaitTool.WaitRequest waitReq)
        {
            if (!EditorApplication.isPlaying)
            {
                return true; // Play Mode ended — stop waiting
            }

            var elapsedFrames = Time.frameCount - waitReq.StartFrame;
            var elapsedMs = (long)(DateTime.UtcNow - waitReq.StartedAt).TotalMilliseconds;

            switch (waitReq.Condition)
            {
                case "duration":
                    return elapsedMs >= waitReq.RequestedDurationMs;

                case "frame-count":
                    return elapsedFrames >= waitReq.FramesToWait;

                default:
                    return true; // next-frame or unknown — always met
            }
        }

        /// <summary>
        /// Build the full runtime tool response envelope for a deferred wait result.
        /// Wraps the wait result JSON in the standard runtime-tool.run response format.
        /// </summary>
        private static string RuntimeWaitResponse(
            RuntimeWaitTool.WaitRequest waitReq,
            string name,
            UnityCliProjectToolDefinition definition,
            DateTime startedAt,
            string artifactDir,
            BridgeRequest request,
            int timeoutMs)
        {
            var endedAt = DateTime.UtcNow;
            var durationMs = (long)(endedAt - startedAt).TotalMilliseconds;
            var elapsedFrames = Time.frameCount - waitReq.StartFrame;
            var elapsedMs = (long)(DateTime.UtcNow - waitReq.StartedAt).TotalMilliseconds;

            var resultJson = new UnityCliJsonWriter().BeginObject()
                .Prop("framesWaited", Math.Max(1, elapsedFrames))
                .Prop("elapsedMs", elapsedMs)
                .Prop("condition", waitReq.Condition)
                .Prop("requestedFrames", waitReq.RequestedFrames)
                .Prop("isPlaying", EditorApplication.isPlaying)
                .Prop("frameCount", Time.frameCount)
                .EndObject()
                .ToString();

            var manifest = ProjectToolManifestJson(request.RequestId, definition, artifactDir, startedAt, endedAt);
            try
            {
                Directory.CreateDirectory(artifactDir);
                File.WriteAllText(Path.Combine(artifactDir, "manifest.json"), manifest);
            }
            catch (Exception exception)
            {
                return ErrorJson("E_ARTIFACT_WRITE_FAILED", "Failed to write artifact manifest: " + exception.Message,
                    "Pass --artifact-dir to a writable directory.");
            }

            if (durationMs > timeoutMs)
            {
                WriteLog("runtime_tool_timeout requestId=" + request.RequestId + " name=" + name + " durationMs=" + durationMs.ToString(CultureInfo.InvariantCulture) + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
                return ErrorJsonWithDetails("E_RUNTIME_TIMEOUT",
                    "Runtime tool exceeded timeoutMs: " + durationMs.ToString(CultureInfo.InvariantCulture) + "ms > " + timeoutMs.ToString(CultureInfo.InvariantCulture) + "ms.",
                    new UnityCliJsonWriter().BeginObject()
                        .Prop("durationMs", durationMs)
                        .Prop("timeoutMs", timeoutMs)
                        .EndObject().ToString(),
                    "Increase the timeout or optimize the runtime tool, then retry.");
            }

            var data = new UnityCliJsonWriter().BeginObject()
                .Prop("command", "runtime-tool.run")
                .PropObject("tool", tool =>
                {
                    tool.Prop("name", definition.Name)
                        .Prop("version", definition.Version)
                        .Prop("target", definition.Target);
                })
                .PropObject("session", session =>
                {
                    session.Prop("playModeSessionId", currentPlayModeSessionId)
                        .Prop("ready", playModeReady)
                        .Prop("registryVersion", runtimeRegistryVersion);
                })
                .PropObject("execution", execution =>
                {
                    execution.Prop("channel", "runtime")
                        .Prop("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture))
                        .Prop("endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture))
                        .Prop("durationMs", durationMs)
                        .Prop("exitState", "completed");
                })
                .PropRaw("result", resultJson)
                .PropArray("artifacts", artifacts =>
                {
                    artifacts.BeginObject()
                        .Prop("id", "manifest")
                        .Prop("kind", "json")
                        .Prop("path", Path.Combine(artifactDir, "manifest.json"))
                        .Prop("description", "runtime-tool artifact manifest")
                        .EndObject();
                })
                .PropObject("logs", logs => logs.Prop("bridge", LogPath()))
                .EndObject()
                .ToString();
            return SuccessJson(data);
        }

        private static string RuntimeToolDescriptorJson(UnityCliProjectToolDefinition definition)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("name", definition.Name)
                .Prop("summary", definition.Summary)
                .Prop("description", definition.Description)
                .Prop("group", definition.Group)
                .Prop("version", definition.Version)
                .PropRaw("inputSchema", definition.InputSchema)
                .PropRaw("outputSchema", definition.OutputSchema)
                .Prop("capabilityType", definition.CapabilityType)
                .Prop("destructiveHint", definition.DestructiveHint)
                .Prop("target", definition.Target)
                .Prop("availabilityScope", definition.AvailabilityScope)
                .Prop("playModeSupport", definition.PlayModeSupport)
                .Prop("available", playModeReady)
                .Prop("defaultTimeoutMs", definition.DefaultTimeoutMs)
                .Prop("maxTimeoutMs", definition.MaxTimeoutMs)
                .Prop("supportsJob", false)
                .PropRaw("artifactKinds", StringArrayJson(definition.ArtifactKinds))
                .Prop("handler", "bridge-runtime")
                .Prop("supportStatus", "live-bridge-runtime")
                .Prop("cliCommand", "tools run " + definition.Name)
                .Prop("docsTopic", "runtime-tool")
                .Prop("descriptorSource", definition.DescriptorSource)
                .Prop("registeredBy", definition.RegisteredBy)
                .Prop("assembly", definition.Assembly)
                .Prop("method", definition.Method)
                .PropRaw("fingerprint", ProjectToolFingerprintJson(definition))
                .EndObject()
                .ToString();
        }
    }
}
#endif