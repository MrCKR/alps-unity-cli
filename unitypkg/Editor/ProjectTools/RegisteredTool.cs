#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>构造期冻结的执行条目；所有 descriptor 集合和 JSON 值均防御性复制。</summary>
    internal sealed class RegisteredTool
    {
        private readonly string[] sideEffects;
        private readonly string[] permissions;
        private readonly string[] requiresPackages;
        private readonly string examplesJson;

        internal RegisteredTool(
            ToolDescriptor descriptor,
            RegistryToolOwnership ownership,
            string inputSchema,
            string outputSchema,
            string availabilityScope,
            string contractProfile,
            Type requestType,
            Type resultType,
            UnityCliToolCompletionContract completionContract,
            OfficialAttributeToolBinding officialAttributeBinding,
            Func<object, UnityCliProjectToolContext, CancellationToken, ValueTask<object>> invokeAsync)
        {
            if (descriptor == null) throw new ArgumentNullException("descriptor");
            if (ownership == null) throw new ArgumentNullException("ownership");
            Name = descriptor.Name;
            Summary = descriptor.Summary;
            Description = descriptor.Description;
            Group = string.IsNullOrEmpty(descriptor.Group) ? DefaultGroup(descriptor.Name) : descriptor.Group;
            Source = ownership.Source;
            OwnerIdentity = ownership;
            InputSchema = inputSchema;
            OutputSchema = outputSchema;
            DestructiveHint = descriptor.DestructiveHint;
            sideEffects = (descriptor.SideEffects ?? Array.Empty<string>()).ToArray();
            SupportsDryRun = descriptor.SupportsDryRun;
            permissions = (descriptor.Permissions ?? Array.Empty<string>()).ToArray();
            requiresPackages = (descriptor.RequiresPackages ?? Array.Empty<string>()).ToArray();
            examplesJson = UnityCliSerializer.Serialize(
                UnityCliSerializer.Parse(UnityCliSerializer.Serialize(descriptor.Examples ?? Array.Empty<ToolExample>())));
            Target = string.IsNullOrEmpty(descriptor.Target) ? "editor" : descriptor.Target;
            AvailabilityScope = string.IsNullOrEmpty(availabilityScope)
                ? (Target == "runtime" ? "playModeSession" : "editorSession")
                : availabilityScope;
            PlayModeSupport = NormalizePlayModeSupport(descriptor.PlayModeSupport, Target);
            ContractProfile = string.IsNullOrEmpty(contractProfile) ? "native" : contractProfile;
            DefaultTimeoutMs = descriptor.DefaultTimeoutMs <= 0 ? 30000 : descriptor.DefaultTimeoutMs;
            MaxTimeoutMs = descriptor.MaxTimeoutMs <= 0 ? 600000 : descriptor.MaxTimeoutMs;
            RequestType = requestType;
            ResultType = resultType;
            CompletionContract = completionContract;
            OfficialAttributeBinding = officialAttributeBinding;
            InvokeAsync = invokeAsync;
        }

        public string Name { get; }
        public string Summary { get; }
        public string Description { get; }
        public string Group { get; }
        public string Source { get; }
        public string InputSchema { get; }
        public string OutputSchema { get; }
        public bool DestructiveHint { get; }
        public IReadOnlyList<string> SideEffects { get { return new ReadOnlyCollection<string>(sideEffects.ToArray()); } }
        public bool SupportsDryRun { get; }
        public IReadOnlyList<string> Permissions { get { return new ReadOnlyCollection<string>(permissions.ToArray()); } }
        public IReadOnlyList<string> RequiresPackages { get { return new ReadOnlyCollection<string>(requiresPackages.ToArray()); } }
        public string Target { get; }
        public string AvailabilityScope { get; }
        public string PlayModeSupport { get; }
        public string ContractProfile { get; }
        public int DefaultTimeoutMs { get; }
        public int MaxTimeoutMs { get; }
        public Type RequestType { get; }
        public Type ResultType { get; }
        public UnityCliToolCompletionContract CompletionContract { get; }
        internal RegistryToolOwnership OwnerIdentity { get; }
        internal OfficialAttributeToolBinding OfficialAttributeBinding { get; }
        internal Func<object, UnityCliProjectToolContext, CancellationToken, ValueTask<object>> InvokeAsync { get; }
        internal string ExamplesJson { get { return examplesJson; } }
        internal object CloneExamples() { return UnityCliSerializer.Parse(examplesJson); }

        private static string DefaultGroup(string name)
        {
            if (string.IsNullOrEmpty(name)) return "custom";
            var dot = name.IndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : "custom";
        }

        private static string NormalizePlayModeSupport(string value, string target)
        {
            if (value == "supported" || value == "runtime-only" || value == "session-affecting"
                || value == "editor-only" || value == "unknown") return value;
            return target == "runtime" ? "runtime-only" : "editor-only";
        }
    }

    /// <summary>snapshot reader 唯一可见的 descriptor；不含 CLR Type、binding 或 invoker。</summary>
    internal sealed class RegisteredToolDescriptor
    {
        private readonly string[] sideEffects;
        private readonly string[] permissions;
        private readonly string[] requiresPackages;
        private readonly string examplesJson;

        internal RegisteredToolDescriptor(RegisteredTool tool)
        {
            Name = tool.Name;
            Summary = tool.Summary;
            Description = tool.Description;
            Group = tool.Group;
            Source = tool.Source;
            InputSchema = tool.InputSchema;
            OutputSchema = tool.OutputSchema;
            DestructiveHint = tool.DestructiveHint;
            sideEffects = tool.SideEffects.ToArray();
            SupportsDryRun = tool.SupportsDryRun;
            permissions = tool.Permissions.ToArray();
            requiresPackages = tool.RequiresPackages.ToArray();
            examplesJson = tool.ExamplesJson;
            Target = tool.Target;
            AvailabilityScope = tool.AvailabilityScope;
            PlayModeSupport = tool.PlayModeSupport;
            ContractProfile = tool.ContractProfile;
            DefaultTimeoutMs = tool.DefaultTimeoutMs;
            CompletionContract = new PublishedToolCompletionContract(tool.CompletionContract);
        }

        public string Name { get; }
        public string Summary { get; }
        public string Description { get; }
        public string Group { get; }
        public string Source { get; }
        public string InputSchema { get; }
        public string OutputSchema { get; }
        public bool DestructiveHint { get; }
        public IReadOnlyList<string> SideEffects { get { return new ReadOnlyCollection<string>(sideEffects.ToArray()); } }
        public bool SupportsDryRun { get; }
        public IReadOnlyList<string> Permissions { get { return new ReadOnlyCollection<string>(permissions.ToArray()); } }
        public IReadOnlyList<string> RequiresPackages { get { return new ReadOnlyCollection<string>(requiresPackages.ToArray()); } }
        public string Target { get; }
        public string AvailabilityScope { get; }
        public string PlayModeSupport { get; }
        public string ContractProfile { get; }
        public int DefaultTimeoutMs { get; }
        public PublishedToolCompletionContract CompletionContract { get; }
        internal object CloneExamples() { return UnityCliSerializer.Parse(examplesJson); }
    }

    /// <summary>published descriptor 使用的 completion 深拷贝。</summary>
    internal sealed class PublishedToolCompletionContract
    {
        internal PublishedToolCompletionContract(UnityCliToolCompletionContract source)
        {
            if (source == null) throw new ArgumentNullException("source");
            ExecutionClass = source.ExecutionClass;
            CompletionPredicate = source.CompletionPredicate;
            ProofSource = source.ProofSource;
            ReloadPolicy = source.ReloadPolicy;
            TimeoutError = source.TimeoutError;
            OutcomeUnknownPolicy = source.OutcomeUnknownPolicy;
            CompletionDriverId = source.CompletionDriverId;
            JournalPolicy = source.JournalPolicy;
            Mutation = source.Mutation;
        }

        public string ExecutionClass { get; }
        public string CompletionPredicate { get; }
        public string ProofSource { get; }
        public string ReloadPolicy { get; }
        public string TimeoutError { get; }
        public string OutcomeUnknownPolicy { get; }
        public string CompletionDriverId { get; }
        public string JournalPolicy { get; }
        public bool Mutation { get; }
    }

    internal enum UnityCliToolProviderKind
    {
        BuiltIn,
        Extension,
        ProjectInterface,
        OfficialAttribute
    }

    /// <summary>只返回当前 domain 的 descriptor snapshot，不能取得 executable entry。</summary>
    internal interface IUnityCliToolCatalogSnapshotReader
    {
        UnityCliToolCatalogSnapshot Read();
    }

    internal interface IUnityCliToolCatalogHealthSnapshotReader
    {
        UnityCliToolCatalogHealthSnapshot Read();
    }

    internal enum UnityCliToolCatalogOperationKind
    {
        DiscoveryPass,
        ProviderMetadataRead,
        AggregateSuccessLog
    }

    /// <summary>候选构建期唯一操作账本；所有计数只能由生产动作发生点写入。</summary>
    internal sealed class UnityCliToolCatalogOperationJournal
    {
        private readonly Dictionary<UnityCliToolCatalogOperationKind, int> counts =
            new Dictionary<UnityCliToolCatalogOperationKind, int>();

        internal void Record(UnityCliToolCatalogOperationKind operation)
        {
            int current;
            counts.TryGetValue(operation, out current);
            counts[operation] = checked(current + 1);
        }

        internal int Count(UnityCliToolCatalogOperationKind operation)
        {
            int count;
            return counts.TryGetValue(operation, out count) ? count : 0;
        }
    }

    /// <summary>一次 publication 的不可变 descriptor 投影与真实 discovery 操作计数。</summary>
    internal sealed class UnityCliToolCatalogSnapshot
    {
        internal UnityCliToolCatalogSnapshot(
            IEnumerable<RegisteredToolDescriptor> descriptors,
            int assemblyCount,
            int providerAssemblyCount,
            int builtInToolCount,
            int extensionToolCount,
            int projectInterfaceToolCount,
            int officialAttributeToolCount,
            long elapsedMilliseconds,
            UnityCliToolCatalogOperationJournal operations)
        {
            Descriptors = new ReadOnlyCollection<RegisteredToolDescriptor>(
                descriptors.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray());
            AssemblyCount = assemblyCount;
            ProviderAssemblyCount = providerAssemblyCount;
            BuiltInToolCount = builtInToolCount;
            ExtensionToolCount = extensionToolCount;
            ProjectInterfaceToolCount = projectInterfaceToolCount;
            OfficialAttributeToolCount = officialAttributeToolCount;
            if (operations == null) throw new ArgumentNullException("operations");
            ElapsedMilliseconds = Math.Max(0, elapsedMilliseconds);
            DiscoveryPassCount = operations.Count(UnityCliToolCatalogOperationKind.DiscoveryPass);
            ProviderMetadataReadCount = operations.Count(UnityCliToolCatalogOperationKind.ProviderMetadataRead);
            AggregateSuccessLogCount = operations.Count(UnityCliToolCatalogOperationKind.AggregateSuccessLog);
        }

        public IReadOnlyList<RegisteredToolDescriptor> Descriptors { get; }
        public int ToolCount { get { return Descriptors.Count; } }
        public int AssemblyCount { get; }
        public int ProviderAssemblyCount { get; }
        public int BuiltInToolCount { get; }
        public int ExtensionToolCount { get; }
        public int ProjectInterfaceToolCount { get; }
        public int OfficialAttributeToolCount { get; }
        public long ElapsedMilliseconds { get; }
        public int DiscoveryPassCount { get; }
        public int ProviderMetadataReadCount { get; }
        public int AggregateSuccessLogCount { get; }
    }

    /// <summary>doctor 可独立读取的 bounded health；失败时不暴露 executable table。</summary>
    internal sealed class UnityCliToolCatalogHealthSnapshot
    {
        internal UnityCliToolCatalogHealthSnapshot(bool ready, int toolCount, string failure, string nextAction)
        {
            Ready = ready;
            ToolCount = Math.Max(0, toolCount);
            Failure = failure ?? string.Empty;
            NextAction = nextAction ?? string.Empty;
        }

        public bool Ready { get; }
        public int ToolCount { get; }
        public string Failure { get; }
        public string NextAction { get; }
    }

    /// <summary>唯一原子 publication；executable table 与其 descriptor 投影同源、同代。</summary>
    internal sealed class UnityCliPublishedToolCatalog
    {
        internal UnityCliPublishedToolCatalog(Dictionary<string, RegisteredTool> tools, UnityCliToolCatalogSnapshot snapshot)
        {
            ExecutableTools = new ReadOnlyDictionary<string, RegisteredTool>(
                new Dictionary<string, RegisteredTool>(tools, StringComparer.Ordinal));
            Snapshot = snapshot ?? throw new ArgumentNullException("snapshot");
        }

        internal IReadOnlyDictionary<string, RegisteredTool> ExecutableTools { get; }
        internal UnityCliToolCatalogSnapshot Snapshot { get; }
    }

    /// <summary>纯内存 pending registry；任一验证失败后永久 fault，失败对象不能替换 publication slot。</summary>
    internal sealed class UnityCliToolCatalogBuilder
    {
        private readonly Dictionary<string, RegisteredTool> pending = new Dictionary<string, RegisteredTool>(StringComparer.Ordinal);
        private readonly HashSet<string> providerMetadata = new HashSet<string>(StringComparer.Ordinal);
        private readonly UnityCliToolCatalogOperationJournal operations = new UnityCliToolCatalogOperationJournal();
        private bool discoveryStarted;
        private bool faulted;
        private bool published;
        private int assemblyCount;
        private int expectedProviderAssemblyCount;
        private int builtInToolCount;
        private int extensionToolCount;
        private int projectInterfaceToolCount;
        private int officialAttributeToolCount;

        internal void BeginDiscovery(int loadedAssemblyCount, int candidateProviderAssemblyCount)
        {
            if (discoveryStarted || published) throw new InvalidOperationException("Tool catalog discovery can begin only once.");
            if (loadedAssemblyCount < 1 || candidateProviderAssemblyCount < 1 || candidateProviderAssemblyCount > loadedAssemblyCount)
                throw new ArgumentOutOfRangeException("candidateProviderAssemblyCount");
            discoveryStarted = true;
            operations.Record(UnityCliToolCatalogOperationKind.DiscoveryPass);
            assemblyCount = loadedAssemblyCount;
            expectedProviderAssemblyCount = candidateProviderAssemblyCount;
        }

        /// <summary>生产 discovery 与 301-provider 测试共用此 exact assembly metadata 路径。</summary>
        internal void RecordProviderMetadata(string assemblyIdentity)
        {
            if (!discoveryStarted || published || faulted || string.IsNullOrWhiteSpace(assemblyIdentity))
                throw new InvalidOperationException("Tool provider metadata is not writable.");
            if (!providerMetadata.Add(assemblyIdentity)) throw new InvalidOperationException("Duplicate Tool provider assembly identity: " + assemblyIdentity + ".");
            operations.Record(UnityCliToolCatalogOperationKind.ProviderMetadataRead);
        }

        internal void Add(RegisteredTool definition, UnityCliToolProviderKind providerKind)
        {
            if (!discoveryStarted || published || faulted) throw new InvalidOperationException("Tool catalog builder is not writable.");
            try
            {
                Validate(definition, providerKind);
                if (pending.ContainsKey(definition.Name)) throw new InvalidOperationException("Duplicate tool registration: " + definition.Name);
                pending.Add(definition.Name, definition);
                switch (providerKind)
                {
                    case UnityCliToolProviderKind.BuiltIn: builtInToolCount++; break;
                    case UnityCliToolProviderKind.Extension: extensionToolCount++; break;
                    case UnityCliToolProviderKind.ProjectInterface: projectInterfaceToolCount++; break;
                    case UnityCliToolProviderKind.OfficialAttribute: officialAttributeToolCount++; break;
                    default: throw new ArgumentOutOfRangeException("providerKind");
                }
            }
            catch
            {
                faulted = true;
                throw;
            }
        }

        internal void Abort() { faulted = true; }

        internal UnityCliPublishedToolCatalog Publish(
            long elapsedMilliseconds,
            Action<UnityCliToolCatalogSnapshot> writeAggregateSuccessLog)
        {
            if (!discoveryStarted || faulted || published) throw new InvalidOperationException("Tool catalog cannot publish an incomplete or faulted table.");
            if (writeAggregateSuccessLog == null) throw new ArgumentNullException("writeAggregateSuccessLog");
            if (providerMetadata.Count != expectedProviderAssemblyCount)
                throw new InvalidOperationException("Tool catalog provider metadata does not match the discovered provider assembly set.");
            if (builtInToolCount + extensionToolCount + projectInterfaceToolCount + officialAttributeToolCount != pending.Count)
                throw new InvalidOperationException("Tool catalog provider counts do not match the pending table.");

            operations.Record(UnityCliToolCatalogOperationKind.AggregateSuccessLog);
            var descriptors = pending.Values.Select(tool => new RegisteredToolDescriptor(tool));
            var snapshot = new UnityCliToolCatalogSnapshot(
                descriptors,
                assemblyCount,
                providerMetadata.Count,
                builtInToolCount,
                extensionToolCount,
                projectInterfaceToolCount,
                officialAttributeToolCount,
                elapsedMilliseconds,
                operations);
            try
            {
                writeAggregateSuccessLog(snapshot);
            }
            catch
            {
                faulted = true;
                throw;
            }
            published = true;
            return new UnityCliPublishedToolCatalog(pending, snapshot);
        }

        private static void Validate(RegisteredTool definition, UnityCliToolProviderKind providerKind)
        {
            if (definition != null) UnityCliToolName.Validate(definition.Name, definition.ContractProfile);
            if (definition == null || string.IsNullOrWhiteSpace(definition.Name) || string.IsNullOrWhiteSpace(definition.Summary)
                || string.IsNullOrWhiteSpace(definition.Description) || string.IsNullOrWhiteSpace(definition.Group)
                || definition.CloneExamples() == null || definition.InvokeAsync == null)
                throw new ArgumentException("Tool registration requires name, summary, description, group, examples, and a typed async invoker.");
            if (Utf8ByteCount(definition.Name) > 256 || Utf8ByteCount(definition.Summary) > 512
                || Utf8ByteCount(definition.Group) > 256 || Utf8ByteCount(definition.Description) > 65536
                || Utf8ByteCount(definition.InputSchema) > 1024 * 1024
                || Utf8ByteCount(definition.OutputSchema) > 1024 * 1024
                || Utf8ByteCount(definition.ExamplesJson) > 256 * 1024
                || !BoundedStringList(definition.SideEffects, 128, 256)
                || !BoundedStringList(definition.Permissions, 128, 256)
                || !BoundedStringList(definition.RequiresPackages, 128, 256))
                throw new ArgumentException("Tool descriptor exceeds a UTF-8 catalog transport bound: " + definition.Name);
            if (definition.RequestType == null || definition.ResultType == null
                || string.IsNullOrWhiteSpace(definition.InputSchema) || string.IsNullOrWhiteSpace(definition.OutputSchema)
                || !(UnityCliSerializer.Parse(definition.InputSchema) is IDictionary<string, object>)
                || !(UnityCliSerializer.Parse(definition.OutputSchema) is IDictionary<string, object>))
                throw new ArgumentException("Tool registration requires request/result types and valid object schemas: " + definition.Name);
            if (definition.CompletionContract == null
                || !string.Equals(definition.CompletionContract.ToolName, definition.Name, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(definition.CompletionContract.ExecutionClass)
                || string.IsNullOrWhiteSpace(definition.CompletionContract.CompletionPredicate)
                || string.IsNullOrWhiteSpace(definition.CompletionContract.ProofSource)
                || string.IsNullOrWhiteSpace(definition.CompletionContract.ReloadPolicy)
                || string.IsNullOrWhiteSpace(definition.CompletionContract.TimeoutError)
                || string.IsNullOrWhiteSpace(definition.CompletionContract.OutcomeUnknownPolicy)
                || string.IsNullOrWhiteSpace(definition.CompletionContract.CompletionDriverId)
                || string.IsNullOrWhiteSpace(definition.CompletionContract.JournalPolicy))
                throw new ArgumentException("Tool registration requires a complete completion contract: " + definition.Name);
            if (definition.DefaultTimeoutMs <= 0 || definition.MaxTimeoutMs < definition.DefaultTimeoutMs)
                throw new ArgumentException("Tool registration timeout bounds are invalid: " + definition.Name);
            if (!(definition.CloneExamples() is IEnumerable)
                || (definition.Target != "editor" && definition.Target != "runtime" && definition.Target != "both")
                || (definition.AvailabilityScope != "always" && definition.AvailabilityScope != "projectFiles"
                    && definition.AvailabilityScope != "editorSession" && definition.AvailabilityScope != "playModeSession")
                || (definition.PlayModeSupport != "supported" && definition.PlayModeSupport != "runtime-only"
                    && definition.PlayModeSupport != "session-affecting" && definition.PlayModeSupport != "editor-only"
                    && definition.PlayModeSupport != "unknown"))
                throw new ArgumentException("Tool descriptor metadata is incomplete or invalid: " + definition.Name);

            var expectedSource = providerKind == UnityCliToolProviderKind.BuiltIn ? "built-in"
                : providerKind == UnityCliToolProviderKind.Extension ? "extension" : "project";
            var expectedProfile = providerKind == UnityCliToolProviderKind.OfficialAttribute ? "official-attribute" : "native";
            if (!string.Equals(definition.Source, expectedSource, StringComparison.Ordinal)
                || !string.Equals(definition.ContractProfile, expectedProfile, StringComparison.Ordinal)
                || ((providerKind == UnityCliToolProviderKind.OfficialAttribute) != (definition.OfficialAttributeBinding != null)))
                throw new InvalidOperationException("Tool provider ownership does not match its descriptor: " + definition.Name);
            if (definition.OwnerIdentity == null
                || definition.OwnerIdentity.ImplementationType == null
                || definition.OwnerIdentity.ImplementationType.Assembly != definition.OwnerIdentity.Assembly)
                throw new InvalidOperationException("Tool provider has no exact compiled owner identity: " + definition.Name);
            if (providerKind == UnityCliToolProviderKind.Extension && string.IsNullOrWhiteSpace(definition.OwnerIdentity.ExtensionId))
                throw new InvalidOperationException("Extension Tool has no exact extension id: " + definition.Name);
            if (providerKind != UnityCliToolProviderKind.Extension && !string.IsNullOrEmpty(definition.OwnerIdentity.ExtensionId))
                throw new InvalidOperationException("Non-extension Tool cannot claim an extension id: " + definition.Name);
            if (definition.OfficialAttributeBinding != null
                && (definition.OfficialAttributeBinding.Method == null || definition.OfficialAttributeBinding.DeclaredResultType == null
                    || definition.OfficialAttributeBinding.Parameters == null))
                throw new InvalidOperationException("Official attribute Tool binding is incomplete: " + definition.Name);
            var completionOwner = definition.CompletionContract.DescriptorOwner ?? string.Empty;
            if ((providerKind == UnityCliToolProviderKind.BuiltIn && completionOwner != "built-in")
                || (providerKind == UnityCliToolProviderKind.Extension
                    && completionOwner != "extension:" + definition.OwnerIdentity.ExtensionId)
                || ((providerKind == UnityCliToolProviderKind.ProjectInterface || providerKind == UnityCliToolProviderKind.OfficialAttribute)
                    && completionOwner != "project"))
                throw new InvalidOperationException("Tool completion owner does not match its provider: " + definition.Name);
            var usesOfficialName = definition.Name.StartsWith("UnityPipeline_", StringComparison.Ordinal);
            var usesOfficialGroup = string.Equals(definition.Group, "UnityPipeline", StringComparison.Ordinal);
            if ((providerKind == UnityCliToolProviderKind.OfficialAttribute && (!usesOfficialName || !usesOfficialGroup))
                || (providerKind != UnityCliToolProviderKind.OfficialAttribute && (usesOfficialName || usesOfficialGroup)))
                throw new InvalidOperationException("Tool provider kind does not match the reserved official attribute namespace: " + definition.Name);
        }

        /// <summary>descriptor field bound统一按wire UTF-8字节计算。</summary>
        private static int Utf8ByteCount(string value)
        {
            return Encoding.UTF8.GetByteCount(value ?? string.Empty);
        }

        /// <summary>限制公开字符串数组的条目数与单项UTF-8大小。</summary>
        private static bool BoundedStringList(IEnumerable<string> values, int maximumCount, int maximumItemBytes)
        {
            if (values == null) return true;
            var items = values.ToArray();
            return items.Length <= maximumCount
                && items.All(value => !string.IsNullOrWhiteSpace(value) && Utf8ByteCount(value) <= maximumItemBytes);
        }
    }
}
#endif
