#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// go/component 域 IUnityCliTool 共享的基础设施 helper:目标解析、组件类型解析、
    /// typed 属性变更、值转换、向量解析、层级路径。仅供 go/component 域自包含工具复用;
    /// 不实现 IUnityCliTool,不参与工具发现。
    /// </summary>
    public static class GameObjectToolSupport
    {
        private static readonly MethodInfo DisallowMultipleTypeMethod = typeof(Component).Assembly
            .GetType("UnityEngine.AttributeHelperEngine")?
            .GetMethod("GetParentTypeDisallowingMultipleInclusion", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly HashSet<Type> NativeSingleInstanceComponentTypes = new HashSet<Type>
        {
            typeof(Rigidbody)
        };

        // ─── 深序列化选项构造(component.get 复用) ─────────

        /// <summary>从 UnityCli.Protocol.BridgeRequest 构造 ObjectSerializer.Options;paths/viewQuery 互斥。</summary>
        internal static UnityCliBridge.ObjectSerializer.Options BuildSerializerOptions(IUnityCliObjectSerializationInput request)
        {
            var options = new UnityCliBridge.ObjectSerializer.Options
            {
                MaxDepth = request.MaxDepth ?? 4,
                MaxArrayItems = Math.Max(0, Math.Min(request.MaxArrayItems ?? 50, 1000)),
                IncludeFields = request.IncludeFields ?? true,
                IncludeProperties = request.IncludeProperties ?? true,
                DeepSerialization = request.DeepSerialization ?? true
            };
            if (request.Paths != null && request.Paths.Count > 0 && request.ViewQuery != null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_OBJECT_READ_SCOPE_CONFLICT", "paths and viewQuery are mutually exclusive.", "Use paths for exact member reads, or viewQuery for one filtered subtree.");
            }
            if (request.Paths != null)
            {
                options.Paths = new HashSet<string>(request.Paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim()));
            }
            var query = request.ViewQuery as Dictionary<string, object>;
            if (query != null)
            {
                object value;
                if (query.TryGetValue("path", out value)) options.ViewPath = value as string;
                if (query.TryGetValue("namePattern", out value)) options.NamePattern = value as string;
                if (query.TryGetValue("typeFilter", out value)) options.TypeFilter = value as string;
                if (query.TryGetValue("maxDepth", out value) && value != null) options.MaxDepth = Math.Max(0, Convert.ToInt32(value, CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(request.NamePattern))
            {
                options.NameFilters = new HashSet<string>(request.NamePattern.Split(',').Select(value => value.Trim()));
            }
            return options;
        }

        // ─── 变更校验与应用(复刻 go.create/modify 的 ApplyGameObjectMutations/ValidateGameObjectMutationInputs) ──

        /// <summary>校验 tag/layer 输入是否有效,无效抛 BridgeCommandException。</summary>
        public static void ValidateMutationInputs(string tagValue, string layerValue)
        {
            if (!string.IsNullOrEmpty(tagValue) && !IsKnownTag(tagValue))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TAG_NOT_FOUND", "Tag not found: " + tagValue, "Create the tag first with project.tag.add or pass an existing Unity tag.");
            }
            if (!string.IsNullOrEmpty(layerValue)) ParseLayer(layerValue);
        }

        /// <summary>Reject values Unity cannot represent as finite single-precision transform components.</summary>
        public static void ValidateTransformMutationValues(params double?[] values)
        {
            foreach (var value in values)
            {
                if (!value.HasValue) continue;
                if (double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < -float.MaxValue || value.Value > float.MaxValue)
                    throw new UnityCliBridge.BridgeCommandException("E_TRANSFORM_VALUE_INVALID", "Transform values must be finite and representable as Unity floats.", "Pass finite values between -3.402823e38 and 3.402823e38.");
            }
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
            if (float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && !float.IsNaN(parsed) && !float.IsInfinity(parsed)) return parsed;
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
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                if (parsed >= 0 && parsed <= 31) return parsed;
                throw new UnityCliBridge.BridgeCommandException("E_LAYER_NOT_FOUND", "Layer index must be between 0 and 31: " + value, "Pass a numeric layer index from 0 through 31 or a configured Unity layer name.");
            }
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

        /// <summary>按短名、FullName 或 UnityEngine namespace 名解析 Component Type。</summary>
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
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_INDEX_OUT_OF_RANGE", "componentIndex " + componentIndex.Value + " is out of range for " + componentName + ".", "Run tools run scene.gameobject.components to discover component indices.");
            }
            return components[componentIndex.Value];
        }

        /// <summary>判断 Component 类型是否允许在同一 GameObject 上添加多个实例。</summary>
        public static bool AllowsMultipleComponents(Type type)
        {
            // Unity 未公开无副作用的 native AddComponent 预检；显式登记已验证但没有 managed attribute 的单实例类型。
            if (NativeSingleInstanceComponentTypes.Contains(type))
                return false;
            if (DisallowMultipleTypeMethod != null)
                return DisallowMultipleTypeMethod.Invoke(null, new object[] { type }) == null;
            return type.GetCustomAttributes(typeof(DisallowMultipleComponent), true).Length == 0;
        }

        // ─── 组件属性变更(复刻 component.add/set 的属性写入逻辑) ────────────

        /// <summary>从显式 AnyJson properties 对象构建属性变更列表。</summary>
        public static List<ComponentPropertyChange> BuildComponentPropertyChanges(Type componentType, Dictionary<string, object> properties)
        {
            var changes = new List<ComponentPropertyChange>();
            if (properties == null) return changes;
            foreach (var property in properties)
            {
                changes.Add(BuildComponentPropertyChange(componentType, property.Key, property.Value));
            }
            return changes;
        }

        public static ComponentPropertyChange BuildComponentPropertyChange(Type componentType, string propertyName, object value)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public;
            var property = componentType.GetProperty(propertyName, flags);
            if (property != null && property.CanWrite)
            {
                return new ComponentPropertyChange(propertyName, ConvertComponentValue(value, property.PropertyType), property.PropertyType, property, null, value);
            }
            var field = componentType.GetField(propertyName, flags);
            if (field != null)
            {
                return new ComponentPropertyChange(propertyName, ConvertComponentValue(value, field.FieldType), field.FieldType, null, field, value);
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

        /// <summary>将 AnyJson 值树转换为组件属性类型；转换失败保持稳定业务错误。</summary>
        public static object ConvertComponentValue(object value, Type type)
        {
            try
            {
                var nullableType = Nullable.GetUnderlyingType(type);
                if (nullableType != null)
                {
                    if (value == null) return null;
                    type = nullableType;
                }
                if (value == null && !type.IsValueType) return null;
                return UnityCliValueConverter.ConvertValue(value, type);
            }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_COMPONENT_VALUE_INVALID",
                    "Component value could not be converted to " + type.Name + ": " + exception.Message,
                    "Pass a JSON value matching the writable member type shown by component.get or type.schema.");
            }
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
        public ComponentPropertyChange(string name, object value, Type valueType, PropertyInfo property, FieldInfo field, object inputValue)
        {
            Name = name; Value = value; ValueType = valueType; Property = property; Field = field; InputValue = inputValue;
        }
        public string Name { get; private set; }
        [UnityCliAnyJson] public object Value { get; private set; }
        public Type ValueType { get; private set; }
        public PropertyInfo Property { get; private set; }
        public FieldInfo Field { get; private set; }
        [UnityCliAnyJson] public object InputValue { get; private set; }
    }

    /// <summary>变更属性摘要(name/type/value),用于 Result 序列化。</summary>
    public sealed class ChangedProperty
    {
        public string Name { get; set; }
        public string Type { get; set; }
        [UnityCliAnyJson] public object Value { get; set; }
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
        public long? NextCursor { get; set; }
        public long Total { get; set; }
    }
}
#endif
