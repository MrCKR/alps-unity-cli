#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
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
        private static readonly byte[] projectToolCatalogCursorSecret = CreateProjectToolCatalogCursorSecret();
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
            var namespaceFailure = ReservedNamespaceFailure(definition);
            if (namespaceFailure != null)
            {
                builder.RecordToolFailure(definition.Name, namespaceFailure);
                return;
            }
            builder.Add(definition, providerKind);
        }

        /// <summary>保留namespace只属于official attribute投影；违规实现只被禁用，不掠夺合法权威名称。</summary>
        private static string ReservedNamespaceFailure(RegisteredTool definition)
        {
            var officialAttribute = string.Equals(definition.ContractProfile, "official-attribute", StringComparison.Ordinal);
            var usesPrefix = definition.Name.StartsWith(OfficialAttributeToolPrefix, StringComparison.Ordinal);
            var usesGroup = string.Equals(definition.Group, OfficialAttributeToolGroup, StringComparison.Ordinal);
            if (officialAttribute && (!usesPrefix || !usesGroup))
                return "Official attribute Tool must use the reserved UnityPipeline_ name prefix and UnityPipeline group: " + definition.Name + ".";
            if (!officialAttribute && (usesPrefix || usesGroup))
                return "Native Tool cannot use the reserved UnityPipeline_ name prefix or UnityPipeline group: " + definition.Name + ".";
            return null;
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
                CollectProjectToolCatalog(AppDomain.CurrentDomain.GetAssemblies(), builder);
                PublishProjectToolCatalog(builder, (long)Math.Ceiling((DateTime.UtcNow - startedAt).TotalMilliseconds));
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

        /// <summary>生产与大目录fixture共享同一assembly筛选、owner解析、类型扫描和pending table构造。</summary>
        private static void CollectProjectToolCatalog(Assembly[] assemblies, UnityCliToolCatalogBuilder builder)
        {
            if (assemblies == null) throw new ArgumentNullException("assemblies");
            if (builder == null) throw new ArgumentNullException("builder");
            var officialRoots = ConfigureOfficialAuthoringApi(assemblies);
            var candidates = ToolProviderAssemblies(assemblies, officialRoots);
            builder.BeginDiscovery(assemblies.Length, candidates.Count);
            var ownership = RegistryOwnershipResolver.Load(candidates);
            foreach (var assembly in candidates)
            {
                var assemblyIdentity = assembly.GetName().FullName;
                builder.RecordProviderMetadata(assemblyIdentity);
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (Exception exception)
                {
                    // provider 自身无法完整枚举时排除它已贡献的全部候选，其他可信 provider 继续。
                    builder.RecordProviderFailure(assemblyIdentity, ProviderFailureDiagnostic(assembly, exception));
                    WriteLog("tool_provider_isolated assembly=" + assemblyIdentity + " error=" + ProviderFailureDiagnostic(assembly, exception));
                    continue;
                }
                foreach (var type in types)
                {
                    if (type == null) continue;
                    try
                    {
                        TryRegisterInterfaceProjectTool(builder, ownership, assembly, type);
                        TryRegisterOfficialAttributeTools(builder, ownership, assembly, type);
                    }
                    catch (Exception exception)
                    {
                        // 未归类失败无法归因到单个候选：按 provider 级隔离，撤销它已贡献的候选并继续其他 provider。
                        builder.RecordProviderFailure(assemblyIdentity, ProviderFailureDiagnostic(assembly, exception));
                        WriteLog("tool_provider_isolated assembly=" + assemblyIdentity + " error=" + ProviderFailureDiagnostic(assembly, exception));
                        break;
                    }
                }
            }
            ownership.ValidateComplete();
        }

        /// <summary>provider 枚举失败的诊断只取有界异常链，不展开完整 LoaderExceptions 集合。</summary>
        private static string ProviderFailureDiagnostic(Assembly assembly, Exception exception)
        {
            var text = "Unable to inspect every type in Tool provider assembly " + (assembly == null ? "<unknown>" : assembly.FullName) + ". " + ExceptionChain(exception);
            var loader = exception as ReflectionTypeLoadException;
            if (loader != null && loader.LoaderExceptions != null)
            {
                var first = loader.LoaderExceptions.FirstOrDefault(item => item != null);
                if (first != null) text += " First loader exception: " + first.GetType().Name + ": " + first.Message;
            }
            return BoundedCatalogDiagnostic(text);
        }

        private static bool ProjectToolCatalogReady()
        {
            return Volatile.Read(ref publishedProjectToolCatalog) != null;
        }

        /// <summary>候选完整 Publish 后才替换唯一 slot；任何第 N 项失败都保留既有 publication。</summary>
        private static UnityCliToolCatalogSnapshot PublishProjectToolCatalog(UnityCliToolCatalogBuilder builder, long elapsedMilliseconds)
        {
            var candidate = builder.Publish(elapsedMilliseconds, WriteProjectToolCatalogInitializedLog);
            Volatile.Write(ref publishedProjectToolCatalog, candidate);
            Volatile.Write(ref projectToolCatalogHealth, new UnityCliToolCatalogHealthSnapshot(
                true, candidate.Snapshot.ToolCount, string.Empty, string.Empty,
                candidate.Snapshot.Diagnostics, candidate.Snapshot.DiagnosticCount));
            return candidate.Snapshot;
        }

        /// <summary>候选提交前写入唯一 aggregate success log；失败时旧 publication 保持不变。</summary>
        private static void WriteProjectToolCatalogInitializedLog(UnityCliToolCatalogSnapshot snapshot)
        {
            WriteLog("tool_catalog_initialized assemblies=" + snapshot.AssemblyCount.ToString(CultureInfo.InvariantCulture)
                + " providers=" + snapshot.ProviderAssemblyCount.ToString(CultureInfo.InvariantCulture)
                + " tools=" + snapshot.ToolCount.ToString(CultureInfo.InvariantCulture)
                + " builtIn=" + snapshot.BuiltInToolCount.ToString(CultureInfo.InvariantCulture)
                + " extension=" + snapshot.ExtensionToolCount.ToString(CultureInfo.InvariantCulture)
                + " projectInterface=" + snapshot.ProjectInterfaceToolCount.ToString(CultureInfo.InvariantCulture)
                + " officialAttribute=" + snapshot.OfficialAttributeToolCount.ToString(CultureInfo.InvariantCulture)
                + " isolated=" + snapshot.DiagnosticCount.ToString(CultureInfo.InvariantCulture)
                + " elapsedMs=" + snapshot.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            if (snapshot.DiagnosticCount > 0)
                WriteLog("tool_catalog_isolated_registrations count=" + snapshot.DiagnosticCount.ToString(CultureInfo.InvariantCulture)
                    + " detail=" + IsolatedRegistrationSummary(snapshot));
        }

        /// <summary>隔离诊断只写一条有界汇总，不逐工具同步追加日志。</summary>
        private static string IsolatedRegistrationSummary(UnityCliToolCatalogSnapshot snapshot)
        {
            var parts = snapshot.Diagnostics.Take(8).Select(item => item.Kind + ":" + item.Name + "(" + string.Join(",", item.Sources.ToArray()) + ")").ToArray();
            var text = string.Join(" | ", parts);
            if (snapshot.DiagnosticTruncated) text += " | truncated";
            return BoundedCatalogDiagnostic(text);
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
                { "toolCount", (long)health.ToolCount },
                { "isolatedCount", (long)health.DiagnosticCount },
                { "isolatedTruncated", health.DiagnosticTruncated }
            };
            if (!health.Ready)
            {
                result["failure"] = BoundedCatalogDiagnostic(health.Failure);
                result["nextActions"] = new[] { health.NextAction };
            }
            else if (health.DiagnosticCount > 0)
            {
                result["isolated"] = health.Diagnostics.Select(ToolCatalogDiagnosticData).ToList();
            }
            return result;
        }

        /// <summary>公开诊断 DTO 只含编译元数据身份，不带源码路径、堆栈或内部路由字段。</summary>
        private static Dictionary<string, object> ToolCatalogDiagnosticData(UnityCliToolCatalogDiagnostic diagnostic)
        {
            var data = new Dictionary<string, object>
            {
                { "kind", diagnostic.Kind },
                { "name", diagnostic.Name },
                { "reason", diagnostic.Reason },
                { "sources", diagnostic.Sources.ToArray() }
            };
            if (diagnostic.Truncated) data["sourcesTruncated"] = true;
            return data;
        }

        /// <summary>名称不在可执行目录时按同一冻结判定区分冲突、被拒绝与真正不存在；无可归因结论时返回 null。</summary>
        private static BridgeCommandException CatalogToolUnavailable(UnityCliPublishedToolCatalog catalog, string name)
        {
            if (catalog == null || string.IsNullOrEmpty(name)) return null;
            var index = catalog.DiagnosticIndex;
            if (index.IsConflicted(name))
                return new BridgeCommandException(
                    "E_TOOL_NAME_CONFLICT",
                    "Tool name is registered by multiple implementations in the current Unity domain: " + name + ".",
                    CatalogDiagnosticDetails(index, name),
                    CatalogDiagnosticNextActions(),
                    false,
                    0);
            string reason;
            if (index.TryRejectedReason(name, out reason))
                return new BridgeCommandException(
                    "E_TOOL_REGISTRATION_FAILED",
                    "Tool was rejected while building the current Unity domain catalog: " + name + ". " + reason,
                    CatalogDiagnosticDetails(index, name),
                    CatalogDiagnosticNextActions(),
                    false,
                    0);
            if (catalog.Snapshot.DiagnosticTruncated || index.NamesTruncated)
                return new BridgeCommandException(
                    "E_TOOL_REGISTRATION_FAILED",
                    "Tool is not in the executable catalog and this domain reported more registration faults than its bounded diagnostic list can show: " + name + ".",
                    CatalogDiagnosticDetails(index, name),
                    CatalogDiagnosticNextActions(),
                    false,
                    0);
            return null;
        }

        /// <summary>details 只列有界来源，不暴露 listener、端口或项目路径。</summary>
        private static Dictionary<string, object> CatalogDiagnosticDetails(UnityCliToolCatalogDiagnosticIndex index, string name)
        {
            var details = new Dictionary<string, object> { { "toolName", name } };
            var diagnostic = index.Entries.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
            if (diagnostic != null)
            {
                details["reason"] = diagnostic.Reason;
                details["sources"] = diagnostic.Sources.ToArray();
                if (diagnostic.Truncated) details["sourcesTruncated"] = true;
            }
            return details;
        }

        private static string[] CatalogDiagnosticNextActions()
        {
            return new[]
            {
                "Fix the reported Tool definition or duplicate command name in the selected Unity project, then let Unity compile and reload the domain before rediscovering the catalog.",
                "Run unitycli doctor to read the bounded catalog isolation summary for this instance."
            };
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
                builder.RecordToolFailure(type.FullName, ExceptionChain(new InvalidOperationException("Tool implementation cannot be instantiated: " + type.FullName + ".", e)));
                return;
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
                builder.RecordToolFailure(type.FullName, ExceptionChain(new InvalidOperationException("Tool descriptor failed: " + type.FullName + ".", e)));
                return;
            }
            if (descriptor == null || string.IsNullOrEmpty(descriptor.Name))
            {
                builder.RecordToolFailure(type.FullName, "Tool descriptor is incomplete: " + type.FullName + ".");
                return;
            }

            RegisteredTool definition;
            RegistryToolOwnership ownership;
            try
            {
                ownership = ownershipResolver.Resolve(type, descriptor.Name);
            }
            catch (Exception exception)
            {
                builder.RecordToolFailure(descriptor.Name, ExceptionChain(new InvalidOperationException("Tool ownership could not be resolved: " + type.FullName + ".", exception)));
                return;
            }
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
                builder.RecordToolFailure(descriptor.Name, "Tool registration failed: " + type.FullName + ": " + diagnostics);
                return;
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
            public int Limit { get; set; }
            public string ExpectedCatalogGeneration { get; set; }
            public string[] ReservedNames { get; set; }
            public string[] LocalNames { get; set; }
            public string CursorContext { get; set; }
            public string CursorToken { get; set; }
            public string Name { get; set; }
            public string FallbackName { get; set; }
        }

        /// <summary>continuation只绑定transport session与listener generation；Tool级identifier不参与stale判定。</summary>
        private static string ProjectToolCatalogGeneration()
        {
            return Sha256Hex(Encoding.UTF8.GetBytes(EditorSessionId() + "\n" + ListenerId + "\n" + CurrentListenerGeneration().ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>generation变化时 continuation 必须稳定失败，不能混合两代目录。</summary>
        private static void RequireProjectToolCatalogGeneration(ProjectToolCatalogArgs args, string generation)
        {
            if (args != null && Utf8ByteCount(args.ExpectedCatalogGeneration) > 128)
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
            if (reservedNames.Length > 4096 || reservedNames.Any(name => string.IsNullOrWhiteSpace(name) || Utf8ByteCount(name) > 256))
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
            var data = new Dictionary<string, object> { { "catalogGeneration", generation }, { "groups", groups } };
            RequireCatalogDataBudget(data, 2 * 1024 * 1024, "Tool catalog groups exceed the bounded UTF-8 transport size.");
            return data;
        }

        /// <summary>summary由目标domain签发opaque continuation；CLI不持有可编辑边界。</summary>
        private static object ProjectToolCatalogSummary(UnityCli.Protocol.BridgeRequest request, bool runtimeOnly)
        {
            RequireProjectToolCatalogInputKeys(request, "group", "keyword", "limit", "expectedCatalogGeneration", "reservedNames",
                "localNames", "cursorContext", "cursorToken");
            var summaryInput = request.Input as IDictionary<string, object>;
            if (!summaryInput.ContainsKey("reservedNames") || !summaryInput.ContainsKey("localNames") || !summaryInput.ContainsKey("cursorContext"))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog summary input is missing its cursor context fields.");
            var catalog = ProjectToolCatalogSnapshotReader.Read();
            var args = request.DeserializeArgs<ProjectToolCatalogArgs>() ?? new ProjectToolCatalogArgs();
            if (args.Limit < 1 || args.Limit > 101)
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog summary limit must be an integer in [1,101].");
            RequireNoReservedProjectToolNames(catalog, args);
            var generation = ProjectToolCatalogGeneration();
            RequireProjectToolCatalogGeneration(args, generation);
            if (Utf8ByteCount(args.Group) > 256 || Utf8ByteCount(args.Keyword) > 2048)
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog summary filters exceed their bounded UTF-8 string limits.");
            var tokens = (args.Keyword ?? string.Empty).Split('|').Select(token => token.Trim()).Where(token => token.Length > 0).ToArray();
            if (tokens.Length > 16 || tokens.Any(token => Utf8ByteCount(token) > 128))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog search accepts at most 16 keywords of at most 128 UTF-8 bytes each.");
            var matches = ProjectToolCatalogDefinitions(catalog, runtimeOnly)
                .Where(definition => string.IsNullOrEmpty(args.Group) || string.Equals(definition.Group, args.Group, StringComparison.Ordinal))
                .Where(definition => tokens.Length == 0 || tokens.Any(token => ProjectToolCatalogMatches(definition, token)))
                .OrderBy(definition => definition.Name, StringComparer.Ordinal)
                .ToList();
            var nativeNames = new HashSet<string>(catalog.Descriptors.Where(definition => definition.ContractProfile != "official-attribute").Select(definition => definition.Name), StringComparer.Ordinal);
            foreach (var reservedName in args.ReservedNames ?? new string[0]) nativeNames.Add(reservedName);

            var localNames = ValidateCatalogCursorLocalNames(args);
            var scope = ProjectToolCatalogCursorScope(generation, args, runtimeOnly, localNames);
            int offset;
            string boundary;
            ReadProjectToolCatalogCursor(args.CursorToken, scope, out offset, out boundary);
            var combinedNames = localNames.Concat(matches.Select(definition => definition.Name)).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList();
            if (offset > combinedNames.Count || offset > 0 && !string.Equals(combinedNames[offset - 1], boundary, StringComparison.Ordinal))
                throw InvalidProjectToolCatalogCursor();
            var pageNames = combinedNames.Skip(offset).Take(args.Limit).ToList();
            var pageSet = new HashSet<string>(pageNames, StringComparer.Ordinal);
            var tools = matches.Where(definition => pageSet.Contains(definition.Name))
                .Select(definition => (object)ProjectToolRoutingSummaryData(definition, runtimeOnly, nativeNames)).ToList();
            return BoundSignedCatalogSummaryData(generation, matches.Count, combinedNames, offset, pageNames, tools, scope);
        }

        /// <summary>签名页在序列化预算内同步裁剪pageNames与live summaries，token指向真实末项。</summary>
        private static object BoundSignedCatalogSummaryData(
            string generation, int liveTotal, List<string> combinedNames, int offset, List<string> pageNames,
            List<object> tools, string scope)
        {
            Dictionary<string, object> data;
            while (true)
            {
                var end = offset + pageNames.Count;
                var boundary = end > 0 ? combinedNames[end - 1] : string.Empty;
                data = new Dictionary<string, object>
                {
                    { "catalogGeneration", generation }, { "total", (long)liveTotal },
                    { "hasMore", end < combinedNames.Count }, { "tools", tools },
                    { "pageNames", pageNames.Cast<object>().ToList() },
                    { "continuationToken", SignProjectToolCatalogCursor(scope, end, boundary) }
                };
                if (CatalogDataUtf8Bytes(data) <= 256 * 1024) return data;
                if (pageNames.Count == 0)
                    throw new BridgeCommandException("E_TOOL_REGISTRY_UNHEALTHY", "Tool catalog summary cannot fit the bounded UTF-8 transport size.");
                var removed = pageNames[pageNames.Count - 1];
                pageNames.RemoveAt(pageNames.Count - 1);
                var toolIndex = tools.FindLastIndex(value => string.Equals(((Dictionary<string, object>)value)["name"] as string, removed, StringComparison.Ordinal));
                if (toolIndex >= 0) tools.RemoveAt(toolIndex);
            }
        }

        /// <summary>校验CLI提供的已筛选local名称只来自reserved ownership集合。</summary>
        private static List<string> ValidateCatalogCursorLocalNames(ProjectToolCatalogArgs args)
        {
            var values = (args.LocalNames ?? new string[0]).ToList();
            if (values.Count > 4096 || values.Any(value => string.IsNullOrWhiteSpace(value) || Utf8ByteCount(value) > 256)
                || !values.SequenceEqual(values.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog local names must be a bounded ordinally sorted set.");
            var reserved = new HashSet<string>(args.ReservedNames ?? new string[0], StringComparer.Ordinal);
            if (values.Any(value => !reserved.Contains(value)))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog local names must be a subset of reserved package-local names.");
            if (Utf8ByteCount(args.CursorContext) != 64 || args.CursorContext.Any(value => !Uri.IsHexDigit(value)))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog cursor context is invalid.");
            return values;
        }

        /// <summary>把目标generation、query和local投影视图绑定到签名scope。</summary>
        private static string ProjectToolCatalogCursorScope(string generation, ProjectToolCatalogArgs args, bool runtimeOnly, List<string> localNames)
        {
            return Sha256Hex(Encoding.UTF8.GetBytes(generation + "\n" + (runtimeOnly ? "runtime" : "editor") + "\n" + args.CursorContext + "\n"
                + (args.Group ?? string.Empty) + "\n" + (args.Keyword ?? string.Empty) + "\n"
                + string.Join("\n", localNames) + "\n--reserved--\n" + string.Join("\n", args.ReservedNames ?? new string[0])));
        }

        /// <summary>验证opaque token并恢复目标签发的序号与前页边界。</summary>
        private static void ReadProjectToolCatalogCursor(string token, string scope, out int offset, out string boundary)
        {
            offset = 0;
            boundary = string.Empty;
            if (string.IsNullOrEmpty(token)) return;
            if (Utf8ByteCount(token) > 8192) throw InvalidProjectToolCatalogCursor();
            try
            {
                var parts = token.Split('.');
                if (parts.Length != 2) throw InvalidProjectToolCatalogCursor();
                var payloadBytes = FromBase64Url(parts[0]);
                var signature = FromBase64Url(parts[1]);
                byte[] expected;
                using (var hmac = new HMACSHA256(projectToolCatalogCursorSecret)) expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(parts[0]));
                if (!FixedTimeEqual(signature, expected)) throw InvalidProjectToolCatalogCursor();
                var fields = Encoding.UTF8.GetString(payloadBytes).Split('\n');
                int parsedOffset;
                if (fields.Length != 3 || !string.Equals(fields[0], scope, StringComparison.Ordinal)
                    || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out parsedOffset) || parsedOffset < 0)
                    throw InvalidProjectToolCatalogCursor();
                offset = parsedOffset;
                boundary = Encoding.UTF8.GetString(Convert.FromBase64String(fields[2]));
                if (Utf8ByteCount(boundary) > 256 || (offset == 0) != string.IsNullOrEmpty(boundary))
                    throw InvalidProjectToolCatalogCursor();
            }
            catch (BridgeCommandException) { throw; }
            catch { throw InvalidProjectToolCatalogCursor(); }
        }

        /// <summary>使用当前domain secret签发不可由CLI改写的continuation。</summary>
        private static string SignProjectToolCatalogCursor(string scope, int offset, string boundary)
        {
            var payloadText = scope + "\n" + offset.ToString(CultureInfo.InvariantCulture) + "\n"
                + Convert.ToBase64String(Encoding.UTF8.GetBytes(boundary ?? string.Empty));
            var payload = ToBase64Url(Encoding.UTF8.GetBytes(payloadText));
            byte[] signature;
            using (var hmac = new HMACSHA256(projectToolCatalogCursorSecret)) signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            return payload + "." + ToBase64Url(signature);
        }

        /// <summary>统一返回可机器分支的cursor拒绝错误。</summary>
        private static BridgeCommandException InvalidProjectToolCatalogCursor()
        {
            return new BridgeCommandException("E_DISCOVERY_CURSOR_INVALID", "The target rejected an altered or cross-query discovery continuation.", null,
                new[] { "Restart this exact discovery query from its first page." });
        }

        /// <summary>secret只活在当前domain内，reload后旧token自然失效。</summary>
        private static byte[] CreateProjectToolCatalogCursorSecret()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return bytes;
        }

        /// <summary>编码URL安全token片段。</summary>
        private static string ToBase64Url(byte[] value)
        {
            return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>解码URL安全token片段。</summary>
        private static byte[] FromBase64Url(string value)
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            return Convert.FromBase64String(normalized);
        }

        /// <summary>常量步数比较签名，避免提前返回泄露首个差异位置。</summary>
        private static bool FixedTimeEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            var difference = 0;
            for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }

        /// <summary>所有catalog string bound统一按wire UTF-8字节计算。</summary>
        private static int Utf8ByteCount(string value)
        {
            return Encoding.UTF8.GetByteCount(value ?? string.Empty);
        }

        /// <summary>以真实Bridge serializer计算完整data大小。</summary>
        private static int CatalogDataUtf8Bytes(object data)
        {
            return Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(data));
        }

        /// <summary>完整data超出consumer预算时在返回前失败。</summary>
        private static void RequireCatalogDataBudget(object data, int maximumBytes, string message)
        {
            if (CatalogDataUtf8Bytes(data) > maximumBytes) throw new BridgeCommandException("E_TOOL_REGISTRY_UNHEALTHY", message);
        }

        private static bool ProjectToolCatalogMatches(RegisteredToolDescriptor definition, string token)
        {
            return (definition.Name ?? string.Empty).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0
                || (definition.Summary ?? string.Empty).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0
                || (definition.Description ?? string.Empty).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0
                || (definition.Group ?? string.Empty).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>exact describe只读取一次published snapshot；native未命中时可返回一份指定fallback descriptor。</summary>
        private static object ProjectToolCatalogDescribe(UnityCli.Protocol.BridgeRequest request, bool runtimeOnly)
        {
            if (runtimeOnly) RequireProjectToolCatalogInputKeys(request, "name");
            else RequireProjectToolCatalogInputKeys(request, "name", "fallbackName");
            var published = ReadPublishedProjectToolCatalog();
            var catalog = published.Snapshot;
            var args = request.DeserializeArgs<ProjectToolCatalogArgs>() ?? new ProjectToolCatalogArgs();
            if (string.IsNullOrWhiteSpace(args.Name) || Utf8ByteCount(args.Name) > 256)
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog describe name is required and must not exceed 256 UTF-8 bytes.");
            if (args.FallbackName != null && (string.IsNullOrWhiteSpace(args.FallbackName) || Utf8ByteCount(args.FallbackName) > 256
                || string.Equals(args.Name, args.FallbackName, StringComparison.Ordinal)))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Catalog describe fallbackName must be distinct and must not exceed 256 UTF-8 bytes.");
            var generation = ProjectToolCatalogGeneration();
            Func<RegisteredToolDescriptor, bool> availableForTarget = tool => !runtimeOnly || tool.Target == "runtime" || tool.Target == "both";
            var definition = catalog.Descriptors.FirstOrDefault(tool => string.Equals(tool.Name, args.Name, StringComparison.Ordinal) && availableForTarget(tool));
            if (definition == null && args.FallbackName != null)
                definition = catalog.Descriptors.FirstOrDefault(tool => string.Equals(tool.Name, args.FallbackName, StringComparison.Ordinal) && availableForTarget(tool));
            if (definition == null)
            {
                // 冲突/被拒绝的名称不能静默退化成 found:false，否则 AI 只能看到“工具不存在”。
                var unavailable = CatalogToolUnavailable(published, args.Name)
                    ?? (args.FallbackName != null ? CatalogToolUnavailable(published, args.FallbackName) : null);
                if (unavailable != null) throw unavailable;
            }
            var found = definition != null;
            var result = new Dictionary<string, object> { { "catalogGeneration", generation }, { "found", found } };
            if (found)
            {
                result["descriptor"] = ProjectToolDescriptorData(definition);
            }
            if (CatalogDataUtf8Bytes(result) > 3 * 1024 * 1024)
                throw new BridgeCommandException("E_TOOL_REGISTRY_UNHEALTHY", "Exact Tool descriptor exceeds the bounded UTF-8 transport size.", null,
                    new[] { "Reduce the Tool schema or examples, then let Unity reload the domain." });
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
                ? "E_RUNTIME_UNAVAILABLE: Enter Play Mode before executing this Tool."
                : ToolUnavailableReason(definition);
        }

        private static bool IsProjectToolCatalogReadCommand(string command)
        {
            return command == "catalog.editor.health" || command == "catalog.editor.groups" || command == "catalog.editor.summary" || command == "catalog.editor.describe" || command == "catalog.editor.list"
                || command == "catalog.player.groups" || command == "catalog.player.summary" || command == "catalog.player.describe" || command == "catalog.player.list";
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
                // 同一冻结判定优先于泛化 E_TOOL_NOT_FOUND，且不静默回退到其他实现。
                var unavailable = CatalogToolUnavailable(catalog, name);
                if (unavailable != null) throw unavailable;
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

            // If the tool has Target=runtime, reject from catalog.editor.run and suggest catalog.player.run
            if (definition.Target == "runtime")
            {
                throw new BridgeCommandException("E_RUNTIME_TOOL_TARGET_MISMATCH",
                    "Tool " + name + " has Target=runtime and must be executed via the runtime path (catalog.player.run).",
                    new Dictionary<string, object> { { "toolName", name }, { "toolTarget", definition.Target } },
                    new[] { "Use catalog.player.run <name> or tools run <name> with a runtime target to execute this tool in Play Mode." });
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
            using (var toolTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
            try
            {
                WriteLog("custom_tool_run requestId=" + request.RequestId + " name=" + name + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
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
            catch (UnityCliRequestBudgetExceededException exception)
            {
                if (executionContext != null && executionContext.EffectCommitted)
                    throw PostEffectExecutionTimeout(name, "request-budget", exception.Message);
                throw RequestBudgetExceeded(name, exception.Dimension);
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
                var terminalBudget = executionContext.ReadBudget ?? executionContext.UseReadBudget();
                terminalBudget.CheckpointOrThrow();
                var outputJson = UnityCliSerializer.Serialize(result, terminalBudget, toolTimeout.Token);
                terminalBudget.CheckpointOrThrow();
                var outputError = ValidateProjectToolOutput(definition, outputJson);
                if (outputError != null)
                {
                    throw new BridgeCommandException(
                        "E_TOOL_COMPLETION_PROOF_INVALID",
                        "Tool result failed its derived output schema at " + outputError.Path + ".",
                        new Dictionary<string, object> { { "toolName", definition.Name }, { "path", outputError.Path }, { "expected", outputError.Expected }, { "actual", outputError.Actual } },
                        new[] { "Fix the Tool result DTO or completion driver before retrying." });
                }
                terminalBudget.CheckpointOrThrow();
                var terminalData = CompleteProjectToolSuccess(request, definition, result);
                terminalBudget.CheckpointOrThrow();
                RecordToolCompletionEvidence(request, definition, result, terminalData);
                terminalBudget.CheckpointOrThrow();
                var terminalJson = SerializeTerminalSuccess(request.RequestId, terminalData, terminalBudget, toolTimeout.Token);
                terminalBudget.CheckpointOrThrow();
                return new SerializedBridgeEnvelope(terminalJson);
            }
            catch (BridgeCommandException exception)
            {
                if (executionContext != null && executionContext.EffectCommitted
                    && (definition.OfficialAttributeBinding != null || exception.Code != "E_EXECUTION_TIMEOUT"))
                    throw PostEffectExecutionTimeout(name, "postcondition", ProjectToolFailureReason(definition, exception.Code + ": " + exception.Message));
                throw;
            }
            catch (UnityCliRequestBudgetExceededException exception)
            {
                if (executionContext != null && executionContext.EffectCommitted)
                    throw PostEffectExecutionTimeout(name, "request-budget", exception.Message);
                throw RequestBudgetExceeded(name, exception.Dimension);
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
        }

        /// <summary>最终 envelope 的自描述字节数最多经过8次定点收敛，且每次都服从同一 deadline 与预算。</summary>
        internal static string SerializeTerminalSuccess(string requestId, object terminalData, UnityCliRequestBudget budget, CancellationToken cancellationToken)
        {
            if (budget == null) throw new ArgumentNullException("budget");
            for (var attempt = 0; attempt < 8; attempt++)
            {
                budget.CheckpointOrThrow();
                long actualBytes;
                var json = UnityCliSerializer.Serialize(
                    UnityCli.Protocol.ProtocolEnvelopeFactory.Success(requestId, terminalData),
                    budget,
                    cancellationToken,
                    out actualBytes);
                if (actualBytes == budget.ActualSerializedUtf8Bytes) return json;
                budget.ObserveActualSerializedUtf8Bytes(actualBytes);
            }

            throw new InvalidOperationException("Final envelope UTF-8 byte accounting did not converge.");
        }

        /// <summary>成功 Tool 已在自身 deadline 与预算内完成最终 envelope 序列化。</summary>
        private sealed class SerializedBridgeEnvelope
        {
            internal SerializedBridgeEnvelope(string json) { Json = json; }
            internal string Json { get; private set; }
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

        private static BridgeCommandException RequestBudgetExceeded(string toolName, string dimension)
        {
            return new BridgeCommandException(
                "E_REQUEST_BUDGET_LIMIT_EXCEEDED",
                "Tool " + toolName + " exhausted its request-wide Unity main-thread budget before a complete result was available.",
                new Dictionary<string, object> { { "toolName", toolName }, { "dimension", dimension } },
                new[]
                {
                    "Reduce the request cost first: narrow the selector, lower the page size, or pass a smaller explicit budget.",
                    "If this tool's described result exposes nextCursor, continue from it instead of restarting the read; a result without nextCursor is non-resumable and accepts only a narrower selector."
                });
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
            if (definition.Notes.Count > 0) data["notes"] = definition.Notes.ToArray();
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
                return IsPlayingNow() && !IsPlayModeChangingNow();
            if (definition.PlayModeSupport == "editor-only")
                return !IsPlayingOrWillChangePlaymodeNow() && !IsPlayModeChangingNow();
            return !IsPlayModeChangingNow();
        }

        /// <summary>
        /// 主线程暂停期间的目录可用性判定标志。
        /// 主线程停住时后台线程不得调用 Unity API，因此可用性改为读取主线程 tick 已冻结的 mode 缓存；
        /// 此时主线程无法再切换 PlayMode，该缓存仍是当前 mode，极端情况下也会保守报告 transitioning 而不是伪造 ready。
        /// </summary>
        [ThreadStatic]
        private static bool catalogOffMainRead;

        internal static bool CatalogOffMainRead
        {
            get { return catalogOffMainRead; }
            set { catalogOffMainRead = value; }
        }

        private static bool IsPlayingNow()
        {
            return catalogOffMainRead ? cachedIsPlaying : EditorApplication.isPlaying;
        }

        private static bool IsPlayModeChangingNow()
        {
            return catalogOffMainRead ? cachedIsPlayModeChanging : IsPlayModeChanging();
        }

        private static bool IsPlayingOrWillChangePlaymodeNow()
        {
            return catalogOffMainRead ? cachedIsPlayingOrWillChangePlaymode : EditorApplication.isPlayingOrWillChangePlaymode;
        }

        /// <summary>按执行门禁优先级返回与 available=false 对应的稳定原因。</summary>
        private static string ToolUnavailableReason(RegisteredToolDescriptor definition)
        {
            if (definition.Target == "runtime" || definition.PlayModeSupport == "runtime-only")
                return "E_RUNTIME_UNAVAILABLE: Enter Play Mode before executing this Tool.";
            if (definition.PlayModeSupport == "editor-only" && IsPlayingOrWillChangePlaymodeNow())
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
            return definition.Name == "build.profile-create"
                || definition.Name == "build.profile-modify"
                || definition.Name == "build.profile-switch";
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

        /// <summary>
        /// catalog.editor.run 成功终态的唯一构造点：公开 CLI 把该命令的成功响应投影为 data.result，
        /// 因此正常执行路径与暂停控制通道都必须经由这里产出同一形状，不得各自包装。
        /// </summary>
        internal static object CompleteProjectToolSuccess(UnityCli.Protocol.BridgeRequest request, RegisteredTool definition, object result)
        {
            WriteLog("custom_tool_completed requestId=" + request.RequestId + " name=" + definition.Name);
            return new Dictionary<string, object> { { "result", result } };
        }

    }
}
#endif
