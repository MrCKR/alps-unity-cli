#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// prefab 域 IUnityCliTool 共享的基础设施 helper:Prefab 路径校验、Prefab 资产加载、
    /// Prefab Stage 反射操作、依赖/嵌套 Prefab 摘要构造、层级路径。仅供 prefab 域自包含工具复用;
    /// 不实现 IUnityCliTool,不参与工具发现。
    /// </summary>
    public static class PrefabToolSupport
    {
        /// <summary>拦截编译/导入/构建/PlayMode 过渡期的写入请求,避免脏状态写入。</summary>
        public static void EnsureEditorNotBusy(string command)
        {
            SceneToolSupport.EnsureEditorNotBusy(command);
        }

        /// <summary>Wrapper for ObjectRefResolver.FindGameObjectByPath,供 prefab 工具使用(ObjectRefResolver 是 internal nested)。</summary>
        public static GameObject ResolveGameObjectByPath(string path)
        {
            return UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(path);
        }

        /// <summary>按名称/实例ID/层级路径首个匹配 GameObject;无匹配返回 null(不抛,与旧 ResolveGameObjectTarget.Target 语义一致)。</summary>
        public static GameObject ResolveGameObjectFirstOrDefault(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
            {
                if (gameObject.name == target
                    || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target
                    || GameObjectToolSupport.HierarchyPath(gameObject) == target)
                {
                    return gameObject;
                }
            }
            return null;
        }

        // 校验并返回 Assets/*.prefab 路径;空或格式不符抛 BridgeCommandException。
        public static string RequirePrefabPath(string rawPath)
        {
            var path = AssetToolSupport.NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_PATH_REQUIRED", "Pass an Assets/*.prefab path.", "Use tools run prefab.info --input '{\"path\":\"<path>\"}' or pass an Assets/*.prefab path.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_PATH_INVALID", "Prefab path must be under Assets/ and end with .prefab: " + path, "Pass a Unity asset path such as Assets/Prefabs/Player.prefab.");
            }
            return path;
        }

        /// <summary>校验 Prefab 输出路径(语义同 RequirePrefabPath)。</summary>
        public static string RequirePrefabOutputPath(string rawPath)
        {
            return RequirePrefabPath(rawPath);
        }

        /// <summary>加载 Assets/*.prefab 资产;不存在或非 Prefab 抛 BridgeCommandException。</summary>
        public static GameObject LoadPrefabAsset(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_NOT_FOUND", "Prefab asset not found: " + path, "Check the path or run tools run asset.search --input '{\"filter\":\"t:Prefab\"}' to discover the path.");
            }
            if (PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.NotAPrefab)
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_NOT_PREFAB", "Asset is not a Prefab: " + path, "Pass an Assets/*.prefab asset path.");
            }
            return prefab;
        }

        // ─── Prefab Stage 反射操作(PrefabStage API 跨版本,统一反射访问) ───

        /// <summary>获取当前打开的 Prefab Stage(无则 null)。</summary>
        public static object CurrentPrefabStage()
        {
            return InvokePrefabStageUtility("GetCurrentPrefabStage", null);
        }

        /// <summary>要求当前有打开的 Prefab Stage;可选校验路径一致。</summary>
        public static object RequireCurrentPrefabStage(string requestedPath)
        {
            var stage = CurrentPrefabStage();
            if (stage == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_STAGE_NOT_OPEN", "No Prefab Stage is currently open.", "Run tools run prefab.stage-open --input '{\"path\":\"<path>\"}' first.");
            }
            var path = StageAssetPath(stage);
            if (!string.IsNullOrEmpty(requestedPath) && !string.Equals(AssetToolSupport.NormalizeAssetPath(requestedPath), path, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_STAGE_MISMATCH", "Current Prefab Stage is " + path + ", not " + requestedPath, "Run stage-save/stage-close without path or use the currently open Prefab path.");
            }
            return stage;
        }

        /// <summary>打开指定路径的 Prefab Stage;成功返回 true。</summary>
        public static bool OpenPrefabStage(string path)
        {
            return InvokePrefabStageUtility("OpenPrefab", new object[] { path }) != null;
        }

        /// <summary>保存当前 Prefab Stage;成功返回 true。</summary>
        public static bool SavePrefabStage(object stage)
        {
            var method = stage.GetType().GetMethod("SavePrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null)
            {
                method.Invoke(stage, null);
                return true;
            }
            var root = StageRoot(stage);
            var path = StageAssetPath(stage);
            if (root == null || string.IsNullOrEmpty(path))
            {
                return false;
            }
            PrefabUtility.SaveAsPrefabAsset(root, path);
            return true;
        }

        /// <summary>关闭当前 Prefab Stage;成功返回 true。</summary>
        public static bool ClosePrefabStage()
        {
            if (InvokePrefabStageUtilityVoid("CloseCurrentPrefabStage", null))
            {
                return true;
            }
            var method = typeof(StageUtility).GetMethod("GoBackToPreviousStage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                return false;
            }
            method.Invoke(null, null);
            return true;
        }

        /// <summary>读取 Prefab Stage 的资产路径。</summary>
        public static string StageAssetPath(object stage)
        {
            var property = stage.GetType().GetProperty("assetPath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : AssetToolSupport.NormalizeAssetPath(property.GetValue(stage, null) as string);
        }

        /// <summary>读取 Prefab Stage 的 prefabContentsRoot。</summary>
        public static GameObject StageRoot(object stage)
        {
            var property = stage.GetType().GetProperty("prefabContentsRoot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(stage, null) as GameObject;
        }

        /// <summary>判断 Prefab Stage 是否有未保存改动。</summary>
        public static bool StageIsDirty(object stage)
        {
            var root = StageRoot(stage);
            return root != null && root.scene.isDirty;
        }

        /// <summary>从当前实际 Prefab Stage 构造读回结果。</summary>
        public static PrefabStageInfo BuildStageInfo(string action, object stage)
        {
            return new PrefabStageInfo
            {
                Action = action,
                Path = StageAssetPath(stage),
                Dirty = StageIsDirty(stage),
                Closed = false
            };
        }

        /// <summary>从已验证关闭前的实际路径构造关闭终态。</summary>
        public static PrefabStageInfo BuildClosedStageInfo(string path)
        {
            return new PrefabStageInfo { Action = "close", Path = path, Closed = true };
        }

        private static object InvokePrefabStageUtility(string methodName, object[] args)
        {
            var method = PrefabStageUtilityMethod(methodName, args == null ? 0 : args.Length);
            return method == null ? null : method.Invoke(null, args);
        }

        private static bool InvokePrefabStageUtilityVoid(string methodName, object[] args)
        {
            var method = PrefabStageUtilityMethod(methodName, args == null ? 0 : args.Length);
            if (method == null)
            {
                return false;
            }
            method.Invoke(null, args);
            return true;
        }

        private static MethodInfo PrefabStageUtilityMethod(string methodName, int parameterCount)
        {
            var type = typeof(PrefabStageUtility);
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var method in type.GetMethods(flags))
            {
                if (method.Name == methodName && method.GetParameters().Length == parameterCount)
                {
                    return method;
                }
            }
            return null;
        }

        // ─── DTO 构造 helper ───────────────────────────────────────────────

        /// <summary>构造 Prefab 资产摘要(含变体/依赖/嵌套 Prefab)。</summary>
        public static PrefabInfo BuildPrefabInfo(string path, GameObject prefab, GameObject contentsRoot, bool includeSummaryDetails)
        {
            var dependencies = AssetDatabase.GetDependencies(path, false);
            return new PrefabInfo
            {
                Path = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                RootName = prefab.name,
                AssetType = PrefabUtility.GetPrefabAssetType(prefab).ToString(),
                InstanceStatus = PrefabUtility.GetPrefabInstanceStatus(prefab).ToString(),
                IsVariant = PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.Variant,
                Variant = BuildVariantInfo(prefab),
                DependencyCount = dependencies.Length,
                Dependencies = BuildDependencySummary(dependencies, includeSummaryDetails),
                NestedPrefabs = BuildNestedPrefabSummary(path, contentsRoot, includeSummaryDetails)
            };
        }

        /// <summary>构造变体来源信息。</summary>
        public static PrefabVariantInfo BuildVariantInfo(GameObject prefab)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
            var sourcePath = source == null ? null : AssetToolSupport.NormalizeAssetPath(AssetDatabase.GetAssetPath(source));
            var sourceGuid = string.IsNullOrEmpty(sourcePath) ? null : AssetDatabase.AssetPathToGUID(sourcePath);
            return new PrefabVariantInfo
            {
                IsVariant = PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.Variant,
                BasePath = sourcePath,
                BaseGuid = sourceGuid,
                SourcePath = sourcePath,
                SourceGuid = sourceGuid
            };
        }

        /// <summary>构造依赖摘要(可选附带 items)。</summary>
        public static DependencySummary BuildDependencySummary(string[] dependencies, bool includeItems)
        {
            var items = new List<DependencyItem>();
            if (includeItems)
            {
                foreach (var dependency in dependencies)
                {
                    var type = AssetDatabase.GetMainAssetTypeAtPath(dependency);
                    items.Add(new DependencyItem
                    {
                        Path = AssetToolSupport.NormalizeAssetPath(dependency),
                        Guid = AssetDatabase.AssetPathToGUID(dependency),
                        Type = type == null ? null : type.Name
                    });
                }
            }
            return new DependencySummary { Count = dependencies.Length, ItemsIncluded = includeItems, Items = items };
        }

        /// <summary>构造嵌套 Prefab 引用摘要;contentsRoot 为空时加载 Prefab 内容。</summary>
        public static NestedPrefabSummary BuildNestedPrefabSummary(string prefabPath, GameObject contentsRoot, bool includeItems)
        {
            var root = contentsRoot;
            var unload = false;
            if (root == null)
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                unload = true;
            }
            try
            {
                var items = new List<NestedPrefabItem>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectNestedPrefabReferences(prefabPath, root.transform, root.transform, items, seen, includeItems);
                return new NestedPrefabSummary { Count = seen.Count, ItemsIncluded = includeItems, Items = items };
            }
            finally
            {
                if (unload) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void CollectNestedPrefabReferences(string prefabPath, Transform root, Transform current, List<NestedPrefabItem> items, HashSet<string> seen, bool includeItems)
        {
            if (current != root && PrefabUtility.IsAnyPrefabInstanceRoot(current.gameObject))
            {
                var sourcePath = AssetToolSupport.NormalizeAssetPath(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(current.gameObject));
                if (!string.IsNullOrEmpty(sourcePath) && !string.Equals(sourcePath, prefabPath, StringComparison.OrdinalIgnoreCase))
                {
                    var key = sourcePath + "|" + GameObjectToolSupport.HierarchyPath(current.gameObject);
                    if (seen.Add(key) && includeItems)
                    {
                        var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
                        items.Add(new NestedPrefabItem
                        {
                            Name = current.gameObject.name,
                            Path = GameObjectToolSupport.HierarchyPath(current.gameObject),
                            SourcePath = sourcePath,
                            SourceGuid = AssetDatabase.AssetPathToGUID(sourcePath),
                            AssetType = source == null ? null : PrefabUtility.GetPrefabAssetType(source).ToString(),
                            InstanceStatus = PrefabUtility.GetPrefabInstanceStatus(current.gameObject).ToString()
                        });
                    }
                }
            }
            foreach (Transform child in current)
            {
                CollectNestedPrefabReferences(prefabPath, root, child, items, seen, includeItems);
            }
        }

        /// <summary>构造 Prefab 层级项摘要。</summary>
        public static PrefabHierarchyItem BuildHierarchyItem(GameObject gameObject)
        {
            var componentTypes = new List<string>();
            foreach (var component in gameObject.GetComponents<Component>())
            {
                componentTypes.Add(component != null ? component.GetType().Name : "MissingScript");
            }
            return new PrefabHierarchyItem
            {
                Id = gameObject.GetInstanceID(),
                Name = gameObject.name,
                Path = GameObjectToolSupport.HierarchyPath(gameObject),
                Active = gameObject.activeSelf,
                ActiveInHierarchy = gameObject.activeInHierarchy,
                Tag = gameObject.tag,
                Layer = gameObject.layer,
                ChildCount = gameObject.transform.childCount,
                ChildrenTruncated = gameObject.transform.childCount > 0,
                ComponentCount = componentTypes.Count,
                ComponentTypes = componentTypes,
                Components = componentTypes
            };
        }
    }

    // ─── prefab 域共享 DTO ────────────────────────────────────────────────

    public sealed class PrefabInfo
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string RootName { get; set; }
        public string AssetType { get; set; }
        public string InstanceStatus { get; set; }
        public bool IsVariant { get; set; }
        public PrefabVariantInfo Variant { get; set; }
        public long DependencyCount { get; set; }
        public DependencySummary Dependencies { get; set; }
        public NestedPrefabSummary NestedPrefabs { get; set; }
    }

    public sealed class PrefabVariantInfo
    {
        public bool IsVariant { get; set; }
        public string BasePath { get; set; }
        public string BaseGuid { get; set; }
        public string SourcePath { get; set; }
        public string SourceGuid { get; set; }
    }

    public sealed class DependencySummary
    {
        public long Count { get; set; }
        public bool ItemsIncluded { get; set; }
        public List<DependencyItem> Items { get; set; }
    }

    public sealed class DependencyItem
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string Type { get; set; }
    }

    public sealed class NestedPrefabSummary
    {
        public long Count { get; set; }
        public bool ItemsIncluded { get; set; }
        public List<NestedPrefabItem> Items { get; set; }
    }

    public sealed class NestedPrefabItem
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string SourcePath { get; set; }
        public string SourceGuid { get; set; }
        public string AssetType { get; set; }
        public string InstanceStatus { get; set; }
    }

    public sealed class PrefabHierarchyItem
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public bool Active { get; set; }
        public bool ActiveInHierarchy { get; set; }
        public string Tag { get; set; }
        public long Layer { get; set; }
        public long ChildCount { get; set; }
        public bool ChildrenTruncated { get; set; }
        public long ComponentCount { get; set; }
        public List<string> ComponentTypes { get; set; }
        public List<string> Components { get; set; }
    }

    /// <summary>计划副作用项,prefab 域 mutation 工具 Result 共享。</summary>
    public sealed class PrefabPlannedEffect
    {
        public string Type { get; set; }
        public string Path { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
        public string Property { get; set; }
        public string AddChild { get; set; }
        public string RemoveChild { get; set; }
    }

    /// <summary>Prefab Stage 操作结果。</summary>
    public sealed class PrefabStageInfo
    {
        public string Action { get; set; }
        public string Path { get; set; }
        public bool Dirty { get; set; }
        public bool Closed { get; set; }
    }
}
#endif
