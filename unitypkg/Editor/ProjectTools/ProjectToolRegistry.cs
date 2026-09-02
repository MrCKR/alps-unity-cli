#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
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
        private const string OfficialAttributeToolPrefix = "UnityPipeline_";
        private const string OfficialAttributeToolGroup = "UnityPipeline";
        private static UnityCliToolCatalogSnapshot publishedProjectToolCatalog;
        private static string projectToolCatalogFailure;
        private static int projectToolCatalogConstructionCount;
        internal static IUnityCliToolCatalogSnapshotReader ProjectToolCatalogSnapshotReader { get; } = new PublishedToolCatalogSnapshotReader();

        private sealed class PublishedToolCatalogSnapshotReader : IUnityCliToolCatalogSnapshotReader
        {
            public UnityCliToolCatalogSnapshot Read() { return ReadPublishedProjectToolCatalog(); }
        }

        private static void RegisterProjectTool(UnityCliToolCatalogBuilder builder, RegisteredTool definition, UnityCliToolProviderKind providerKind)
        {
            if (builder == null) throw new ArgumentNullException("builder");
            if (definition == null) throw new ArgumentNullException("definition");
            definition.Group = string.IsNullOrEmpty(definition.Group) ? ProjectToolGroupFromName(definition.Name) : definition.Group;
            definition.Target = string.IsNullOrEmpty(definition.Target) ? "editor" : definition.Target;
            definition.AvailabilityScope = string.IsNullOrEmpty(definition.AvailabilityScope) ? (definition.Target == "runtime" ? "playModeSession" : "editorSession") : definition.AvailabilityScope;
            definition.PlayModeSupport = NormalizeProjectToolPlayModeSupport(definition.PlayModeSupport, definition.Target);
            definition.DefaultTimeoutMs = definition.DefaultTimeoutMs <= 0 ? 30000 : definition.DefaultTimeoutMs;
            definition.MaxTimeoutMs = definition.MaxTimeoutMs <= 0 ? 600000 : definition.MaxTimeoutMs;
            ValidateOfficialAttributeNamespace(definition);
            builder.Add(definition, providerKind);
        }

        /// <summary>保留namespace只属于official attribute投影；任一owner违规使整轮pending registry作废。</summary>
        private static void ValidateOfficialAttributeNamespace(RegisteredTool definition)
        {
            var officialAttribute = string.Equals(definition.ContractProfile, "official-attribute", StringComparison.Ordinal);
            var usesPrefix = definition.Name.StartsWith(OfficialAttributeToolPrefix, StringComparison.Ordinal);
            var usesGroup = string.Equals(definition.Group, OfficialAttributeToolGroup, StringComparison.Ordinal);
            if (officialAttribute && (!usesPrefix || !usesGroup))
                throw new InvalidOperationException("Official attribute Tool must use the reserved UnityPipeline_ name prefix and UnityPipeline group: " + definition.Name + ".");
            if (!officialAttribute && (usesPrefix || usesGroup))
                throw new InvalidOperationException("Native Tool cannot use the reserved UnityPipeline_ name prefix or UnityPipeline group: " + definition.Name + ".");
        }

        /// <summary>InitializeOnLoad静态构造函数调用一次；局部pending完整成功后才原子发布。</summary>
        private static void InitializeProjectToolCatalog()
        {
            if (projectToolCatalogConstructionCount != 0) return;
            projectToolCatalogConstructionCount = 1;
            var startedAt = DateTime.UtcNow;
            var builder = new UnityCliToolCatalogBuilder();
            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                var officialRoots = ConfigureOfficialAuthoringApi(assemblies);
                var candidates = ToolProviderAssemblies(assemblies, officialRoots);
                builder.BeginDiscovery(assemblies.Length, candidates.Count);
                var ownership = RegistryOwnershipResolver.Load(candidates);
                foreach (var assembly in candidates)
                {
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException exception) { throw new InvalidOperationException("Unable to inspect every type in Tool provider assembly " + assembly.FullName + ".", exception); }
                    catch (Exception exception) { throw new InvalidOperationException("Unable to inspect Tool provider assembly " + assembly.FullName + ".", exception); }
                    foreach (var type in types)
                    {
                        if (type == null) continue;
                        TryRegisterInterfaceProjectTool(builder, ownership, assembly, type);
                        TryRegisterOfficialAttributeTools(builder, ownership, assembly, type);
                    }
                }

                var snapshot = builder.Publish((long)Math.Ceiling((DateTime.UtcNow - startedAt).TotalMilliseconds));
                Volatile.Write(ref publishedProjectToolCatalog, snapshot);
                WriteLog("tool_catalog_initialized assemblies=" + snapshot.AssemblyCount.ToString(CultureInfo.InvariantCulture)
                    + " providers=" + snapshot.ProviderAssemblyCount.ToString(CultureInfo.InvariantCulture)
                    + " tools=" + snapshot.ToolCount.ToString(CultureInfo.InvariantCulture)
                    + " builtIn=" + snapshot.BuiltInToolCount.ToString(CultureInfo.InvariantCulture)
                    + " extension=" + snapshot.ExtensionToolCount.ToString(CultureInfo.InvariantCulture)
                    + " projectInterface=" + snapshot.ProjectInterfaceToolCount.ToString(CultureInfo.InvariantCulture)
                    + " officialAttribute=" + snapshot.OfficialAttributeToolCount.ToString(CultureInfo.InvariantCulture)
                    + " elapsedMs=" + snapshot.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception exception)
            {
                builder.Abort();
                projectToolCatalogFailure = BoundedCatalogDiagnostic(ExceptionChain(exception));
                WriteLog("tool_catalog_initialization_failed error=" + projectToolCatalogFailure);
            }
        }

        private static bool ProjectToolCatalogReady()
        {
            return Volatile.Read(ref publishedProjectToolCatalog) != null;
        }

        /// <summary>所有后续入口只返回同一个published snapshot；失败domain内绝不重试或重建。</summary>
        private static UnityCliToolCatalogSnapshot ReadPublishedProjectToolCatalog()
        {
            var snapshot = Volatile.Read(ref publishedProjectToolCatalog);
            if (snapshot != null) return snapshot;
            throw new BridgeCommandException(
                "E_TOOL_REGISTRY_UNHEALTHY",
                "Tool catalog initialization failed in the current Unity domain: " + (projectToolCatalogFailure ?? "catalog was not initialized"),
                new Dictionary<string, object> { { "registry", "tools" } },
                new[] { "Fix the Tool definition, then let Unity compile and reload the domain before retrying." },
                true,
                1000);
        }

        private static string BoundedCatalogDiagnostic(string value)
        {
            const int maxChars = 4096;
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars) return value ?? string.Empty;
            return value.Substring(0, maxChars) + " [truncated]";
        }

        /// <summary>
        /// 只读取依赖 CLI authoring API 或官方 Pipeline attribute API 的程序集。
        /// 无关第三方程序集即使存在不可加载类型，也不能阻断 Bridge listener 启动。
        /// </summary>
        private static List<Assembly> ToolProviderAssemblies(Assembly[] assemblies, IEnumerable<Assembly> officialRoots)
        {
            var reachable = new HashSet<string>(StringComparer.Ordinal)
            {
                typeof(IUnityCliTool<,>).Assembly.GetName().FullName
            };
            foreach (var root in officialRoots) reachable.Add(root.GetName().FullName);

            // 反向闭包按完整AssemblyName收敛；依赖环不会产生递归负缓存或扫描顺序差异。
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var assembly in assemblies)
                {
                    var identity = assembly.GetName().FullName;
                    if (reachable.Contains(identity)) continue;
                    AssemblyName[] references;
                    try { references = assembly.GetReferencedAssemblies(); }
                    catch (Exception exception) { throw new InvalidOperationException("Unable to inspect Tool provider dependencies for assembly " + assembly.FullName + ".", exception); }
                    if (!references.Any(reference => reachable.Contains(reference.FullName))) continue;
                    reachable.Add(identity);
                    changed = true;
                }
            }
            return assemblies.Where(assembly => reachable.Contains(assembly.GetName().FullName)).ToList();
        }

        private static void TryRegisterInterfaceProjectTool(UnityCliToolCatalogBuilder builder, RegistryOwnershipResolver ownershipResolver, Assembly assembly, Type type)
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
            var ownership = ownershipResolver.Resolve(type, descriptor.Name);
            try
            {
                var factory = typeof(UnityCliBridge).GetMethod("CreateRegisteredTool", BindingFlags.Static | BindingFlags.NonPublic)
                    .MakeGenericMethod(reqType, resType);
                definition = (RegisteredTool)factory.Invoke(null, new object[] { instance, descriptor, ownership });
            }
            catch (Exception exception)
            {
                var diagnostics = ExceptionChain(exception);
                WriteLog("custom_tool_registration_failed type=" + type.FullName + " error=" + diagnostics);
                throw new InvalidOperationException("Tool registration failed: " + type.FullName + ": " + diagnostics, exception);
            }
            var providerKind = string.Equals(ownership, "built-in", StringComparison.Ordinal)
                ? UnityCliToolProviderKind.BuiltIn
                : string.Equals(ownership, "extension", StringComparison.Ordinal)
                    ? UnityCliToolProviderKind.Extension
                    : UnityCliToolProviderKind.ProjectInterface;
            RegisterProjectTool(builder, definition, providerKind);
        }

        // 展开反射包装异常，确保 registry 失败能指向具体 schema/descriptor 根因。
        private static string ExceptionChain(Exception exception)
        {
            const int maxDepth = 8;
            var messages = new List<string>();
            for (var current = exception; current != null && messages.Count < maxDepth; current = current.InnerException)
            {
                messages.Add(current.GetType().Name + ": " + current.Message);
            }
            return BoundedCatalogDiagnostic(string.Join(" -> ", messages.ToArray()));
        }

        private static RegisteredTool CreateRegisteredTool<TReq, TRes>(object instance, ToolDescriptor descriptor, string ownership)
        {
            var tool = (IUnityCliTool<TReq, TRes>)instance;
            var completion = ResolveToolCompletionContract(descriptor, ownership);
            return new RegisteredTool
            {
                Name = descriptor.Name,
                Summary = descriptor.Summary,
                Description = descriptor.Description,
                Group = string.IsNullOrEmpty(descriptor.Group) ? "custom" : descriptor.Group,
                Source = ownership,
                InputSchema = UnityCliSchemaDeriver.DeriveSchema(typeof(TReq)),
                OutputSchema = UnityCliSchemaDeriver.DeriveSchema(typeof(TRes)),
                DestructiveHint = descriptor.DestructiveHint,
                SideEffects = (descriptor.SideEffects ?? Array.Empty<string>()).ToArray(),
                SupportsDryRun = descriptor.SupportsDryRun,
                Permissions = (descriptor.Permissions ?? Array.Empty<string>()).ToArray(),
                RequiresPackages = (descriptor.RequiresPackages ?? Array.Empty<string>()).ToArray(),
                Examples = UnityCliSerializer.Parse(UnityCliSerializer.Serialize(descriptor.Examples ?? Array.Empty<ToolExample>())),
                Target = string.IsNullOrEmpty(descriptor.Target) ? "editor" : descriptor.Target,
                PlayModeSupport = string.IsNullOrEmpty(descriptor.PlayModeSupport) ? "editor-only" : descriptor.PlayModeSupport,
                ContractProfile = "native",
                DefaultTimeoutMs = descriptor.DefaultTimeoutMs <= 0 ? 30000 : descriptor.DefaultTimeoutMs,
                MaxTimeoutMs = descriptor.MaxTimeoutMs <= 0 ? 600000 : descriptor.MaxTimeoutMs,
                RequestType = typeof(TReq),
                ResultType = typeof(TRes),
                CompletionContract = completion,
                InvokeAsync = async (request, context, cancellationToken) => (object)await tool.ExecuteAsync((TReq)request, context, cancellationToken)
            };
        }

        /// <summary>built-in/Extension 只信任生成 registry；自定义 Tool 必须提供完整机器契约。</summary>
        private static UnityCliToolCompletionContract ResolveToolCompletionContract(ToolDescriptor descriptor, string ownership)
        {
            UnityCliToolCompletionContract generated;
            if (UnityCliToolCompletionRegistry.TryGet(descriptor.Name, out generated))
            {
                var generatedExtension = generated.DescriptorOwner.StartsWith("extension:", StringComparison.Ordinal);
                if ((generatedExtension && !string.Equals(ownership, "extension", StringComparison.Ordinal))
                    || (!generatedExtension && !string.Equals(generated.DescriptorOwner, ownership, StringComparison.Ordinal)))
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
                custom.CompletionDriverId, mutation ? "required" : "none", mutation);
        }

        /// <summary>operation coordinator 在主线程解析自定义 Tool 的真实 contract。</summary>
        private static UnityCliToolCompletionContract ResolveRegisteredCustomToolCompletionContract(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return null;
            RegisteredTool definition;
            return ReadPublishedProjectToolCatalog().Tools.TryGetValue(toolName, out definition) ? definition.CompletionContract : null;
        }

        private static string ProjectToolGroupFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "custom";
            var dot = name.IndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : "custom";
        }

        private static object ProjectToolList(UnityCli.Protocol.BridgeRequest request)
        {
            var catalog = ReadPublishedProjectToolCatalog();
            return new Dictionary<string, object>
            {
                { "tools", catalog.Tools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal).Select(ProjectToolDescriptorData).ToList() },
                { "logPath", LogPath() }
            };
        }

        private sealed class ProjectToolRunArgs
        {
            public string Name { get; set; }
            [UnityCliAnyJson] public object Input { get; set; }
            public string ArtifactDir { get; set; }
            public string ProjectPath { get; set; }
        }

        private static async ValueTask<object> ProjectToolRun(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            var catalog = ReadPublishedProjectToolCatalog();
            var args = request.DeserializeArgs<ProjectToolRunArgs>();
            var name = args.Name;
            if (string.IsNullOrEmpty(name) || !catalog.Tools.ContainsKey(name))
            {
                throw new BridgeCommandException("E_TOOL_NOT_FOUND", "Tool not found: " + name, "Run unitycli tools group --instance <id|projectPath|alias> or tools search <keyword> to inspect the connected instance catalog.");
            }

            var definition = catalog.Tools[name];
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
            if (!EditorApplication.isPlaying && string.Equals(definition.PlayModeSupport, "runtime-only", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_TOOL_PLAYMODE_UNAVAILABLE",
                    "Tool " + name + " is runtime-only and cannot run outside Play Mode.",
                    new Dictionary<string, object>
                    {
                        { "toolName", name },
                        { "playModeSupport", definition.PlayModeSupport },
                        { "isPlaying", false }
                    },
                    new[] { "Enter Play Mode before running this Tool." });
            }

            // If the tool has Target=runtime, reject from project-tool.run and suggest runtime-tool.run
            if (definition.Target == "runtime")
            {
                throw new BridgeCommandException("E_RUNTIME_TOOL_TARGET_MISMATCH",
                    "Tool " + name + " has Target=runtime and must be executed via the runtime path (runtime-tool.run).",
                    new Dictionary<string, object> { { "toolName", name }, { "toolTarget", definition.Target } },
                    new[] { "Use runtime-tool.run <name> or tools run <name> with a runtime target to execute this tool in Play Mode." });
            }
            var dryRun = request.Execution != null && request.Execution.DryRun == true;
            var timeoutMs = request.ExecutionTimeoutMs(definition.DefaultTimeoutMs, definition.MaxTimeoutMs);

            var inputJson = request.RawJsonArg("input") ?? "{}";
            var schemaError = ValidateProjectToolInput(definition, inputJson);
            if (schemaError != null) throw SchemaValidationException(schemaError);

            var artifactDir = string.IsNullOrWhiteSpace(args.ArtifactDir) ? null : ResolveArtifactDir(args.ArtifactDir);
            object result;
            object parsedInput;
            UnityCliProjectToolContext executionContext = null;
            try
            {
                WriteLog("custom_tool_run requestId=" + request.RequestId + " name=" + name + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
                using (var toolTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    toolTimeout.CancelAfter(timeoutMs);
                    parsedInput = UnityCliSerializer.Deserialize(inputJson, definition.RequestType);
                    var effectPreparation = PrepareToolEffect(request, definition.CompletionContract, parsedInput);
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
                    result = await definition.InvokeAsync(parsedInput, executionContext, toolTimeout.Token);
                    if (toolTimeout.IsCancellationRequested && !executionContext.EffectCommitted)
                        throw new OperationCanceledException(toolTimeout.Token);
                }
            }
            catch (BridgeCommandException bce)
            {
                WriteLog("custom_tool_error requestId=" + request.RequestId + " name=" + name + " error=" + bce.Code);
                if (executionContext != null && executionContext.EffectCommitted
                    && (definition.OfficialAttributeBinding != null || bce.Code != "E_EXECUTION_TIMEOUT"))
                    throw PostEffectExecutionTimeout(name, "invoke", ProjectToolFailureReason(definition, bce.Code + ": " + bce.Message));
                throw;
            }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException ?? exception;
                var innerBce = inner as BridgeCommandException;
                if (innerBce != null)
                {
                    WriteLog("custom_tool_error requestId=" + request.RequestId + " name=" + name + " error=" + innerBce.Code);
                    if (executionContext != null && executionContext.EffectCommitted
                        && (definition.OfficialAttributeBinding != null || innerBce.Code != "E_EXECUTION_TIMEOUT"))
                        throw PostEffectExecutionTimeout(name, "invoke", ProjectToolFailureReason(definition, innerBce.Code + ": " + innerBce.Message));
                    throw innerBce;
                }
                if (inner is OperationCanceledException)
                {
                    if (executionContext != null && executionContext.EffectCommitted)
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "The immutable request deadline elapsed after the Tool crossed its effect barrier.", new Dictionary<string, object> { { "stage", "wait" }, { "effectStarted", true } }, new[] { "Inspect the affected business state before submitting a new request." });
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "The immutable request deadline elapsed before the Tool reached its terminal proof.", "Inspect editor.state before submitting a new request.");
                }
                if (executionContext != null && executionContext.EffectCommitted)
                    throw PostEffectExecutionTimeout(name, "invoke", ProjectToolFailureReason(definition, inner.GetType().Name + ": " + inner.Message));
                throw new BridgeCommandException("E_TOOL_EXECUTION_FAILED", inner.GetType().Name + ": " + inner.Message, "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }
            catch (OperationCanceledException)
            {
                if (executionContext != null && executionContext.EffectCommitted)
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "The immutable request deadline elapsed after the Tool crossed its effect barrier.", new Dictionary<string, object> { { "stage", "wait" }, { "effectStarted", true } }, new[] { "Inspect the affected business state before submitting a new request." });
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "The immutable request deadline elapsed before the Tool reached its terminal proof.", "Inspect editor.state before submitting a new request.");
            }
            catch (Exception exception)
            {
                if (executionContext != null && executionContext.EffectCommitted)
                    throw PostEffectExecutionTimeout(name, "invoke", ProjectToolFailureReason(definition, exception.GetType().Name + ": " + exception.Message));
                throw new BridgeCommandException("E_TOOL_EXECUTION_FAILED", exception.GetType().Name + ": " + exception.Message, "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }

            // method 返回后仍可能在序列化 getter、schema 或 completion driver 中失败；整个 postcondition 区域必须服从同一 effect 边界。
            try
            {
                var outputJson = UnityCliSerializer.Serialize(result);
                var outputError = ValidateProjectToolOutput(definition, outputJson);
                if (outputError != null)
                {
                    throw new BridgeCommandException(
                        "E_TOOL_COMPLETION_PROOF_INVALID",
                        "Tool result failed its derived output schema at " + outputError.Path + ".",
                        new Dictionary<string, object> { { "toolName", definition.Name }, { "path", outputError.Path }, { "expected", outputError.Expected }, { "actual", outputError.Actual } },
                        new[] { "Fix the Tool result DTO or completion driver before retrying." });
                }
                var terminalData = CompleteProjectToolSuccess(request, definition, result);
                RecordToolCompletionEvidence(request, definition, result, terminalData);
                return terminalData;
            }
            catch (BridgeCommandException exception)
            {
                if (executionContext != null && executionContext.EffectCommitted
                    && (definition.OfficialAttributeBinding != null || exception.Code != "E_EXECUTION_TIMEOUT"))
                    throw PostEffectExecutionTimeout(name, "postcondition", ProjectToolFailureReason(definition, exception.Code + ": " + exception.Message));
                throw;
            }
            catch (Exception exception)
            {
                if (executionContext != null && executionContext.EffectCommitted)
                    throw PostEffectExecutionTimeout(name, "postcondition", ProjectToolFailureReason(definition, exception.GetType().Name + ": " + exception.Message));
                throw new BridgeCommandException(
                    "E_TOOL_COMPLETION_PROOF_INVALID",
                    "Tool result could not be serialized or proven against its declared output contract.",
                    new Dictionary<string, object> { { "toolName", definition.Name }, { "stage", "postcondition" } },
                    new[] { "Fix the Tool result DTO or completion driver before retrying." });
            }
        }

        /// <summary>内部 raw discovery 需要 completion contract；公开投影与 static capture 会移除此字段。</summary>
        private static Dictionary<string, object> CompletionContractData(UnityCliToolCompletionContract contract)
        {
            return new Dictionary<string, object>
            {
                { "executionClass", contract.ExecutionClass },
                { "completionPredicate", contract.CompletionPredicate },
                { "proofSource", contract.ProofSource },
                { "reloadPolicy", contract.ReloadPolicy },
                { "timeoutError", contract.TimeoutError },
                { "outcomeUnknownPolicy", contract.OutcomeUnknownPolicy },
                { "completionDriverId", contract.CompletionDriverId },
                { "journalPolicy", contract.JournalPolicy },
                { "mutation", contract.Mutation }
            };
        }

        /// <summary>effect 后无法确认终态的异常统一收敛为执行超时。</summary>
        private static BridgeCommandException PostEffectExecutionTimeout(string toolName, string stage, string reason)
        {
            return new BridgeCommandException(
                "E_EXECUTION_TIMEOUT",
                "The Tool crossed its effect barrier but did not produce a proven terminal result.",
                new Dictionary<string, object> { { "toolName", toolName }, { "stage", stage }, { "reason", reason } },
                new[] { "Inspect the affected business state before submitting a new request." });
        }

        private static Dictionary<string, object> ProjectToolDescriptorData(RegisteredTool definition)
        {
            var data = new Dictionary<string, object>
            {
                { "name", definition.Name }, { "summary", definition.Summary }, { "description", definition.Description },
                { "group", definition.Group }, { "source", definition.Source },
                { "inputSchema", UnityCliSerializer.Parse(definition.InputSchema) }, { "outputSchema", UnityCliSerializer.Parse(definition.OutputSchema) },
                { "destructiveHint", definition.DestructiveHint },
                { "supportsDryRun", definition.SupportsDryRun },
                { "contractProfile", definition.ContractProfile },
                { "target", definition.Target }, { "availabilityScope", definition.AvailabilityScope }, { "playModeSupport", definition.PlayModeSupport },
                { "examples", definition.Examples },
                { "defaultTimeoutMs", definition.DefaultTimeoutMs },
                { "completionContract", CompletionContractData(definition.CompletionContract) },
                { "available", ToolAvailableNow(definition) }
            };
            if (!(bool)data["available"])
                data["unavailableReason"] = ToolUnavailableReason(definition);
            if (definition.SideEffects != null && definition.SideEffects.Length > 0) data["sideEffects"] = definition.SideEffects;
            if (definition.Permissions != null && definition.Permissions.Length > 0) data["permissions"] = definition.Permissions;
            if (definition.RequiresPackages != null && definition.RequiresPackages.Length > 0) data["requiresPackages"] = definition.RequiresPackages;
            return data;
        }

        /// <summary>目录始终显示 Tool；available 只描述当前 mode/API 状态，不参与过滤。</summary>
        private static bool ToolAvailableNow(RegisteredTool definition)
        {
            if (BuildProfileApiRequired(definition) && BuildToolSupport.BuildProfileType() == null)
                return false;
            if (EditorElementCaptureApiRequired(definition) && !EditorElementCaptureApiAvailable())
                return false;
            if (definition.Target == "runtime" || definition.PlayModeSupport == "runtime-only")
                return EditorApplication.isPlaying && !IsPlayModeChanging();
            if (definition.PlayModeSupport == "editor-only")
                return !EditorApplication.isPlayingOrWillChangePlaymode && !IsPlayModeChanging();
            return !IsPlayModeChanging();
        }

        /// <summary>按执行门禁优先级返回与 available=false 对应的稳定原因。</summary>
        private static string ToolUnavailableReason(RegisteredTool definition)
        {
            if (definition.Target == "runtime" || definition.PlayModeSupport == "runtime-only")
                return "E_RUNTIME_NOT_READY: Enter Play Mode before executing this Tool.";
            if (definition.PlayModeSupport == "editor-only" && EditorApplication.isPlayingOrWillChangePlaymode)
                return "E_TOOL_PLAYMODE_UNAVAILABLE: Exit Play Mode before executing this editor-only Tool.";
            if (BuildProfileApiRequired(definition) && BuildToolSupport.BuildProfileType() == null)
                return "E_BUILD_PROFILE_API_UNAVAILABLE: This Unity Editor version does not expose the Build Profile API.";
            if (EditorElementCaptureApiRequired(definition) && !EditorElementCaptureApiAvailable())
                return "E_EDITOR_ELEMENT_CAPTURE_UNAVAILABLE: screenshot.editor-element requires Unity 6000.7 or newer because VisualElement CaptureToRenderTexture is unavailable in this Editor.";
            return "E_UNITY_TRANSITIONING: Wait until the Editor mode transition finishes.";
        }

        /// <summary>这些 Tool 在所有版本注册；版本差异仅通过 availability 与执行错误表达。</summary>
        private static bool BuildProfileApiRequired(RegisteredTool definition)
        {
            return definition.Name == "build.profile.create"
                || definition.Name == "build.profile.modify"
                || definition.Name == "build.profile.switch";
        }

        private static bool EditorElementCaptureApiRequired(RegisteredTool definition)
        {
            return definition.Name == "screenshot.editor-element";
        }

        /// <summary>UI Toolkit capture API availability由Unity编译符号精确证明。</summary>
        private static bool EditorElementCaptureApiAvailable()
        {
#if UNITY_6000_7_OR_NEWER
            return true;
#else
            return false;
#endif
        }

        private static object CompleteProjectToolSuccess(UnityCli.Protocol.BridgeRequest request, RegisteredTool definition, object result)
        {
            WriteLog("custom_tool_completed requestId=" + request.RequestId + " name=" + definition.Name);
            return new Dictionary<string, object> { { "result", result } };
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
    }
}
#endif
