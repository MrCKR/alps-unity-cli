#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>prefab.info 内置 Context：只读 Prefab 资产摘要。</summary>
        public sealed class PrefabInfoContext : IUnityCliContext<PrefabInfoContext.Request, PrefabInfoContext.Result>
        {
            public sealed class Request { [UnityCliRequired] public string Path { get; set; } }
            public sealed class Result { public PrefabSummary Prefab { get; set; } }
            public sealed class PrefabSummary
            {
                public string Path { get; set; } public string Guid { get; set; } public string RootName { get; set; } public string AssetType { get; set; }
                public string InstanceStatus { get; set; } public bool IsVariant { get; set; } public PrefabVariantInfo Variant { get; set; }
                public long DependencyCount { get; set; } public SummaryList<DependencyInfo> Dependencies { get; set; } public SummaryList<NestedPrefabInfo> NestedPrefabs { get; set; }
            }
            public sealed class PrefabVariantInfo { public bool IsVariant { get; set; } public string BasePath { get; set; } public string BaseGuid { get; set; } public string SourcePath { get; set; } public string SourceGuid { get; set; } }
            public sealed class SummaryList<T> { public long Count { get; set; } public bool ItemsIncluded { get; set; } public List<T> Items { get; set; } }
            public sealed class DependencyInfo { public string Path { get; set; } public string Guid { get; set; } public string Type { get; set; } }
            public sealed class NestedPrefabInfo { public string Name { get; set; } public string Path { get; set; } public string SourcePath { get; set; } public string SourceGuid { get; set; } public string AssetType { get; set; } public string InstanceStatus { get; set; } }

            // 返回 Context 的发现元数据。
            public ContextDescriptor Describe() => new ContextDescriptor
            {
                Name = "prefab.info", Summary = "Read Prefab asset metadata, dependencies, variant data, and nested Prefab references.",
                Description = "Read-only Prefab asset metadata for one Assets/*.prefab path: GUID, root name, Prefab asset type, instance status, variant source, dependency summary, and nested Prefab references. Does not open Prefab Stage, save assets, or modify the Prefab.",
                Category = "prefab",Source = "built-in", Target = "editor", AvailabilityScope = "editorSession", PlayModeSupport = "supported",
                Examples = new[] { new ContextExample { Description = "Read Prefab metadata", Input = new Request { Path = "Assets/Prefabs/Player.prefab" } } }
            };

            /// <summary>校验并读取 Prefab 资产，不打开或保存 Prefab Stage。</summary>
            public System.Threading.Tasks.ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
            {
                var path = RequirePrefabPath(request == null ? null : request.Path);
                var prefab = LoadPrefabAsset(path);
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Prefab = PrefabSummaryFrom(path, prefab, null, true) });
            }
        }

        // 构造 Prefab 摘要；contentsRoot 仅用于复用已加载的只读 contents。
        private static PrefabInfoContext.PrefabSummary PrefabSummaryFrom(string path, GameObject prefab, GameObject contentsRoot, bool includeItems)
        {
            var dependencies = AssetDatabase.GetDependencies(path, false);
            var assetType = PrefabUtility.GetPrefabAssetType(prefab).ToString();
            return new PrefabInfoContext.PrefabSummary
            {
                Path = path, Guid = AssetDatabase.AssetPathToGUID(path), RootName = prefab.name, AssetType = assetType,
                InstanceStatus = PrefabUtility.GetPrefabInstanceStatus(prefab).ToString(), IsVariant = assetType == "Variant",
                Variant = PrefabVariantInfoFrom(prefab), DependencyCount = dependencies.Length,
                Dependencies = PrefabDependenciesFrom(dependencies, includeItems), NestedPrefabs = NestedPrefabInfosFrom(path, contentsRoot, includeItems)
            };
        }

        // 读取 Prefab 变体的源资产引用。
        private static PrefabInfoContext.PrefabVariantInfo PrefabVariantInfoFrom(GameObject prefab)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
            var sourcePath = source == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(source));
            var sourceGuid = string.IsNullOrEmpty(sourcePath) ? null : AssetDatabase.AssetPathToGUID(sourcePath);
            return new PrefabInfoContext.PrefabVariantInfo
            {
                IsVariant = PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.Variant,
                BasePath = sourcePath, BaseGuid = sourceGuid, SourcePath = sourcePath, SourceGuid = sourceGuid
            };
        }

        // 读取直接依赖列表及其主资产类型。
        private static PrefabInfoContext.SummaryList<PrefabInfoContext.DependencyInfo> PrefabDependenciesFrom(string[] dependencies, bool includeItems)
        {
            var items = new List<PrefabInfoContext.DependencyInfo>();
            if (includeItems)
            {
                foreach (var dependency in dependencies)
                {
                    var type = AssetDatabase.GetMainAssetTypeAtPath(dependency);
                    items.Add(new PrefabInfoContext.DependencyInfo { Path = NormalizeAssetPath(dependency), Guid = AssetDatabase.AssetPathToGUID(dependency), Type = type == null ? null : type.Name });
                }
            }
            return new PrefabInfoContext.SummaryList<PrefabInfoContext.DependencyInfo> { Count = dependencies.Length, ItemsIncluded = includeItems, Items = items };
        }

        // 收集嵌套 Prefab；自行加载的 contents 始终在 finally 中卸载。
        private static PrefabInfoContext.SummaryList<PrefabInfoContext.NestedPrefabInfo> NestedPrefabInfosFrom(string prefabPath, GameObject contentsRoot, bool includeItems)
        {
            var ownsRoot = contentsRoot == null;
            var root = contentsRoot ?? PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var items = new List<PrefabInfoContext.NestedPrefabInfo>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectNestedPrefabInfos(prefabPath, root.transform, root.transform, items, seen, includeItems);
                return new PrefabInfoContext.SummaryList<PrefabInfoContext.NestedPrefabInfo> { Count = seen.Count, ItemsIncluded = includeItems, Items = items };
            }
            finally
            {
                if (ownsRoot) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // 深度优先收集不同路径的嵌套 Prefab 引用。
        private static void CollectNestedPrefabInfos(string prefabPath, Transform root, Transform current, List<PrefabInfoContext.NestedPrefabInfo> items, HashSet<string> seen, bool includeItems)
        {
            if (current != root && PrefabUtility.IsAnyPrefabInstanceRoot(current.gameObject))
            {
                var sourcePath = NormalizeAssetPath(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(current.gameObject));
                if (!string.IsNullOrEmpty(sourcePath) && !string.Equals(sourcePath, prefabPath, StringComparison.OrdinalIgnoreCase))
                {
                    var key = sourcePath + "|" + HierarchyPath(current.gameObject);
                    if (seen.Add(key) && includeItems)
                    {
                        var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
                        items.Add(new PrefabInfoContext.NestedPrefabInfo
                        {
                            Name = current.gameObject.name, Path = HierarchyPath(current.gameObject), SourcePath = sourcePath,
                            SourceGuid = AssetDatabase.AssetPathToGUID(sourcePath), AssetType = source == null ? null : PrefabUtility.GetPrefabAssetType(source).ToString(),
                            InstanceStatus = PrefabUtility.GetPrefabInstanceStatus(current.gameObject).ToString()
                        });
                    }
                }
            }
            foreach (Transform child in current) CollectNestedPrefabInfos(prefabPath, root, child, items, seen, includeItems);
        }
    }
}
#endif
