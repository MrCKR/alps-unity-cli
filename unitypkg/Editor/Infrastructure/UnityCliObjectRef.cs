#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
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
            /// <summary>解析任意 UnityEngine.Object 引用。</summary>
            public static UnityEngine.Object ResolveObject(IUnityCliObjectReferenceInput request)
            {
                // 优先级：instanceId > assetPath/guid > name(path)
                var instanceId = request.InstanceId;
                if (instanceId.HasValue && instanceId.Value != 0)
                {
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
                    var pathByGuid = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(pathByGuid)) assetPath = pathByGuid;
                }
                if (!string.IsNullOrEmpty(assetPath))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                    if (asset == null)
                    {
                        throw new BridgeCommandException("E_OBJECTREF_ASSET_NOT_FOUND",
                            "Asset not found at path: " + assetPath,
                            "Run unitycli tools run asset.search --input '{\"query\":\"<name>\"}' to discover the asset path.");
                    }
                    return asset;
                }

                // 名称/路径解析：优先在活动场景中找 GameObject，再回退资产
                var path = request.Path ?? request.Name ?? request.Target;
                if (!string.IsNullOrEmpty(path))
                {
                    var go = FindGameObjectByPath(path);
                    if (go != null) return go;

                    var assetByName = AssetDatabase.FindAssets(path);
                    if (assetByName != null && assetByName.Length > 0)
                    {
                        var resolved = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(assetByName[0]));
                        if (resolved != null) return resolved;
                    }
                }

                throw new BridgeCommandException("E_OBJECTREF_REQUIRED",
                    "Object reference required: pass instanceId, assetPath/guid, or path/name.",
                    "Run object.get-data --input '{\"instanceId\":12345}' or '{\"assetPath\":\"Assets/Mat.mat\"}'.");
            }

            /// <summary>解析 GameObject 引用。</summary>
            public static GameObject ResolveGameObject(IUnityCliObjectReferenceInput request)
            {
                var instanceId = request.InstanceId;
                if (instanceId.HasValue && instanceId.Value != 0)
                {
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
                    var go = FindGameObjectByPath(path);
                    if (go != null) return go;
                    throw new BridgeCommandException("E_OBJECTREF_GAMEOBJECT_REQUIRED",
                        "No GameObject matched path/name '" + path + "' in any loaded scene.",
                        "Run go.find to locate the GameObject and obtain its instanceId, then pass that instanceId.");
                }

                throw new BridgeCommandException("E_OBJECTREF_GAMEOBJECT_REQUIRED",
                    "GameObject reference required: pass instanceId or path/name.",
                    "Run go.find to locate the GameObject and obtain its instanceId.");
            }

            /// <summary>
            /// 解析 Component 引用，支持三种路径：
            /// 1) component instanceId
            /// 2) GameObject + component index
            /// 3) GameObject + component typeName
            /// </summary>
            public static Component ResolveComponent(IUnityCliObjectReferenceInput request)
            {
                var componentInstanceId = request.ComponentInstanceId;
                if (componentInstanceId.HasValue && componentInstanceId.Value != 0)
                {
                    var obj = EditorUtility.InstanceIDToObject(componentInstanceId.Value) as Component;
                    if (obj == null)
                    {
                        throw new BridgeCommandException("E_OBJECTREF_COMPONENT_NOT_FOUND",
                            "componentInstanceId=" + componentInstanceId.Value + " is not a loaded Component.",
                            "Re-read the component list to obtain a fresh componentInstanceId.");
                    }
                    return obj;
                }

                var gameObject = ResolveGameObject(request);
                var componentIndex = request.ComponentIndex;
                if (componentIndex.HasValue)
                {
                    var components = gameObject.GetComponents<Component>();
                    if (componentIndex.Value < 0 || componentIndex.Value >= components.Length)
                    {
                        throw new BridgeCommandException("E_OBJECTREF_COMPONENT_INDEX_OUT_OF_RANGE",
                            "componentIndex=" + componentIndex.Value + " out of range (0.." + (components.Length - 1) + ").",
                            "Run component.get or tools run scene.gameobject.components to list valid indices.");
                    }
                    return components[componentIndex.Value];
                }

                var componentType = request.Component ?? request.ComponentType;
                if (!string.IsNullOrEmpty(componentType))
                {
                    var type = ResolveComponentType(componentType);
                    var component = gameObject.GetComponent(type);
                    if (component == null)
                    {
                        throw new BridgeCommandException("E_OBJECTREF_COMPONENT_NOT_FOUND",
                            "Component " + componentType + " not found on " + gameObject.name + ".",
                            "Run tools run scene.gameobject.components to list available components.");
                    }
                    return component;
                }

                throw new BridgeCommandException("E_OBJECTREF_COMPONENT_REQUIRED",
                    "Component reference required: pass componentInstanceId, or (instanceId|path) + componentIndex/component.",
                    "Run component.get --input '{\"instanceId\":123,\"component\":\"Transform\"}'.");
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
                var candidates = matches
                    .Select(m => FullPath(m.transform))
                    .Distinct()
                    .Take(10)
                    .ToList();
                throw new BridgeCommandException("E_OBJECTREF_AMBIGUOUS",
                    "Path '" + path + "' matches " + matches.Count + " GameObjects: " + string.Join(", ", candidates),
                    "Pass the full hierarchy path from a scene root, or use go.find to obtain the instanceId.");
            }
        }
    }
}
#endif
