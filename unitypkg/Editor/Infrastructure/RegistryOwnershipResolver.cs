#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnityCli
{
    /// <summary>Tool name、extension id、assembly identity 与 implementation Type 的一次性编译身份。</summary>
    internal sealed class RegistryToolOwnership
    {
        internal RegistryToolOwnership(string source, string extensionId, Assembly assembly, Type implementationType)
        {
            Source = source;
            ExtensionId = extensionId;
            Assembly = assembly ?? throw new ArgumentNullException("assembly");
            AssemblyIdentity = assembly.GetName().FullName;
            ImplementationType = implementationType ?? throw new ArgumentNullException("implementationType");
        }

        public string Source { get; }
        public string ExtensionId { get; }
        public Assembly Assembly { get; }
        public string AssemblyIdentity { get; }
        public Type ImplementationType { get; }
    }

    /// <summary>generated exact inventory 决定 Extension owner；名称前缀和项目 assembly 名称不参与冒充。</summary>
    internal sealed class RegistryOwnershipResolver
    {
        private readonly HashSet<Assembly> candidateAssemblies;
        private readonly Dictionary<Assembly, string> loadedExtensionAssemblies;
        private readonly Dictionary<string, RegistryToolOwnership> actualPackageInventory = new Dictionary<string, RegistryToolOwnership>(StringComparer.Ordinal);

        private RegistryOwnershipResolver(HashSet<Assembly> candidateAssemblies, Dictionary<Assembly, string> loadedExtensionAssemblies)
        {
            this.candidateAssemblies = candidateAssemblies;
            this.loadedExtensionAssemblies = loadedExtensionAssemblies;
        }

        public static RegistryOwnershipResolver Load(IEnumerable<Assembly> candidates)
        {
            if (candidates == null) throw new ArgumentNullException("candidates");
            var assemblies = new HashSet<Assembly>(candidates.Where(assembly => assembly != null));
            var bridgeAssembly = typeof(UnityCliBridge).Assembly;
            if (assemblies.Count == 0 || !assemblies.Contains(bridgeAssembly))
                throw new InvalidOperationException("Tool provider metadata does not contain the UnityCLI Editor assembly.");

            var expectedExtensions = InventoryEntries()
                .Where(entry => entry.DescriptorOwner.StartsWith("extension:", StringComparison.Ordinal))
                .GroupBy(entry => entry.DescriptorOwner.Substring("extension:".Length), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(entry => entry.AssemblyName).Distinct(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);
            foreach (var expected in expectedExtensions)
            {
                if (string.IsNullOrWhiteSpace(expected.Key) || expected.Value.Length != 1 || string.IsNullOrWhiteSpace(expected.Value[0]))
                    throw new InvalidOperationException("Generated Extension inventory must map each extension id to one exact assembly identity: " + expected.Key + ".");
            }

            var loadedExtensions = new Dictionary<Assembly, string>();
            var loadedExtensionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in assemblies)
            {
                var markers = assembly.GetCustomAttributes(typeof(UnityCliExtensionAssemblyAttribute), false)
                    .Cast<UnityCliExtensionAssemblyAttribute>()
                    .ToArray();
                if (markers.Length > 1)
                    throw new InvalidOperationException("Compiled assembly declares multiple UnityCLI Extension owners: " + assembly.GetName().Name + ".");
                if (markers.Length == 0)
                {
                    if (expectedExtensions.Any(pair => string.Equals(pair.Value[0], assembly.GetName().Name, StringComparison.Ordinal)))
                        throw new InvalidOperationException("Generated Extension assembly is loaded without its exact UnityCliExtensionAssembly marker: " + assembly.GetName().Name + ".");
                    continue;
                }
                if (assembly == bridgeAssembly)
                    throw new InvalidOperationException("The UnityCLI Editor assembly cannot declare an Extension owner.");

                var extensionId = markers[0].ExtensionId;
                string[] expectedAssemblyNames;
                if (string.IsNullOrWhiteSpace(extensionId) || !expectedExtensions.TryGetValue(extensionId, out expectedAssemblyNames))
                    throw new InvalidOperationException("Compiled assembly declares an unknown UnityCLI Extension owner: " + extensionId + ".");
                if (!string.Equals(expectedAssemblyNames[0], assembly.GetName().Name, StringComparison.Ordinal))
                    throw new InvalidOperationException("Extension assembly marker does not match generated exact assembly identity: " + extensionId + ".");
                if (!loadedExtensionIds.Add(extensionId))
                    throw new InvalidOperationException("Multiple compiled assemblies declare the same UnityCLI Extension owner: " + extensionId + ".");
                loadedExtensions.Add(assembly, extensionId);
            }
            return new RegistryOwnershipResolver(assemblies, loadedExtensions);
        }

        /// <summary>每个 package Tool 在注册前绑定 generated owner、exact assembly 与 compiled Type。</summary>
        public RegistryToolOwnership Resolve(Type implementationType, string toolName)
        {
            if (implementationType == null) throw new ArgumentNullException("implementationType");
            if (string.IsNullOrWhiteSpace(toolName)) throw new ArgumentException("Tool name is required.", "toolName");
            var assembly = implementationType.Assembly;
            if (!candidateAssemblies.Contains(assembly))
                throw new InvalidOperationException("Tool implementation assembly is outside the provider metadata set: " + implementationType.FullName + ".");

            UnityCliToolInventoryEntry expected;
            if (!UnityCliToolCompletionRegistry.TryGetInventory(toolName, out expected))
            {
                if (assembly == typeof(UnityCliBridge).Assembly || loadedExtensionAssemblies.ContainsKey(assembly))
                    throw new InvalidOperationException("Package Tool has no generated exact inventory entry: " + toolName + ".");
                return new RegistryToolOwnership("project", null, assembly, implementationType);
            }

            if (string.IsNullOrWhiteSpace(expected.AssemblyName) || string.IsNullOrWhiteSpace(expected.ImplementationType)
                || !string.Equals(expected.AssemblyName, assembly.GetName().Name, StringComparison.Ordinal)
                || !string.Equals(expected.ImplementationType, implementationType.FullName, StringComparison.Ordinal))
                throw new InvalidOperationException("Tool owner does not match the generated exact assembly identity and implementation type: " + toolName + ".");

            RegistryToolOwnership ownership;
            if (string.Equals(expected.DescriptorOwner, "built-in", StringComparison.Ordinal))
            {
                if (assembly != typeof(UnityCliBridge).Assembly)
                    throw new InvalidOperationException("Built-in Tool owner does not match its exact compiled assembly: " + toolName + ".");
                ownership = new RegistryToolOwnership("built-in", null, assembly, implementationType);
            }
            else if (expected.DescriptorOwner.StartsWith("extension:", StringComparison.Ordinal))
            {
                var extensionId = expected.DescriptorOwner.Substring("extension:".Length);
                string loadedExtensionId;
                if (!loadedExtensionAssemblies.TryGetValue(assembly, out loadedExtensionId)
                    || !string.Equals(loadedExtensionId, extensionId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Extension Tool owner does not match its generated extension and exact compiled assembly: " + toolName + ".");
                ownership = new RegistryToolOwnership("extension", extensionId, assembly, implementationType);
            }
            else
            {
                throw new InvalidOperationException("Live package Tool has a non-package generated owner: " + toolName + ".");
            }

            if (actualPackageInventory.ContainsKey(toolName))
                throw new InvalidOperationException("Generated Tool inventory contains duplicate actual implementation: " + toolName + ".");
            actualPackageInventory.Add(toolName, ownership);
            return ownership;
        }

        /// <summary>publication 前按已加载 compiled assembly 验证 expected/actual 全集，缺项、多项和跨 Extension 全部失败。</summary>
        public void ValidateComplete()
        {
            var bridgeAssemblyName = typeof(UnityCliBridge).Assembly.GetName().Name;
            var expected = InventoryEntries()
                .Where(entry => string.Equals(entry.AssemblyName, bridgeAssemblyName, StringComparison.Ordinal)
                    || loadedExtensionAssemblies.Any(pair => string.Equals(pair.Key.GetName().Name, entry.AssemblyName, StringComparison.Ordinal)
                        && string.Equals(pair.Value, entry.DescriptorOwner.StartsWith("extension:", StringComparison.Ordinal) ? entry.DescriptorOwner.Substring("extension:".Length) : string.Empty, StringComparison.Ordinal)))
                .ToDictionary(entry => entry.ToolName, entry => entry, StringComparer.Ordinal);

            var missing = expected.Keys.Where(name => !actualPackageInventory.ContainsKey(name)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            var extra = actualPackageInventory.Keys.Where(name => !expected.ContainsKey(name)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (missing.Length > 0 || extra.Length > 0)
                throw new InvalidOperationException("Generated Tool inventory expected/actual set mismatch; missing=" + string.Join(",", missing) + "; extra=" + string.Join(",", extra) + ".");

            foreach (var pair in expected)
            {
                var actual = actualPackageInventory[pair.Key];
                var expectedOwner = pair.Value.DescriptorOwner;
                var actualOwner = actual.Source == "extension" ? "extension:" + actual.ExtensionId : actual.Source;
                if (!string.Equals(expectedOwner, actualOwner, StringComparison.Ordinal)
                    || !string.Equals(pair.Value.AssemblyName, actual.Assembly.GetName().Name, StringComparison.Ordinal)
                    || !string.Equals(pair.Value.ImplementationType, actual.ImplementationType.FullName, StringComparison.Ordinal))
                    throw new InvalidOperationException("Generated Tool inventory actual identity differs from expected identity: " + pair.Key + ".");
            }
        }

        private static IEnumerable<UnityCliToolInventoryEntry> InventoryEntries()
        {
            return UnityCliToolCompletionRegistry.InventoryEntries();
        }
    }
}
#endif
