#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
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
        private static UnityCliPublishedToolCatalog publishedProjectToolCatalog;
        private static UnityCliToolCatalogHealthSnapshot projectToolCatalogHealth = new UnityCliToolCatalogHealthSnapshot(
            false, 0, "catalog was not initialized", "Let Unity compile and reload the domain before retrying.");
        private static int projectToolCatalogConstructionCount;
        internal static IUnityCliToolCatalogSnapshotReader ProjectToolCatalogSnapshotReader { get; } = new PublishedToolCatalogSnapshotReader();
        internal static IUnityCliToolCatalogHealthSnapshotReader ProjectToolCatalogHealthSnapshotReader { get; } = new PublishedToolCatalogHealthSnapshotReader();

        private sealed class PublishedToolCatalogSnapshotReader : IUnityCliToolCatalogSnapshotReader
        {
            public UnityCliToolCatalogSnapshot Read() { return ReadPublishedProjectToolCatalogSnapshot(); }
        }

        private sealed class PublishedToolCatalogHealthSnapshotReader : IUnityCliToolCatalogHealthSnapshotReader
        {
            public UnityCliToolCatalogHealthSnapshot Read() { return Volatile.Read(ref projectToolCatalogHealth); }
        }

        private static void RegisterProjectTool(UnityCliToolCatalogBuilder builder, RegisteredTool definition, UnityCliToolProviderKind providerKind)
        {
            if (builder == null) throw new ArgumentNullException("builder");
            if (definition == null) throw new ArgumentNullException("definition");
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
                    builder.RecordProviderMetadata(assembly.GetName().FullName);
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
                ownership.ValidateComplete();

                var snapshot = PublishProjectToolCatalog(builder, (long)Math.Ceiling((DateTime.UtcNow - startedAt).TotalMilliseconds));
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
                var failure = BoundedCatalogDiagnostic(ExceptionChain(exception));
                Volatile.Write(ref projectToolCatalogHealth, new UnityCliToolCatalogHealthSnapshot(
                    false, 0, failure, "Fix the Tool definition, then let Unity compile and reload the domain before retrying."));
                WriteLog("tool_catalog_initialization_failed error=" + failure);
            }
        }

        private static bool ProjectToolCatalogReady()
        {
            return Volatile.Read(ref publishedProjectToolCatalog) != null;
        }

        /// <summary>候选完整 Publish 后才替换唯一 slot；任何第 N 项失败都保留既有 publication。</summary>
        private static UnityCliToolCatalogSnapshot PublishProjectToolCatalog(UnityCliToolCatalogBuilder builder, long elapsedMilliseconds)
        {
            var candidate = builder.Publish(elapsedMilliseconds);
            Volatile.Write(ref publishedProjectToolCatalog, candidate);
            Volatile.Write(ref projectToolCatalogHealth, new UnityCliToolCatalogHealthSnapshot(
                true, candidate.Snapshot.ToolCount, string.Empty, string.Empty));
            return candidate.Snapshot;
        }

        /// <summary>执行入口读取唯一 published executable table；失败 domain 内绝不重建。</summary>
        private static UnityCliPublishedToolCatalog ReadPublishedProjectToolCatalog()
        {
            var catalog = Volatile.Read(ref publishedProjectToolCatalog);
            if (catalog != null) return catalog;
            var health = ProjectToolCatalogHealthSnapshotReader.Read();
            throw new BridgeCommandException(
                "E_TOOL_REGISTRY_UNHEALTHY",
                "Tool catalog initialization failed in the current Unity domain: " + health.Failure,
                new Dictionary<string, object> { { "registry", "tools" } },
                new[] { health.NextAction },
                true,
                1000);
        }

        private static UnityCliToolCatalogSnapshot ReadPublishedProjectToolCatalogSnapshot()
        {
            return ReadPublishedProjectToolCatalog().Snapshot;
        }

        /// <summary>doctor 专用只读 health，不取得 invoker、schema 对象或 preflight 聚合。</summary>
        private static object ProjectToolCatalogHealth(UnityCli.Protocol.BridgeRequest request)
        {
            RequireProjectToolCatalogInputKeys(request);
            var health = ProjectToolCatalogHealthSnapshotReader.Read();
            var result = new Dictionary<string, object>
            {
                { "ready", health.Ready },
                { "toolCount", (long)health.ToolCount }
            };
            if (!health.Ready)
            {
                result["failure"] = BoundedCatalogDiagnostic(health.Failure);
                result["nextActions"] = new[] { health.NextAction };
            }
            return result;
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
            var providerKind = string.Equals(ownership.Source, "built-in", StringComparison.Ordinal)
                ? UnityCliToolProviderKind.BuiltIn
                : string.Equals(ownership.Source, "extension", StringComparison.Ordinal)
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

        private static RegisteredTool CreateRegisteredTool<TReq, TRes>(object instance, ToolDescriptor descriptor, RegistryToolOwnership ownership)
        {
            var tool = (IUnityCliTool<TReq, TRes>)instance;
            var completion = ResolveToolCompletionContract(descriptor, ownership);
            return new RegisteredTool(
                descriptor,
                ownership,
                UnityCliSchemaDeriver.DeriveSchema(typeof(TReq)),
                UnityCliSchemaDeriver.DeriveSchema(typeof(TRes)),
                string.IsNullOrEmpty(descriptor.Target) || descriptor.Target != "runtime" ? "editorSession" : "playModeSession",
                "native",
                typeof(TReq),
                typeof(TRes),
                completion,
                null,
                async (request, context, cancellationToken) => (object)await tool.ExecuteAsync((TReq)request, context, cancellationToken));
        }

        /// <summary>built-in/Extension 只信任生成 registry；自定义 Tool 必须提供完整机器契约。</summary>
        private static UnityCliToolCompletionContract ResolveToolCompletionContract(ToolDescriptor descriptor, RegistryToolOwnership ownership)
        {
            UnityCliToolCompletionContract generated;
            if (UnityCliToolCompletionRegistry.TryGet(descriptor.Name, out generated))
            {
                var generatedExtension = generated.DescriptorOwner.StartsWith("extension:", StringComparison.Ordinal);
                if ((generatedExtension && (!string.Equals(ownership.Source, "extension", StringComparison.Ordinal)
                        || !string.Equals(generated.DescriptorOwner, "extension:" + ownership.ExtensionId, StringComparison.Ordinal)))
                    || (!generatedExtension && !string.Equals(generated.DescriptorOwner, ownership.Source, StringComparison.Ordinal)))
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
                descriptor.Name, ownership.Source, "direct-terminal", custom.ExecutionClass, custom.CompletionPredicate,
                custom.ProofSource, custom.ReloadPolicy, custom.TimeoutError, custom.OutcomeUnknownPolicy,
                custom.CompletionDriverId, mutation ? "required" : "none", mutation);
        }

        /// <summary>operation coordinator 在主线程解析自定义 Tool 的真实 contract。</summary>
        private static UnityCliToolCompletionContract ResolveRegisteredCustomToolCompletionContract(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return null;
            RegisteredTool definition;
            return ReadPublishedProjectToolCatalog().ExecutableTools.TryGetValue(toolName, out definition) ? definition.CompletionContract : null;
        }

        private sealed class ProjectToolCatalogArgs
        {
            public string Group { get; set; }
            public string Keyword { get; set; }
            public string AfterName { get; set; }
            public int Limit { get; set; }
            public string ExpectedCatalogGeneration { get; set; }
            public string[] ReservedNames { get; set; }
            public string Name { get; set; }
        }

        /// <summary>continuation只绑定transport session与listener generation；Tool级identifier不参与stale判定。</summary>
        private static string ProjectToolCatalogGeneration()
        {
            return Sha256Hex(EditorSessionId() + "\n" + ListenerId + "\n" + CurrentListenerGeneration().ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>generation变化时 continuation 必须稳定失败，不能混合两代目录。</summary>
        private static void RequireProjectToolCatalogGeneration(ProjectToolCatalogArgs args, string generation)
        {
            if (args != null && (args.ExpectedCatalogGeneration ?? string.Empty).Length > 128)
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog generation exceeds its bounded string limit.");
            if (args != null && !string.IsNullOrEmpty(args.ExpectedCatalogGeneration)
                && !string.Equals(args.ExpectedCatalogGeneration, generation, StringComparison.Ordinal))
                throw new BridgeCommandException("E_TOOL_CATALOG_CHANGED", "The Tool catalog generation changed before this continuation page was read.", null,
                    new[] { "Restart this discovery query from its first page against the current Unity generation." });
        }

        /// <summary>package-local names由CLI冻结并传入，Bridge在筛选前执行全registry ownership检查。</summary>
        private static void RequireNoReservedProjectToolNames(UnityCliToolCatalogSnapshot catalog, ProjectToolCatalogArgs args)
        {
            var reservedNames = (args == null ? null : args.ReservedNames) ?? new string[0];
            if (reservedNames.Length > 4096 || reservedNames.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 256))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog reserved names exceed their bounded item or string limits.");
            var reserved = new HashSet<string>(reservedNames, StringComparer.Ordinal);
            var conflict = catalog.Descriptors.Select(tool => tool.Name).FirstOrDefault(reserved.Contains);
            if (conflict != null)
                throw new BridgeCommandException("E_TOOL_SCHEMA_CONFLICT", "Tool catalog conflict for " + conflict + ": package-local and live registrations use the same name.",
                    new Dictionary<string, object> { { "toolName", conflict }, { "reason", "package-local and live registrations use the same name" } },
                    new[] { "Fix the duplicate Tool registration in the selected Unity project, then retry discovery." });
        }

        private static IEnumerable<RegisteredToolDescriptor> ProjectToolCatalogDefinitions(UnityCliToolCatalogSnapshot catalog, bool runtimeOnly)
        {
            return catalog.Descriptors.Where(definition => !runtimeOnly || definition.Target == "runtime" || definition.Target == "both");
        }

        private static void RequireProjectToolCatalogInputKeys(UnityCli.Protocol.BridgeRequest request, params string[] allowed)
        {
            var input = request.Input as IDictionary<string, object>;
            if (input == null) throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog input must be an object.");
            var keys = new HashSet<string>(allowed, StringComparer.Ordinal);
            var unknown = input.Keys.FirstOrDefault(key => !keys.Contains(key));
            if (unknown != null) throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog input contains unknown field " + unknown + ".");
        }

        /// <summary>groups只读取一次published snapshot并返回group/count，不物化descriptor图。</summary>
        private static object ProjectToolCatalogGroups(UnityCli.Protocol.BridgeRequest request, bool runtimeOnly)
        {
            RequireProjectToolCatalogInputKeys(request, "expectedCatalogGeneration", "reservedNames");
            var catalog = ProjectToolCatalogSnapshotReader.Read();
            var args = request.DeserializeArgs<ProjectToolCatalogArgs>() ?? new ProjectToolCatalogArgs();
            RequireNoReservedProjectToolNames(catalog, args);
            var generation = ProjectToolCatalogGeneration();
            RequireProjectToolCatalogGeneration(args, generation);
            var groups = ProjectToolCatalogDefinitions(catalog, runtimeOnly)
                .GroupBy(definition => definition.Group, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => (object)new Dictionary<string, object> { { "name", group.Key }, { "toolCount", (long)group.Count() } })
                .ToList();
            if (groups.Count > 4096)
                throw new BridgeCommandException("E_TOOL_REGISTRY_UNHEALTHY", "Tool catalog exceeds the bounded group count.", null,
                    new[] { "Reduce invalid project Tool group fragmentation, then let Unity reload the domain." });
            return new Dictionary<string, object> { { "catalogGeneration", generation }, { "groups", groups } };
        }

        /// <summary>summary只读取一次published snapshot，先筛选和裁剪再构造有界路由字段。</summary>
        private static object ProjectToolCatalogSummary(UnityCli.Protocol.BridgeRequest request, bool runtimeOnly)
        {
            RequireProjectToolCatalogInputKeys(request, "group", "keyword", "afterName", "limit", "expectedCatalogGeneration", "reservedNames");
            var catalog = ProjectToolCatalogSnapshotReader.Read();
            var args = request.DeserializeArgs<ProjectToolCatalogArgs>() ?? new ProjectToolCatalogArgs();
            if (args.Limit < 1 || args.Limit > 101)
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog summary limit must be an integer in [1,101].");
            RequireNoReservedProjectToolNames(catalog, args);
            var generation = ProjectToolCatalogGeneration();
            RequireProjectToolCatalogGeneration(args, generation);
            if ((args.Group ?? string.Empty).Length > 256 || (args.AfterName ?? string.Empty).Length > 256
                || (args.Keyword ?? string.Empty).Length > 2048)
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog summary filters exceed their bounded string limits.");
            var tokens = (args.Keyword ?? string.Empty).Split('|').Select(token => token.Trim()).Where(token => token.Length > 0).ToArray();
            if (tokens.Length > 16 || tokens.Any(token => token.Length > 128))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog search accepts at most 16 keywords of at most 128 characters each.");
            var matches = ProjectToolCatalogDefinitions(catalog, runtimeOnly)
                .Where(definition => string.IsNullOrEmpty(args.Group) || string.Equals(definition.Group, args.Group, StringComparison.Ordinal))
                .Where(definition => tokens.Length == 0 || tokens.Any(token => ProjectToolCatalogMatches(definition, token)))
                .OrderBy(definition => definition.Name, StringComparer.Ordinal)
                .ToList();
            var remaining = matches.Where(definition => string.IsNullOrEmpty(args.AfterName) || string.CompareOrdinal(definition.Name, args.AfterName) > 0).ToList();
            var nativeNames = new HashSet<string>(catalog.Descriptors.Where(definition => definition.ContractProfile != "official-attribute").Select(definition => definition.Name), StringComparer.Ordinal);
            foreach (var reservedName in args.ReservedNames ?? new string[0]) nativeNames.Add(reservedName);
            var tools = remaining.Take(args.Limit).Select(definition => (object)ProjectToolRoutingSummaryData(definition, runtimeOnly, nativeNames)).ToList();
            return new Dictionary<string, object>
            {
                { "catalogGeneration", generation }, { "total", (long)matches.Count },
                { "hasMore", remaining.Count > args.Limit }, { "tools", tools }
            };
        }

        private static bool ProjectToolCatalogMatches(RegisteredToolDescriptor definition, string token)
        {
            return (definition.Name ?? string.Empty).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0
                || (definition.Summary ?? string.Empty).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0
                || (definition.Description ?? string.Empty).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0
                || (definition.Group ?? string.Empty).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>exact describe只读取一次published snapshot，并且至多传输指定name的一份完整descriptor。</summary>
        private static object ProjectToolCatalogDescribe(UnityCli.Protocol.BridgeRequest request, bool runtimeOnly)
        {
            RequireProjectToolCatalogInputKeys(request, "name");
            var catalog = ProjectToolCatalogSnapshotReader.Read();
            var args = request.DeserializeArgs<ProjectToolCatalogArgs>() ?? new ProjectToolCatalogArgs();
            if (string.IsNullOrWhiteSpace(args.Name) || args.Name.Length > 256)
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog describe name is required and must not exceed 256 characters.");
            var generation = ProjectToolCatalogGeneration();
            var definition = catalog.Descriptors.FirstOrDefault(tool => string.Equals(tool.Name, args.Name, StringComparison.Ordinal));
            var found = definition != null && (!runtimeOnly || definition.Target == "runtime" || definition.Target == "both");
            var result = new Dictionary<string, object> { { "catalogGeneration", generation }, { "found", found } };
            if (found)
            {
                var descriptor = ProjectToolDescriptorData(definition);
                if (Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(descriptor)) > 3 * 1024 * 1024)
                    throw new BridgeCommandException("E_TOOL_REGISTRY_UNHEALTHY", "Exact Tool descriptor exceeds the bounded transport size.", null,
                        new[] { "Reduce the Tool schema or examples, then let Unity reload the domain." });
                result["descriptor"] = descriptor;
            }
            return result;
        }

        private static Dictionary<string, object> ProjectToolRoutingSummaryData(RegisteredToolDescriptor definition, bool runtimeOnly, HashSet<string> nativeNames)
        {
            var commandName = definition.Name;
            if (definition.ContractProfile == "official-attribute" && definition.Name.StartsWith(OfficialAttributeToolPrefix, StringComparison.Ordinal))
            {
                var originalName = definition.Name.Substring(OfficialAttributeToolPrefix.Length);
                if (!nativeNames.Contains(originalName)) commandName = originalName;
            }
            var available = ProjectToolAvailableForCatalog(definition, runtimeOnly);
            var data = new Dictionary<string, object>
            {
                { "name", definition.Name }, { "summary", definition.Summary }, { "group", definition.Group },
                { "target", definition.Target }, { "contractProfile", definition.ContractProfile },
                { "commandName", commandName }
            };
            if (!available)
            {
                data["available"] = false;
                data["unavailableReason"] = ProjectToolUnavailableForCatalog(definition, runtimeOnly);
            }
            return data;
        }

        private static bool ProjectToolAvailableForCatalog(RegisteredToolDescriptor definition, bool runtimeOnly)
        {
            return runtimeOnly ? playModeReady : ToolAvailableNow(definition);
        }

        private static string ProjectToolUnavailableForCatalog(RegisteredToolDescriptor definition, bool runtimeOnly)
        {
            return runtimeOnly && !playModeReady
                ? "E_RUNTIME_NOT_READY: Enter Play Mode before executing this Tool."
                : ToolUnavailableReason(definition);
        }

        private static bool IsProjectToolCatalogReadCommand(string command)
        {
            return command == "project-tool.health" || command == "project-tool.groups" || command == "project-tool.summary" || command == "project-tool.describe" || command == "project-tool.list"
                || command == "runtime-tool.groups" || command == "runtime-tool.summary" || command == "runtime-tool.describe" || command == "runtime-tool.list";
        }

        private static object ProjectToolGroups(UnityCli.Protocol.BridgeRequest request) { return ProjectToolCatalogGroups(request, false); }
        private static object ProjectToolSummary(UnityCli.Protocol.BridgeRequest request) { return ProjectToolCatalogSummary(request, false); }
        private static object ProjectToolDescribe(UnityCli.Protocol.BridgeRequest request) { return ProjectToolCatalogDescribe(request, false); }
        private static object RuntimeToolGroups(UnityCli.Protocol.BridgeRequest request) { InitializeRuntimeExecutor(); return ProjectToolCatalogGroups(request, true); }
        private static object RuntimeToolSummary(UnityCli.Protocol.BridgeRequest request) { InitializeRuntimeExecutor(); return ProjectToolCatalogSummary(request, true); }
        private static object RuntimeToolDescribe(UnityCli.Protocol.BridgeRequest request) { InitializeRuntimeExecutor(); return ProjectToolCatalogDescribe(request, true); }

        /// <summary>完整descriptor导出仅供显式doctor/static capture，不进入公开discovery。</summary>
        private static object ProjectToolList(UnityCli.Protocol.BridgeRequest request)
        {
            var catalog = ReadPublishedProjectToolCatalogSnapshot();
            return new Dictionary<string, object>
            {
                { "tools", catalog.Descriptors.Select(ProjectToolDescriptorData).ToList() },
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
            if (string.IsNullOrEmpty(name) || !catalog.ExecutableTools.ContainsKey(name))
            {
                throw new BridgeCommandException("E_TOOL_NOT_FOUND", "Tool not found: " + name, "Run unitycli tools group --instance <id|projectPath|alias> or tools search <keyword> to inspect the connected instance catalog.");
            }

            var definition = catalog.ExecutableTools[name];
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

        private static Dictionary<string, object> CompletionContractData(PublishedToolCompletionContract contract)
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

        private static Dictionary<string, object> ProjectToolDescriptorData(RegisteredToolDescriptor definition)
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
                { "examples", definition.CloneExamples() },
                { "defaultTimeoutMs", definition.DefaultTimeoutMs },
                { "completionContract", CompletionContractData(definition.CompletionContract) },
                { "available", ToolAvailableNow(definition) }
            };
            if (!(bool)data["available"])
                data["unavailableReason"] = ToolUnavailableReason(definition);
            if (definition.SideEffects.Count > 0) data["sideEffects"] = definition.SideEffects.ToArray();
            if (definition.Permissions.Count > 0) data["permissions"] = definition.Permissions.ToArray();
            if (definition.RequiresPackages.Count > 0) data["requiresPackages"] = definition.RequiresPackages.ToArray();
            return data;
        }

        /// <summary>目录始终显示 Tool；available 只描述当前 mode/API 状态，不参与过滤。</summary>
        private static bool ToolAvailableNow(RegisteredToolDescriptor definition)
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
        private static string ToolUnavailableReason(RegisteredToolDescriptor definition)
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
        private static bool BuildProfileApiRequired(RegisteredToolDescriptor definition)
        {
            return definition.Name == "build.profile.create"
                || definition.Name == "build.profile.modify"
                || definition.Name == "build.profile.switch";
        }

        private static bool EditorElementCaptureApiRequired(RegisteredToolDescriptor definition)
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

    }
}
#endif
