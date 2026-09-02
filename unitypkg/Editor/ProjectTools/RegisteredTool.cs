#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    internal sealed class RegisteredTool
    {
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public string Group { get; set; }
        public string Source { get; set; }
        public string InputSchema { get; set; }
        public string OutputSchema { get; set; }
        public bool DestructiveHint { get; set; }
        public string[] SideEffects { get; set; }
        public bool SupportsDryRun { get; set; }
        public string[] Permissions { get; set; }
        public string[] RequiresPackages { get; set; }
        public object Examples { get; set; }
        public string Target { get; set; }
        public string AvailabilityScope { get; set; }
        public string PlayModeSupport { get; set; }
        public string ContractProfile { get; set; }
        public int DefaultTimeoutMs { get; set; }
        public int MaxTimeoutMs { get; set; }
        public Type RequestType { get; set; }
        public Type ResultType { get; set; }
        public UnityCliToolCompletionContract CompletionContract { get; set; }
        internal OfficialAttributeToolBinding OfficialAttributeBinding { get; set; }
        public Func<object, UnityCliProjectToolContext, CancellationToken, ValueTask<object>> InvokeAsync { get; set; }
    }

    internal enum UnityCliToolProviderKind
    {
        BuiltIn,
        Extension,
        ProjectInterface,
        OfficialAttribute
    }

    /// <summary>041 的内部读取边界；实现只能返回当前domain已经发布的同一个只读快照。</summary>
    internal interface IUnityCliToolCatalogSnapshotReader
    {
        UnityCliToolCatalogSnapshot Read();
    }

    /// <summary>一次domain构建的终态；字典在构造时复制并封装，发布后不再接受写入。</summary>
    internal sealed class UnityCliToolCatalogSnapshot
    {
        internal UnityCliToolCatalogSnapshot(
            Dictionary<string, RegisteredTool> tools,
            int assemblyCount,
            int providerAssemblyCount,
            int builtInToolCount,
            int extensionToolCount,
            int projectInterfaceToolCount,
            int officialAttributeToolCount,
            long elapsedMilliseconds)
        {
            Tools = new ReadOnlyDictionary<string, RegisteredTool>(new Dictionary<string, RegisteredTool>(tools, StringComparer.Ordinal));
            AssemblyCount = assemblyCount;
            ProviderAssemblyCount = providerAssemblyCount;
            BuiltInToolCount = builtInToolCount;
            ExtensionToolCount = extensionToolCount;
            ProjectInterfaceToolCount = projectInterfaceToolCount;
            OfficialAttributeToolCount = officialAttributeToolCount;
            ElapsedMilliseconds = elapsedMilliseconds;
        }

        public IReadOnlyDictionary<string, RegisteredTool> Tools { get; private set; }
        public int ToolCount { get { return Tools.Count; } }
        public int AssemblyCount { get; private set; }
        public int ProviderAssemblyCount { get; private set; }
        public int BuiltInToolCount { get; private set; }
        public int ExtensionToolCount { get; private set; }
        public int ProjectInterfaceToolCount { get; private set; }
        public int OfficialAttributeToolCount { get; private set; }
        public long ElapsedMilliseconds { get; private set; }
        public int DiscoveryPassCount { get { return 1; } }
        public int AssetDatabaseOperationCount { get { return 0; } }
        public int SourceFileReadCount { get { return 0; } }
        public int PersistedToolValueOperationCount { get { return 0; } }
        public int PerToolSuccessLogCount { get { return 0; } }
    }

    /// <summary>纯内存pending registry；任一验证失败后永久fault，不能发布部分结果。</summary>
    internal sealed class UnityCliToolCatalogBuilder
    {
        private readonly Dictionary<string, RegisteredTool> pending = new Dictionary<string, RegisteredTool>(StringComparer.Ordinal);
        private bool discoveryStarted;
        private bool faulted;
        private bool published;
        private int assemblyCount;
        private int providerAssemblyCount;
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
            assemblyCount = loadedAssemblyCount;
            providerAssemblyCount = candidateProviderAssemblyCount;
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

        internal void Abort()
        {
            faulted = true;
        }

        internal UnityCliToolCatalogSnapshot Publish(long elapsedMilliseconds)
        {
            if (!discoveryStarted || faulted || published) throw new InvalidOperationException("Tool catalog cannot publish an incomplete or faulted table.");
            if (builtInToolCount + extensionToolCount + projectInterfaceToolCount + officialAttributeToolCount != pending.Count)
                throw new InvalidOperationException("Tool catalog provider counts do not match the pending table.");
            published = true;
            return new UnityCliToolCatalogSnapshot(
                pending,
                assemblyCount,
                providerAssemblyCount,
                builtInToolCount,
                extensionToolCount,
                projectInterfaceToolCount,
                officialAttributeToolCount,
                Math.Max(0, elapsedMilliseconds));
        }

        private static void Validate(RegisteredTool definition, UnityCliToolProviderKind providerKind)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.Name) || string.IsNullOrWhiteSpace(definition.Summary)
                || string.IsNullOrWhiteSpace(definition.Description) || string.IsNullOrWhiteSpace(definition.Group)
                || definition.Examples == null || definition.InvokeAsync == null)
                throw new ArgumentException("Tool registration requires name, summary, description, group, and a typed async invoker.");
            if (definition.Name.Length > 256 || definition.Summary.Length > 512 || definition.Group.Length > 256)
                throw new ArgumentException("Tool name, summary, or group exceeds the catalog transport bound: " + definition.Name);
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
            if (definition.SideEffects == null || definition.Permissions == null || definition.RequiresPackages == null
                || !(definition.Examples is IEnumerable) || definition.Examples is string
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
            if (definition.OfficialAttributeBinding != null
                && (definition.OfficialAttributeBinding.Method == null || definition.OfficialAttributeBinding.DeclaredResultType == null
                    || definition.OfficialAttributeBinding.Parameters == null))
                throw new InvalidOperationException("Official attribute Tool binding is incomplete: " + definition.Name);
            var completionOwner = definition.CompletionContract.DescriptorOwner ?? string.Empty;
            if ((providerKind == UnityCliToolProviderKind.BuiltIn && completionOwner != "built-in")
                || (providerKind == UnityCliToolProviderKind.Extension && !completionOwner.StartsWith("extension:", StringComparison.Ordinal))
                || ((providerKind == UnityCliToolProviderKind.ProjectInterface || providerKind == UnityCliToolProviderKind.OfficialAttribute)
                    && completionOwner != "project"))
                throw new InvalidOperationException("Tool completion owner does not match its provider: " + definition.Name);
            var usesOfficialName = definition.Name.StartsWith("UnityPipeline_", StringComparison.Ordinal);
            var usesOfficialGroup = string.Equals(definition.Group, "UnityPipeline", StringComparison.Ordinal);
            if ((providerKind == UnityCliToolProviderKind.OfficialAttribute && (!usesOfficialName || !usesOfficialGroup))
                || (providerKind != UnityCliToolProviderKind.OfficialAttribute && (usesOfficialName || usesOfficialGroup)))
                throw new InvalidOperationException("Tool provider kind does not match the reserved official attribute namespace: " + definition.Name);
        }
    }
}
#endif
