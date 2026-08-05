#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityCli.Protocol;
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
        private static readonly HashSet<string> RegisteredCompletionDriverIds = new HashSet<string>(new[]
        {
            "atomic-typed-result-v1",
            "observed-terminal-readback-v1",
            "active-operation-terminal-readback-v1",
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
            // coordinator-owned roots own their journal and must never enter the direct handler path.
            if (UnityCliLiveBuildCoordinator.IsCommand(request.Command))
                return await UnityCliLiveBuildCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            if (UnityCliLiveRefreshCoordinator.IsCommand(request.Command))
                return await UnityCliLiveRefreshCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            if (UnityCliAssetRefreshCoordinator.IsCommand(request.Command))
                return await UnityCliAssetRefreshCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            if (UnityCliPackageEmbedCoordinator.IsCommand(request.Command))
                return await UnityCliPackageEmbedCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            if (UnityCliPackageResolveCoordinator.IsCommand(request.Command))
                return await UnityCliPackageResolveCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            if (UnityCliTestRunCoordinator.IsCommand(request.Command))
                return await UnityCliTestRunCoordinator.Execute(request, rawRequestBytes, tryAcceptJournal, cancellationToken);
            var contract = ResolveRequestCompletionContract(request);
            if (contract == null)
            {
                if (IsReadOnlyBridgeCommand(request.Command)) return await Execute(request, cancellationToken);
                return CompletionContractRequiredEnvelope(request);
            }
            if (!string.Equals(contract.DeliveryMode, "direct-terminal", StringComparison.Ordinal))
                return CompletionContractRequiredEnvelope(request);
            if (!IsRegisteredCompletionDriver(contract.ExecutionClass, contract.CompletionDriverId))
                return CompletionDriverRequiredEnvelope(request, contract);

            var mutation = RequestRequiresMutation(request, contract);
            if (!mutation)
            {
                var response = await Execute(request, cancellationToken);
                ValidateCoordinatedTerminal(response, request.RequestId);
                return response;
            }

            // 所有 C# 与 TS mutation 共用跨进程 instance lease；拒绝发生在请求接受前。
            bool newlyAcquiredLease;
            if (!UnityCliSessionMutationLease.TryAcquire(request.RequestId, UnityCliRequestDigest.Compute(rawRequestBytes, request.InstanceId), out newlyAcquiredLease))
                return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(
                    request.RequestId,
                    "E_UNITY_BUSY",
                    "Another mutation owns the selected Unity instance.",
                    new Dictionary<string, object> { { "owner", "session-mutation" } },
                    new[] { "Wait for the current mutation to reach its terminal result, then submit this request again." },
                    transient: true,
                    retryAfterMs: 250));

            try
            {
                // 接受后 HTTP 超时不再取消已入队的业务执行；普通 Tool 不创建请求级文件。
                if (tryAcceptJournal == null || !tryAcceptJournal())
                    throw new OperationCanceledException("HTTP timeout cancelled mutation before acceptance.");
            }
            catch (OperationCanceledException)
            {
                if (newlyAcquiredLease) UnityCliSessionMutationLease.Release(request.RequestId);
                return ExecutionTimeoutEnvelope(request.RequestId, "The request was not accepted before the execution deadline elapsed.");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await Execute(request, cancellationToken);
                var envelope = ValidateCoordinatedTerminal(response, request.RequestId);
                object okValue;
                var ok = envelope.TryGetValue("ok", out okValue) && okValue is bool && (bool)okValue;
                if (!ok) return UnityCliSerializer.Serialize(envelope);

                var evidence = TakeCompletionEvidence(request.RequestId, contract);
                if (evidence == null)
                    return UnityCliSerializer.Serialize(CompletionProofInvalidEnvelope(request, contract, "No completion-driver evidence was registered."));
                return await FinalizeResult(request, envelope, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return ExecutionTimeoutEnvelope(request.RequestId, "The execution deadline elapsed and the result cannot be confirmed. Check the affected business state.");
            }
            catch (BridgeCommandException exception)
            {
                return SerializeBridgeExceptionEnvelope(request.RequestId, exception);
            }
            catch (Exception exception)
            {
                return SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException(
                    "E_BRIDGE_COMMAND_FAILED", exception.Message, "Check Unity Console and UnityCliBridge.log."));
            }
            finally
            {
                RemoveCompletionEvidence(request.RequestId);
                UnityCliSessionMutationLease.Release(request.RequestId);
            }
        }

        /// <summary>普通 Tool 不跨重载恢复；active operation 由专用 coordinator 管理。</summary>
        private static bool TryFinalizeRecoveredPlayModeRoot(string requestId, string requestDigest)
        {
            return false;
        }

        /// <summary>普通 Tool 的 effect 提交只保留 runner 协调语义，不写请求级持久状态。</summary>
        private static object PrepareToolEffect(BridgeRequest request, UnityCliToolCompletionContract contract, object typedInput)
        {
            return null;
        }

        private static void CommitToolEffect(BridgeRequest request, UnityCliToolCompletionContract contract, object preparation)
        {
            if (!RequestRequiresMutation(request, contract))
                throw new BridgeCommandException("E_TOOL_EFFECT_BARRIER_INVALID", "The current request is not classified as a mutation.", "Remove ctx.CommitEffect() from read-only and dry-run paths.");
        }

        /// <summary>内部命令保留 effect boundary 调用，但不产生请求级持久状态。</summary>
        private static void BeginDirectEffect(string requestId) { }

        private static void RecordToolCompletionEvidence(BridgeRequest request, RegisteredTool definition, object result, object terminalData)
        {
            if (definition == null || definition.CompletionContract == null)
                throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Tool completion contract is unavailable at the runner boundary.", "Fix Tool registration before retrying.");
            var contract = definition.CompletionContract;
            ValidateToolTerminalData(request, definition, result, terminalData);
            if (!RequestRequiresMutation(request, contract))
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
            if (string.Equals(executionClass, "resumable", StringComparison.Ordinal)) return completionDriverId == "active-operation-terminal-readback-v1";
            if (string.Equals(executionClass, "external", StringComparison.Ordinal)) return completionDriverId == "external-terminal-readback-v1";
            return false;
        }

        private static void ValidateToolTerminalData(BridgeRequest request, RegisteredTool definition, object result, object terminalData)
        {
            var data = terminalData as IDictionary<string, object>;
            object resultValue;
            if (data == null
                || data.Count != 1
                || !data.TryGetValue("result", out resultValue)
                || !string.Equals(UnityCliSerializer.Serialize(resultValue), UnityCliSerializer.Serialize(result), StringComparison.Ordinal))
            {
                throw new BridgeCommandException("E_TOOL_COMPLETION_PROOF_INVALID", "Tool runner did not produce its exact typed business terminal.", "Fix the Tool runner completion boundary before retrying.");
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
                return RequestRequiresMutation(request, contract);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>descriptor 表示潜在风险；本次 input 没有 setter/action 时按 read 执行。</summary>
        private static bool RequestRequiresMutation(BridgeRequest request, UnityCliToolCompletionContract contract)
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

        /// <summary>普通 Tool 只在当前异步窗口等待稳定状态，超时后由调用方检查业务状态。</summary>
        private static async Task<string> FinalizeResult(BridgeRequest request, object terminalEnvelope, CancellationToken cancellationToken)
        {
            int remainingMs;
            if (!request.TryDeadlineRemainingMs(out remainingMs))
                return ExecutionTimeoutEnvelope(request.RequestId, "The execution result cannot be confirmed before the deadline. Check the affected business state.");
            try
            {
                if (!await CreateWaiter(request.RequestId, "completion.finalize").UntilEditorReady(TimeSpan.FromMilliseconds(remainingMs), cancellationToken))
                    return ExecutionTimeoutEnvelope(request.RequestId, "The execution result cannot be confirmed before the deadline. Check the affected business state.");
            }
            catch (OperationCanceledException)
            {
                return ExecutionTimeoutEnvelope(request.RequestId, "The execution result cannot be confirmed before the deadline. Check the affected business state.");
            }
            return UnityCliSerializer.Serialize(terminalEnvelope);
        }

        private static string ExecutionTimeoutEnvelope(string requestId, string reason)
        {
            return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(
                requestId,
                "E_EXECUTION_TIMEOUT",
                reason,
                null,
                new[] { "Check the affected business state before submitting another request." }));
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
