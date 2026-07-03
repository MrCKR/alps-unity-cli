#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        // go/component 域工具已全部迁移为 IUnityCliTool 自包含形态(unitypkg/Editor/Tools/go/*.cs、component/*.cs)。
        // 此处仅保留被其他域(Physics/Graphics/Animation/Context/UI/Prefab)复用的私有 helper 与空注册方法。
        private static void RegisterGameObjectCommands(BridgeCommandRegistry registry)
        {
        }

        private static TargetResolution ResolveGameObjectTarget(string target)
        {
            var matches = new List<GameObject>();
            if (string.IsNullOrEmpty(target))
            {
                return TargetResolution.FromMatches(matches);
            }

            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
            {
                if (gameObject.name == target || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target || HierarchyPath(gameObject) == target)
                {
                    matches.Add(gameObject);
                }
            }
            return TargetResolution.FromMatches(matches);
        }

        private static GameObject ResolveUniqueTarget(string target, string requiredMessage)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", requiredMessage, "Pass a GameObject name, hierarchy path or instance id.");
            }
            var matches = new List<GameObject>();
            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
            {
                if (gameObject.name == target || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target || HierarchyPath(gameObject) == target)
                {
                    matches.Add(gameObject);
                }
            }
            if (matches.Count > 1)
            {
                throw new BridgeCommandException("E_TARGET_AMBIGUOUS", "Multiple GameObjects matched: " + target, "Pass a hierarchy path or instance id.");
            }
            if (matches.Count == 0)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "Target not found: " + target, "Run unity_cli tools run scene.hierarchy or pass a hierarchy path / instance id.");
            }
            return matches[0];
        }

        private static string GameObjectTargetJson(GameObject gameObject)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("id", (long)gameObject.GetInstanceID())
                .Prop("name", gameObject.name)
                .Prop("path", HierarchyPath(gameObject))
                .EndObject()
                .ToString();
        }


        private static string InferGameObjectSearchMethod(BridgeRequest request)
        {
            if (!string.IsNullOrEmpty(request.StringArg("tag"))) return "by_tag";
            if (!string.IsNullOrEmpty(request.StringArg("layer"))) return "by_layer";
            if (!string.IsNullOrEmpty(request.StringArg("component"))) return "by_component";
            if (!string.IsNullOrEmpty(request.StringArg("path"))) return "by_path";
            if (!string.IsNullOrEmpty(request.StringArg("id"))) return "by_id";
            return "by_name";
        }

        private static List<GameObject> SearchGameObjects(string searchMethod, string searchTerm, bool includeInactive)
        {
            var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>(includeInactive).OrderBy(HierarchyPath, StringComparer.Ordinal).ToList();
            return allObjects.Where(gameObject => MatchesGameObject(gameObject, searchMethod, searchTerm)).ToList();
        }

        // 提取单个 GameObject 的搜索匹配判定，供场景搜索与 Prefab Stage 优先搜索复用。
        private static bool MatchesGameObject(GameObject gameObject, string searchMethod, string searchTerm)
        {
            var normalized = (searchMethod ?? "by_name").Trim().ToLowerInvariant();
            if (normalized == "by_id") return gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == searchTerm;
            if (normalized == "by_path") return string.Equals(HierarchyPath(gameObject), searchTerm, StringComparison.Ordinal);
            if (normalized == "by_tag") return string.Equals(gameObject.tag, searchTerm, StringComparison.Ordinal);
            if (normalized == "by_layer") return gameObject.layer == ParseLayer(searchTerm);
            if (normalized == "by_component") { var type = ResolveComponentType(searchTerm); return type != null && gameObject.GetComponent(type) != null; }
            if (normalized != "by_name") throw new BridgeCommandException("E_SEARCH_METHOD_INVALID", "Unsupported GameObject searchMethod: " + searchMethod, "Use by_name, by_tag, by_layer, by_component, by_path, or by_id.");
            return string.Equals(gameObject.name, searchTerm, StringComparison.Ordinal);
        }

        private static string HierarchyItemJson(Transform transform)
        {
            return HierarchyItemJson(transform, -1, true);
        }

        /// <summary>生成轻量 GameObject 层级项；默认只给组件类型摘要，详细字段交给 component.get。</summary>
        private static string HierarchyItemJson(Transform transform, int depth, bool includeComponents)
        {
            var componentNames = new List<string>();
            foreach (var component in transform.GetComponents<Component>())
            {
                componentNames.Add(component != null ? component.GetType().Name : "MissingScript");
            }
            var gameObject = transform.gameObject;
            var writer = new UnityCliJsonWriter().BeginObject()
                .Prop("id", (long)gameObject.GetInstanceID())
                .Prop("name", gameObject.name)
                .Prop("path", HierarchyPath(gameObject))
                .Prop("active", gameObject.activeSelf)
                .Prop("activeInHierarchy", gameObject.activeInHierarchy)
                .Prop("tag", gameObject.tag)
                .Prop("layer", (long)gameObject.layer)
                .Prop("childCount", (long)transform.childCount)
                .Prop("childrenTruncated", transform.childCount > 0)
                .Prop("componentCount", (long)componentNames.Count)
                .PropArray("componentTypes", arr => { foreach (var name in componentNames) { arr.Value(name); } });
            if (depth >= 0)
            {
                writer.Prop("depth", (long)depth);
            }
            if (includeComponents)
            {
                writer.PropArray("components", arr => { foreach (var name in componentNames) { arr.Value(name); } });
            }
            return writer.EndObject().ToString();
        }

        /// <summary>按深度、名称过滤和 inactive 策略收集层级摘要，分页在调用方统一处理。</summary>
        private static void CollectHierarchy(Transform transform, List<string> items, int depth, int maxDepth, string nameFilter, bool includeInactive, bool includeComponents)
        {
            CollectHierarchy(transform, items, depth, maxDepth, nameFilter, includeInactive, includeComponents, null);
        }

        private static void CollectHierarchy(Transform transform, List<string> items, int depth, int maxDepth, string query, bool includeInactive, bool includeComponents, string componentType)
        {
            var gameObject = transform.gameObject;
            if (includeInactive || gameObject.activeInHierarchy)
            {
                if (LargeOutputTextMatches(query, gameObject.name, HierarchyPath(gameObject)) && LargeOutputComponentMatches(gameObject, componentType))
                {
                    items.Add(HierarchyItemJson(transform, depth, includeComponents));
                }
            }
            if (depth >= maxDepth)
            {
                return;
            }
            foreach (Transform child in transform)
            {
                CollectHierarchy(child, items, depth + 1, maxDepth, query, includeInactive, includeComponents, componentType);
            }
        }

        /// <summary>兼容 Prefab/Context 分页层级读取：统计整棵 Transform 树节点数。</summary>
        private static int CountHierarchy(Transform transform)
        {
            var count = 1;
            foreach (Transform child in transform)
            {
                count += CountHierarchy(child);
            }
            return count;
        }

        /// <summary>兼容 Prefab/Context 分页层级读取：按 DFS 顺序跳过 cursor 后收集 pageSize 个节点。</summary>
        private static void CollectHierarchy(Transform transform, List<string> items, int cursor, int pageSize, Counter counter)
        {
            if (counter.Value >= cursor && items.Count < pageSize)
            {
                items.Add(HierarchyItemJson(transform));
            }
            counter.Value++;
            foreach (Transform child in transform)
            {
                if (items.Count >= pageSize)
                {
                    return;
                }
                CollectHierarchy(child, items, cursor, pageSize, counter);
            }
        }

        private static bool ComponentEnabled(Component component)
        {
            var property = component.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            return property == null || (bool)property.GetValue(component, null);
        }

        private static string RequiredTarget(BridgeRequest request, string message)
        {
            var target = request.StringArg("target") ?? request.StringArg("name") ?? request.StringArg("path") ?? request.StringArg("id");
            if (string.IsNullOrEmpty(target))
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", message, "Pass a GameObject name, hierarchy path or instance id.");
            }
            return target;
        }

        // go.modify 支持 targets[] 批量修改；先完整解析，避免前半成功后半失败的部分写入。
        private static List<GameObject> ResolveModifyTargets(BridgeRequest request)
        {
            var rawTargets = request.RawJsonArg("targets");
            var targetNames = JsonStringArray(rawTargets);
            if (targetNames.Length == 0)
            {
                return new List<GameObject> { ResolveUniqueTarget(RequiredTarget(request, "GameObject modify requires target or targets[]."), "GameObject modify requires target or targets[].") };
            }
            var targets = new List<GameObject>();
            var seen = new HashSet<int>();
            foreach (var targetName in targetNames)
            {
                var gameObject = ResolveUniqueTarget(targetName, "GameObject modify targets[] contains an empty target.");
                if (seen.Add(gameObject.GetInstanceID())) targets.Add(gameObject);
            }
            if (targets.Count == 0)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject modify targets[] must contain at least one target.", "Pass target for one object or targets as a non-empty string array.");
            }
            return targets;
        }

        private static void ValidateGameObjectMutationInputs(BridgeRequest request)
        {
            var tagValue = request.StringArg("tagValue");
            if (!string.IsNullOrEmpty(tagValue) && !IsKnownTag(tagValue))
            {
                throw new BridgeCommandException("E_TAG_NOT_FOUND", "Tag not found: " + tagValue, "Create the tag first with project.tag or pass an existing Unity tag.");
            }
            var layerValue = request.StringArg("layerValue");
            if (!string.IsNullOrEmpty(layerValue))
            {
                ParseLayer(layerValue);
            }
        }

        private static void ApplyGameObjectMutations(GameObject gameObject, BridgeRequest request)
        {
            var newName = request.StringArg("newName");
            if (!string.IsNullOrEmpty(newName))
            {
                gameObject.name = newName;
            }
            var active = request.OptionalBoolArg("active");
            if (active.HasValue)
            {
                gameObject.SetActive(active.Value);
            }
            var tagValue = request.StringArg("tagValue");
            if (!string.IsNullOrEmpty(tagValue))
            {
                gameObject.tag = tagValue;
            }
            var layerValue = request.StringArg("layerValue");
            if (!string.IsNullOrEmpty(layerValue))
            {
                gameObject.layer = ParseLayer(layerValue);
            }
            gameObject.transform.position = new Vector3(request.FloatArg("x", gameObject.transform.position.x), request.FloatArg("y", gameObject.transform.position.y), request.FloatArg("z", gameObject.transform.position.z));
            gameObject.transform.eulerAngles = new Vector3(request.FloatArg("rotationX", gameObject.transform.eulerAngles.x), request.FloatArg("rotationY", gameObject.transform.eulerAngles.y), request.FloatArg("rotationZ", gameObject.transform.eulerAngles.z));
            gameObject.transform.localScale = new Vector3(request.FloatArg("scaleX", gameObject.transform.localScale.x), request.FloatArg("scaleY", gameObject.transform.localScale.y), request.FloatArg("scaleZ", gameObject.transform.localScale.z));
        }

        private static PrimitiveType ParsePrimitiveType(string value)
        {
            if (string.Equals(value, "cube", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Cube;
            if (string.Equals(value, "sphere", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Sphere;
            if (string.Equals(value, "capsule", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Capsule;
            if (string.Equals(value, "cylinder", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Cylinder;
            if (string.Equals(value, "plane", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Plane;
            if (string.Equals(value, "quad", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Quad;
            throw new BridgeCommandException("E_PRIMITIVE_TYPE_INVALID", "Unsupported primitive type: " + value, "Use cube, sphere, capsule, cylinder, plane, or quad.");
        }

        private static bool IsKnownTag(string value)
        {
            return UnityEditorInternal.InternalEditorUtility.tags.Contains(value);
        }

        private static int ParseLayer(string value)
        {
            int parsed;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            var layer = LayerMask.NameToLayer(value);
            if (layer < 0)
            {
                throw new BridgeCommandException("E_LAYER_NOT_FOUND", "Layer not found: " + value, "Pass a numeric layer index or a configured Unity layer name.");
            }
            return layer;
        }

        private static string GameObjectMutationJson(string type, GameObject gameObject)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("gameObject", GameObjectDetailJson(gameObject))
                .PropArray("plannedEffects", arr => arr
                    .RawValue(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", type)
                        .Prop("target", HierarchyPath(gameObject))
                        .EndObject().ToString()))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
        }

        // 生成批量 go.modify 返回：保留每个目标详情与对应 plannedEffects，便于 AI 审计结果。
        private static string GameObjectBatchMutationJson(string type, List<GameObject> gameObjects)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("modifiedCount", (long)gameObjects.Count)
                .PropArray("gameObjects", arr => { foreach (var gameObject in gameObjects) arr.RawValue(GameObjectDetailJson(gameObject)); })
                .PropArray("plannedEffects", arr =>
                {
                    foreach (var gameObject in gameObjects)
                    {
                        arr.RawValue(new UnityCliJsonWriter().BeginObject()
                            .Prop("type", type)
                            .Prop("target", HierarchyPath(gameObject))
                            .EndObject().ToString());
                    }
                })
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
        }

        private static string GameObjectDetailJson(GameObject gameObject)
        {
            var children = new List<string>();
            foreach (Transform child in gameObject.transform)
            {
                children.Add(GameObjectTargetJson(child.gameObject));
            }
            return new UnityCliJsonWriter().BeginObject()
                .Prop("id", (long)gameObject.GetInstanceID())
                .Prop("name", gameObject.name)
                .Prop("path", HierarchyPath(gameObject))
                .Prop("active", gameObject.activeSelf)
                .Prop("tag", gameObject.tag)
                .Prop("layer", (long)gameObject.layer)
                .PropRaw("transform", TransformJson(gameObject.transform))
                .PropRaw("components", ComponentsJson(gameObject))
                .PropArray("children", arr => { foreach (var child in children) { arr.RawValue(child); } })
                .EndObject()
                .ToString();
        }

        private static string TransformJson(Transform transform)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("position", Vector3Json(transform.position))
                .PropRaw("rotation", Vector3Json(transform.eulerAngles))
                .PropRaw("scale", Vector3Json(transform.localScale))
                .EndObject()
                .ToString();
        }

        private static string ComponentsJson(GameObject gameObject)
        {
            var writer = new UnityCliJsonWriter().BeginArray();
            foreach (var component in gameObject.GetComponents<Component>())
            {
                writer.RawValue(ComponentSummaryJson(component));
            }
            return writer.EndArray().ToString();
        }

        private static string ComponentSummaryJson(Component component)
        {
            if (component == null)
            {
                return new UnityCliJsonWriter().BeginObject()
                    .Prop("type", "MissingScript")
                    .Prop("enabled", false)
                    .EndObject()
                    .ToString();
            }
            return new UnityCliJsonWriter().BeginObject()
                .Prop("type", component.GetType().Name)
                .Prop("fullName", component.GetType().FullName)
                .Prop("enabled", ComponentEnabled(component))
                .EndObject()
                .ToString();
        }

        private static string RequiredComponentName(BridgeRequest request)
        {
            var componentName = request.StringArg("component");
            if (string.IsNullOrEmpty(componentName))
            {
                throw new BridgeCommandException("E_COMPONENT_TYPE_REQUIRED", "Component command requires --component <type>.", "Pass a Unity component type name, such as BoxCollider or UnityEngine.Rigidbody.");
            }
            return componentName;
        }

        private static Type ResolveComponentType(string componentName)
        {
            var type = FindType(componentName) ?? FindType("UnityEngine." + componentName) ?? FindType("UnityEngine.UI." + componentName);
            if (type == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] candidates;
                    try
                    {
                        candidates = assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException exception)
                    {
                        candidates = exception.Types.Where(candidate => candidate != null).ToArray();
                    }
                    foreach (var candidate in candidates)
                    {
                        if (string.Equals(candidate.Name, componentName, StringComparison.Ordinal))
                        {
                            type = candidate;
                            break;
                        }
                    }
                    if (type != null)
                    {
                        break;
                    }
                }
            }
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                throw new BridgeCommandException("E_COMPONENT_TYPE_NOT_FOUND", "Component type not found: " + componentName, "Pass a full component type name or a UnityEngine component name.");
            }
            return type;
        }

        /// <summary>判断 Component 类型是否允许在同一 GameObject 上添加多个实例。</summary>
        private static bool AllowsMultipleComponents(Type type)
        {
            return type.GetCustomAttributes(typeof(DisallowMultipleComponent), true).Length == 0;
        }

        private static Component ResolveComponent(GameObject gameObject, string componentName)
        {
            return ResolveComponent(gameObject, componentName, null);
        }

        private static Component ResolveComponent(GameObject gameObject, string componentName, int? componentIndex)
        {
            var type = ResolveComponentType(componentName);
            if (!componentIndex.HasValue)
            {
                return gameObject.GetComponent(type);
            }
            var components = gameObject.GetComponents(type);
            if (componentIndex.Value < 0 || componentIndex.Value >= components.Length)
            {
                throw new BridgeCommandException("E_COMPONENT_INDEX_OUT_OF_RANGE", "componentIndex " + componentIndex.Value + " is out of range for " + componentName + ".", "Run context read scene.gameobject.components to discover component indices.");
            }
            return components[componentIndex.Value];
        }

        private static string ComponentPropertiesJson(BridgeRequest request)
        {
            var propertiesJson = request.RawJsonArg("properties") ?? request.RawJsonArg("componentProperties");
            if (string.IsNullOrEmpty(propertiesJson)) return null;
            var trimmed = propertiesJson.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == (char)34 && trimmed[trimmed.Length - 1] == (char)34)
            {
                trimmed = JsonUnescape(trimmed.Substring(1, trimmed.Length - 2));
            }
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
            {
                throw new BridgeCommandException("E_COMPONENT_PROPERTIES_INVALID", "component properties must be a JSON object.", "Pass properties as --input '{\"properties\":{\"mass\":5}}'.");
            }
            return trimmed;
        }

        private static List<ComponentPropertyChange> BuildComponentPropertyChanges(Type componentType, string propertiesJson)
        {
            var changes = new List<ComponentPropertyChange>();
            if (string.IsNullOrEmpty(propertiesJson)) return changes;
            foreach (var propertyName in JsonObjectPropertyNames(propertiesJson))
            {
                changes.Add(BuildComponentPropertyChange(componentType, propertyName, JsonRawProperty(propertiesJson, propertyName)));
            }
            return changes;
        }

        private static ComponentPropertyChange BuildComponentPropertyChange(Type componentType, string propertyName, string rawJson)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public;
            var property = componentType.GetProperty(propertyName, flags);
            if (property != null && property.CanWrite)
            {
                return new ComponentPropertyChange(propertyName, ConvertComponentJsonValue(rawJson, property.PropertyType), property.PropertyType, property, null, rawJson);
            }
            var field = componentType.GetField(propertyName, flags);
            if (field != null)
            {
                return new ComponentPropertyChange(propertyName, ConvertComponentJsonValue(rawJson, field.FieldType), field.FieldType, null, field, rawJson);
            }
            throw new BridgeCommandException("E_COMPONENT_PROPERTY_NOT_FOUND", "Writable component property not found: " + propertyName, "Use component.get or type.schema to inspect writable public fields/properties.");
        }

        private static void ApplyComponentPropertyChanges(Component component, List<ComponentPropertyChange> changes)
        {
            foreach (var change in changes)
            {
                if (change.Property != null) change.Property.SetValue(component, change.Value, null);
                else if (change.Field != null) change.Field.SetValue(component, change.Value);
            }
        }

        private static object ConvertComponentJsonValue(string rawJson, Type type)
        {
            var value = (rawJson ?? "null").Trim();
            if (Nullable.GetUnderlyingType(type) != null)
            {
                if (value == "null") return null;
                type = Nullable.GetUnderlyingType(type);
            }
            if (type == typeof(string)) return JsonValueToString(value);
            if (type == typeof(bool)) return JsonValueToBool(value);
            if (type == typeof(int)) return (int)JsonValueToLong(value);
            if (type == typeof(long)) return JsonValueToLong(value);
            if (type == typeof(float)) return JsonValueToFloat(value);
            if (type == typeof(double)) return JsonValueToDouble(value);
            if (type == typeof(Vector2)) return ParseVector2(JsonValueToString(value));
            if (type == typeof(Vector3)) return ParseVector3(JsonValueToString(value));
            if (type.IsEnum) return Enum.Parse(type, JsonValueToString(value) ?? string.Empty, true);
            throw new BridgeCommandException("E_COMPONENT_PROPERTY_TYPE_UNSUPPORTED", "Unsupported component property type: " + type.Name, "Use component.modify for nested/object references, or simple string/bool/int/float/double/enum/Vector2/Vector3 here.");
        }

        private static string JsonValueToString(string value)
        {
            if (value == "null") return null;
            if (value.Length >= 2 && value[0] == (char)34 && value[value.Length - 1] == (char)34)
            {
                return JsonUnescape(value.Substring(1, value.Length - 2));
            }
            return value;
        }

        private static bool JsonValueToBool(string value)
        {
            var text = JsonValueToString(value);
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1") return true;
            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0") return false;
            throw new BridgeCommandException("E_COMPONENT_VALUE_INVALID", "Expected boolean component value but got: " + text, "Pass true/false as JSON booleans in --input.");
        }

        private static long JsonValueToLong(string value)
        {
            var text = JsonValueToString(value);
            long parsed;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
            throw new BridgeCommandException("E_COMPONENT_VALUE_INVALID", "Expected integer component value but got: " + text, "Pass a JSON number or numeric string.");
        }

        private static float JsonValueToFloat(string value)
        {
            return (float)JsonValueToDouble(value);
        }

        private static double JsonValueToDouble(string value)
        {
            var text = JsonValueToString(value);
            double parsed;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            throw new BridgeCommandException("E_COMPONENT_VALUE_INVALID", "Expected numeric component value but got: " + text, "Pass a JSON number or numeric string.");
        }

        private static Vector2 ParseVector2(string value)
        {
            var parts = (value ?? string.Empty).Split(',');
            return new Vector2(parts.Length > 0 ? ParseFloat(parts[0], "x") : 0f, parts.Length > 1 ? ParseFloat(parts[1], "y") : 0f);
        }

        private static Vector3 ParseVector3(string value)
        {
            var parts = (value ?? string.Empty).Split(',');
            return new Vector3(parts.Length > 0 ? ParseFloat(parts[0], "x") : 0f, parts.Length > 1 ? ParseFloat(parts[1], "y") : 0f, parts.Length > 2 ? ParseFloat(parts[2], "z") : 0f);
        }

        private static string ComponentMutationJson(string type, GameObject gameObject, Component component, string propertyName, List<ComponentPropertyChange> changes, int? componentIndex)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropRaw("component", ComponentSummaryJson(component))
                .Prop("property", propertyName)
                .Prop("componentIndex", componentIndex.HasValue ? componentIndex.Value.ToString(CultureInfo.InvariantCulture) : null)
                .PropArray("changedProperties", arr =>
                {
                    foreach (var change in changes ?? new List<ComponentPropertyChange>())
                    {
                        arr.BeginObject()
                            .Prop("name", change.Name)
                            .Prop("type", change.ValueType.Name)
                            .PropRaw("value", change.RawJson)
                            .EndObject();
                    }
                })
                .PropArray("plannedEffects", arr => arr
                    .RawValue(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", type)
                        .Prop("target", HierarchyPath(gameObject))
                        .Prop("component", component == null ? null : component.GetType().Name)
                        .EndObject().ToString()))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
        }

        private sealed class ComponentPropertyChange
        {
            public ComponentPropertyChange(string name, object value, Type valueType, PropertyInfo property, FieldInfo field, string rawJson)
            {
                Name = name;
                Value = value;
                ValueType = valueType;
                Property = property;
                Field = field;
                RawJson = rawJson;
            }

            public string Name { get; private set; }
            public object Value { get; private set; }
            public Type ValueType { get; private set; }
            public PropertyInfo Property { get; private set; }
            public FieldInfo Field { get; private set; }
            public string RawJson { get; private set; }
        }

        private sealed class Counter
        {
            public int Value;
        }
    }
}
#endif
