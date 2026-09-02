#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnityCli
{
    /// <summary>仅使用已编译assembly identity与生成completion owner解析Tool归属，不读取安装或源码文件。</summary>
    internal sealed class RegistryOwnershipResolver
    {
        private const string ExtensionAssemblyPrefix = "UnityCli.Extensions.";
        private readonly HashSet<Assembly> candidateAssemblies;

        private RegistryOwnershipResolver(HashSet<Assembly> candidateAssemblies)
        {
            this.candidateAssemblies = candidateAssemblies;
        }

        public static RegistryOwnershipResolver Load(IEnumerable<Assembly> candidates)
        {
            if (candidates == null) throw new ArgumentNullException("candidates");
            var assemblies = new HashSet<Assembly>(candidates.Where(assembly => assembly != null));
            if (assemblies.Count == 0 || !assemblies.Contains(typeof(UnityCliBridge).Assembly))
                throw new InvalidOperationException("Tool provider metadata does not contain the UnityCLI Editor assembly.");
            return new RegistryOwnershipResolver(assemblies);
        }

        /// <summary>生成owner与实现assembly必须双向一致；伪装或缺失owner使整张pending table失败。</summary>
        public string Resolve(Type implementationType, string toolName)
        {
            if (implementationType == null) throw new ArgumentNullException("implementationType");
            if (string.IsNullOrWhiteSpace(toolName)) throw new ArgumentException("Tool name is required.", "toolName");
            var assembly = implementationType.Assembly;
            if (!candidateAssemblies.Contains(assembly))
                throw new InvalidOperationException("Tool implementation assembly is outside the provider metadata set: " + implementationType.FullName + ".");

            UnityCliToolCompletionContract generated;
            if (UnityCliToolCompletionRegistry.TryGet(toolName, out generated))
            {
                if (string.Equals(generated.DescriptorOwner, "built-in", StringComparison.Ordinal))
                {
                    if (assembly != typeof(UnityCliBridge).Assembly)
                        throw new InvalidOperationException("Built-in Tool owner does not match its compiled assembly: " + toolName + ".");
                    return "built-in";
                }
                if (generated.DescriptorOwner.StartsWith("extension:", StringComparison.Ordinal))
                {
                    var assemblyName = assembly.GetName().Name ?? string.Empty;
                    if (assembly == typeof(UnityCliBridge).Assembly || !assemblyName.StartsWith(ExtensionAssemblyPrefix, StringComparison.Ordinal))
                        throw new InvalidOperationException("Extension Tool owner does not match its compiled assembly: " + toolName + ".");
                    return "extension";
                }
                throw new InvalidOperationException("Live Tool has a non-live generated owner: " + toolName + ".");
            }

            var candidateName = assembly.GetName().Name ?? string.Empty;
            if (assembly == typeof(UnityCliBridge).Assembly || candidateName.StartsWith(ExtensionAssemblyPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Package Tool has no generated descriptor owner: " + toolName + ".");
            return "project";
        }
    }
}
#endif
