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
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// 统一引用解析：支持 instanceId / path / name / assetPath / guid / component index|type|instanceId。
        /// 对齐 IvanMurzak ObjectRef/GameObjectRef/ComponentRef/AssetObjectRef/SceneRef 模型，
        /// 但保持 unity_cli native 形态：单一解析入口，按优先级回退。
        /// </summary>
        internal static class ObjectRefResolver
        {
            /// <summary>解析任意 UnityEngine.Object 引用。</summary>
            public static UnityEngine.Object ResolveObject(BridgeRequest request)
            {
                // 优先级：instanceId > assetPath/guid > name(path)
                var instanceId = request.OptionalIntArg("instanceId");
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

                var assetPath = request.StringArg("assetPath");
                var guid = request.StringArg("guid");
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
                            "Run unity_cli asset search to discover the asset path.");
                    }
                    return asset;
                }

                // 名称/路径解析：优先在活动场景中找 GameObject，再回退资产
                var path = request.StringArg("path") ?? request.StringArg("name") ?? request.StringArg("target");
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
            public static GameObject ResolveGameObject(BridgeRequest request)
            {
                var instanceId = request.OptionalIntArg("instanceId");
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

                var path = request.StringArg("path") ?? request.StringArg("target") ?? request.StringArg("name");
                if (!string.IsNullOrEmpty(path))
                {
                    var go = FindGameObjectByPath(path);
                    if (go != null) return go;
                }

                throw new BridgeCommandException("E_OBJECTREF_GAMEOBJECT_REQUIRED",
                    "GameObject reference required: pass instanceId or path/name.",
                    "Run context read scene.gameobject or go.find to locate the GameObject.");
            }

            /// <summary>
            /// 解析 Component 引用，支持三种路径：
            /// 1) component instanceId
            /// 2) GameObject + component index
            /// 3) GameObject + component typeName
            /// </summary>
            public static Component ResolveComponent(BridgeRequest request)
            {
                var componentInstanceId = request.OptionalIntArg("componentInstanceId");
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
                var componentIndex = request.OptionalIntArg("componentIndex");
                if (componentIndex.HasValue)
                {
                    var components = gameObject.GetComponents<Component>();
                    if (componentIndex.Value < 0 || componentIndex.Value >= components.Length)
                    {
                        throw new BridgeCommandException("E_OBJECTREF_COMPONENT_INDEX_OUT_OF_RANGE",
                            "componentIndex=" + componentIndex.Value + " out of range (0.." + (components.Length - 1) + ").",
                            "Run component.get or context read scene.gameobject.components to list valid indices.");
                    }
                    return components[componentIndex.Value];
                }

                var componentType = request.StringArg("component") ?? request.StringArg("componentType");
                if (!string.IsNullOrEmpty(componentType))
                {
                    var type = ResolveComponentType(componentType);
                    var component = gameObject.GetComponent(type);
                    if (component == null)
                    {
                        throw new BridgeCommandException("E_OBJECTREF_COMPONENT_NOT_FOUND",
                            "Component " + componentType + " not found on " + gameObject.name + ".",
                            "Run context read scene.gameobject.components to list available components.");
                    }
                    return component;
                }

                throw new BridgeCommandException("E_OBJECTREF_COMPONENT_REQUIRED",
                    "Component reference required: pass componentInstanceId, or (instanceId|path) + componentIndex/component.",
                    "Run component.get --input '{\"instanceId\":123,\"component\":\"Transform\"}'.");
            }

            /// <summary>按路径或名称查找 GameObject，支持 "A/B/C" 层级路径与全场景名称匹配。</summary>
            public static GameObject FindGameObjectByPath(string path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                // 先按层级路径精确匹配
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        var found = FindByPath(root.transform, path);
                        if (found != null) return found;
                    }
                }
                // 回退：全场景按名称匹配
                if (path.IndexOf('/') < 0)
                {
                    for (var i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var scene = SceneManager.GetSceneAt(i);
                        if (!scene.isLoaded) continue;
                        foreach (var root in scene.GetRootGameObjects())
                        {
                            var found = FindByName(root.transform, path);
                            if (found != null) return found;
                        }
                    }
                }
                return null;
            }

            private static GameObject FindByPath(Transform current, string path)
            {
                if (string.Equals(current.name, path, StringComparison.Ordinal)) return current.gameObject;
                var slash = path.IndexOf('/');
                if (slash < 0) return null;
                var head = path.Substring(0, slash);
                var rest = path.Substring(slash + 1);
                if (!string.Equals(current.name, head, StringComparison.Ordinal)) return null;
                for (var i = 0; i < current.childCount; i++)
                {
                    var found = FindByPath(current.GetChild(i), rest);
                    if (found != null) return found;
                }
                return null;
            }

            private static GameObject FindByName(Transform current, string name)
            {
                if (string.Equals(current.name, name, StringComparison.Ordinal)) return current.gameObject;
                for (var i = 0; i < current.childCount; i++)
                {
                    var found = FindByName(current.GetChild(i), name);
                    if (found != null) return found;
                }
                return null;
            }
        }
    }
}
#endif
