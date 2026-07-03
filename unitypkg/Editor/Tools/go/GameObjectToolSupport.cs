#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// go/component 域 IUnityCliTool 共享的基础设施 helper:目标解析、组件类型解析、
    /// 属性变更、JSON 解析、向量解析、层级路径。仅供 go/component 域自包含工具复用;
    /// 不实现 IUnityCliTool,不参与工具发现。
    /// 设计依据:.plan/工具统一形态.md(自包含工具共享只读 helper,handler 逻辑各自内聚)。
    /// </summary>
    public static class GameObjectToolSupport
    {
        // ─── 深序列化选项构造(component.get 复用) ─────────

        /// <summary>从 BridgeRequest 构造 ObjectSerializer.Options;paths/viewQuery 互斥。</summary>
        internal static UnityCliBridge.ObjectSerializer.Options BuildSerializerOptions(UnityCliBridge.BridgeRequest request)
        {
            var options = new UnityCliBridge.ObjectSerializer.Options
            {
                MaxDepth = request.IntArg("maxDepth", 4),
                MaxArrayItems = Math.Max(0, Math.Min(request.IntArg("maxArrayItems", 50), 1000)),
                IncludeFields = request.BoolArg("includeFields", true),
                IncludeProperties = request.BoolArg("includeProperties", true),
                DeepSerialization = request.BoolArg("deepSerialization", true)
            };
            var pathsRaw = request.RawJsonArg("paths");
            var viewQueryRaw = request.RawJsonArg("viewQuery");
            if (!string.IsNullOrEmpty(pathsRaw) && !string.IsNullOrEmpty(viewQueryRaw))
            {
                throw new UnityCliBridge.BridgeCommandException("E_OBJECT_READ_SCOPE_CONFLICT", "paths and viewQuery are mutually exclusive.", "Use paths for exact member reads, or viewQuery for one filtered subtree.");
            }
            if (!string.IsNullOrEmpty(pathsRaw))
            {
                options.Paths = new HashSet<string>();
                foreach (var p in JsonStringArray(pathsRaw))
                {
                    var clean = p == null ? null : p.Trim();
                    if (!string.IsNullOrEmpty(clean)) options.Paths.Add(clean);
                }
            }
            if (!string.IsNullOrEmpty(viewQueryRaw))
            {
                options.ViewPath = JsonStringProperty(viewQueryRaw, "path") ?? JsonStringProperty(viewQueryRaw, "Path");
                options.NamePattern = JsonStringProperty(viewQueryRaw, "namePattern") ?? JsonStringProperty(viewQueryRaw, "NamePattern");
                options.TypeFilter = JsonStringProperty(viewQueryRaw, "typeFilter") ?? JsonStringProperty(viewQueryRaw, "TypeFilter");
                var maxDepthRaw = JsonRawProperty(viewQueryRaw, "maxDepth") ?? JsonRawProperty(viewQueryRaw, "MaxDepth");
                if (!string.IsNullOrEmpty(maxDepthRaw))
                {
                    int maxDepth;
                    if (int.TryParse(maxDepthRaw.Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out maxDepth))
                    {
                        options.MaxDepth = Math.Max(0, maxDepth);
                    }
                }
            }
            var nameFilter = request.StringArg("namePattern");
            if (!string.IsNullOrEmpty(nameFilter))
            {
                options.NameFilters = new HashSet<string>(nameFilter.Split(',').Select(s => s.Trim()));
            }
            return options;
        }

        // ─── 变更校验与应用(复刻 go.create/modify 的 ApplyGameObjectMutations/ValidateGameObjectMutationInputs) ──

        /// <summary>校验 tag/layer 输入是否有效,无效抛 BridgeCommandException。</summary>
        public static void ValidateMutationInputs(string tagValue, string layerValue)
        {
            if (!string.IsNullOrEmpty(tagValue) && !IsKnownTag(tagValue))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TAG_NOT_FOUND", "Tag not found: " + tagValue, "Create the tag first with project.tag or pass an existing Unity tag.");
            }
            if (!string.IsNullOrEmpty(layerValue)) ParseLayer(layerValue);
        }

        /// <summary>将名称/active/tag/layer/绝对变换应用到 GameObject。未提供的字段保持原值。</summary>
        public static void ApplyGameObjectMutations(GameObject gameObject, string newName, bool? active, string tagValue, string layerValue,
            double? x, double? y, double? z, double? rotationX, double? rotationY, double? rotationZ, double? scaleX, double? scaleY, double? scaleZ)
        {
            if (!string.IsNullOrEmpty(newName)) gameObject.name = newName;
            if (active.HasValue) gameObject.SetActive(active.Value);
            if (!string.IsNullOrEmpty(tagValue)) gameObject.tag = tagValue;
            if (!string.IsNullOrEmpty(layerValue)) gameObject.layer = ParseLayer(layerValue);
            gameObject.transform.position = new Vector3(
                (float)(x ?? gameObject.transform.position.x),
                (float)(y ?? gameObject.transform.position.y),
                (float)(z ?? gameObject.transform.position.z));
            gameObject.transform.eulerAngles = new Vector3(
                (float)(rotationX ?? gameObject.transform.eulerAngles.x),
                (float)(rotationY ?? gameObject.transform.eulerAngles.y),
                (float)(rotationZ ?? gameObject.transform.eulerAngles.z));
            gameObject.transform.localScale = new Vector3(
                (float)(scaleX ?? gameObject.transform.localScale.x),
                (float)(scaleY ?? gameObject.transform.localScale.y),
                (float)(scaleZ ?? gameObject.transform.localScale.z));
        }

        // ─── DTO 构造 helper ───────────────────────────────────────────────

        /// <summary>构造 GameObject 轻量引用(id/name/path)。</summary>
        public static GameObjectRef BuildGameObjectRef(GameObject gameObject)
        {
            return new GameObjectRef { Id = gameObject.GetInstanceID(), Name = gameObject.name, Path = HierarchyPath(gameObject) };
        }

        /// <summary>构造 Transform 摘要(position/rotation/scale)。</summary>
        public static TransformInfo BuildTransformInfo(Transform transform)
        {
            return new TransformInfo
            {
                Position = new Vector3Data(transform.position),
                Rotation = new Vector3Data(transform.eulerAngles),
                Scale = new Vector3Data(transform.localScale)
            };
        }

        /// <summary>构造 Component 摘要(type/fullName/enabled)。</summary>
        public static ComponentSummary BuildComponentSummary(Component component)
        {
            if (component == null)
            {
                return new ComponentSummary { Type = "MissingScript", Enabled = false };
            }
            return new ComponentSummary { Type = component.GetType().Name, FullName = component.GetType().FullName, Enabled = ComponentEnabled(component) };
        }

        /// <summary>判断 Component 是否启用(无 enabled 属性视为启用)。</summary>
        public static bool ComponentEnabled(Component component)
        {
            var property = component.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            return property == null || (bool)property.GetValue(component, null);
        }

        /// <summary>构造 GameObject 详情(id/name/path/active/tag/layer/transform/components/children)。</summary>
        public static GameObjectDetail BuildGameObjectDetail(GameObject gameObject)
        {
            var components = new List<ComponentSummary>();
            foreach (var component in gameObject.GetComponents<Component>())
            {
                components.Add(BuildComponentSummary(component));
            }
            var children = new List<GameObjectRef>();
            foreach (Transform child in gameObject.transform)
            {
                children.Add(BuildGameObjectRef(child.gameObject));
            }
            return new GameObjectDetail
            {
                Id = gameObject.GetInstanceID(),
                Name = gameObject.name,
                Path = HierarchyPath(gameObject),
                Active = gameObject.activeSelf,
                Tag = gameObject.tag,
                Layer = gameObject.layer,
                Transform = BuildTransformInfo(gameObject.transform),
                Components = components,
                Children = children
            };
        }

        // ─── 忙状态拦截(复用 SceneToolSupport 语义) ───────────────────────

        public static void EnsureEditorNotBusy(string command)
        {
            SceneToolSupport.EnsureEditorNotBusy(command);
        }

        // ─── 目标解析 ──────────────────────────────────────────────────────

        /// <summary>按名称/实例ID/层级路径唯一解析 GameObject;空或多匹配抛 BridgeCommandException。</summary>
        public static GameObject ResolveUniqueTarget(string target, string requiredMessage)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", requiredMessage, "Pass a GameObject name, hierarchy path or instance id.");
            }
            var matches = new List<GameObject>();
            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
            {
                if (gameObject.name == target
                    || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target
                    || HierarchyPath(gameObject) == target)
                {
                    matches.Add(gameObject);
                }
            }
            if (matches.Count > 1)
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_AMBIGUOUS", "Multiple GameObjects matched: " + target, "Pass a hierarchy path or instance id.");
            }
            if (matches.Count == 0)
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "Target not found: " + target, "Run unity_cli tools run scene.hierarchy or pass a hierarchy path / instance id.");
            }
            return matches[0];
        }

        /// <summary>构造 GameObject 层级路径(A/B/C)。</summary>
        public static string HierarchyPath(GameObject gameObject)
        {
            var names = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", names.ToArray());
        }

        // ─── 向量解析 ──────────────────────────────────────────────────────

        public static Vector3 ParseVector3(string value)
        {
            var parts = (value ?? string.Empty).Split(',');
            return new Vector3(
                parts.Length > 0 ? ParseFloat(parts[0], "x") : 0f,
                parts.Length > 1 ? ParseFloat(parts[1], "y") : 0f,
                parts.Length > 2 ? ParseFloat(parts[2], "z") : 0f);
        }

        public static Vector2 ParseVector2(string value)
        {
            var parts = (value ?? string.Empty).Split(',');
            return new Vector2(
                parts.Length > 0 ? ParseFloat(parts[0], "x") : 0f,
                parts.Length > 1 ? ParseFloat(parts[1], "y") : 0f);
        }

        public static float ParseFloat(string rawValue, string property)
        {
            float parsed;
            if (float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_VALUE_INVALID", "Expected numeric value for " + property + " but got: " + rawValue, "Pass a JSON number or numeric string.");
        }

        // ─── 标签/层/原型解析 ───────────────────────────────────────────────

        public static bool IsKnownTag(string value)
        {
            return InternalEditorUtility.tags.Contains(value);
        }

        public static int ParseLayer(string value)
        {
            int parsed;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
            var layer = LayerMask.NameToLayer(value);
            if (layer < 0)
            {
                throw new UnityCliBridge.BridgeCommandException("E_LAYER_NOT_FOUND", "Layer not found: " + value, "Pass a numeric layer index or a configured Unity layer name.");
            }
            return layer;
        }

        public static PrimitiveType ParsePrimitiveType(string value)
        {
            if (string.Equals(value, "cube", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Cube;
            if (string.Equals(value, "sphere", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Sphere;
            if (string.Equals(value, "capsule", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Capsule;
            if (string.Equals(value, "cylinder", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Cylinder;
            if (string.Equals(value, "plane", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Plane;
            if (string.Equals(value, "quad", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Quad;
            throw new UnityCliBridge.BridgeCommandException("E_PRIMITIVE_TYPE_INVALID", "Unsupported primitive type: " + value, "Use cube, sphere, capsule, cylinder, plane, or quad.");
        }

        // ─── 类型解析(复刻 UnityCliBridge.FindType + ResolveComponentType) ──

        /// <summary>按完整类型名在所有已加载程序集中查找 Type。</summary>
        public static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            var type = Type.GetType(fullName);
            if (type != null) return type;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { type = assembly.GetType(fullName); }
                catch { /* 忽略加载失败的程序集 */ }
                if (type != null) return type;
            }
            return null;
        }

        /// <summary>按组件类型名解析 Component Type;支持短名/FullName/UnityEngine 前缀回退。</summary>
        public static Type ResolveComponentType(string componentName)
        {
            var type = FindType(componentName) ?? FindType("UnityEngine." + componentName) ?? FindType("UnityEngine.UI." + componentName);
            if (type == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] candidates;
                    try { candidates = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException exception) { candidates = exception.Types.Where(candidate => candidate != null).ToArray(); }
                    foreach (var candidate in candidates)
                    {
                        if (string.Equals(candidate.Name, componentName, StringComparison.Ordinal)) { type = candidate; break; }
                    }
                    if (type != null) break;
                }
            }
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_TYPE_NOT_FOUND", "Component type not found: " + componentName, "Pass a full component type name or a UnityEngine component name.");
            }
            return type;
        }

        /// <summary>在 GameObject 上按类型名+可选索引解析 Component。</summary>
        public static Component ResolveComponent(GameObject gameObject, string componentName, int? componentIndex)
        {
            var type = ResolveComponentType(componentName);
            if (!componentIndex.HasValue) return gameObject.GetComponent(type);
            var components = gameObject.GetComponents(type);
            if (componentIndex.Value < 0 || componentIndex.Value >= components.Length)
            {
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_INDEX_OUT_OF_RANGE", "componentIndex " + componentIndex.Value + " is out of range for " + componentName + ".", "Run context read scene.gameobject.components to discover component indices.");
            }
            return components[componentIndex.Value];
        }

        /// <summary>判断 Component 类型是否允许在同一 GameObject 上添加多个实例。</summary>
        public static bool AllowsMultipleComponents(Type type)
        {
            return type.GetCustomAttributes(typeof(DisallowMultipleComponent), true).Length == 0;
        }

        // ─── 组件属性变更(复刻 component.add/set 的属性写入逻辑) ────────────

        /// <summary>从 properties JSON 对象构建属性变更列表。</summary>
        public static List<ComponentPropertyChange> BuildComponentPropertyChanges(Type componentType, string propertiesJson)
        {
            var changes = new List<ComponentPropertyChange>();
            if (string.IsNullOrEmpty(propertiesJson)) return changes;
            foreach (var propertyName in JsonObjectPropertyNames(propertiesJson))
            {
                changes.Add(BuildComponentPropertyChange(componentType, propertyName, JsonRawProperty(propertiesJson, propertyName)));
            }
            return changes;
        }

        public static ComponentPropertyChange BuildComponentPropertyChange(Type componentType, string propertyName, string rawJson)
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
            throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_PROPERTY_NOT_FOUND", "Writable component property not found: " + propertyName, "Use component.get or type.schema to inspect writable public fields/properties.");
        }

        public static void ApplyComponentPropertyChanges(Component component, List<ComponentPropertyChange> changes)
        {
            foreach (var change in changes)
            {
                if (change.Property != null) change.Property.SetValue(component, change.Value, null);
                else if (change.Field != null) change.Field.SetValue(component, change.Value);
            }
        }

        /// <summary>将 JSON 原始字面转换为组件属性的目标类型值。</summary>
        public static object ConvertComponentJsonValue(string rawJson, Type type)
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
            if (type == typeof(float)) return (float)JsonValueToDouble(value);
            if (type == typeof(double)) return JsonValueToDouble(value);
            if (type == typeof(Vector2)) return ParseVector2(JsonValueToString(value));
            if (type == typeof(Vector3)) return ParseVector3(JsonValueToString(value));
            if (type.IsEnum) return Enum.Parse(type, JsonValueToString(value) ?? string.Empty, true);
            throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_PROPERTY_TYPE_UNSUPPORTED", "Unsupported component property type: " + type.Name, "Use component.modify for nested/object references, or simple string/bool/int/float/double/enum/Vector2/Vector3 here.");
        }

        /// <summary>从 request 的 properties/componentProperties 字段提取并校验 JSON 对象字符串。</summary>
        public static string ComponentPropertiesJson(string propertiesJson)
        {
            if (string.IsNullOrEmpty(propertiesJson)) return null;
            var trimmed = propertiesJson.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
            {
                trimmed = JsonUnescape(trimmed.Substring(1, trimmed.Length - 2));
            }
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
            {
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_PROPERTIES_INVALID", "component properties must be a JSON object.", "Pass properties as --input '{\"properties\":{\"mass\":5}}'.");
            }
            return trimmed;
        }

        // ─── JSON 解析 helper(复刻 UnityCliJson 私有方法) ───────────────────

        public static string JsonUnescape(string value)
        {
            return value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        }

        public static string JsonValueToString(string value)
        {
            if (value == "null") return null;
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                return JsonUnescape(value.Substring(1, value.Length - 2));
            }
            return value;
        }

        public static bool JsonValueToBool(string value)
        {
            var text = JsonValueToString(value);
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1") return true;
            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0") return false;
            throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_VALUE_INVALID", "Expected boolean component value but got: " + text, "Pass true/false as JSON booleans in --input.");
        }

        public static long JsonValueToLong(string value)
        {
            var text = JsonValueToString(value);
            long parsed;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
            throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_VALUE_INVALID", "Expected integer component value but got: " + text, "Pass a JSON number or numeric string.");
        }

        public static double JsonValueToDouble(string value)
        {
            var text = JsonValueToString(value);
            double parsed;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_VALUE_INVALID", "Expected numeric component value but got: " + text, "Pass a JSON number or numeric string.");
        }

        public static string[] JsonStringArray(string json)
        {
            if (string.IsNullOrEmpty(json)) return new string[0];
            var values = new List<string>();
            foreach (Match match in Regex.Matches(json, "\\\"((?:\\\\.|[^\\\"])*)\\\""))
            {
                values.Add(JsonUnescape(match.Groups[1].Value));
            }
            return values.ToArray();
        }

        public static string JsonRawProperty(string json, string name)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(name)) return null;
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}")) return null;
            var index = 1;
            while (index < trimmed.Length - 1)
            {
                while (index < trimmed.Length && (char.IsWhiteSpace(trimmed[index]) || trimmed[index] == ',')) index++;
                if (index >= trimmed.Length - 1 || trimmed[index] != '"') return null;
                var keyEnd = FindJsonStringEnd(trimmed, index);
                if (keyEnd <= index) return null;
                var key = JsonUnescape(trimmed.Substring(index + 1, keyEnd - index - 1));
                index = keyEnd + 1;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index])) index++;
                if (index >= trimmed.Length || trimmed[index] != ':') return null;
                index++;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index])) index++;
                var valueEnd = FindJsonValueEnd(trimmed, index);
                if (valueEnd <= index) return null;
                if (string.Equals(key, name, StringComparison.Ordinal)) return trimmed.Substring(index, valueEnd - index).Trim();
                index = valueEnd;
            }
            return null;
        }

        public static string JsonStringProperty(string json, string name)
        {
            var raw = JsonRawProperty(json, name);
            if (string.IsNullOrEmpty(raw)) return null;
            var trimmed = raw.Trim();
            return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"' ? JsonUnescape(trimmed.Substring(1, trimmed.Length - 2)) : null;
        }

        public static string[] JsonObjectPropertyNames(string json)
        {
            if (string.IsNullOrEmpty(json)) return new string[0];
            var names = new List<string>();
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}")) return names.ToArray();
            var index = 1;
            while (index < trimmed.Length - 1)
            {
                while (index < trimmed.Length && (char.IsWhiteSpace(trimmed[index]) || trimmed[index] == ',')) index++;
                if (index >= trimmed.Length - 1 || trimmed[index] != '"') break;
                var keyEnd = FindJsonStringEnd(trimmed, index);
                if (keyEnd <= index) break;
                names.Add(JsonUnescape(trimmed.Substring(index + 1, keyEnd - index - 1)));
                index = keyEnd + 1;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index])) index++;
                if (index >= trimmed.Length || trimmed[index] != ':') break;
                index++;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index])) index++;
                var valueEnd = FindJsonValueEnd(trimmed, index);
                if (valueEnd <= index) break;
                index = valueEnd;
            }
            return names.ToArray();
        }

        private static int FindJsonStringEnd(string json, int start)
        {
            var escaping = false;
            for (var index = start + 1; index < json.Length; index++)
            {
                var ch = json[index];
                if (escaping) escaping = false;
                else if (ch == '\\') escaping = true;
                else if (ch == '"') return index;
            }
            return -1;
        }

        private static int FindJsonValueEnd(string json, int start)
        {
            var depth = 0;
            var inString = false;
            var escaping = false;
            for (var index = start; index < json.Length; index++)
            {
                var ch = json[index];
                if (inString)
                {
                    if (escaping) escaping = false;
                    else if (ch == '\\') escaping = true;
                    else if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') { inString = true; continue; }
                if (ch == '{' || ch == '[') { depth++; continue; }
                if (ch == '}' || ch == ']')
                {
                    if (depth == 0) return index;
                    depth--;
                    if (depth == 0) return index + 1;
                    continue;
                }
                if (depth == 0 && ch == ',') return index;
            }
            return json.Length;
        }

        // ─── JSON 字符串 → 嵌套结构(供 component.get/modify 嵌入动态 JSON) ──

        /// <summary>将 JSON 字符串解析为嵌套 Dictionary/List/标量结构,供 Result DTO 嵌入动态 JSON。</summary>
        public static object ParseJsonToObject(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var index = 0;
            return ParseJsonValue(json, ref index);
        }

        private static object ParseJsonValue(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
            if (index >= json.Length) return null;
            var ch = json[index];
            if (ch == '{') return ParseJsonObject(json, ref index);
            if (ch == '[') return ParseJsonArray(json, ref index);
            if (ch == '"') return ParseJsonString(json, ref index);
            if (char.IsDigit(ch) || ch == '-') return ParseJsonNumber(json, ref index);
            if (MatchLiteral(json, ref index, "true")) return true;
            if (MatchLiteral(json, ref index, "false")) return false;
            if (MatchLiteral(json, ref index, "null")) return null;
            return null;
        }

        private static Dictionary<string, object> ParseJsonObject(string json, ref int index)
        {
            index++; // {
            var obj = new Dictionary<string, object>();
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
            if (index < json.Length && json[index] == '}') { index++; return obj; }
            while (index < json.Length)
            {
                while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
                var key = ParseJsonString(json, ref index);
                while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
                if (index < json.Length && json[index] == ':') index++;
                var value = ParseJsonValue(json, ref index);
                obj[key] = value;
                while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
                if (index < json.Length && json[index] == ',') { index++; continue; }
                if (index < json.Length && json[index] == '}') { index++; return obj; }
                break;
            }
            return obj;
        }

        private static List<object> ParseJsonArray(string json, ref int index)
        {
            index++; // [
            var arr = new List<object>();
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
            if (index < json.Length && json[index] == ']') { index++; return arr; }
            while (index < json.Length)
            {
                arr.Add(ParseJsonValue(json, ref index));
                while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
                if (index < json.Length && json[index] == ',') { index++; continue; }
                if (index < json.Length && json[index] == ']') { index++; return arr; }
                break;
            }
            return arr;
        }

        private static string ParseJsonString(string json, ref int index)
        {
            index++; // 跳过开头 "
            var sb = new System.Text.StringBuilder();
            while (index < json.Length)
            {
                var ch = json[index++];
                if (ch == '"') return sb.ToString();
                if (ch == '\\' && index < json.Length)
                {
                    var esc = json[index++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (index + 4 <= json.Length)
                            {
                                sb.Append((char)int.Parse(json.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                index += 4;
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        private static object ParseJsonNumber(string json, ref int index)
        {
            var start = index;
            if (json[index] == '-') index++;
            while (index < json.Length && char.IsDigit(json[index])) index++;
            var isDouble = false;
            if (index < json.Length && json[index] == '.') { isDouble = true; index++; while (index < json.Length && char.IsDigit(json[index])) index++; }
            if (index < json.Length && (json[index] == 'e' || json[index] == 'E')) { isDouble = true; index++; if (index < json.Length && (json[index] == '+' || json[index] == '-')) index++; while (index < json.Length && char.IsDigit(json[index])) index++; }
            var text = json.Substring(start, index - start);
            return isDouble ? (object)double.Parse(text, CultureInfo.InvariantCulture) : (object)long.Parse(text, CultureInfo.InvariantCulture);
        }

        private static bool MatchLiteral(string json, ref int index, string literal)
        {
            if (index + literal.Length > json.Length) return false;
            if (string.CompareOrdinal(json, index, literal, 0, literal.Length) != 0) return false;
            index += literal.Length;
            return true;
        }
    }

    // ─── 共享 DTO ──────────────────────────────────────────────────────────

    /// <summary>GameObject 轻量引用(id/name/path)。</summary>
    public sealed class GameObjectRef
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
    }

    /// <summary>Transform 摘要(position/rotation/scale)。</summary>
    public sealed class TransformInfo
    {
        public Vector3Data Position { get; set; }
        public Vector3Data Rotation { get; set; }
        public Vector3Data Scale { get; set; }
    }

    /// <summary>Vector3 序列化 DTO。</summary>
    public sealed class Vector3Data
    {
        public Vector3Data() { }
        public Vector3Data(Vector3 v) { X = v.x; Y = v.y; Z = v.z; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    /// <summary>Component 摘要(type/fullName/enabled)。</summary>
    public sealed class ComponentSummary
    {
        public string Type { get; set; }
        public string FullName { get; set; }
        public bool Enabled { get; set; }
    }

    /// <summary>GameObject 详情(id/name/path/active/tag/layer/transform/components/children)。</summary>
    public sealed class GameObjectDetail
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public bool Active { get; set; }
        public string Tag { get; set; }
        public long Layer { get; set; }
        public TransformInfo Transform { get; set; }
        public List<ComponentSummary> Components { get; set; }
        public List<GameObjectRef> Children { get; set; }
    }

    /// <summary>组件属性变更记录(name/type/value)。</summary>
    public sealed class ComponentPropertyChange
    {
        public ComponentPropertyChange(string name, object value, Type valueType, PropertyInfo property, FieldInfo field, string rawJson)
        {
            Name = name; Value = value; ValueType = valueType; Property = property; Field = field; RawJson = rawJson;
        }
        public string Name { get; private set; }
        public object Value { get; private set; }
        public Type ValueType { get; private set; }
        public PropertyInfo Property { get; private set; }
        public FieldInfo Field { get; private set; }
        public string RawJson { get; private set; }
    }

    /// <summary>变更属性摘要(name/type/value),用于 Result 序列化。</summary>
    public sealed class ChangedProperty
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public object Value { get; set; }
    }

    /// <summary>计划副作用项,go/component 域 mutation 工具 Result 共享。</summary>
    public sealed class GameObjectPlannedEffect
    {
        public string Type { get; set; }
        public string Target { get; set; }
        public string Component { get; set; }
        public string Parent { get; set; }
        public string SideEffects { get; set; }
    }

    /// <summary>层级搜索结果项。</summary>
    public sealed class GoFindItem
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

    /// <summary>分页信息。</summary>
    public sealed class GoPageInfo
    {
        public long PageSize { get; set; }
        public long Cursor { get; set; }
        public string NextCursor { get; set; }
        public long Total { get; set; }
    }
}
#endif
