#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
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
    public static partial class UnityCliBridge
    {
        private const string ProjectToolFingerprintDefaultMode = "block";
        private static readonly Dictionary<string, RegisteredTool> ProjectTools = new Dictionary<string, RegisteredTool>(StringComparer.Ordinal);
        private static Dictionary<string, RegisteredTool> discoveryTools;
        private static RegistryOwnershipResolver registryOwnership;

        private static void RegisterProjectTool(RegisteredTool definition)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.Name) || string.IsNullOrWhiteSpace(definition.Summary) || definition.InvokeAsync == null)
            {
                throw new ArgumentException("Tool registration requires name, summary, and a typed async invoker.");
            }
            var target = discoveryTools ?? ProjectTools;
            if (target.ContainsKey(definition.Name)) throw new InvalidOperationException("Duplicate tool registration: " + definition.Name);
            if (definition.RequestType == null || definition.ResultType == null || string.IsNullOrWhiteSpace(definition.InputSchema) || string.IsNullOrWhiteSpace(definition.OutputSchema))
            {
                throw new ArgumentException("Tool registration requires request/result types and derived schemas: " + definition.Name);
            }
            if (definition.ArtifactKinds == null)
            {
                definition.ArtifactKinds = new[] { "json", "log", "file", "image" };
            }
            if (definition.RequiredInput == null)
            {
                definition.RequiredInput = new string[0];
            }
            definition.Group = string.IsNullOrEmpty(definition.Group) ? ProjectToolGroupFromName(definition.Name) : definition.Group;
            definition.Version = string.IsNullOrEmpty(definition.Version) ? "1.0.0" : definition.Version;
            definition.CapabilityType = string.IsNullOrEmpty(definition.CapabilityType) ? "project-write" : definition.CapabilityType;
            definition.Target = string.IsNullOrEmpty(definition.Target) ? "editor" : definition.Target;
            definition.AvailabilityScope = string.IsNullOrEmpty(definition.AvailabilityScope) ? (definition.Target == "runtime" ? "playModeSession" : "editorSession") : definition.AvailabilityScope;
            definition.PlayModeSupport = NormalizeProjectToolPlayModeSupport(definition.PlayModeSupport, definition.Target);
            definition.DefaultTimeoutMs = definition.DefaultTimeoutMs <= 0 ? 30000 : definition.DefaultTimeoutMs;
            definition.MaxTimeoutMs = definition.MaxTimeoutMs <= 0 ? 600000 : definition.MaxTimeoutMs;
            definition.RegisteredBy = string.IsNullOrEmpty(definition.RegisteredBy) ? "unity-editor-package" : definition.RegisteredBy;
            definition.DescriptorSource = string.IsNullOrEmpty(definition.DescriptorSource) ? "runtime-registration" : definition.DescriptorSource;
            RefreshProjectToolFingerprint(definition, null);
            target.Add(definition.Name, definition);
        }

        private static void DiscoverProjectTools()
        {
            if (projectToolsDiscovered) return;

            var pending = new Dictionary<string, RegisteredTool>(StringComparer.Ordinal);
            try
            {
                discoveryTools = pending;
                registryOwnership = RegistryOwnershipResolver.Load();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException exception) { types = exception.Types; }
                    catch (Exception exception) { throw new InvalidOperationException("Unable to inspect assembly " + assembly.FullName + ".", exception); }
                    foreach (var type in types)
                    {
                        if (type != null) TryRegisterInterfaceProjectTool(assembly, type);
                    }
                }

                ProjectTools.Clear();
                foreach (var item in pending) ProjectTools.Add(item.Key, item.Value);
                projectToolsDiscovered = true;
            }
            catch (Exception exception)
            {
                // 失败前从未写入公开 registry；下一次请求可在 Unity reload 后重新发现。
                throw new BridgeCommandException("E_REGISTRY_UNHEALTHY", "Tool registry discovery failed: " + exception.Message,
                    new Dictionary<string, object> { { "registry", "tools" } },
                    new[] { "Fix the extension manifest or capability implementation, then retry discovery.", "Run unity_cli extensions doctor --project <path> to inspect extension installation state." }, true, 1000);
            }
            finally
            {
                discoveryTools = null;
            }
        }

        private static void TryRegisterInterfaceProjectTool(Assembly assembly, Type type)
        {
            if (type == null || type.IsAbstract || type.IsInterface) return;

            Type toolInterface = null;
            Type reqType = null;
            Type resType = null;
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IUnityCliTool<,>))
                {
                    toolInterface = iface;
                    var args = iface.GetGenericArguments();
                    reqType = args[0];
                    resType = args[1];
                    break;
                }
            }
            if (toolInterface == null) return;

            object instance;
            try
            {
                instance = Activator.CreateInstance(type);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Tool implementation cannot be instantiated: " + type.FullName + ".", e);
            }

            ToolDescriptor descriptor = null;
            try
            {
                var describeMethod = type.GetMethod("Describe", Type.EmptyTypes);
                if (describeMethod == null)
                    throw new InvalidOperationException("Tool implementation has no Describe method: " + type.FullName + ".");
                descriptor = describeMethod.Invoke(instance, null) as ToolDescriptor;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Tool descriptor failed: " + type.FullName + ".", e);
            }
            if (descriptor == null || string.IsNullOrEmpty(descriptor.Name))
                throw new InvalidOperationException("Tool descriptor is incomplete: " + type.FullName + ".");

            RegisteredTool definition;
            var ownership = registryOwnership.Resolve(type);
            try
            {
                var factory = typeof(UnityCliBridge).GetMethod("CreateRegisteredTool", BindingFlags.Static | BindingFlags.NonPublic)
                    .MakeGenericMethod(reqType, resType);
                definition = (RegisteredTool)factory.Invoke(null, new[] { instance, descriptor, assembly, type });
            }
            catch (Exception exception)
            {
                var diagnostics = ExceptionChain(exception);
                if (string.Equals(ownership, "project", StringComparison.Ordinal) && diagnostics.Contains("E_TOOL_COMPLETION_CONTRACT_REQUIRED"))
                {
                    WriteLog("custom_tool_registration_rejected type=" + type.FullName + " error=" + diagnostics);
                    return;
                }
                WriteLog("custom_tool_registration_failed type=" + type.FullName + " error=" + diagnostics);
                throw new InvalidOperationException("Tool registration failed: " + type.FullName + ": " + diagnostics, exception);
            }
            RegisterProjectTool(definition);
            WriteLog("custom_tool_registered name=" + descriptor.Name + " source=IUnityCliTool type=" + type.FullName);
        }

        // 展开反射包装异常，确保 registry 失败能指向具体 schema/descriptor 根因。
        private static string ExceptionChain(Exception exception)
        {
            var messages = new List<string>();
            for (var current = exception; current != null; current = current.InnerException)
            {
                messages.Add(current.GetType().Name + ": " + current.Message);
            }
            return string.Join(" -> ", messages.ToArray());
        }

        private static RegisteredTool CreateRegisteredTool<TReq, TRes>(object instance, ToolDescriptor descriptor, Assembly assembly, Type implementationType)
        {
            var tool = (IUnityCliTool<TReq, TRes>)instance;
            var source = registryOwnership.Resolve(implementationType);
            var completion = ResolveToolCompletionContract(descriptor, source);
            return new RegisteredTool
            {
                Name = descriptor.Name,
                Summary = descriptor.Summary,
                Description = descriptor.Description,
                Group = string.IsNullOrEmpty(descriptor.Group) ? "custom" : descriptor.Group,
                Descriptor = descriptor,
                Source = source,
                Version = string.IsNullOrEmpty(descriptor.Version) ? "1.0.0" : descriptor.Version,
                InputSchema = UnityCliSchemaDeriver.DeriveSchema(typeof(TReq)),
                OutputSchema = UnityCliSchemaDeriver.DeriveSchema(typeof(TRes)),
                CapabilityType = string.IsNullOrEmpty(descriptor.CapabilityType) ? "project-write" : descriptor.CapabilityType,
                DestructiveHint = descriptor.DestructiveHint,
                SideEffects = descriptor.SideEffects ?? Array.Empty<string>(),
                SupportsDryRun = descriptor.SupportsDryRun,
                Permissions = descriptor.Permissions ?? Array.Empty<string>(),
                RequiresPackages = descriptor.RequiresPackages ?? Array.Empty<string>(),
                Examples = descriptor.Examples ?? Array.Empty<ToolExample>(),
                Target = string.IsNullOrEmpty(descriptor.Target) ? "editor" : descriptor.Target,
                PlayModeSupport = string.IsNullOrEmpty(descriptor.PlayModeSupport) ? "editor-only" : descriptor.PlayModeSupport,
                DefaultTimeoutMs = descriptor.DefaultTimeoutMs <= 0 ? 30000 : descriptor.DefaultTimeoutMs,
                MaxTimeoutMs = descriptor.MaxTimeoutMs <= 0 ? 600000 : descriptor.MaxTimeoutMs,
                SupportsJob = descriptor.SupportsJob,
                ArtifactKinds = descriptor.ArtifactKinds ?? Array.Empty<string>(),
                RequiredInput = Array.Empty<string>(),
                RegisteredBy = "unity-editor-package",
                DescriptorSource = "IUnityCliTool",
                Assembly = assembly.GetName().Name,
                Method = implementationType.FullName + ".ExecuteAsync",
                Fingerprint = descriptor.Fingerprint,
                PinnedFingerprint = descriptor.Fingerprint,
                ExpectedFingerprint = descriptor.Fingerprint,
                RequestType = typeof(TReq),
                ResultType = typeof(TRes),
                CompletionContract = completion,
                InvokeAsync = async (request, context, cancellationToken) => (object)await tool.ExecuteAsync((TReq)request, context, cancellationToken)
            };
        }

        /// <summary>built-in/Extension 只信任生成 registry；自定义 Tool 必须提供完整机器契约。</summary>
        private static UnityCliToolCompletionContract ResolveToolCompletionContract(ToolDescriptor descriptor, string source)
        {
            UnityCliToolCompletionContract generated;
            if (UnityCliToolCompletionRegistry.TryGet(descriptor.Name, out generated))
            {
                var generatedExtension = generated.DescriptorOwner.StartsWith("extension:", StringComparison.Ordinal);
                if ((generatedExtension && !string.Equals(source, "extension", StringComparison.Ordinal))
                    || (!generatedExtension && !string.Equals(generated.DescriptorOwner, source, StringComparison.Ordinal)))
                    throw new InvalidOperationException("Completion contract owner mismatch for " + descriptor.Name + ".");
                var descriptorMutation = descriptor.DestructiveHint
                    || descriptor.SupportsDryRun
                    || (descriptor.SideEffects != null && descriptor.SideEffects.Length > 0)
                    || string.Equals(descriptor.PlayModeSupport, "session-affecting", StringComparison.Ordinal);
                if (generated.Mutation != descriptorMutation)
                    throw new InvalidOperationException("Completion contract mutation classification mismatch for " + descriptor.Name + ".");
                return generated;
            }

            var custom = descriptor.Completion;
            if (custom == null)
                throw new InvalidOperationException("E_TOOL_COMPLETION_CONTRACT_REQUIRED: custom Tool " + descriptor.Name + " must declare completion semantics.");
            var allowedClasses = new HashSet<string>(new[] { "atomic", "observed-state", "resumable", "external" }, StringComparer.Ordinal);
            if (!allowedClasses.Contains(custom.ExecutionClass)
                || string.IsNullOrWhiteSpace(custom.CompletionPredicate)
                || string.IsNullOrWhiteSpace(custom.ProofSource)
                || string.IsNullOrWhiteSpace(custom.ReloadPolicy)
                || string.IsNullOrWhiteSpace(custom.TimeoutError)
                || string.IsNullOrWhiteSpace(custom.OutcomeUnknownPolicy)
                || string.IsNullOrWhiteSpace(custom.CompletionDriverId)
                || !IsRegisteredCompletionDriver(custom.ExecutionClass, custom.CompletionDriverId))
                throw new InvalidOperationException("E_TOOL_COMPLETION_CONTRACT_REQUIRED: custom Tool " + descriptor.Name + " has an incomplete or unregistered completion contract.");
            var mutation = descriptor.DestructiveHint
                || descriptor.SupportsDryRun
                || (descriptor.SideEffects != null && descriptor.SideEffects.Length > 0)
                || string.Equals(descriptor.PlayModeSupport, "session-affecting", StringComparison.Ordinal);
            return new UnityCliToolCompletionContract(
                descriptor.Name, "project", "direct-terminal", custom.ExecutionClass, custom.CompletionPredicate,
                custom.ProofSource, custom.ReloadPolicy, custom.TimeoutError, custom.OutcomeUnknownPolicy,
                "allowed", "ordinary", custom.CompletionDriverId, mutation ? "required" : "none", mutation);
        }

        /// <summary>operation coordinator 在主线程解析自定义 Tool 的真实 contract。</summary>
        private static UnityCliToolCompletionContract ResolveRegisteredCustomToolCompletionContract(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return null;
            DiscoverProjectTools();
            RegisteredTool definition;
            return ProjectTools.TryGetValue(toolName, out definition) ? definition.CompletionContract : null;
        }

        private static string ProjectToolGroupFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "custom";
            var dot = name.IndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : "custom";
        }

        private static object ProjectToolList(UnityCli.Protocol.BridgeRequest request)
        {
            DiscoverProjectTools();
            return new Dictionary<string, object>
            {
                { "tools", ProjectTools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal).Select(ProjectToolDescriptorData).ToList() },
                { "logPath", LogPath() }
            };
        }

        private sealed class ProjectToolRunArgs
        {
            public string Name { get; set; }
            [UnityCliAnyJson] public object Input { get; set; }
            public string FingerprintMode { get; set; }
            public string ExpectedDescriptorFingerprint { get; set; }
            public string ArtifactDir { get; set; }
            public string ProjectPath { get; set; }
        }

        private static async ValueTask<object> ProjectToolRun(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            DiscoverProjectTools();
            var args = request.DeserializeArgs<ProjectToolRunArgs>();
            var name = args.Name;
            if (string.IsNullOrEmpty(name) || !ProjectTools.ContainsKey(name))
            {
                throw new BridgeCommandException("E_TOOL_NOT_FOUND", "Tool not found: " + name, "Run unity_cli tools list --include-custom to inspect registered tools.");
            }

            var definition = ProjectTools[name];
            // 门禁前刷新 live handler+descriptor 指纹，旧 discovery proof 在 domain reload/descriptor 变化后必须失效。
            var fingerprintCheck = RefreshProjectToolFingerprint(definition, args.FingerprintMode);
            if (!string.IsNullOrEmpty(args.ExpectedDescriptorFingerprint)
                && !string.Equals(args.ExpectedDescriptorFingerprint, definition.Fingerprint, StringComparison.Ordinal))
            {
                throw new BridgeCommandException(
                    "E_TOOL_DESCRIPTOR_CHANGED",
                    "The Tool descriptor changed after discovery: " + name,
                    new Dictionary<string, object> { { "toolName", name }, { "expectedFingerprint", args.ExpectedDescriptorFingerprint }, { "actualFingerprint", definition.Fingerprint } },
                    new[] { "Run tools describe again against the selected Unity generation before constructing a new request." });
            }
            var dryRun = request.Execution != null && request.Execution.DryRun == true;
            if (definition.DestructiveHint
                && !(dryRun && definition.SupportsDryRun)
                && !request.SecurityConfirmed(name, true, definition.SupportsDryRun, "project-tool.run", definition.Fingerprint))
            {
                throw new BridgeCommandException(
                    "E_DESTRUCTIVE_CONFIRMATION_REQUIRED",
                    "Destructive tool requires a security proof bound to its descriptor and input: " + name,
                    "Review the live descriptor, then rerun the same tools run command with --yes.");
            }

            if (EditorApplication.isPlaying && string.Equals(definition.PlayModeSupport, "editor-only", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_TOOL_PLAYMODE_UNAVAILABLE",
                    "Tool " + name + " is editor-only and cannot run while Unity is in Play Mode.",
                    new Dictionary<string, object>
                    {
                        { "toolName", name },
                        { "playModeSupport", definition.PlayModeSupport },
                        { "isPlaying", true }
                    },
                    new[] { "Exit Play Mode before running this tool, or use a runtime tool with PlayModeSupport=runtime-only." });
            }

            // If the tool has Target=runtime, reject from project-tool.run and suggest runtime-tool.run
            if (definition.Target == "runtime")
            {
                throw new BridgeCommandException("E_RUNTIME_TOOL_TARGET_MISMATCH",
                    "Tool " + name + " has Target=runtime and must be executed via the runtime path (runtime-tool.run).",
                    new Dictionary<string, object> { { "toolName", name }, { "toolTarget", definition.Target } },
                    new[] { "Use runtime-tool.run <name> or tools run <name> with a runtime target to execute this tool in Play Mode." });
            }
            if (fingerprintCheck.Blocked)
            {
                WriteLog("custom_tool_fingerprint_blocked requestId=" + request.RequestId + " name=" + name + " expected=" + fingerprintCheck.Expected + " actual=" + fingerprintCheck.Actual);
                throw new BridgeCommandException("E_TOOL_FINGERPRINT_MISMATCH", fingerprintCheck.Message, ProjectToolFingerprintDetails(definition), new[] { "Review the registered custom tool descriptor/handler code, then update the expected fingerprint or rerun with --fingerprint-mode warn if policy allows." });
            }
            if (fingerprintCheck.Warn)
            {
                WriteLog("custom_tool_fingerprint_warn requestId=" + request.RequestId + " name=" + name + " expected=" + fingerprintCheck.Expected + " actual=" + fingerprintCheck.Actual);
            }

            var timeoutMs = request.ExecutionTimeoutMs(definition.DefaultTimeoutMs, definition.MaxTimeoutMs);

            var inputJson = request.RawJsonArg("input") ?? "{}";
            var schemaError = ValidateProjectToolInput(definition, inputJson);
            if (schemaError != null) throw SchemaValidationException(schemaError);

            var startedAt = DateTime.UtcNow;
            var artifactDir = ResolveArtifactDir(args.ArtifactDir, request.RequestId);
            try
            {
                Directory.CreateDirectory(artifactDir);
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_ARTIFACT_WRITE_FAILED", "Failed to create artifact directory: " + exception.Message, "Pass --artifact-dir to a writable directory.");
            }

            object result;
            object parsedInput;
            try
            {
                WriteLog("custom_tool_run requestId=" + request.RequestId + " name=" + name + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
                using (var toolTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    toolTimeout.CancelAfter(timeoutMs);
                    parsedInput = UnityCliSerializer.Deserialize(inputJson, definition.RequestType);
                    var effectPreparation = PrepareToolEffect(request, definition.CompletionContract, parsedInput);
                    result = await definition.InvokeAsync(parsedInput, new UnityCliProjectToolContext
                    {
                        RequestId = request.RequestId,
                        ProjectPath = args.ProjectPath ?? ProjectRoot(),
                        ArtifactDir = artifactDir,
                        LogPath = LogPath(),
                        DryRun = dryRun,
                        ProofOnly = IsProofRecoveryRequest(request.RequestId),
                        ProofContinuation = effectPreparation == null ? null : effectPreparation.Value,
                        CommitEffectAction = () => CommitToolEffect(request, definition.CompletionContract, effectPreparation),
                        ListenerGeneration = CurrentListenerGeneration(),
                        TimeoutMs = timeoutMs,
                        Wait = CreateWaiter(request.RequestId, name),
                        CancellationToken = toolTimeout.Token
                    }, toolTimeout.Token);
                    toolTimeout.Token.ThrowIfCancellationRequested();
                }
            }
            catch (BridgeCommandException bce)
            {
                WriteLog("custom_tool_error requestId=" + request.RequestId + " name=" + name + " error=" + bce.Code);
                throw;
            }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException ?? exception;
                var innerBce = inner as BridgeCommandException;
                if (innerBce != null) throw innerBce;
                if (inner is OperationCanceledException)
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "The immutable request deadline elapsed before the Tool reached its terminal proof.", "Inspect editor.state before submitting a new request.");
                throw new BridgeCommandException("E_TOOL_EXECUTION_FAILED", inner.GetType().Name + ": " + inner.Message, "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }
            catch (OperationCanceledException)
            {
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "The immutable request deadline elapsed before the Tool reached its terminal proof.", "Inspect editor.state before submitting a new request.");
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_TOOL_EXECUTION_FAILED", exception.GetType().Name + ": " + exception.Message, "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var outputJson = UnityCliSerializer.Serialize(result);
            var outputError = ValidateTypedInput(definition.ResultType, outputJson);
            if (outputError != null)
                throw new BridgeCommandException(
                    "E_TOOL_COMPLETION_PROOF_INVALID",
                    "Tool result failed its derived output schema at " + outputError.Path + ".",
                    new Dictionary<string, object> { { "toolName", definition.Name }, { "path", outputError.Path }, { "expected", outputError.Expected }, { "actual", outputError.Actual } },
                    new[] { "Fix the Tool result DTO or completion driver before retrying." });
            var terminalData = CompleteProjectToolSuccess(request, definition, artifactDir, parsedInput, timeoutMs, startedAt, result);
            RecordToolCompletionEvidence(request, definition, result, terminalData);
            return terminalData;
        }

        private static Dictionary<string, object> ProjectToolDescriptorData(RegisteredTool definition)
        {
            var runtimeTarget = string.Equals(definition.Target, "runtime", StringComparison.OrdinalIgnoreCase);
            return new Dictionary<string, object>
            {
                { "name", definition.Name }, { "summary", definition.Summary }, { "description", definition.Description },
                { "group", definition.Group }, { "source", definition.Source }, { "version", definition.Version },
                { "inputSchema", UnityCliSerializer.Parse(definition.InputSchema) }, { "outputSchema", UnityCliSerializer.Parse(definition.OutputSchema) },
                { "capabilityType", definition.CapabilityType }, { "destructiveHint", definition.DestructiveHint },
                { "sideEffects", definition.SideEffects }, { "supportsDryRun", definition.SupportsDryRun },
                { "permissions", definition.Permissions }, { "requiresPackages", definition.RequiresPackages }, { "examples", definition.Examples },
                { "target", definition.Target }, { "availabilityScope", definition.AvailabilityScope }, { "playModeSupport", definition.PlayModeSupport },
                { "defaultTimeoutMs", definition.DefaultTimeoutMs }, { "maxTimeoutMs", definition.MaxTimeoutMs }, { "supportsJob", definition.SupportsJob },
                { "artifactKinds", definition.ArtifactKinds }, { "handler", runtimeTarget ? "bridge-runtime" : "bridge" },
                { "supportStatus", runtimeTarget ? "live-bridge-runtime" : "live-bridge" }, { "cliCommand", "tools run " + definition.Name },
                { "completionContract", CompletionContractData(definition.CompletionContract) },
                { "fingerprint", ProjectToolFingerprintData(definition) }
            };
        }

        private static Dictionary<string, object> CompletionContractData(UnityCliToolCompletionContract contract)
        {
            return new Dictionary<string, object>
            {
                { "executionClass", contract.ExecutionClass }, { "completionPredicate", contract.CompletionPredicate },
                { "proofSource", contract.ProofSource }, { "reloadPolicy", contract.ReloadPolicy },
                { "timeoutError", contract.TimeoutError }, { "outcomeUnknownPolicy", contract.OutcomeUnknownPolicy },
                { "directInvocationPolicy", contract.DirectInvocationPolicy }, { "frozenDeliveryPolicy", contract.FrozenDeliveryPolicy },
                { "completionDriverId", contract.CompletionDriverId }, { "journalPolicy", contract.JournalPolicy },
                { "mutation", contract.Mutation }
            };
        }

        private static object CompleteProjectToolSuccess(UnityCli.Protocol.BridgeRequest request, RegisteredTool definition, string artifactDir, object input, int timeoutMs, DateTime startedAt, object result)
        {
            var endedAt = DateTime.UtcNow;
            var durationMs = (long)(endedAt - startedAt).TotalMilliseconds;
            var manifest = UnityCliSerializer.Serialize(ProjectToolManifestData(request.RequestId, definition, artifactDir, startedAt, endedAt));
            try
            {
                File.WriteAllText(Path.Combine(artifactDir, "manifest.json"), manifest);
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_ARTIFACT_WRITE_FAILED", "Failed to write artifact manifest: " + exception.Message, "Pass --artifact-dir to a writable directory.");
            }
            if (durationMs > timeoutMs)
            {
                WriteLog("custom_tool_timeout requestId=" + request.RequestId + " name=" + definition.Name + " durationMs=" + durationMs.ToString(CultureInfo.InvariantCulture) + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Tool exceeded timeoutMs after completion: " + durationMs.ToString(CultureInfo.InvariantCulture) + "ms > " + timeoutMs.ToString(CultureInfo.InvariantCulture) + "ms.", "Inspect bridge logs and artifact manifest with the returned requestId before retrying.");
            }

            return new Dictionary<string, object>
            {
                { "command", "project-tool.run" },
                { "tool", new Dictionary<string, object> { { "name", definition.Name }, { "version", definition.Version } } },
                { "execution", new Dictionary<string, object> { { "channel", "live" }, { "startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture) }, { "endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture) }, { "durationMs", durationMs }, { "exitState", "completed" } } },
                { "result", result },
                { "artifacts", new List<object> { new Dictionary<string, object> { { "id", "manifest" }, { "kind", "json" }, { "path", Path.Combine(artifactDir, "manifest.json") }, { "description", "project-tool artifact manifest" } } } },
                { "audit", ProjectToolAuditData(request, definition, artifactDir, input, result, timeoutMs, startedAt, endedAt, "completed") },
                { "logs", new Dictionary<string, object> { { "bridge", LogPath() } } }
            };
        }

        private static Dictionary<string, object> ProjectToolManifestData(string requestId, RegisteredTool definition, string artifactDir, DateTime startedAt, DateTime endedAt)
        {
            return new Dictionary<string, object>
            {
                { "requestId", requestId }, { "command", "project-tool.run" }, { "tool", new Dictionary<string, object> { { "name", definition.Name }, { "version", definition.Version } } },
                { "artifact", new List<object> { new Dictionary<string, object> { { "id", "manifest" }, { "kind", "json" }, { "relativePath", "manifest.json" }, { "producer", "unity-editor-package" } } } },
                { "createdAt", endedAt.ToString("O", CultureInfo.InvariantCulture) }, { "startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture) }, { "root", artifactDir }
            };
        }

        private static Dictionary<string, object> ProjectToolAuditData(UnityCli.Protocol.BridgeRequest request, RegisteredTool definition, string artifactDir, object input, object result, int timeoutMs, DateTime startedAt, DateTime endedAt, string exitState)
        {
            return new Dictionary<string, object>
            {
                { "requestId", request.RequestId }, { "command", "project-tool.run" },
                { "projectPath", request.StringArg("projectPath") ?? ProjectRoot() }, { "instanceId", request.StringArg("instanceId") },
                { "unityVersion", Application.unityVersion }, { "bridgeVersion", "com.unity_cli" },
                { "startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture) }, { "endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture) },
                { "durationMs", (long)(endedAt - startedAt).TotalMilliseconds },
                { "initiator", new Dictionary<string, object> { { "cliUser", "local" }, { "cwd", Directory.GetCurrentDirectory() } } },
                { "input", new Dictionary<string, object> { { "sourceKind", "typed" }, { "sizeBytes", Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(input)) } } },
                { "execution", new Dictionary<string, object> { { "channel", "live" }, { "timeoutMs", timeoutMs }, { "exitState", exitState } } },
                { "effects", ProjectToolEffectsData(definition, result) },
                { "artifacts", new List<object> { new Dictionary<string, object> { { "kind", "json" }, { "path", Path.Combine(artifactDir, "manifest.json") } } } },
                { "tool", new Dictionary<string, object> { { "name", definition.Name }, { "capabilityType", definition.CapabilityType }, { "fingerprint", ProjectToolFingerprintData(definition) } } }
            };
        }

        // 组合 Result 的 planned effects 与 descriptor 类别，避免把普通目标路径误报为持久化文件写入。
        private static Dictionary<string, object> ProjectToolEffectsData(RegisteredTool definition, object result)
        {
            var declared = definition.SideEffects ?? Array.Empty<string>();
            var plannedEffects = ProjectToolPlannedEffects(result);
            var hasPlannedEffects = plannedEffects.Count > 0;
            var refreshed = ProjectToolResultBool(result, "Refreshed");
            var fileWritesDeclared = hasPlannedEffects && declared.Any(effect => effect == "file-write" || effect == "asset-write" || effect == "asset-delete" || effect == "package-write" || effect == "project-settings-write" || effect == "persistent-artifact-write" || effect == "memory-snapshot-write" || effect == "settings-write");
            var fileWrites = fileWritesDeclared ? ProjectToolEffectPaths(plannedEffects) : Array.Empty<string>();
            var assetRefresh = refreshed || (hasPlannedEffects && declared.Any(effect => effect == "asset-import"));
            var domainReload = hasPlannedEffects && declared.Any(effect => effect == "shader-recompile" || effect == "package-write" || effect == "build-target-switch");
            var playModeChanged = hasPlannedEffects && (definition.Name == "editor.play" || definition.Name == "editor.stop");
            var packagesChanged = hasPlannedEffects && declared.Contains("package-write");
            return new Dictionary<string, object>
            {
                { "fileWrites", fileWrites }, { "fileWritesDeclared", fileWritesDeclared },
                { "assetRefresh", assetRefresh }, { "domainReload", domainReload },
                { "playModeChanged", playModeChanged }, { "packagesChanged", packagesChanged },
                { "plannedEffectCount", plannedEffects.Count }, { "declaredSideEffects", declared }
            };
        }

        // 返回 Result.PlannedEffects；缺失或非集合时按无副作用证据处理。
        private static List<object> ProjectToolPlannedEffects(object result)
        {
            var value = ProjectToolResultMember(result, "PlannedEffects") as System.Collections.IEnumerable;
            var effects = new List<object>();
            if (value == null) return effects;
            foreach (var item in value) if (item != null) effects.Add(item);
            return effects;
        }

        // 收集 planned effect 中可审计的项目/资产输出路径。
        private static string[] ProjectToolEffectPaths(IEnumerable<object> effects)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var effect in effects)
            {
                foreach (var name in new[] { "Path", "OutputPath", "AssetPath", "Destination" })
                {
                    var value = ProjectToolResultMember(effect, name) as string;
                    if (!string.IsNullOrWhiteSpace(value)) paths.Add(value);
                }
            }
            return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        // 读取 typed Result 的 bool 成员；不存在时返回 false。
        private static bool ProjectToolResultBool(object result, string name)
        {
            var value = ProjectToolResultMember(result, name);
            return value is bool && (bool)value;
        }

        // 使用大小写不敏感反射读取 Result/PlannedEffect 公共属性或字段。
        private static object ProjectToolResultMember(object value, string name)
        {
            if (value == null) return null;
            var type = value.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property != null && property.CanRead) return property.GetValue(value, null);
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            return field == null ? null : field.GetValue(value);
        }

        private sealed class ProjectToolFingerprintCheck
        {
            public bool Blocked;
            public bool Warn;
            public string Expected;
            public string Actual;
            public string Message;
        }

        private static ProjectToolFingerprintCheck RefreshProjectToolFingerprint(RegisteredTool definition, string requestedMode)
        {
            var descriptor = ProjectToolDescriptorFingerprintMaterial(definition);
            definition.DescriptorSha256 = Sha256Hex(descriptor);
            definition.HandlerSha256 = Sha256Hex(ProjectToolHandlerFingerprintMaterial(definition));
            definition.Fingerprint = Sha256Hex("unity-cli-project-tool-fingerprint-v1\nhandler=" + definition.HandlerSha256 + "\ndescriptor=" + definition.DescriptorSha256);

            // When the attribute has an explicit Fingerprint (pinned by the developer),
            // enforce strict matching. When it is not pinned (null/empty), auto-accept
            // the current computed fingerprint as the new baseline — this allows
            // built-in sample tools to evolve without manual fingerprint updates.
            var isPinned = !string.IsNullOrEmpty(definition.PinnedFingerprint);
            if (isPinned)
            {
                definition.ExpectedFingerprint = definition.PinnedFingerprint;
            }
            else
            {
                // Not pinned: always accept current fingerprint as expected.
                // Update the persisted baseline so mismatch is detectable if
                // the user later pins the fingerprint.
                definition.ExpectedFingerprint = definition.Fingerprint;
                SaveProjectToolExpectedFingerprint(definition.Name, definition.Fingerprint);
            }

            definition.FingerprintMode = NormalizeProjectToolFingerprintMode(requestedMode);
            var matches = string.Equals(definition.ExpectedFingerprint, definition.Fingerprint, StringComparison.OrdinalIgnoreCase);
            definition.FingerprintStatus = matches ? "match" : (definition.FingerprintMode == "warn" ? "warn" : "blocked");
            definition.FingerprintMessage = matches
                ? "Custom tool fingerprint matches handler code + descriptor."
                : "Custom tool fingerprint mismatch for " + definition.Name + ": expected " + definition.ExpectedFingerprint + " but computed " + definition.Fingerprint + ".";
            return new ProjectToolFingerprintCheck
            {
                Blocked = !matches && definition.FingerprintMode == "block",
                Warn = !matches && definition.FingerprintMode == "warn",
                Expected = definition.ExpectedFingerprint,
                Actual = definition.Fingerprint,
                Message = definition.FingerprintMessage
            };
        }


        private static string LoadProjectToolExpectedFingerprint(string name)
        {
            try
            {
                var path = ProjectToolExpectedFingerprintPath(name);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (Exception exception)
            {
                WriteLog("custom_tool_fingerprint_baseline_read_failed name=" + name + " error=" + exception.GetType().Name + ": " + exception.Message);
                return null;
            }
        }

        private static void SaveProjectToolExpectedFingerprint(string name, string fingerprint)
        {
            try
            {
                var path = ProjectToolExpectedFingerprintPath(name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, fingerprint ?? string.Empty);
            }
            catch (Exception exception)
            {
                WriteLog("custom_tool_fingerprint_baseline_write_failed name=" + name + " error=" + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static string ProjectToolExpectedFingerprintPath(string name)
        {
            return Path.Combine(ProjectRoot(), "Library", "UnityCliProjectToolFingerprints", SafeProjectToolFingerprintFileName(name) + ".sha256");
        }

        private static string SafeProjectToolFingerprintFileName(string name)
        {
            return Regex.Replace(name ?? "project-tool", "[^A-Za-z0-9_.-]", "_");
        }

        private static string NormalizeProjectToolFingerprintMode(string requestedMode)
        {
            if (string.Equals(requestedMode, "warn", StringComparison.OrdinalIgnoreCase)) return "warn";
            if (string.Equals(requestedMode, "block", StringComparison.OrdinalIgnoreCase)) return "block";
            var environment = Environment.GetEnvironmentVariable("UNITY_CLI_PROJECT_TOOL_FINGERPRINT_MODE");
            if (string.Equals(environment, "warn", StringComparison.OrdinalIgnoreCase)) return "warn";
            if (string.Equals(environment, "block", StringComparison.OrdinalIgnoreCase)) return "block";
            return ProjectToolFingerprintDefaultMode;
        }

        private static string ProjectToolDescriptorFingerprintMaterial(RegisteredTool definition)
        {
            return "name=" + (definition.Name ?? string.Empty)
                + "\nsummary=" + (definition.Summary ?? string.Empty)
                + "\ndescription=" + (definition.Description ?? string.Empty)
                + "\ngroup=" + (definition.Group ?? string.Empty)
                + "\nversion=" + (definition.Version ?? string.Empty)
                + "\ninputSchema=" + NormalizeFingerprintJson(definition.InputSchema)
                + "\noutputSchema=" + NormalizeFingerprintJson(definition.OutputSchema)
                + "\ncapabilityType=" + (definition.CapabilityType ?? string.Empty)
                + "\ndestructiveHint=" + Bool(definition.DestructiveHint)
                + "\nsupportsDryRun=" + Bool(definition.SupportsDryRun)
                + "\nsideEffects=" + string.Join(",", (definition.SideEffects ?? new string[0]).OrderBy(value => value, StringComparer.Ordinal).ToArray())
                + "\npermissions=" + string.Join(",", (definition.Permissions ?? new string[0]).OrderBy(value => value, StringComparer.Ordinal).ToArray())
                + "\nrequiresPackages=" + string.Join(",", (definition.RequiresPackages ?? new string[0]).OrderBy(value => value, StringComparer.Ordinal).ToArray())
                + "\nplayModeSupport=" + (definition.PlayModeSupport ?? string.Empty)
                + "\ndefaultTimeoutMs=" + definition.DefaultTimeoutMs.ToString(CultureInfo.InvariantCulture)
                + "\nmaxTimeoutMs=" + definition.MaxTimeoutMs.ToString(CultureInfo.InvariantCulture)
                + "\nsupportsJob=" + Bool(definition.SupportsJob)
                + "\nartifactKinds=" + string.Join(",", (definition.ArtifactKinds ?? new string[0]).OrderBy(value => value, StringComparer.Ordinal).ToArray())
                + "\nrequiredInput=" + string.Join(",", (definition.RequiredInput ?? new string[0]).OrderBy(value => value, StringComparer.Ordinal).ToArray())
                + "\ndescriptorSource=" + (definition.DescriptorSource ?? string.Empty);
        }

        private static string NormalizeProjectToolPlayModeSupport(string value, string target)
        {
            if (string.Equals(value, "supported", StringComparison.OrdinalIgnoreCase)) return "supported";
            if (string.Equals(value, "runtime-only", StringComparison.OrdinalIgnoreCase)) return "runtime-only";
            if (string.Equals(value, "session-affecting", StringComparison.OrdinalIgnoreCase)) return "session-affecting";
            if (string.Equals(value, "editor-only", StringComparison.OrdinalIgnoreCase)) return "editor-only";
            if (string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase)) return "unknown";
            return string.Equals(target, "runtime", StringComparison.OrdinalIgnoreCase) ? "runtime-only" : "editor-only";
        }

        private static string ProjectToolHandlerFingerprintMaterial(RegisteredTool definition)
        {
            return "assembly=" + (definition.Assembly ?? string.Empty)
                + "\nmethod=" + (definition.Method ?? string.Empty)
                + "\nhandler=" + ProjectToolHandlerSource(definition);
        }

        private static string ProjectToolHandlerSource(RegisteredTool definition)
        {
            try
            {
                var typeName = definition.Method;
                if (string.IsNullOrEmpty(typeName)) return string.Empty;
                var dot = typeName.LastIndexOf('.');
                if (dot <= 0) return string.Empty;
                typeName = typeName.Substring(0, dot);
                var fileName = typeName.Substring(typeName.LastIndexOf('.') + 1) + ".cs";
                foreach (var guid in AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(fileName) + " t:MonoScript"))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                    var text = File.ReadAllText(path);
                    if (text.IndexOf(definition.Method.Substring(dot + 1), StringComparison.Ordinal) >= 0)
                    {
                        return NormalizeFingerprintText(text);
                    }
                }
            }
            catch (Exception exception)
            {
                WriteLog("custom_tool_fingerprint_source_failed name=" + definition.Name + " error=" + exception.GetType().Name + ": " + exception.Message);
            }
            return string.Empty;
        }

        private static string NormalizeFingerprintText(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static string NormalizeFingerprintJson(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim(), "\\s+", " ");
        }

        private static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
        }

        private static Dictionary<string, object> ProjectToolFingerprintData(RegisteredTool definition)
        {
            return new Dictionary<string, object> { { "algorithm", "sha256-handler-code-plus-descriptor-v1" }, { "value", definition.Fingerprint }, { "expected", definition.ExpectedFingerprint }, { "status", definition.FingerprintStatus }, { "mode", definition.FingerprintMode }, { "message", definition.FingerprintMessage }, { "descriptorSha256", definition.DescriptorSha256 }, { "handlerSha256", definition.HandlerSha256 } };
        }


        private static Dictionary<string, object> ProjectToolFingerprintSummary()
        {
            var items = new List<object>();
            var blocked = 0;
            var warn = 0;
            foreach (var pair in ProjectTools)
            {
                var definition = pair.Value;
                RefreshProjectToolFingerprint(definition, null);
                if (definition.FingerprintStatus == "blocked") blocked++;
                if (definition.FingerprintStatus == "warn") warn++;
                items.Add(new Dictionary<string, object>
                {
                    { "name", definition.Name },
                    { "fingerprint", new Dictionary<string, object>
                        {
                            { "algorithm", "sha256-handler-code-plus-descriptor-v1" },
                            { "value", definition.Fingerprint },
                            { "expected", definition.ExpectedFingerprint },
                            { "status", definition.FingerprintStatus },
                            { "mode", definition.FingerprintMode },
                            { "message", definition.FingerprintMessage },
                            { "descriptorSha256", definition.DescriptorSha256 },
                            { "handlerSha256", definition.HandlerSha256 }
                        }
                    }
                });
            }
            items.Sort((left, right) => StringComparer.Ordinal.Compare(
                ((Dictionary<string, object>)left)["name"] as string,
                ((Dictionary<string, object>)right)["name"] as string));
            return new Dictionary<string, object>
            {
                { "algorithm", "sha256-handler-code-plus-descriptor-v1" },
                { "mode", NormalizeProjectToolFingerprintMode(null) },
                { "toolCount", (long)ProjectTools.Count },
                { "blockedCount", (long)blocked },
                { "warnCount", (long)warn },
                { "tools", items }
            };
        }

        private static Dictionary<string, object> ProjectToolFingerprintDetails(RegisteredTool definition)
        {
            return new Dictionary<string, object>
            {
                { "tool", definition.Name },
                { "fingerprint", ProjectToolFingerprintData(definition) }
            };
        }
    }
}
#endif
