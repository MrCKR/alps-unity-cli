#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        // 按 id、path、target 顺序解析场景对象；只读层级，不修改 Selection。
        private static GameObject ResolveSceneContextGameObject(string target, int? id, string path)
        {
            GameObject gameObject = null;
            if (id.HasValue) gameObject = EditorUtility.InstanceIDToObject(id.Value) as GameObject;
            else if (!string.IsNullOrEmpty(path)) gameObject = GameObject.Find(path);
            else if (!string.IsNullOrEmpty(target))
            {
                var resolution = ResolveGameObjectTarget(target);
                if (resolution.Matches.Count == 1) gameObject = resolution.Target;
                else if (resolution.Matches.Count > 1)
                {
                    throw new BridgeCommandException("E_TARGET_AMBIGUOUS", "Multiple GameObjects matched context target: " + target, "Pass a hierarchy path or instance id for disambiguation.");
                }
            }
            if (gameObject == null)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject context target not found.", "Pass input target, path, or id. Use a scene context such as scene.cameras to discover reusable scene object references.");
            }
            return gameObject;
        }

        /// <summary>构造同 session 内稳定的场景资源 claim；GlobalObjectId 不可用时绑定 scene handle 与 instance id。</summary>
        private static UnityCliProofResourceClaim SceneProofResource(GameObject gameObject, Component component = null)
        {
            if (gameObject == null || !gameObject.scene.IsValid())
                throw new BridgeCommandException("E_COMPLETION_PROBE_RESOURCE_INVALID", "completionProbe requires a live scene GameObject resource.", "Pass a scene.gameobject Context input that resolves one scene object.");
            var objectGlobalId = GlobalObjectId.GetGlobalObjectIdSlow(gameObject).ToString();
            var objectIdentity = string.IsNullOrWhiteSpace(objectGlobalId)
                ? "scene:" + gameObject.scene.handle.ToString(CultureInfo.InvariantCulture) + ":instance:" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)
                : objectGlobalId;
            var observed = component == null ? (UnityEngine.Object)gameObject : component;
            var observedGlobalId = GlobalObjectId.GetGlobalObjectIdSlow(observed).ToString();
            var observedIdentity = string.IsNullOrWhiteSpace(observedGlobalId)
                ? objectIdentity + ":instance:" + observed.GetInstanceID().ToString(CultureInfo.InvariantCulture)
                : observedGlobalId;
            return new UnityCliProofResourceClaim
            {
                ResourceKey = "scene-object:" + objectIdentity,
                Identity = observedIdentity,
                InstanceId = observed.GetInstanceID(),
                SceneHandle = gameObject.scene.handle
            };
        }

        // 读取单个组件的公开属性预览；getter 失败时跳过该属性。
        private static Dictionary<string, object> SceneContextComponentReadableProperties(Component component)
        {
            var values = new Dictionary<string, object>();
            foreach (var property in component.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                try
                {
                    var value = property.GetValue(component, null);
                    values[property.Name] = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                }
                catch
                {
                    // Unity getter 可能依赖不可用的内部状态，属性级失败不影响其余只读结果。
                }
            }
            return values;
        }

        // 直接构造组件 DTO，避免 Context 内出现序列化往返。
        private static List<SceneGameObjectComponentsContext.ComponentInfo> SceneContextComponentDetails(GameObject gameObject, bool includeProperties)
        {
            var items = new List<SceneGameObjectComponentsContext.ComponentInfo>();
            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                {
                    items.Add(new SceneGameObjectComponentsContext.ComponentInfo { Type = "MissingScript", Enabled = false });
                    continue;
                }
                items.Add(new SceneGameObjectComponentsContext.ComponentInfo
                {
                    Type = component.GetType().Name,
                    FullName = component.GetType().FullName,
                    Enabled = ComponentEnabled(component),
                    Properties = includeProperties ? SceneContextComponentReadableProperties(component) : null
                });
            }
            return items;
        }
    }
}
#endif
