#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private sealed class CompletionEvidence
        {
            public string RequestId;
            public string ToolName;
            public string CompletionDriverId;
            public string ProofKind;
            public byte[] ProofBytes;
        }

        private static readonly object CompletionEvidenceLock = new object();
        private static readonly Dictionary<string, CompletionEvidence> CompletionEvidenceByRequest = new Dictionary<string, CompletionEvidence>(StringComparer.Ordinal);
        private sealed class EffectContinuation
        {
            public string Kind;
            public Dictionary<string, object> Value;
        }

        private static readonly HashSet<string> ProofRecoveryRequests = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, IDictionary<string, object>> ProofRecoveryContinuations = new Dictionary<string, IDictionary<string, object>>(StringComparer.Ordinal);
        private static readonly HashSet<string> DurableRecoveryToolNames = new HashSet<string>(new[]
        {
            "asset.import",
            "asset.refresh",
            "build.platform.set",
            "build.profile.switch",
            "editor.play",
            "editor.stop",
            "package.embed",
            "camera.screenshot",
            "editor.menu",
            "graphics.bake-reflection-probe",
            "profiler.capture",
            "profiler.load",
            "profiler.memory-snapshot.take",
            "screenshot.game-view",
            "screenshot.isolated-object",
            "screenshot.orbit",
            "screenshot.scene-view",
            "screenshot.surround",
            "ui.render-preview"
        }, StringComparer.Ordinal);
        private static readonly HashSet<string> RegisteredCompletionDriverIds = new HashSet<string>(new[]
        {
            "atomic-typed-result-v1",
            "observed-terminal-readback-v1",
            "resumable-terminal-readback-v1",
            "external-terminal-readback-v1"
        }, StringComparer.Ordinal);

        /// <summary>
        /// live HTTP Tool 的唯一完成协调器。mutation 成功只能消费 Tool runner 显式登记的 typed evidence；
        /// 普通 handler envelope、transport success 与中间状态不能跨过 effect_proven barrier。
        /// </summary>
        private static async Task<string> ExecuteCoordinatedRequest(BridgeRequest request, byte[] rawRequestBytes, Func<bool> tryAcceptJournal, CancellationToken cancellationToken)
        {
            // 私有 compile start/status 必须绕开通用 completion 路径；start 的 accepted 不是公开 terminal。
            if (UnityCliCompileCoordinator.IsCommand(request.Command))
                return await UnityCliCompileCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            var contract = ResolveRequestCompletionContract(request);
            if (contract == null)
            {
                if (IsReadOnlyBridgeCommand(request.Command)) return await Execute(request, cancellationToken);
                return CompletionContractRequiredEnvelope(request);
            }
            if (!string.Equals(contract.DeliveryMode, "direct-terminal", StringComparison.Ordinal))
                return CompletionContractRequiredEnvelope(request);
            var directAllowed = string.Equals(contract.DirectInvocationPolicy, "allowed", StringComparison.Ordinal);
            // HTTP coordinator 永不授予 frozen delivery；Unity batchmode 走独立的进程内 ExecuteBatchCommandAsync。
            if (!directAllowed && !RequestIsSupportedDryRun(request))
                return SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException(
                    "E_DIRECT_BUSINESS_PROOF_UNAVAILABLE",
                    "Tool " + contract.ToolName + " cannot prove attributable business completion for live direct delivery.",
                    "Use only its preserved batch/job delivery when available, or choose a Tool with an allowed direct completion contract."));
            if (!IsRegisteredCompletionDriver(contract.ExecutionClass, contract.CompletionDriverId))
                return CompletionDriverRequiredEnvelope(request, contract);

            var mutation = RequestRequiresMutationJournal(request, contract);
            if (!mutation)
            {
                var response = await Execute(request, cancellationToken);
                ValidateCoordinatedTerminal(response, request.RequestId);
                return response;
            }

            // A compile owns the global mutation lane from durable acceptance through root terminal.
            // Do this before AcceptOrRecover so a rejected ordinary mutation leaves no terminal journal.
            if (UnityCliCompileCoordinator.IsActive())
                return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(
                    request.RequestId,
                    "E_UNITY_COMPILING",
                    "Unity is running a durable editor.compile operation.",
                    null,
                    new[] { "Wait for editor.compile to reach its terminal result, then retry this mutation." },
                    transient: true,
                    retryAfterMs: 250));

            RequestJournalDecision decision;
            try
            {
                decision = AcceptOrRecover(request, rawRequestBytes, contract, () =>
                {
                    if (tryAcceptJournal == null || !tryAcceptJournal()) throw new OperationCanceledException("HTTP timeout cancelled mutation before journal acceptance.");
                });
            }
            catch (BridgeCommandException exception)
            {
                return SerializeBridgeExceptionEnvelope(request.RequestId, exception);
            }
            if (decision.ResumeFinalize)
                return await FinalizeProvenResult(request, decision.TerminalEnvelope, cancellationToken);
            if (decision.TerminalEnvelope != null) return UnityCliSerializer.Serialize(decision.TerminalEnvelope);
            if (!decision.Execute)
                return SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException(
                    "E_REQUEST_JOURNAL_CORRUPT",
                    "Request journal did not authorize execution or terminal recovery.",
                    "Inspect the request journal record and retry with a new requestId."));

            try
            {
                if (decision.ResumeProof)
                {
                    if (IsGenericExternalReobserveContinuation(decision.Continuation))
                    {
                        ReobserveGenericExternalContinuation(decision.Continuation);
                        return PersistOutcomeUnknown(request.RequestId, true, "The prior external operation has only a non-attributable recovery readback; its mutation will not be replayed.");
                    }
                    MarkProofRecoveryRequest(request.RequestId, decision.Continuation);
                }
                else MarkAcceptedBeforeEffect(request.RequestId);
                cancellationToken.ThrowIfCancellationRequested();
                var response = await Execute(request, cancellationToken);
                var envelope = ValidateCoordinatedTerminal(response, request.RequestId);
                object okValue;
                var ok = envelope.TryGetValue("ok", out okValue) && okValue is bool && (bool)okValue;
                if (!ok)
                {
                    if (RequestJournalEffectStarted(request.RequestId))
                        return PersistOutcomeUnknown(request.RequestId, true, "The Tool returned an error after its effect barrier; no terminal business proof was registered.");
                    return UnityCliSerializer.Serialize(PersistTerminal(request.RequestId, envelope, "failed"));
                }

                var evidence = TakeCompletionEvidence(request.RequestId, contract);
                if (evidence == null)
                {
                    if (RequestJournalEffectStarted(request.RequestId))
                        return PersistOutcomeUnknown(request.RequestId, true, "The Tool returned success without completion-driver evidence after its effect barrier.");
                    var invalid = CompletionProofInvalidEnvelope(request, contract, "No completion-driver evidence was registered.");
                    return UnityCliSerializer.Serialize(PersistTerminal(request.RequestId, invalid, "failed"));
                }
                if (!RequestJournalEffectStarted(request.RequestId))
                {
                    var invalid = CompletionProofInvalidEnvelope(request, contract, "Completion evidence exists but the durable effect_started barrier is missing.");
                    return UnityCliSerializer.Serialize(PersistTerminal(request.RequestId, invalid, "failed"));
                }

                MarkEffectProven(request.RequestId, evidence.ProofKind, evidence.ProofBytes, envelope);
                return await FinalizeProvenResult(request, envelope, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!RequestJournalEffectStarted(request.RequestId))
                    return PersistFailedBeforeEffect(request.RequestId, new BridgeCommandException("E_EXECUTION_TIMEOUT", "The immutable request deadline elapsed before any effect began.", "Inspect editor.state before submitting a new request."));
                return PersistOutcomeUnknown(request.RequestId, true,
                    "The immutable deadline or Unity session ended before a durable terminal proof was persisted.");
            }
            catch (BridgeCommandException exception)
            {
                if (!RequestJournalEffectStarted(request.RequestId)) return PersistFailedBeforeEffect(request.RequestId, exception);
                return PersistOutcomeUnknown(request.RequestId, true,
                    "The mutation crossed effect_started but completion proof failed: " + exception.Code + ".");
            }
            catch (Exception exception)
            {
                if (!RequestJournalEffectStarted(request.RequestId))
                    return PersistFailedBeforeEffect(request.RequestId, new BridgeCommandException(
                        "E_BRIDGE_COMMAND_FAILED", exception.Message, "Check Unity Console and UnityCliBridge.log."));
                return PersistOutcomeUnknown(request.RequestId, true,
                    "The mutation crossed effect_started but completion became uncertain: " + exception.GetType().Name + ".");
            }
            finally
            {
                RemoveCompletionEvidence(request.RequestId);
                RemoveProofRecoveryRequest(request.RequestId);
            }
        }

        /// <summary>
        /// handler 进入前只构造可持久化 continuation 与完成只读前置校验，不写 effect_started。
        /// 真正 barrier 由 Tool 在首个 mutation 前通过 ctx.CommitEffect() 显式触发。
        /// </summary>
        private static EffectContinuation PrepareToolEffect(BridgeRequest request, UnityCliToolCompletionContract contract, object typedInput)
        {
            if (IsProofRecoveryRequest(request.RequestId))
            {
                var recovered = ProofRecoveryContinuation(request.RequestId);
                object kind;
                if (recovered == null || !recovered.TryGetValue("kind", out kind) || string.IsNullOrEmpty(kind as string))
                    throw new BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "Proof recovery continuation has no kind.", "Inspect the request journal before retrying.");
                return new EffectContinuation { Kind = kind as string, Value = new Dictionary<string, object>(recovered) };
            }
            if (!RequestRequiresMutationJournal(request, contract)) return null;
            return BuildEffectContinuation(contract.ToolName, typedInput);
        }

        /// <summary>显式 commit boundary 紧邻首个业务 effect 写 durable barrier；一次请求只能调用一次。</summary>
        private static void CommitToolEffect(BridgeRequest request, UnityCliToolCompletionContract contract, EffectContinuation continuation)
        {
            if (IsProofRecoveryRequest(request.RequestId))
                throw new BridgeCommandException("E_TOOL_EFFECT_BARRIER_INVALID", "Proof recovery cannot commit the mutation again.", "Reobserve the existing effect without replaying it.");
            if (!RequestRequiresMutationJournal(request, contract))
                throw new BridgeCommandException("E_TOOL_EFFECT_BARRIER_INVALID", "The current request is not classified as a mutation.", "Remove ctx.CommitEffect() from read-only and dry-run paths.");
            MarkEffectStarted(request.RequestId, continuation == null ? null : continuation.Kind, continuation == null ? null : continuation.Value);
            if (continuation != null)
            {
                lock (CompletionEvidenceLock) ProofRecoveryContinuations[request.RequestId] = continuation.Value;
            }
        }

        private static bool SupportsProofRecovery(string toolName)
        {
            return !string.IsNullOrWhiteSpace(toolName) && DurableRecoveryToolNames.Contains(toolName);
        }

        /// <summary>在 effect_started 前冻结可重观察所需的最小非敏感 seed；未注册 Tool 不获得恢复资格。</summary>
        private static EffectContinuation BuildEffectContinuation(string toolName, object typedInput)
        {
            if (!SupportsProofRecovery(toolName)) return null;
            if (string.Equals(toolName, "asset.import", StringComparison.Ordinal))
            {
                AssetToolSupport.EnsureEditorNotBusy(toolName);
                var path = AssetToolSupport.RequireAssetPath(InputString(typedInput, "Path") ?? InputString(typedInput, "Destination"));
                var source = InputString(typedInput, "Source");
                var content = InputString(typedInput, "Content");
                var destinationPath = AssetToolSupport.AssetFullPath(path);
                var overwrite = InputNullableBool(typedInput, "Overwrite") ?? false;
                if (File.Exists(destinationPath) && !overwrite)
                    throw new BridgeCommandException("E_ASSET_OUTPUT_EXISTS", "Asset already exists: " + path, "Pass --overwrite or choose another destination.");
                byte[] bytes;
                if (!string.IsNullOrEmpty(source))
                {
                    var normalizedSource = AssetToolSupport.NormalizeAssetPath(source);
                    var sourcePath = normalizedSource.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                        ? AssetToolSupport.AssetFullPath(normalizedSource)
                        : Path.GetFullPath(source);
                    if (!File.Exists(sourcePath)) throw new BridgeCommandException("E_ASSET_SOURCE_NOT_FOUND", "Asset import source file not found: " + source, "Pass an existing source path.");
                    if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                        throw new BridgeCommandException("E_ASSET_SOURCE_EQUALS_DESTINATION", "Asset import source and destination resolve to the same file.", "Choose a different destination path.");
                    bytes = File.ReadAllBytes(sourcePath);
                }
                else
                {
                    if (content == null) throw new BridgeCommandException("E_ASSET_IMPORT_INPUT_REQUIRED", "asset.import requires source or content.", "Pass a source file or inline content.");
                    bytes = Encoding.UTF8.GetBytes(content);
                }
                return Continuation("asset-content-sha256-v1", new Dictionary<string, object> { { "path", path }, { "contentSha256", Sha256Hex(bytes) } });
            }
            if (string.Equals(toolName, "asset.refresh", StringComparison.Ordinal))
            {
                AssetToolSupport.EnsureEditorNotBusy(toolName);
                var path = InputString(typedInput, "Path") ?? InputString(typedInput, "AssetPath") ?? InputString(typedInput, "Target");
                if (!string.IsNullOrEmpty(path)) path = AssetToolSupport.RequireAssetPath(path);
                var continuation = new Dictionary<string, object> { { "path", path ?? string.Empty }, { "beforeListenerGeneration", CurrentListenerGeneration() } };
                if (!string.IsNullOrEmpty(path))
                {
                    var fullPath = AssetToolSupport.AssetFullPath(path);
                    if (!File.Exists(fullPath)) throw new BridgeCommandException("E_ASSET_NOT_FOUND", "Asset refresh target does not exist: " + path, "Pass an existing Assets/ path.");
                    continuation.Add("contentSha256", Sha256Hex(File.ReadAllBytes(fullPath)));
                }
                return Continuation("asset-refresh-v1", continuation);
            }
            if (string.Equals(toolName, "build.platform.set", StringComparison.Ordinal))
            {
                BuildToolSupport.EnsureEditorNotBusy(toolName);
                var expected = InputString(typedInput, "Target");
                BuildTarget parsed;
                if (string.IsNullOrEmpty(expected)) throw new BridgeCommandException("E_BUILD_TARGET_REQUIRED", "build.platform.set requires target.", "Pass a valid BuildTarget enum name.");
                if (!Enum.TryParse(expected, true, out parsed)) throw new BridgeCommandException("E_BUILD_TARGET_UNKNOWN", "Unknown BuildTarget: " + expected, "Pass a valid BuildTarget enum name.");
                return Continuation("build-platform-v1", new Dictionary<string, object> { { "expected", expected }, { "before", EditorUserBuildSettings.activeBuildTarget.ToString() } });
            }
            if (string.Equals(toolName, "build.profile.switch", StringComparison.Ordinal))
            {
                BuildToolSupport.EnsureEditorNotBusy(toolName);
                var profileType = BuildToolSupport.RequireBuildProfileType(toolName);
                var path = InputString(typedInput, "Path");
                BuildToolSupport.LoadProfile(profileType, path, toolName);
                var active = BuildToolSupport.ActiveBuildProfile(profileType);
                return Continuation("build-profile-v1", new Dictionary<string, object> { { "expectedPath", path }, { "beforePath", active == null ? string.Empty : AssetDatabase.GetAssetPath(active) } });
            }
            if (string.Equals(toolName, "editor.play", StringComparison.Ordinal))
            {
                EditorToolSupport.EnsureEditorNotBusy(toolName);
                return Continuation("playmode-v1", new Dictionary<string, object> { { "expectedPlaying", true }, { "beforePlaying", EditorApplication.isPlaying } });
            }
            if (string.Equals(toolName, "editor.stop", StringComparison.Ordinal))
            {
                EditorToolSupport.EnsureEditorNotBusy(toolName);
                return Continuation("playmode-v1", new Dictionary<string, object> { { "expectedPlaying", false }, { "beforePlaying", EditorApplication.isPlaying } });
            }
            if (string.Equals(toolName, "package.embed", StringComparison.Ordinal))
            {
                var packageId = InputString(typedInput, "Package");
                if (string.IsNullOrEmpty(packageId)) throw new BridgeCommandException("E_PACKAGE_ID_REQUIRED", "package.embed requires --package <id>.", "Pass a package id like com.unity.x.");
                if (EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer || UnityCliBridge.IsPlayModeChanging())
                    throw new BridgeCommandException("E_UNITY_BUSY", "Unity Editor is not ready to embed a package.", "Wait for editor.state to become ready.");
                var embedMethod = typeof(UnityEditor.PackageManager.Client).GetMethod("Embed", BindingFlags.Static | BindingFlags.Public);
                if (embedMethod == null) throw new BridgeCommandException("E_PACKAGE_EMBED_UNSUPPORTED", "PackageClient.Embed is not available in this Unity version.", "Use a Unity Editor version with PackageClient.Embed.");
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + packageId);
                if (info == null) throw new BridgeCommandException("E_PACKAGE_NOT_FOUND", "Installed package was not found: " + packageId + ".", "Inspect package.list and pass an installed registry package.");
                if (info.source == UnityEditor.PackageManager.PackageSource.Embedded)
                    throw new BridgeCommandException("E_PACKAGE_ALREADY_EMBEDDED", "Package is already embedded: " + packageId + ".", "Inspect package.info or choose a registry package.");
                return Continuation("package-embed-v1", new Dictionary<string, object> { { "packageId", packageId }, { "beforeEmbedded", false } });
            }
            if (IsGenericExternalRecoveryTool(toolName))
            {
                var path = InputString(typedInput, "Path") ?? InputString(typedInput, "Output") ?? InputString(typedInput, "ContactSheetPath");
                var target = InputString(typedInput, "Target");
                var format = InputString(typedInput, "Format");
                return Continuation("external-reobserve-v1", new Dictionary<string, object>
                {
                    { "toolName", toolName },
                    { "path", path ?? string.Empty },
                    { "target", target ?? string.Empty },
                    { "format", format ?? string.Empty }
                });
            }
            return null;
        }

        /// <summary>无可恢复 operation identity 的外部 Tool 只保存非敏感观察 seed，reload 后绝不 blind replay。</summary>
        private static bool IsGenericExternalRecoveryTool(string toolName)
        {
            return toolName == "camera.screenshot" || toolName == "editor.menu" || toolName == "graphics.bake-reflection-probe"
                || toolName == "profiler.capture" || toolName == "profiler.load" || toolName == "profiler.memory-snapshot.take"
                || toolName == "screenshot.game-view" || toolName == "screenshot.isolated-object" || toolName == "screenshot.orbit"
                || toolName == "screenshot.scene-view" || toolName == "screenshot.surround" || toolName == "ui.render-preview";
        }

        private static bool IsGenericExternalReobserveContinuation(object continuation)
        {
            var values = continuation as IDictionary<string, object>;
            object kind;
            return values != null && values.TryGetValue("kind", out kind) && string.Equals(kind as string, "external-reobserve-v1", StringComparison.Ordinal);
        }

        /// <summary>恢复时只重新读取 durable path；即使文件存在也不能把它归因于中断前的 effect。</summary>
        private static void ReobserveGenericExternalContinuation(object continuation)
        {
            var values = continuation as IDictionary<string, object>;
            object pathValue;
            if (values == null || !values.TryGetValue("path", out pathValue))
                throw new BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "External recovery continuation has no path field.", "Inspect the request journal before submitting a new request.");
            var path = pathValue as string;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                // 强制 fresh bytes readback，且不把这一观察提升为 completion proof。
                Sha256Hex(File.ReadAllBytes(path));
            }
        }

        private static EffectContinuation Continuation(string kind, Dictionary<string, object> value)
        {
            value["kind"] = kind;
            return new EffectContinuation { Kind = kind, Value = value };
        }

        private static object InputProperty(object input, string name)
        {
            if (input == null) return null;
            var property = input.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return property == null ? null : property.GetValue(input, null);
        }

        private static string InputString(object input, string name) { return InputProperty(input, name) as string; }
        private static bool? InputNullableBool(object input, string name) { var value = InputProperty(input, name); return value is bool ? (bool?)value : null; }

        private static void MarkProofRecoveryRequest(string requestId, object continuation)
        {
            var dictionary = continuation as IDictionary<string, object>;
            if (dictionary == null) throw new BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "Proof recovery requires a validated durable continuation.", "Inspect the request journal record before retrying.");
            lock (CompletionEvidenceLock)
            {
                ProofRecoveryRequests.Add(requestId);
                ProofRecoveryContinuations[requestId] = dictionary;
            }
        }

        private static bool IsProofRecoveryRequest(string requestId)
        {
            lock (CompletionEvidenceLock) return ProofRecoveryRequests.Contains(requestId);
        }

        private static IDictionary<string, object> ProofRecoveryContinuation(string requestId)
        {
            lock (CompletionEvidenceLock)
            {
                IDictionary<string, object> continuation;
                return ProofRecoveryContinuations.TryGetValue(requestId, out continuation) ? continuation : null;
            }
        }

        private static void RemoveProofRecoveryRequest(string requestId)
        {
            lock (CompletionEvidenceLock)
            {
                ProofRecoveryRequests.Remove(requestId);
                ProofRecoveryContinuations.Remove(requestId);
            }
        }

        /// <summary>内部窄命令在紧邻真实 effect 前调用；校验失败路径不得调用。</summary>
        private static void BeginDirectEffect(string requestId)
        {
            MarkEffectStarted(requestId);
        }

        /// <summary>
        /// typed Tool 已完成 output schema validation 后登记私有 proof。proof 仅持久化摘要，Result 原文不进入 evidence 字段。
        /// resumable Tool 必须由专用 continuation driver 登记，禁止套用普通 handler-returned 证明。
        /// </summary>
        private static void RecordToolCompletionEvidence(BridgeRequest request, RegisteredTool definition, object result, object terminalData)
        {
            if (definition == null || definition.CompletionContract == null)
                throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Tool completion contract is unavailable at the runner boundary.", "Fix Tool registration before retrying.");
            var contract = definition.CompletionContract;
            ValidateToolTerminalData(request, definition, result, terminalData);
            if (!RequestRequiresMutationJournal(request, contract))
            {
                if (request.Execution == null || request.Execution.DryRun != true) ValidateDriverReadback(contract, result);
                return;
            }
            ValidateDriverReadback(contract, result);
            RegisterCompletionEvidence(request.RequestId, definition.Name, contract, result);
        }

        /// <summary>内部 code/package 命令在取得命令专属 readback 后登记同一 proof 结构。</summary>
        private static void RecordDirectCompletionEvidence(string requestId, string toolName, UnityCliToolCompletionContract contract, object result)
        {
            if (contract == null || !contract.Mutation) throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Direct completion evidence requires a mutating completion contract.", "Fix the internal command completion registration.");
            ValidateDriverReadback(contract, result);
            RegisterCompletionEvidence(requestId, toolName, contract, result);
        }

        private static void RegisterCompletionEvidence(string requestId, string toolName, UnityCliToolCompletionContract contract, object result)
        {
            var resultJson = UnityCliSerializer.Serialize(result);
            byte[] resultHash;
            using (var sha = SHA256.Create()) resultHash = sha.ComputeHash(Encoding.UTF8.GetBytes(resultJson));
            var proofText = "unity-cli-completion-proof-v1\0" + requestId + "\0" + toolName + "\0" + contract.CompletionDriverId + "\0" + contract.ProofSource + "\0" + Hex(resultHash);
            var evidence = new CompletionEvidence
            {
                RequestId = requestId,
                ToolName = toolName,
                CompletionDriverId = contract.CompletionDriverId,
                ProofKind = contract.ProofSource,
                ProofBytes = Encoding.UTF8.GetBytes(proofText)
            };
            lock (CompletionEvidenceLock)
            {
                if (CompletionEvidenceByRequest.ContainsKey(requestId))
                    throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Duplicate completion evidence was registered for requestId " + requestId + ".", "Ensure exactly one completion driver owns the Tool request.");
                CompletionEvidenceByRequest.Add(requestId, evidence);
            }
        }

        private static CompletionEvidence TakeCompletionEvidence(string requestId, UnityCliToolCompletionContract contract)
        {
            lock (CompletionEvidenceLock)
            {
                CompletionEvidence evidence;
                if (!CompletionEvidenceByRequest.TryGetValue(requestId, out evidence)) return null;
                CompletionEvidenceByRequest.Remove(requestId);
                if (!string.Equals(evidence.ToolName, contract.ToolName, StringComparison.Ordinal)
                    || !string.Equals(evidence.CompletionDriverId, contract.CompletionDriverId, StringComparison.Ordinal)
                    || !string.Equals(evidence.ProofKind, contract.ProofSource, StringComparison.Ordinal))
                    throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Completion evidence does not match the immutable Tool contract.", "Fix the Tool completion driver registration.");
                return evidence;
            }
        }

        private static void RemoveCompletionEvidence(string requestId)
        {
            lock (CompletionEvidenceLock) CompletionEvidenceByRequest.Remove(requestId);
        }

        private static bool IsRegisteredCompletionDriver(string executionClass, string completionDriverId)
        {
            if (!RegisteredCompletionDriverIds.Contains(completionDriverId ?? string.Empty)) return false;
            if (string.Equals(executionClass, "atomic", StringComparison.Ordinal)) return completionDriverId == "atomic-typed-result-v1";
            if (string.Equals(executionClass, "observed-state", StringComparison.Ordinal)) return completionDriverId == "observed-terminal-readback-v1";
            if (string.Equals(executionClass, "resumable", StringComparison.Ordinal)) return completionDriverId == "resumable-terminal-readback-v1";
            if (string.Equals(executionClass, "external", StringComparison.Ordinal)) return completionDriverId == "external-terminal-readback-v1";
            return false;
        }

        private static void ValidateToolTerminalData(BridgeRequest request, RegisteredTool definition, object result, object terminalData)
        {
            var data = terminalData as IDictionary<string, object>;
            object commandValue;
            object toolValue;
            object executionValue;
            object resultValue;
            var tool = data != null && data.TryGetValue("tool", out toolValue) ? toolValue as IDictionary<string, object> : null;
            var execution = data != null && data.TryGetValue("execution", out executionValue) ? executionValue as IDictionary<string, object> : null;
            object toolName;
            object exitState;
            if (data == null
                || !data.TryGetValue("command", out commandValue)
                || !(string.Equals(commandValue as string, "project-tool.run", StringComparison.Ordinal) || string.Equals(commandValue as string, "runtime-tool.run", StringComparison.Ordinal))
                || tool == null || !tool.TryGetValue("name", out toolName) || !string.Equals(toolName as string, definition.Name, StringComparison.Ordinal)
                || execution == null || !execution.TryGetValue("exitState", out exitState) || !string.Equals(exitState as string, "completed", StringComparison.Ordinal)
                || !data.TryGetValue("result", out resultValue)
                || !string.Equals(UnityCliSerializer.Serialize(resultValue), UnityCliSerializer.Serialize(result), StringComparison.Ordinal))
            {
                throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Tool runner did not produce a typed terminal result bound to the selected Tool.", "Fix the Tool runner completion boundary before retrying.");
            }
        }

        private static void ValidateDriverReadback(UnityCliToolCompletionContract contract, object result)
        {
            if (result == null) throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Completion result is null.", "Return a typed business result with terminal evidence.");
            var normalized = UnityCliSerializer.Parse(UnityCliSerializer.Serialize(result));
            if (normalized == null) throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Completion result is not a JSON value tree.", "Return a serializable typed business result.");
            if (string.Equals(contract.ExecutionClass, "atomic", StringComparison.Ordinal) && !contract.Mutation) return;
            if (ContainsIntermediateStatus(normalized))
                throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Completion result contains an intermediate accepted/requested/queued/started/triggered/running status.", "Wait for the Tool business terminal and return its stable readback.");
            if (string.Equals(contract.ExecutionClass, "atomic", StringComparison.Ordinal)) return;
            var terminalObject = normalized as IDictionary;
            if (terminalObject == null || terminalObject.Count == 0)
                throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Non-atomic Tool result is not a non-empty typed business terminal.", "Return the Tool-specific terminal readback verified by its completion contract.");
        }

        private static bool ContainsIntermediateStatus(object value)
        {
            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                    var text = entry.Value as string;
                    if ((string.Equals(key, "status", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(key, "state", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(key, "phase", StringComparison.OrdinalIgnoreCase)) && IsIntermediateStatus(text)) return true;
                    if (IsIntermediateField(key) && IntermediateFieldIsSet(entry.Value)) return true;
                    if (ContainsIntermediateStatus(entry.Value)) return true;
                }
                return false;
            }
            var sequence = value as IEnumerable;
            if (sequence != null && !(value is string)) foreach (var item in sequence) if (ContainsIntermediateStatus(item)) return true;
            return false;
        }

        private static bool IsIntermediateStatus(string value)
        {
            if (value == null) return false;
            var status = value.Trim().ToLowerInvariant();
            return status == "accepted" || status == "requested" || status == "queued" || status == "started" || status == "triggered" || status == "running" || status == "in_progress";
        }

        private static bool IsIntermediateField(string name)
        {
            return string.Equals(name, "accepted", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "requested", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "queued", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "started", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "triggered", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IntermediateFieldIsSet(object value)
        {
            if (value == null) return false;
            if (value is bool) return (bool)value;
            var text = value as string;
            return text == null || !string.IsNullOrWhiteSpace(text);
        }

        /// <summary>根据 wrapper input 解析真实 Tool；project/runtime wrapper 不能使用泛化 contract。</summary>
        private static UnityCliToolCompletionContract ResolveRequestCompletionContract(BridgeRequest request)
        {
            var toolName = RequestToolName(request);
            UnityCliToolCompletionContract contract;
            if (UnityCliToolCompletionRegistry.TryGet(toolName, out contract)) return contract;
            if (string.Equals(toolName, "code.exec", StringComparison.Ordinal))
                return UnityCliToolCompletionRegistry.CodeExec;
            if (string.Equals(toolName, "bridge.package.refresh", StringComparison.Ordinal))
                return new UnityCliToolCompletionContract(
                    "bridge.package.refresh", "internal", "direct-terminal", "external", "external-operation-terminal",
                    "operation-terminal-and-readback", "reattach-or-reobserve", "E_EXECUTION_TIMEOUT",
                    "external-proof-or-unknown", "allowed", "ordinary", "external-terminal-readback-v1", "required", true);
            return ResolveRegisteredCustomToolCompletionContract(toolName);
        }

        private static string RequestToolName(BridgeRequest request)
        {
            if (request != null && (string.Equals(request.Command, "project-tool.run", StringComparison.Ordinal)
                || string.Equals(request.Command, "runtime-tool.run", StringComparison.Ordinal)))
            {
                var input = request.Input as IDictionary<string, object>;
                object rawName;
                return input != null && input.TryGetValue("name", out rawName) ? rawName as string : null;
            }
            return request == null ? null : request.Command;
        }

        /// <summary>queue 只需保守判断资源所有权；解析失败按 mutation 串行，最终错误仍由 coordinator 结构化返回。</summary>
        private static bool CoordinatedRequestIsMutation(BridgeRequest request)
        {
            try
            {
                if (request != null && string.Equals(request.Command, "internal.editor.compile.start", StringComparison.Ordinal))
                    return request.Execution == null || request.Execution.DryRun != true;
                var contract = ResolveRequestCompletionContract(request);
                return RequestRequiresMutationJournal(request, contract);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>descriptor 表示潜在风险；本次 input 没有 setter/action 时按 read 执行且不创建 mutation journal。</summary>
        private static bool RequestRequiresMutationJournal(BridgeRequest request, UnityCliToolCompletionContract contract)
        {
            if (request == null || contract == null || !contract.Mutation || (request.Execution != null && request.Execution.DryRun == true)) return false;
            var name = contract.ToolName;
            var input = RequestBusinessInput(request);
            if (name == "graphics.reflection-probe") return HasAnyMeaningfulInput(input, "mode", "resolution", "bounces", "hdr", "boxSize", "boxOffset") || CreateProbeNeedsMutation(input, true);
            if (name == "graphics.light-probe-group") return HasAnyMeaningfulInput(input, "positions") || CreateProbeNeedsMutation(input, false);
            if (name == "graphics.skybox-set-properties") return HasAllMeaningfulInput(input, "property", "value");
            if (name == "build.settings.set") return HasAnyInput(input, "apiCompatibilityLevel", "applicationIdentifier", "bundleVersion", "companyId", "productName", "scriptingBackend");
            if (name == "graphics.bake-settings") return HasAnyInput(input, "bakedGI", "bounces", "directSamples", "environmentSamples", "filtering", "indirectSamples", "lightingMode", "realtimeGI");
            if (name == "graphics.probe-positions") return HasAnyInput(input, "positions");
            if (name == "graphics.quality") return HasAnyInput(input, "anisotropicFiltering", "antiAliasing", "lodBias", "masterTextureLimit", "maximumLODLevel", "qualityLevel", "realtimeReflectionProbes", "shadowDistance", "shadowResolution", "shadows", "softParticles", "vSyncCount");
            if (name == "graphics.settings") return HasAnyInput(input, "antiAliasing", "lodBias", "masterTextureLimit", "maximumLODLevel", "property", "renderPipeline", "shadowDistance", "vSyncCount", "value");
            if (name == "graphics.pipeline-settings") return HasAnyInput(input, "renderPipeline");
            if (name == "graphics.pipeline-quality") return HasAnyInput(input, "qualityLevel");
            if (name == "graphics.skybox-set-ambient") return HasAnyMeaningfulInput(input, "ambientEquatorColor", "ambientGroundColor", "ambientSkyColor", "color", "intensity", "mode");
            if (name == "graphics.skybox-set-fog") return HasAnyMeaningfulInput(input, "color", "density", "enabled", "endDistance", "mode", "startDistance");
            if (name == "graphics.skybox-set-reflection") return HasAnyMeaningfulInput(input, "bounces", "intensity", "mode", "resolution");
            if (name == "graphics.scene-debug") return HasAnyInput(input, "drawMode", "shadingMode");
            if (name == "physics.collision-matrix") return HasAllInput(input, "layerA", "layerB", "ignore");
            if (name == "physics.settings") return HasAnyInput(input, "autoSimulation", "bounceThreshold", "defaultContactOffset", "defaultSolverIterations", "defaultSolverVelocityIterations", "gravity", "gravity2d", "property", "queriesHitBackfaces", "queriesHitTriggers", "queriesStartInColliders", "value");
            if (name == "physics.rigidbody") return !InputBoolean(input, "validate") && (HasAnyMeaningfulInput(input, "angularDrag", "drag", "isKinematic", "mass", "useGravity") || RigidbodyNeedsMutation(input));
            if (name == "physics.simulation") return InputBoolean(input, "simulate");
            if (name == "texture.import-settings") return HasAnyInput(input, "alphaIsTransparency", "compression", "filterMode", "maxTextureSize", "mipmap", "readable", "sRGB", "textureType", "type", "wrapMode");
            if (name == "profiler.deep") return HasAnyInput(input, "enabled");
            if (name == "scene.view-frame") return HasAnyInput(input, "target", "path") || InputBoolean(input, "frameAll");
            if (name == "ui.modify-visual-element") return HasUiVisualElementMutation(input);
            if (name == "cinemachine.lens") return HasAnyInput(input, "dutch", "farClipPlane", "fieldOfView", "nearClipPlane", "orthographicSize");
            if (name == "cinemachine.priority") return HasAnyInput(input, "enabled", "priority");
            if (name == "cinemachine.target") return HasAnyInput(input, "follow", "lookAt");
            if (name == "cinemachine.blend") return HasAnyInput(input, "duration", "style");
            return true;
        }

        /// <summary>仅 live descriptor 明确支持的 dry-run 可以绕过不可证明 Tool 的 direct 拒绝。</summary>
        private static bool RequestIsSupportedDryRun(BridgeRequest request)
        {
            if (request == null || request.Execution == null || request.Execution.DryRun != true) return false;
            if (!string.Equals(request.Command, "project-tool.run", StringComparison.Ordinal)
                && !string.Equals(request.Command, "runtime-tool.run", StringComparison.Ordinal)) return false;
            DiscoverProjectTools();
            RegisteredTool definition;
            return ProjectTools.TryGetValue(RequestToolName(request) ?? string.Empty, out definition)
                && definition.SupportsDryRun;
        }

        private static IDictionary<string, object> RequestBusinessInput(BridgeRequest request)
        {
            var root = request.Input as IDictionary<string, object>;
            if (root == null) return new Dictionary<string, object>();
            if (string.Equals(request.Command, "project-tool.run", StringComparison.Ordinal) || string.Equals(request.Command, "runtime-tool.run", StringComparison.Ordinal))
            {
                object nested;
                var typed = root.TryGetValue("input", out nested) ? nested as IDictionary<string, object> : null;
                return typed ?? new Dictionary<string, object>();
            }
            return root;
        }

        private static bool HasAnyInput(IDictionary<string, object> input, params string[] names)
        {
            return names.Any(name => input.ContainsKey(name) && input[name] != null);
        }

        private static bool HasAllInput(IDictionary<string, object> input, params string[] names)
        {
            return names.All(name => input.ContainsKey(name) && input[name] != null);
        }

        /// <summary>字符串 setter 与 handler 一致：空字符串表示未提供，数值和布尔值只要非 null 即有效。</summary>
        private static bool HasAnyMeaningfulInput(IDictionary<string, object> input, params string[] names)
        {
            return names.Any(name => HasMeaningfulInput(input, name));
        }

        private static bool HasAllMeaningfulInput(IDictionary<string, object> input, params string[] names)
        {
            return names.All(name => HasMeaningfulInput(input, name));
        }

        private static bool HasMeaningfulInput(IDictionary<string, object> input, string name)
        {
            object value;
            return input.TryGetValue(name, out value) && value != null && (!(value is string) || !string.IsNullOrEmpty((string)value));
        }

        /// <summary>UI text 的空字符串是清空文本；其余字符串 setter 与 handler 一样忽略空字符串。</summary>
        private static bool HasUiVisualElementMutation(IDictionary<string, object> input)
        {
            object text;
            return (input.TryGetValue("text", out text) && text != null)
                || HasAnyMeaningfulInput(input, "addClass", "display", "height", "removeClass", "width");
        }

        private static bool InputBoolean(IDictionary<string, object> input, string name)
        {
            object value;
            return input.TryGetValue(name, out value) && value is bool && (bool)value;
        }

        /// <summary>非 validate 的空配置仅在目标缺少 Rigidbody 时会添加组件，已有组件则保持只读。</summary>
        private static bool RigidbodyNeedsMutation(IDictionary<string, object> input)
        {
            object targetValue;
            var target = input.TryGetValue("target", out targetValue) ? targetValue as string : null;
            if (string.IsNullOrEmpty(target)) return false;
            var targetObject = GameObjectToolSupport.ResolveUniqueTarget(target, "physics.rigidbody requires --target.");
            return targetObject.GetComponent<Rigidbody>() == null;
        }

        /// <summary>create=true 只有缺少组件或未指定目标而需要新建对象时才是 mutation；已有组件的幂等读取不越过 barrier。</summary>
        private static bool CreateProbeNeedsMutation(IDictionary<string, object> input, bool reflectionProbe)
        {
            if (!InputBoolean(input, "create")) return false;
            object targetValue;
            var target = input.TryGetValue("target", out targetValue) ? targetValue as string : null;
            if (string.IsNullOrEmpty(target)) return true;
            var targetObject = GameObjectToolSupport.ResolveUniqueTarget(target, "Probe target could not be resolved.");
            return reflectionProbe ? targetObject.GetComponent<ReflectionProbe>() == null : targetObject.GetComponent<LightProbeGroup>() == null;
        }

        /// <summary>Context/catalog/health 类请求没有 business mutation，不得创建 request journal。</summary>
        private static bool IsReadOnlyBridgeCommand(string command)
        {
            if (IsEditorStateSnapshotCommand(command)) return true;
            return string.Equals(command, "project-tool.list", StringComparison.Ordinal)
                || string.Equals(command, "runtime-tool.list", StringComparison.Ordinal)
                || string.Equals(command, "context.list", StringComparison.Ordinal)
                || string.Equals(command, "context.describe", StringComparison.Ordinal)
                || string.Equals(command, "context.read", StringComparison.Ordinal);
        }

        private static Dictionary<string, object> ValidateCoordinatedTerminal(string response, string requestId)
        {
            var envelope = UnityCliSerializer.Parse(response) as Dictionary<string, object>;
            object ok;
            object actualRequestId;
            if (envelope == null
                || !envelope.TryGetValue("ok", out ok) || !(ok is bool)
                || !envelope.TryGetValue("requestId", out actualRequestId)
                || !string.Equals(actualRequestId as string, requestId, StringComparison.Ordinal))
                throw new BridgeCommandException(
                    "E_TOOL_COMPLETION_PROOF_INVALID",
                    "Tool completion did not produce a terminal envelope bound to the root requestId.",
                    "Inspect the Tool completion driver and output schema before retrying.");
            return envelope;
        }

        /// <summary>business effect 已证明后统一吸收延迟 compile/import/reload；恢复时只执行本方法。</summary>
        private static async Task<string> FinalizeProvenResult(BridgeRequest request, object provenEnvelope, CancellationToken cancellationToken)
        {
            int remainingMs;
            if (!request.TryDeadlineRemainingMs(out remainingMs))
                return PersistProvenFinalizationFailure(request.RequestId, "The immutable request deadline elapsed before the proven effect reached a stable Editor generation.");
            bool ready;
            try
            {
                ready = await CreateWaiter(request.RequestId, "completion.finalize").UntilEditorReady(TimeSpan.FromMilliseconds(remainingMs), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return PersistProvenFinalizationFailure(request.RequestId, "The immutable request deadline elapsed while waiting for a stable Editor generation.");
            }
            if (!ready)
                return PersistProvenFinalizationFailure(request.RequestId, "Unity did not remain ready across the completion stability window before the deadline.");
            return UnityCliSerializer.Serialize(PersistTerminal(request.RequestId, provenEnvelope, "completed"));
        }

        private static string PersistProvenFinalizationFailure(string requestId, string reason)
        {
            return UnityCliSerializer.Serialize(PersistTerminal(requestId, ProvenFinalizationFailureEnvelope(requestId, reason), "failed"));
        }

        private static string PersistFailedBeforeEffect(string requestId, BridgeCommandException exception)
        {
            var serialized = SerializeBridgeExceptionEnvelope(requestId, exception);
            var envelope = UnityCliSerializer.Parse(serialized);
            return UnityCliSerializer.Serialize(PersistTerminal(requestId, envelope, "failed"));
        }

        private static string PersistOutcomeUnknown(string requestId, bool effectStarted, string reason)
        {
            var envelope = ProtocolEnvelopeFactory.Error(
                requestId,
                "E_REQUEST_OUTCOME_UNKNOWN",
                effectStarted
                    ? "The request may have changed Unity state, but no durable terminal proof is available."
                    : "The request did not produce a durable terminal result.",
                new Dictionary<string, object> { { "effectStarted", effectStarted }, { "reason", reason } },
                new[] { "Do not replay this mutation automatically.", "Inspect the affected business state using the same requestId before deciding on a new request." });
            try { return UnityCliSerializer.Serialize(PersistTerminal(requestId, envelope, "outcome_unknown")); }
            catch (Exception exception) { WriteLog("request_journal_outcome_persist_failed requestId=" + requestId + " " + exception.GetType().Name + ": " + exception.Message); }
            return UnityCliSerializer.Serialize(envelope);
        }

        private static object CompletionProofInvalidEnvelope(BridgeRequest request, UnityCliToolCompletionContract contract, string reason)
        {
            return ProtocolEnvelopeFactory.Error(
                request == null ? string.Empty : request.RequestId,
                "E_TOOL_COMPLETION_PROOF_INVALID",
                "The Tool did not satisfy its registered terminal completion proof.",
                new Dictionary<string, object>
                {
                    { "toolName", contract == null ? string.Empty : contract.ToolName },
                    { "completionDriverId", contract == null ? string.Empty : contract.CompletionDriverId },
                    { "reason", reason }
                },
                new[] { "Fix the Tool completion driver or business readback before retrying." });
        }

        private static string CompletionContractRequiredEnvelope(BridgeRequest request)
        {
            return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(
                request == null ? string.Empty : request.RequestId,
                "E_TOOL_COMPLETION_CONTRACT_REQUIRED",
                "The requested Tool has no explicit completion contract: " + RequestToolName(request) + ".",
                new Dictionary<string, object> { { "toolName", RequestToolName(request) ?? string.Empty } },
                new[] { "Declare execution class, completion predicate, proof source, reload policy, timeout error, and outcome-unknown policy before registering this Tool." }));
        }

        private static string CompletionDriverRequiredEnvelope(BridgeRequest request, UnityCliToolCompletionContract contract)
        {
            return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(
                request == null ? string.Empty : request.RequestId,
                "E_TOOL_COMPLETION_CONTRACT_REQUIRED",
                "The Tool completion driver is not registered for its execution class: " + (contract == null ? string.Empty : contract.ToolName) + ".",
                new Dictionary<string, object> { { "toolName", contract == null ? string.Empty : contract.ToolName }, { "completionDriverId", contract == null ? string.Empty : contract.CompletionDriverId } },
                new[] { "Register one sealed completion driver before exposing this Tool." }));
        }
    }
}
#endif
