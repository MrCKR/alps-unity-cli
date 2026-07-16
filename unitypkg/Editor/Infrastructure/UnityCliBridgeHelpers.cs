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
        // 为 Infrastructure、Context 和 Tool partial 实现提供共享 Bridge helper。
        private static string NormalizeAssetPath(string rawPath) => AssetToolSupport.NormalizeAssetPath(rawPath);
        private static Type ResolveComponentType(string componentName) => GameObjectToolSupport.ResolveComponentType(componentName);
        private static Vector2 ParseVector2(string value) => GameObjectToolSupport.ParseVector2(value);
        private static Vector3 ParseVector3(string value) => GameObjectToolSupport.ParseVector3(value);

        // 构造组件名称和摘要数据，缺失脚本保持原有 MissingScript 语义。
        private static List<object> ComponentNamesData(GameObject gameObject)
        {
            var result = new List<object>();
            foreach (var component in gameObject.GetComponents<Component>()) result.Add(component == null ? "MissingScript" : component.GetType().Name);
            return result;
        }

        private static Dictionary<string, object> ComponentSummaryData(Component component)
        {
            if (component == null) return new Dictionary<string, object> { { "type", "MissingScript" }, { "enabled", false } };
            return new Dictionary<string, object> { { "type", component.GetType().Name }, { "fullName", component.GetType().FullName }, { "enabled", ComponentEnabled(component) } };
        }

        private static List<object> ComponentsData(GameObject gameObject)
        {
            var result = new List<object>();
            foreach (var component in gameObject.GetComponents<Component>()) result.Add(ComponentSummaryData(component));
            return result;
        }

        private static bool ComponentEnabled(Component component)
        {
            var property = component.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            return property == null || (bool)property.GetValue(component, null);
        }

        private static Dictionary<string, object> Vector3Data(Vector3 value) => new Dictionary<string, object> { { "x", (double)value.x }, { "y", (double)value.y }, { "z", (double)value.z } };
        private static Dictionary<string, object> TransformData(Transform transform) => new Dictionary<string, object> { { "position", Vector3Data(transform.position) }, { "rotation", Vector3Data(transform.eulerAngles) }, { "scale", Vector3Data(transform.localScale) } };
        private static Dictionary<string, object> GameObjectTargetData(GameObject gameObject) => new Dictionary<string, object> { { "id", (long)gameObject.GetInstanceID() }, { "name", gameObject.name }, { "path", HierarchyPath(gameObject) } };

        private static Dictionary<string, object> GameObjectDetailData(GameObject gameObject)
        {
            var children = new List<object>();
            foreach (Transform child in gameObject.transform) children.Add(GameObjectTargetData(child.gameObject));
            return new Dictionary<string, object> { { "id", (long)gameObject.GetInstanceID() }, { "name", gameObject.name }, { "path", HierarchyPath(gameObject) }, { "active", gameObject.activeSelf }, { "tag", gameObject.tag }, { "layer", (long)gameObject.layer }, { "transform", TransformData(gameObject.transform) }, { "components", ComponentsData(gameObject) }, { "children", children } };
        }

        private sealed class Counter { public int Value; }

        private static int CountHierarchy(Transform transform)
        {
            var count = 1;
            foreach (Transform child in transform) count += CountHierarchy(child);
            return count;
        }

        private static Dictionary<string, object> HierarchyItemData(Transform transform) => HierarchyItemData(transform, -1, true);
        private static Dictionary<string, object> HierarchyItemData(Transform transform, int depth, bool includeComponents)
        {
            var componentNames = ComponentNamesData(transform.gameObject);
            var gameObject = transform.gameObject;
            var result = new Dictionary<string, object> { { "id", (long)gameObject.GetInstanceID() }, { "name", gameObject.name }, { "path", HierarchyPath(gameObject) }, { "active", gameObject.activeSelf }, { "activeInHierarchy", gameObject.activeInHierarchy }, { "tag", gameObject.tag }, { "layer", (long)gameObject.layer }, { "childCount", (long)transform.childCount }, { "childrenTruncated", transform.childCount > 0 }, { "componentCount", (long)componentNames.Count }, { "componentTypes", componentNames } };
            if (depth >= 0) result["depth"] = (long)depth;
            if (includeComponents) result["components"] = componentNames;
            return result;
        }

        // prefab/hierarchy 调用的分页收集保持 cursor/pageSize 截断语义。
        private static void CollectHierarchy(Transform transform, List<object> items, int cursor, int pageSize, Counter counter)
        {
            if (counter.Value >= cursor && items.Count < pageSize) items.Add(HierarchyItemData(transform));
            counter.Value++;
            foreach (Transform child in transform) { if (items.Count >= pageSize) return; CollectHierarchy(child, items, cursor, pageSize, counter); }
        }

        private static TargetResolution ResolveGameObjectTarget(string target)
        {
            var matches = new List<GameObject>();
            if (string.IsNullOrEmpty(target)) return TargetResolution.FromMatches(matches);
            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
                if (gameObject.name == target || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target || HierarchyPath(gameObject) == target) matches.Add(gameObject);
            return TargetResolution.FromMatches(matches);
        }

        private static Component ResolveComponent(GameObject gameObject, string componentName) => ResolveComponent(gameObject, componentName, null);
        private static Component ResolveComponent(GameObject gameObject, string componentName, int? componentIndex)
        {
            var type = ResolveComponentType(componentName);
            if (!componentIndex.HasValue) return gameObject.GetComponent(type);
            var components = gameObject.GetComponents(type);
            if (componentIndex.Value < 0 || componentIndex.Value >= components.Length) throw new BridgeCommandException("E_COMPONENT_INDEX_OUT_OF_RANGE", "componentIndex " + componentIndex.Value + " is out of range for " + componentName + ".", "Run context read scene.gameobject.components to discover component indices.");
            return components[componentIndex.Value];
        }

        private static List<object> GraphicsVolumesData(string target)
        {
            var components = new List<Component>();
            var volumeType = FindType("UnityEngine.Rendering.Volume");
            if (volumeType == null) return new List<object>();
            foreach (var component in Resources.FindObjectsOfTypeAll(volumeType))
            {
                var typed = component as Component;
                if (typed == null || typed.gameObject == null || EditorUtility.IsPersistent(typed.gameObject)) continue;
                if (!string.IsNullOrEmpty(target) && !HierarchyPath(typed.gameObject).Equals(target, StringComparison.OrdinalIgnoreCase) && !typed.gameObject.name.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                components.Add(typed);
            }
            components.Sort((left, right) => string.Compare(HierarchyPath(left.gameObject), HierarchyPath(right.gameObject), StringComparison.OrdinalIgnoreCase));
            var result = new List<object>(); foreach (var component in components) result.Add(GraphicsVolumeData(component)); return result;
        }

        private static Dictionary<string, object> GraphicsVolumeData(Component component)
        {
            var type = component.GetType();
            var enabledProperty = type.GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            var isGlobalField = type.GetField("isGlobal", BindingFlags.Instance | BindingFlags.Public);
            var priorityField = type.GetField("priority", BindingFlags.Instance | BindingFlags.Public);
            var weightField = type.GetField("weight", BindingFlags.Instance | BindingFlags.Public);
            var profile = MemberValue(type, component, "profile") as UnityEngine.Object;
            var sharedProfile = MemberValue(type, component, "sharedProfile") as UnityEngine.Object;
            return new Dictionary<string, object> { { "target", GameObjectTargetData(component.gameObject) }, { "enabled", enabledProperty == null || Convert.ToBoolean(enabledProperty.GetValue(component, null), CultureInfo.InvariantCulture) }, { "isGlobal", isGlobalField != null && Convert.ToBoolean(isGlobalField.GetValue(component), CultureInfo.InvariantCulture) }, { "priority", priorityField == null ? 0f : Convert.ToSingle(priorityField.GetValue(component), CultureInfo.InvariantCulture) }, { "weight", weightField == null ? 0f : Convert.ToSingle(weightField.GetValue(component), CultureInfo.InvariantCulture) }, { "profile", AssetReference(profile ?? sharedProfile) } };
        }

        private static string RequirePrefabPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path)) throw new BridgeCommandException("E_PREFAB_PATH_REQUIRED", "Pass an Assets/*.prefab path.", "Pass a concrete Assets/*.prefab path discovered from project assets.");
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) throw new BridgeCommandException("E_PREFAB_PATH_INVALID", "Prefab path must be under Assets/ and end with .prefab: " + path, "Pass a Unity asset path such as Assets/Prefabs/Player.prefab.");
            return path;
        }

        private static GameObject LoadPrefabAsset(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new BridgeCommandException("E_PREFAB_NOT_FOUND", "Prefab asset not found: " + path, "Check the path or discover existing Prefab assets before retrying.");
            if (PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.NotAPrefab) throw new BridgeCommandException("E_ASSET_NOT_PREFAB", "Asset is not a Prefab: " + path, "Pass an Assets/*.prefab asset path.");
            return prefab;
        }

        private static Dictionary<string, object> PrefabVariantData(GameObject prefab)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(prefab); var sourcePath = source == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(source)); var sourceGuid = string.IsNullOrEmpty(sourcePath) ? null : AssetDatabase.AssetPathToGUID(sourcePath);
            return new Dictionary<string, object> { { "isVariant", PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.Variant }, { "basePath", sourcePath }, { "baseGuid", sourceGuid }, { "sourcePath", sourcePath }, { "sourceGuid", sourceGuid } };
        }
    }
}
#endif
