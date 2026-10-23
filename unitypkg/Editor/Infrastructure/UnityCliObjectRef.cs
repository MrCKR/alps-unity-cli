#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    public interface IUnityCliObjectReferenceInput
    {
        int? InstanceId { get; }
        string AssetPath { get; }
        string Guid { get; }
        string Path { get; }
        string Name { get; }
        string Target { get; }
        int? ComponentInstanceId { get; }
        int? ComponentIndex { get; }
        string Component { get; }
        string ComponentType { get; }
    }

    public interface IUnityCliObjectSerializationInput
    {
        int? MaxDepth { get; }
        int? MaxArrayItems { get; }
        bool? IncludeFields { get; }
        bool? IncludeProperties { get; }
        bool? DeepSerialization { get; }
        List<string> Paths { get; }
        object ViewQuery { get; }
        string NamePattern { get; }
        UnityCliReadBudgetInput Budget { get; }
    }

    public static partial class UnityCliBridge
    {
        /// <summary>
        /// 统一引用解析：支持 instanceId / path / name / assetPath / guid / component index|type|instanceId。
        /// 对齐 IvanMurzak ObjectRef/GameObjectRef/ComponentRef/AssetObjectRef/SceneRef 模型，
        /// 使用单一解析入口，并按稳定优先级解析引用。
        /// </summary>
        internal static class ObjectRefResolver
        {
            internal const int AbsoluteMaxBatchItems = 4096;
            private const int MaximumRootSnapshotItems = AbsoluteMaxBatchItems;
            private const int MaximumComponentSnapshotItems = AbsoluteMaxBatchItems;
            private const int MaximumRootSnapshotGenerations = 64;
            private static readonly Dictionary<string, GameObject[]> RootSnapshots = new Dictionary<string, GameObject[]>(StringComparer.Ordinal);

            /// <summary>解析任意 UnityEngine.Object 引用。</summary>
            public static UnityEngine.Object ResolveObject(IUnityCliObjectReferenceInput request, UnityCliRequestBudget budget)
            {
                if (budget == null) throw new ArgumentNullException("budget");
                // 优先级：instanceId > assetPath/guid > exact hierarchy path/name
                var instanceId = request.InstanceId;
                if (instanceId.HasValue && instanceId.Value != 0)
                {
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var obj = EditorUtility.InstanceIDToObject(instanceId.Value);
                    if (obj == null)
                    {
                        throw new BridgeCommandException("E_OBJECTREF_INSTANCE_NOT_FOUND",
                            "No Unity Object with instanceId=" + instanceId.Value + " is loaded.",
                            "Re-read the source object to obtain a fresh instanceId, or resolve by assetPath/guid.");
                    }
                    return obj;
                }

                var assetPath = request.AssetPath;
                var guid = request.Guid;
                if (!string.IsNullOrEmpty(guid))
                {
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var pathByGuid = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(pathByGuid)) assetPath = pathByGuid;
                }
                if (!string.IsNullOrEmpty(assetPath))
                {
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                    if (asset == null)
                    {
                        throw new BridgeCommandException("E_OBJECTREF_ASSET_NOT_FOUND",
                            "Asset not found at path: " + assetPath,
                            "Run unitycli tools run asset.search --input '{\"query\":\"<name>\"}' to discover the asset path.");
                    }
                    return asset;
                }

                var path = request.Path ?? request.Name ?? request.Target;
                if (!string.IsNullOrEmpty(path))
                {
                    var gameObject = FindGameObjectByExactPath(path, budget);
                    if (gameObject != null) return gameObject;
                }

                throw new BridgeCommandException("E_OBJECTREF_REQUIRED",
                    "Object reference required: pass instanceId, assetPath/guid, or path/name.",
                    "Run serialized-object.read --input '{\"instanceId\":12345}' or '{\"assetPath\":\"Assets/Mat.mat\"}'.");
            }

            /// <summary>解析 GameObject 引用。</summary>
            public static GameObject ResolveGameObject(IUnityCliObjectReferenceInput request, UnityCliRequestBudget budget)
            {
                if (budget == null) throw new ArgumentNullException("budget");
                var instanceId = request.InstanceId;
                if (instanceId.HasValue && instanceId.Value != 0)
                {
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var obj = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
                    if (obj == null)
                    {
                        throw new BridgeCommandException("E_OBJECTREF_GAMEOBJECT_NOT_FOUND",
                            "instanceId=" + instanceId.Value + " is not a loaded GameObject.",
                            "Re-read the GameObject, or resolve by path/name.");
                    }
                    return obj;
                }

                var path = request.Path ?? request.Target ?? request.Name;
                if (!string.IsNullOrEmpty(path))
                {
                    var go = FindGameObjectByExactPath(path, budget);
                    if (go != null) return go;
                    throw new BridgeCommandException("E_OBJECTREF_GAMEOBJECT_REQUIRED",
                        "No GameObject matched path/name '" + path + "' in any loaded scene.",
                        "Run game-object.find to locate the GameObject and obtain its instanceId, then pass that instanceId.");
                }

                throw new BridgeCommandException("E_OBJECTREF_GAMEOBJECT_REQUIRED",
                    "GameObject reference required: pass instanceId or path/name.",
                    "Run game-object.find to locate the GameObject and obtain its instanceId.");
            }

            /// <summary>
            /// 解析 Component 引用，支持三种路径：
            /// 1) component instanceId
            /// 2) GameObject + component index
            /// 3) GameObject + component typeName
            /// </summary>
            public static Component ResolveComponent(IUnityCliObjectReferenceInput request, UnityCliRequestBudget budget)
            {
                if (budget == null) throw new ArgumentNullException("budget");
                var componentInstanceId = request.ComponentInstanceId;
                if (componentInstanceId.HasValue && componentInstanceId.Value != 0)
                {
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var obj = EditorUtility.InstanceIDToObject(componentInstanceId.Value) as Component;
                    if (obj == null)
                    {
                        throw new BridgeCommandException("E_OBJECTREF_COMPONENT_NOT_FOUND",
                            "componentInstanceId=" + componentInstanceId.Value + " is not a loaded Component.",
                            "Re-read the component list to obtain a fresh componentInstanceId.");
                    }
                    return obj;
                }

                var gameObject = ResolveGameObject(request, budget);
                var componentIndex = request.ComponentIndex;
                if (componentIndex.HasValue)
                {
                    var components = BoundedComponents(gameObject, budget);
                    if (componentIndex.Value < 0 || componentIndex.Value >= components.Length)
                    {
                        throw new BridgeCommandException("E_OBJECTREF_COMPONENT_INDEX_OUT_OF_RANGE",
                            "componentIndex=" + componentIndex.Value + " out of range (0.." + (components.Length - 1) + ").",
                            "Run component.get or tools run scene.gameobject-components to list valid indices.");
                    }
                    return components[componentIndex.Value];
                }

                var componentType = request.Component ?? request.ComponentType;
                if (!string.IsNullOrEmpty(componentType))
                {
                    var type = GameObjectToolSupport.ResolveComponentType(componentType, budget);
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var component = gameObject.GetComponent(type);
                    if (component == null)
                    {
                        throw new BridgeCommandException("E_OBJECTREF_COMPONENT_NOT_FOUND",
                            "Component " + componentType + " not found on " + gameObject.name + ".",
                            "Run tools run scene.gameobject-components to list available components.");
                    }
                    return component;
                }

                throw new BridgeCommandException("E_OBJECTREF_COMPONENT_REQUIRED",
                    "Component reference required: pass componentInstanceId, or (instanceId|path) + componentIndex/component.",
                    "Run component.get --input '{\"instanceId\":123,\"component\":\"Transform\"}'.");
            }

            /// <summary>按完整大小写敏感路径直接逐段解析；不会在失败时扫描整棵层级树。</summary>
            public static GameObject FindGameObjectByExactPath(string path, UnityCliRequestBudget budget)
            {
                if (string.IsNullOrEmpty(path)) return null;
                if (budget == null) throw new ArgumentNullException("budget");
                if (path.Length > 8192) throw new BridgeCommandException("E_OBJECTREF_PATH_INVALID", "Exact hierarchy paths are limited to 8192 characters.", "Pass a shorter full case-sensitive hierarchy path or an instance id.");
                var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0) return null;
                if (segments.Length > 256) throw new BridgeCommandException("E_OBJECTREF_PATH_INVALID", "Exact hierarchy paths are limited to 256 segments.", "Pass an instance id for deeper objects.");
                GameObject match = null;
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var sceneCount = SceneManager.sceneCount;
                for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
                {
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var scene = SceneManager.GetSceneAt(sceneIndex);
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    if (!scene.isLoaded) continue;
                    foreach (var root in BoundedSceneRoots(scene, budget))
                    {
                        if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                        if (!string.Equals(root.name, segments[0], StringComparison.Ordinal)) continue;
                        var current = root.transform;
                        var resolved = true;
                        for (var segmentIndex = 1; segmentIndex < segments.Length; segmentIndex++)
                        {
                            Transform childMatch = null;
                            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                            var childCount = current.childCount;
                            for (var childIndex = 0; childIndex < childCount; childIndex++)
                            {
                                if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeGetter() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                                var child = current.GetChild(childIndex);
                                if (!string.Equals(child.name, segments[segmentIndex], StringComparison.Ordinal)) continue;
                                if (childMatch != null) ThrowExactAmbiguousPath(path, childMatch.gameObject, child.gameObject, budget);
                                childMatch = child;
                            }
                            if (childMatch == null) { resolved = false; break; }
                            current = childMatch;
                        }
                        if (!resolved) continue;
                        if (match != null) ThrowExactAmbiguousPath(path, match, current.gameObject, budget);
                        match = current.gameObject;
                    }
                }
                return match;
            }

            /// <summary>批量根对象 getter 只在已验证 cardinality 后调用，并在同一层级 generation 复用有限快照。</summary>
            internal static GameObject[] BoundedSceneRoots(Scene scene, UnityCliRequestBudget budget)
            {
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var rootCount = scene.rootCount;
                if (rootCount < 0 || rootCount > MaximumRootSnapshotItems || !budget.TryConsumeScannedRecords(rootCount))
                    throw new UnityCliRequestBudgetExceededException(rootCount > MaximumRootSnapshotItems ? "rootSnapshotItems" : budget.ExhaustedDimension);
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var generation = UnityCliReadGeneration.Hierarchy(scene);
                GameObject[] cached;
                if (RootSnapshots.TryGetValue(generation, out cached))
                {
                    if (cached.Length != rootCount) throw new BridgeCommandException("E_READ_GENERATION_CHANGED", "Scene roots changed while resolving the exact path.", "Retry the read from the beginning.");
                    return cached;
                }
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var roots = scene.GetRootGameObjects();
                if (roots.Length != rootCount) throw new BridgeCommandException("E_READ_GENERATION_CHANGED", "Scene roots changed while building the bounded snapshot.", "Retry the read from the beginning.");
                if (RootSnapshots.Count >= MaximumRootSnapshotGenerations) RootSnapshots.Clear();
                RootSnapshots[generation] = roots;
                return roots;
            }

            /// <summary>组件数组在 materialize 前验证数量并一次性占用扫描预算。</summary>
            internal static Component[] BoundedComponents(GameObject gameObject, UnityCliRequestBudget budget)
            {
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var componentCount = gameObject.GetComponentCount();
                if (componentCount < 0 || componentCount > MaximumComponentSnapshotItems || !budget.TryConsumeScannedRecords(componentCount))
                    throw new UnityCliRequestBudgetExceededException(componentCount > MaximumComponentSnapshotItems ? "componentSnapshotItems" : budget.ExhaustedDimension);
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var components = gameObject.GetComponents<Component>();
                if (components.Length != componentCount) throw new BridgeCommandException("E_READ_GENERATION_CHANGED", "GameObject components changed while building the bounded snapshot.", "Retry the read from the beginning.");
                return components;
            }

            private static void ThrowExactAmbiguousPath(string path, GameObject first, GameObject second, UnityCliRequestBudget budget)
            {
                var firstPath = UnityCliHierarchyPager.Path(first, budget);
                var secondPath = UnityCliHierarchyPager.Path(second, budget);
                throw new BridgeCommandException("E_OBJECTREF_AMBIGUOUS",
                    "Path '" + path + "' matches multiple GameObjects: " + firstPath + ", " + secondPath,
                    "Pass the instanceId from game-object.find to disambiguate objects in different loaded scenes.");
            }

            /// <summary>
            /// 按路径或名称查找 GameObject，分层匹配：
            /// 层1 精确匹配（从场景顶层起、大小写敏感）→ 层2 放宽匹配（任意层级起点、路径后缀、忽略大小写）。
            /// 任何层命中多个即报歧义并列候选完整路径；0 命中返回 null 由调用方报错。
            /// </summary>
            public static GameObject FindGameObjectByPath(string path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                var roots = new List<Transform>();
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    foreach (var root in scene.GetRootGameObjects()) roots.Add(root.transform);
                }

                // 层1：精确匹配（从场景顶层起、大小写敏感）
                var exact = new List<GameObject>();
                foreach (var root in roots) CollectExact(root, path, exact);
                if (exact.Count == 1) return exact[0];
                if (exact.Count > 1) ThrowAmbiguousPath(path, exact);

                // 层2：放宽匹配（任意层级起点、路径后缀、忽略大小写）
                var segments = path.Split('/');
                var relaxed = new List<GameObject>();
                foreach (var root in roots) CollectRelaxed(root, segments, relaxed);
                if (relaxed.Count == 1) return relaxed[0];
                if (relaxed.Count > 1) ThrowAmbiguousPath(path, relaxed);

                return null;
            }

            // 层1：从当前节点起精确匹配 path（大小写敏感），收集所有命中。
            private static void CollectExact(Transform current, string path, List<GameObject> results)
            {
                if (string.Equals(current.name, path, StringComparison.Ordinal)) { results.Add(current.gameObject); return; }
                var slash = path.IndexOf('/');
                if (slash < 0) return;
                var head = path.Substring(0, slash);
                var rest = path.Substring(slash + 1);
                if (!string.Equals(current.name, head, StringComparison.Ordinal)) return;
                for (var i = 0; i < current.childCount; i++) CollectExact(current.GetChild(i), rest, results);
            }

            // 层2：任意层级起点、路径后缀、忽略大小写，收集所有命中。
            private static void CollectRelaxed(Transform current, string[] segments, List<GameObject> results)
            {
                if (PathEndsWith(current, segments)) results.Add(current.gameObject);
                for (var i = 0; i < current.childCount; i++) CollectRelaxed(current.GetChild(i), segments, results);
            }

            // 判断 current 到根的路径链是否以 segments 结尾（忽略大小写）。
            private static bool PathEndsWith(Transform current, string[] segments)
            {
                var node = current;
                for (var i = segments.Length - 1; i >= 0; i--)
                {
                    if (node == null) return false;
                    if (!string.Equals(node.name, segments[i], StringComparison.OrdinalIgnoreCase)) return false;
                    node = node.parent;
                }
                return true;
            }

            // 从场景顶层起的完整路径，用于歧义候选展示。
            private static string FullPath(Transform current)
            {
                var parts = new List<string>();
                var node = current;
                while (node != null) { parts.Add(node.name); node = node.parent; }
                parts.Reverse();
                return string.Join("/", parts);
            }

            // 多命中时抛歧义错误，列候选完整路径（从顶层起），不静默取错。
            private static void ThrowAmbiguousPath(string path, List<GameObject> matches)
            {
                var candidates = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var match in matches)
                {
                    var candidate = FullPath(match.transform);
                    if (!seen.Add(candidate)) continue;
                    candidates.Add(candidate);
                    if (candidates.Count == 10) break;
                }
                throw new BridgeCommandException("E_OBJECTREF_AMBIGUOUS",
                    "Path '" + path + "' matches " + matches.Count + " GameObjects: " + string.Join(", ", candidates),
                    "Pass the full hierarchy path from a scene root, or use game-object.find to obtain the instanceId.");
            }
        }
    }
}
#endif
