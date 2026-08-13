#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace UnityCli
{
    internal sealed class RegistryOwnershipResolver
    {
        private readonly Dictionary<Type, string> extensionClaims;

        private RegistryOwnershipResolver(Dictionary<Type, string> extensionClaims)
        {
            this.extensionClaims = extensionClaims;
        }

        /// <summary>只在已证明可能贡献Tool的程序集内解析Extension类型owner。</summary>
        public static RegistryOwnershipResolver Load(IEnumerable<Assembly> candidateAssemblies)
        {
            if (candidateAssemblies == null) throw new ArgumentNullException("candidateAssemblies");
            var claims = new Dictionary<string, string>(StringComparer.Ordinal);
            var packageRoot = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Packages", "com.unity_cli");
            var extensionRoot = Path.Combine(packageRoot, "Extensions");
            var installedIds = ReadInstalledExtensionIds(Path.Combine(packageRoot, "unity_cli.extensions.manifest.json"));
            if (!Directory.Exists(extensionRoot))
            {
                if (installedIds.Count > 0) throw new InvalidOperationException("Extension registry has installed entries but no Extensions directory.");
                return new RegistryOwnershipResolver(new Dictionary<Type, string>());
            }

            var manifestIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var manifestPath in Directory.GetFiles(extensionRoot, "extension.json", SearchOption.AllDirectories))
            {
                IDictionary<string, object> manifest;
                try
                {
                    manifest = UnityCliSerializer.Parse(File.ReadAllText(manifestPath)) as IDictionary<string, object>;
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException("Extension registry manifest is unreadable: " + manifestPath, exception);
                }
                if (manifest == null) throw new InvalidOperationException("Extension registry manifest must be a JSON object: " + manifestPath);

                var extensionId = StringValue(manifest, "id");
                var contributions = ObjectValue(manifest, "contributions");
                var entries = contributions == null ? null : ArrayValue(contributions, "csharpEntries");
                if (string.IsNullOrWhiteSpace(extensionId) || entries == null)
                {
                    throw new InvalidOperationException("Extension registry manifest has no id or contributions.csharpEntries: " + manifestPath);
                }
                if (!manifestIds.Add(extensionId)) throw new InvalidOperationException("Duplicate extension registry manifest id: " + extensionId);
                if (!installedIds.Contains(extensionId)) throw new InvalidOperationException("Extension manifest is not claimed by the install manifest: " + extensionId);

                foreach (var value in entries)
                {
                    var entry = value as IDictionary<string, object>;
                    var typeName = entry == null ? null : StringValue(entry, "type");
                    if (string.IsNullOrWhiteSpace(typeName)) throw new InvalidOperationException("Extension registry contains an empty C# type claim: " + manifestPath);
                    string owner;
                    if (claims.TryGetValue(typeName, out owner) && !string.Equals(owner, extensionId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Extension registry type claim conflict for " + typeName + ": " + owner + " and " + extensionId + ".");
                    }
                    claims[typeName] = extensionId;
                }
            }

            foreach (var extensionId in installedIds)
            {
                if (!manifestIds.Contains(extensionId)) throw new InvalidOperationException("Extension install manifest has an orphan entry: " + extensionId);
            }

            var loadedTypes = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
            foreach (var assembly in candidateAssemblies)
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException exception) { throw new InvalidOperationException("Unable to inspect every type in Tool provider assembly " + assembly.FullName + ".", exception); }
                catch (Exception exception) { throw new InvalidOperationException("Unable to inspect Tool provider assembly " + assembly.FullName + ".", exception); }
                foreach (var type in types)
                {
                    if (type == null || type.FullName == null) continue;
                    List<Type> matches;
                    if (!loadedTypes.TryGetValue(type.FullName, out matches)) loadedTypes[type.FullName] = matches = new List<Type>();
                    matches.Add(type);
                }
            }

            var resolvedClaims = new Dictionary<Type, string>();
            foreach (var claim in claims)
            {
                List<Type> matches;
                if (!loadedTypes.TryGetValue(claim.Key, out matches)) throw new InvalidOperationException("Extension registry has an orphan C# type claim: " + claim.Key + " (" + claim.Value + ").");
                if (matches.Count != 1) throw new InvalidOperationException("Extension registry C# type claim is ambiguous: " + claim.Key + " (" + claim.Value + ").");
                resolvedClaims.Add(matches[0], claim.Value);
            }
            return new RegistryOwnershipResolver(resolvedClaims);
        }

        public string Resolve(Type implementationType)
        {
            if (implementationType == null) throw new ArgumentNullException("implementationType");
            string extensionId;
            if (extensionClaims.TryGetValue(implementationType, out extensionId)) return "extension";
            return implementationType.Assembly == typeof(UnityCliBridge).Assembly ? "built-in" : "project";
        }

        private static HashSet<string> ReadInstalledExtensionIds(string path)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (!File.Exists(path)) return result;
            var manifest = UnityCliSerializer.Parse(File.ReadAllText(path)) as IDictionary<string, object>;
            var entries = manifest == null ? null : ArrayValue(manifest, "extensions");
            if (entries == null) throw new InvalidOperationException("Extension install manifest is invalid: " + path);
            foreach (var value in entries)
            {
                var entry = value as IDictionary<string, object>;
                var extensionId = entry == null ? null : StringValue(entry, "extensionId");
                if (string.IsNullOrWhiteSpace(extensionId) || !result.Add(extensionId))
                    throw new InvalidOperationException("Extension install manifest contains an invalid or duplicate entry: " + path);
            }
            return result;
        }

        private static string StringValue(IDictionary<string, object> value, string key)
        {
            object result;
            return value.TryGetValue(key, out result) ? result as string : null;
        }

        private static IDictionary<string, object> ObjectValue(IDictionary<string, object> value, string key)
        {
            object result;
            return value.TryGetValue(key, out result) ? result as IDictionary<string, object> : null;
        }

        private static IEnumerable ArrayValue(IDictionary<string, object> value, string key)
        {
            object result;
            return value.TryGetValue(key, out result) ? result as IEnumerable : null;
        }
    }
}
#endif
