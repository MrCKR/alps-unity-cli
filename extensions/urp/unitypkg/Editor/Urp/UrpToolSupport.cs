#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>
    /// URP 工具共享 helper：只负责反射类型解析、活动管线校验、Volume 枚举与设置读写。
    /// 不参与工具发现，避免 3 个 URP 工具重复实现同一套反射逻辑。
    /// </summary>
    internal static class UrpToolSupport
    {
        private static bool s_typesInitialized;
        private static Type s_urpAssetType;
        private static Type s_rendererDataType;
        private static Type s_rendererFeatureType;
        private static Type s_volumeType;
        private static Type s_volumeProfileType;

        public static void EnsureUrpAvailable()
        {
            InitializeTypes();
            if (s_urpAssetType == null || !IsUrpActivePipeline())
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_PACKAGE_NOT_FOUND",
                    "URP (Universal Render Pipeline) is not the active render pipeline, or com.unity.render-pipelines.universal is not installed.",
                    "Install URP via the Unity Package Manager and set a UniversalRenderPipelineAsset as the active render pipeline in Graphics Settings.");
            }
        }

        public static UnityEngine.Object GetActivePipelineAsset()
        {
            EnsureUrpAvailable();
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null || !s_urpAssetType.IsInstanceOfType(pipeline))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_PIPELINE_NOT_ACTIVE",
                    "No active UniversalRenderPipelineAsset found.",
                    "Set a URP asset in Graphics Settings.");
            }
            return pipeline;
        }

        public static Type RendererFeatureType
        {
            get
            {
                InitializeTypes();
                return s_rendererFeatureType;
            }
        }

        public static Type VolumeType
        {
            get
            {
                InitializeTypes();
                return s_volumeType;
            }
        }

        public static Type VolumeProfileType
        {
            get
            {
                InitializeTypes();
                return s_volumeProfileType;
            }
        }

        public static List<UrpFeatureInfo> ListRendererFeatures()
        {
            var results = new List<UrpFeatureInfo>();
            var pipeline = GetActivePipelineAsset();
            if (s_rendererDataType == null) return results;

            using (var so = new SerializedObject(pipeline))
            {
                var renderersProp = so.FindProperty("m_RendererDataList");
                if (renderersProp == null) return results;
                for (var rendererIndex = 0; rendererIndex < renderersProp.arraySize; rendererIndex++)
                {
                    var rendererRef = renderersProp.GetArrayElementAtIndex(rendererIndex);
                    var rendererObject = rendererRef.objectReferenceValue;
                    if (rendererObject == null) continue;

                    using (var rendererSo = new SerializedObject(rendererObject))
                    {
                        var featuresProp = rendererSo.FindProperty("m_RendererFeatures");
                        if (featuresProp == null) continue;
                        for (var featureIndex = 0; featureIndex < featuresProp.arraySize; featureIndex++)
                        {
                            var featureRef = featuresProp.GetArrayElementAtIndex(featureIndex);
                            var featureObject = featureRef.objectReferenceValue;
                            if (featureObject == null) continue;
                            results.Add(new UrpFeatureInfo
                            {
                                RendererIndex = rendererIndex,
                                Index = featureIndex,
                                Name = featureObject.name,
                                Type = featureObject.GetType().Name,
                                Enabled = IsFeatureEnabled(featureObject)
                            });
                        }
                    }
                }
            }

            return results;
        }

        public static UnityEngine.Object GetRendererData(UnityEngine.Object pipelineAsset, int rendererIndex)
        {
            using (var so = new SerializedObject(pipelineAsset))
            {
                var renderersProp = so.FindProperty("m_RendererDataList");
                if (renderersProp == null || rendererIndex < 0 || rendererIndex >= renderersProp.arraySize)
                {
                    return null;
                }
                return renderersProp.GetArrayElementAtIndex(rendererIndex).objectReferenceValue;
            }
        }

        public static Type FindFeatureType(string featureTypeName)
        {
            InitializeTypes();
            return GameObjectToolSupport.FindType("UnityEngine.Rendering.Universal." + featureTypeName + ", Unity.RenderPipelines.Universal.Runtime")
                ?? GameObjectToolSupport.FindType(featureTypeName);
        }

        public static Type FindVolumeEffectType(string effectTypeName)
        {
            InitializeTypes();
            return GameObjectToolSupport.FindType("UnityEngine.Rendering.Universal." + effectTypeName + ", Unity.RenderPipelines.Universal.Runtime")
                ?? GameObjectToolSupport.FindType("UnityEngine.Rendering." + effectTypeName + ", Unity.RenderPipelines.Core.Runtime")
                ?? GameObjectToolSupport.FindType(effectTypeName);
        }

        public static UnityEngine.Object GetVolumeProfile(Component volumeComponent)
        {
            InitializeTypes();
            if (s_volumeType == null || volumeComponent == null) return null;

            // "In-memory profile" means Volume.profile. Never mutate sharedProfile assets.
            var profileProp = s_volumeType.GetProperty("profile", BindingFlags.Instance | BindingFlags.Public);
            return profileProp != null ? profileProp.GetValue(volumeComponent, null) as UnityEngine.Object : null;
        }

        // 规划阶段不得触发 Volume.profile；仅检查 sharedProfile 或已经存在的 internal profile。
        internal static UnityEngine.Object GetProfileForPlanning(Component volumeComponent)
        {
            if (s_volumeType == null || volumeComponent == null) return null;
            // Volume.sharedProfile 在 Core RP 14 里是 public 字段而非属性：GetProperty 永远命中不了，
            // PlayMode 重载后 m_InternalProfile 不被序列化，必须字段优先读回 sharedProfile。
            var shared = s_volumeType.GetField("sharedProfile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(volumeComponent) as UnityEngine.Object;
            if (shared != null) return shared;
            var sharedProperty = s_volumeType.GetProperty("sharedProfile", BindingFlags.Instance | BindingFlags.Public)?.GetValue(volumeComponent, null) as UnityEngine.Object;
            if (sharedProperty != null) return sharedProperty;
            return s_volumeType.GetField("m_InternalProfile", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(volumeComponent) as UnityEngine.Object;
        }

        public static List<VolumeInfo> ListVolumes(string targetName)
        {
            InitializeTypes();
            var results = new List<VolumeInfo>();
            if (s_volumeType == null) return results;

            foreach (var component in Resources.FindObjectsOfTypeAll(s_volumeType))
            {
                var typedComponent = component as Component;
                if (typedComponent == null || typedComponent.gameObject == null) continue;
                if (EditorUtility.IsPersistent(typedComponent.gameObject)) continue;

                var hierarchyPath = GameObjectToolSupport.HierarchyPath(typedComponent.gameObject);
                if (!string.IsNullOrEmpty(targetName)
                    && !string.Equals(typedComponent.gameObject.name, targetName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(hierarchyPath, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var type = typedComponent.GetType();
                var enabledProp = type.GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
                var profile = GetProfileForPlanning(typedComponent);
                var profilePath = profile == null ? null : AssetDatabase.GetAssetPath(profile);

                results.Add(new VolumeInfo
                {
                    Target = new GameObjectTargetRef
                    {
                        Id = typedComponent.gameObject.GetInstanceID(),
                        Name = typedComponent.gameObject.name,
                        Path = hierarchyPath
                    },
                    Enabled = enabledProp == null || Convert.ToBoolean(enabledProp.GetValue(typedComponent, null), CultureInfo.InvariantCulture),
                    IsGlobal = Convert.ToBoolean(UrpVolumeOperations.ReadMember(type, typedComponent, "isGlobal") ?? false, CultureInfo.InvariantCulture),
                    Priority = Convert.ToDouble(UrpVolumeOperations.ReadMember(type, typedComponent, "priority") ?? 0f, CultureInfo.InvariantCulture),
                    Weight = Convert.ToDouble(UrpVolumeOperations.ReadMember(type, typedComponent, "weight") ?? 0f, CultureInfo.InvariantCulture),
                    Profile = profile == null ? null : new AssetRef
                    {
                        Name = profile.name,
                        Path = profilePath,
                        Guid = string.IsNullOrEmpty(profilePath) ? null : AssetDatabase.AssetPathToGUID(profilePath)
                    }
                });
            }

            return results;
        }

        public static Dictionary<string, object> ReadSettings(UnityEngine.Object pipelineAsset)
        {
            var settings = new Dictionary<string, object>(StringComparer.Ordinal);
            var type = pipelineAsset.GetType();
            var readableProps = new[]
            {
                "renderScale", "supportsHDR", "msaaSampleCount", "shadowDistance",
                "shadowCascadeCount", "cascade2Split", "cascade3Split", "cascade4Split",
                "shadowCascadeBias", "supportsLightLayers", "debugLevel",
                "mainLightRenderingMode", "mainLightShadowmapResolution",
                "additionalLightsPerObjectLimit", "additionalLightShadowsSupported",
                "additionalLightShadowmapResolution", "srpBatcher", "gpuResidentDrawerMode"
            };

            foreach (var propertyName in readableProps)
            {
                var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (property == null) continue;
                try
                {
                    var value = property.GetValue(pipelineAsset, null);
                    if (value is float f) settings[propertyName] = f;
                    else if (value is double d) settings[propertyName] = d;
                    else if (value is int i) settings[propertyName] = i;
                    else if (value is long l) settings[propertyName] = l;
                    else if (value is bool b) settings[propertyName] = b;
                    else if (value is Enum e) settings[propertyName] = e.ToString();
                    else if (value != null) settings[propertyName] = value.ToString();
                }
                catch
                {
                    // 不可读属性不参与结果。
                }
            }

            return settings;
        }

        public static object ConvertValue(string value, Type targetType)
        {
            if (targetType == typeof(float)) return ParseFiniteSingle(value, "URP setting");
            if (targetType == typeof(double)) return ParseFiniteDouble(value, "URP setting");
            if (targetType == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(long)) return long.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return bool.Parse(value);
            if (targetType.IsEnum) return ParseDefinedEnum(targetType, value, "URP setting");
            if (targetType == typeof(string)) return value;
            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        internal sealed class SettingMutationPlan
        {
            internal UnityEngine.Object Pipeline { get; set; }
            internal string PropertyName { get; set; }
            internal PropertyInfo PublicProperty { get; set; }
            internal SerializedObject SerializedObject { get; set; }
            internal SerializedProperty SerializedProperty { get; set; }
            internal string SerializedPropertyPath { get; set; }
            internal SerializedPropertyType SerializedPropertyType { get; set; }
            internal object ExpectedValue { get; set; }
            internal string ExpectedReadback { get; set; }
        }

        // 在 barrier 前冻结写入目标、目标类型和严格转换后的期望值。
        internal static SettingMutationPlan PrepareSetting(UnityEngine.Object pipelineAsset, string propertyName, string value)
        {
            if (pipelineAsset == null || string.IsNullOrWhiteSpace(propertyName) || value == null)
                throw new UnityCliBridge.BridgeCommandException("E_URP_SETTING_NOT_FOUND", "URP setting property and value are required.", "Pass a writable property and a compatible value.");
            var publicProperty = pipelineAsset.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (publicProperty != null && publicProperty.CanWrite && publicProperty.GetIndexParameters().Length == 0)
                return new SettingMutationPlan { Pipeline = pipelineAsset, PropertyName = propertyName, PublicProperty = publicProperty, ExpectedValue = ConvertValue(value, publicProperty.PropertyType) };
            var serialized = new SerializedObject(pipelineAsset);
            var property = serialized.FindProperty(propertyName) ?? serialized.FindProperty("m_" + char.ToUpper(propertyName[0]) + propertyName.Substring(1));
            if (property == null)
            {
                serialized.Dispose();
                throw new UnityCliBridge.BridgeCommandException("E_URP_SETTING_NOT_FOUND", "URP setting not found: " + propertyName, "Run urp.settings.get to inspect readable settings.");
            }
            var expected = ConvertSerializedPropertyValue(property, value, "URP setting");
            return new SettingMutationPlan { Pipeline = pipelineAsset, PropertyName = propertyName, SerializedObject = serialized, SerializedProperty = property, SerializedPropertyPath = property.propertyPath, SerializedPropertyType = property.propertyType, ExpectedValue = expected, ExpectedReadback = FormatSerializedPropertyValue(property.propertyType, expected) };
        }

        internal static void ApplySetting(SettingMutationPlan plan)
        {
            if (plan == null) throw new UnityCliBridge.BridgeCommandException("E_URP_SETTING_SET_FAILED", "URP setting plan is unavailable.", "Retry the request.");
            if (plan.PublicProperty != null)
            {
                plan.PublicProperty.SetValue(plan.Pipeline, plan.ExpectedValue, null);
                return;
            }
            if (plan.SerializedObject == null || plan.SerializedProperty == null || plan.SerializedProperty.propertyType != plan.SerializedPropertyType)
                throw new UnityCliBridge.BridgeCommandException("E_URP_SETTING_SET_FAILED", "URP serialized setting plan is no longer valid.", "Retry the request.");
            SetSerializedPropertyValue(plan.SerializedProperty, plan.ExpectedValue, plan.SerializedPropertyType);
            plan.SerializedObject.ApplyModifiedProperties();
        }

        internal static string VerifySettingReadback(SettingMutationPlan plan, UnityEngine.Object reloadedPipeline)
        {
            if (plan == null || reloadedPipeline == null) throw new UnityCliBridge.BridgeCommandException("E_URP_SETTING_VERIFY_FAILED", "URP pipeline asset could not be independently reloaded.", "Check the active pipeline asset and retry.");
            if (plan.PublicProperty != null)
            {
                var observed = reloadedPipeline.GetType().GetProperty(plan.PublicProperty.Name, BindingFlags.Instance | BindingFlags.Public);
                var actual = observed == null || !observed.CanRead || observed.PropertyType != plan.PublicProperty.PropertyType ? null : observed.GetValue(reloadedPipeline, null);
                if (!Equals(actual, plan.ExpectedValue)) throw new UnityCliBridge.BridgeCommandException("E_URP_SETTING_VERIFY_FAILED", "URP setting did not retain the requested value: " + plan.PropertyName, "Check the pipeline asset and retry.");
                return Convert.ToString(actual, CultureInfo.InvariantCulture);
            }
            using (var observed = new SerializedObject(reloadedPipeline))
            {
                observed.Update();
                var property = observed.FindProperty(plan.SerializedPropertyPath);
                var actual = property == null || property.propertyType != plan.SerializedPropertyType ? null : ReadSerializedPropertyValue(property);
                if (!string.Equals(actual, plan.ExpectedReadback, StringComparison.Ordinal)) throw new UnityCliBridge.BridgeCommandException("E_URP_SETTING_VERIFY_FAILED", "URP setting did not retain the requested value: " + plan.PropertyName, "Check the pipeline asset and retry.");
                return actual;
            }
        }

        // 保留现有调用方的验证入口；新写入路径应使用返回的冻结计划。
        internal static void ValidateSetting(UnityEngine.Object pipelineAsset, string propertyName, string value)
        {
            PrepareSetting(pipelineAsset, propertyName, value);
        }

        internal static void ValidateSerializedPropertyValue(SerializedProperty property, string value)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float: ParseFiniteSingle(value, "URP setting"); break;
                case SerializedPropertyType.Integer: int.Parse(value, CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.Boolean: bool.Parse(value); break;
                case SerializedPropertyType.Enum: ParseSerializedEnumIndex(property, value, "URP setting"); break;
                case SerializedPropertyType.String: break;
                default: throw new UnityCliBridge.BridgeCommandException("E_URP_PROPERTY_TYPE_UNSUPPORTED", "Cannot set property type " + property.propertyType + " via string value.", "Use direct Unity editing for complex property types.");
            }
        }

        private static object ConvertSerializedPropertyValue(SerializedProperty property, string value, string field)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float: return ParseFiniteSingle(value, field);
                case SerializedPropertyType.Integer: return int.Parse(value, CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return bool.Parse(value);
                case SerializedPropertyType.Enum: return ParseSerializedEnumIndex(property, value, field);
                case SerializedPropertyType.String: return value;
                default: throw new UnityCliBridge.BridgeCommandException("E_URP_PROPERTY_TYPE_UNSUPPORTED", "Cannot set property type " + property.propertyType + " via string value.", "Use direct Unity editing for complex property types.");
            }
        }

        private static string FormatSerializedPropertyValue(SerializedPropertyType type, object value)
        {
            if (type == SerializedPropertyType.Float) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (type == SerializedPropertyType.Integer || type == SerializedPropertyType.Enum) return Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            if (type == SerializedPropertyType.Boolean) return (bool)value ? "true" : "false";
            if (type == SerializedPropertyType.String) return (string)value;
            throw new UnityCliBridge.BridgeCommandException("E_URP_PROPERTY_TYPE_UNSUPPORTED", "Cannot format property type " + type + ".", "Use a scalar pipeline setting.");
        }

        public static void SetSerializedPropertyValue(SerializedProperty property, string value)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float:
                    property.floatValue = ParseFiniteSingle(value, "URP setting");
                    break;
                case SerializedPropertyType.Integer:
                    property.intValue = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Boolean:
                    property.boolValue = bool.Parse(value);
                    break;
                case SerializedPropertyType.Enum:
                    property.enumValueIndex = ParseSerializedEnumIndex(property, value, "URP setting");
                    break;
                case SerializedPropertyType.String:
                    property.stringValue = value;
                    break;
                default:
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_URP_PROPERTY_TYPE_UNSUPPORTED",
                        "Cannot set property type " + property.propertyType + " via string value.",
                        "Use direct Unity editing for complex property types.");
            }
        }

        internal static void SetSerializedPropertyValue(SerializedProperty property, object value, SerializedPropertyType plannedType)
        {
            if (property == null || property.propertyType != plannedType)
                throw new UnityCliBridge.BridgeCommandException("E_URP_SETTING_SET_FAILED", "URP serialized setting no longer matches its prepared plan.", "Retry the request.");
            switch (plannedType)
            {
                case SerializedPropertyType.Float: property.floatValue = (float)value; break;
                case SerializedPropertyType.Integer: property.intValue = (int)value; break;
                case SerializedPropertyType.Boolean: property.boolValue = (bool)value; break;
                case SerializedPropertyType.Enum: property.enumValueIndex = (int)value; break;
                case SerializedPropertyType.String: property.stringValue = (string)value; break;
                default: throw new UnityCliBridge.BridgeCommandException("E_URP_PROPERTY_TYPE_UNSUPPORTED", "Cannot set property type " + plannedType + " via a prepared value.", "Use a scalar pipeline setting.");
            }
        }

        // 仅允许可稳定比较的标量值作为写入完成证明。
        public static string ReadSerializedPropertyValue(SerializedProperty property)
        {
            if (property == null) throw new UnityCliBridge.BridgeCommandException("E_URP_SETTING_VERIFY_FAILED", "URP setting could not be read back.", "Check the pipeline asset and retry.");
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float: return property.floatValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.Integer: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Enum: return property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return property.stringValue;
                default: throw new UnityCliBridge.BridgeCommandException("E_URP_SETTING_VERIFY_FAILED", "URP setting type cannot be read back: " + property.propertyType, "Use a scalar pipeline setting.");
            }
        }

        internal static float ParseFiniteSingle(string value, string field)
        {
            var parsed = double.Parse(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < -float.MaxValue || parsed > float.MaxValue)
                throw new UnityCliBridge.BridgeCommandException("E_URP_VALUE_INVALID", field + " must be finite and representable as a single-precision float.", "Pass a finite value in the single-precision range.");
            return (float)parsed;
        }

        internal static double ParseFiniteDouble(string value, string field)
        {
            var parsed = double.Parse(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(parsed) || double.IsInfinity(parsed))
                throw new UnityCliBridge.BridgeCommandException("E_URP_VALUE_INVALID", field + " must be finite.", "Pass a finite numeric value.");
            return parsed;
        }

        internal static object ParseDefinedEnum(Type enumType, string value, string field)
        {
            var parsed = Enum.Parse(enumType, value, true);
            if (!Enum.IsDefined(enumType, parsed))
                throw new UnityCliBridge.BridgeCommandException("E_URP_VALUE_INVALID", field + " must be a defined enum value.", "Pass a defined enum member name or value.");
            return parsed;
        }

        internal static int ParseSerializedEnumIndex(SerializedProperty property, string value, string field)
        {
            var index = int.Parse(value, CultureInfo.InvariantCulture);
            if (index < 0 || index >= property.enumDisplayNames.Length)
                throw new UnityCliBridge.BridgeCommandException("E_URP_VALUE_INVALID", field + " must be a defined enum value.", "Pass an enum index exposed by the target property.");
            return index;
        }

        private static void InitializeTypes()
        {
            if (s_typesInitialized) return;
            s_typesInitialized = true;
            s_urpAssetType = GameObjectToolSupport.FindType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset, Unity.RenderPipelines.Universal.Runtime");
            s_rendererDataType = GameObjectToolSupport.FindType("UnityEngine.Rendering.Universal.ScriptableRendererData, Unity.RenderPipelines.Universal.Runtime");
            s_rendererFeatureType = GameObjectToolSupport.FindType("UnityEngine.Rendering.Universal.ScriptableRendererFeature, Unity.RenderPipelines.Universal.Runtime");
            s_volumeType = GameObjectToolSupport.FindType("UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core.Runtime");
            s_volumeProfileType = GameObjectToolSupport.FindType("UnityEngine.Rendering.VolumeProfile, Unity.RenderPipelines.Core.Runtime");
        }

        private static bool IsUrpActivePipeline()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null) return false;
            var typeName = pipeline.GetType().FullName ?? string.Empty;
            return typeName.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("URP", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsFeatureEnabled(UnityEngine.Object feature)
        {
            var activeProp = feature.GetType().GetProperty("isActive", BindingFlags.Instance | BindingFlags.Public);
            if (activeProp != null) return Convert.ToBoolean(activeProp.GetValue(feature, null), CultureInfo.InvariantCulture);
            var enabledProp = feature.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            if (enabledProp != null) return Convert.ToBoolean(enabledProp.GetValue(feature, null), CultureInfo.InvariantCulture);
            return true;
        }
    }

    internal static class UrpVolumeOperations
    {
        internal sealed class CreatedVolume
        {
            public GameObject GameObject { get; set; }
            public bool IsGlobal { get; set; }
        }

        internal sealed class SetVolumeResult
        {
            public GameObject GameObject { get; set; }
            public string ObservedEffectType { get; set; }
            public string Value { get; set; }
        }

        internal sealed class AddedVolumeEffectResult
        {
            public GameObject GameObject { get; set; }
            public string ObservedEffectType { get; set; }
        }

        public static void RequireTarget(string target, string operation)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_VOLUME_TARGET_REQUIRED",
                    operation + " requires a target.",
                    "Pass the target Volume GameObject name, hierarchy path, or instance id.");
            }
        }

        internal sealed class VolumeCreateMutationPlan
        {
            internal float? Priority { get; set; }
            internal float? Weight { get; set; }
        }

        public static VolumeCreateMutationPlan ValidateCreate(double? priority, double? weight, string errorPrefix)
        {
            if (priority.HasValue && (double.IsNaN(priority.Value) || double.IsInfinity(priority.Value))) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_PRIORITY_INVALID", "Volume priority must be finite.", "Pass a finite priority.");
            if (weight.HasValue && (double.IsNaN(weight.Value) || double.IsInfinity(weight.Value) || weight.Value < 0d || weight.Value > 1d)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_WEIGHT_OUT_OF_RANGE", "Volume weight must be finite and between 0 and 1.", "Pass a weight in the inclusive range [0, 1].");
            var volumeType = UrpToolSupport.VolumeType;
            var profileProperty = volumeType?.GetProperty("profile", BindingFlags.Instance | BindingFlags.Public);
            if (volumeType == null || UrpToolSupport.VolumeProfileType == null || !HasWritableMember(volumeType, "isGlobal", typeof(bool)) || (priority.HasValue && !HasWritableMember(volumeType, "priority", typeof(float))) || (weight.HasValue && !HasWritableMember(volumeType, "weight", typeof(float))) || profileProperty == null || !profileProperty.CanWrite) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_SYSTEM_NOT_FOUND", "Volume create contract is unavailable.", "Ensure the render pipeline package provides writable Volume members and profile.");
            if (priority.HasValue && !FitsSingle(priority.Value) || weight.HasValue && !FitsSingle(weight.Value)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VALUE_INVALID", "Volume numeric values must be representable as finite float values.", "Pass finite values in the single-precision range.");
            return new VolumeCreateMutationPlan
            {
                Priority = priority.HasValue ? (float)priority.Value : null,
                Weight = weight.HasValue ? (float)weight.Value : null
            };
        }

        public static CreatedVolume Create(string name, bool? isGlobal, VolumeCreateMutationPlan plan, string defaultPrefix, string errorPrefix)
        {
            var volumeType = UrpToolSupport.VolumeType;
            var profileType = UrpToolSupport.VolumeProfileType;
            if (volumeType == null || profileType == null)
            {
                throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_SYSTEM_NOT_FOUND", "Volume system types not found.", "Ensure the render pipeline package is fully imported.");
            }

            var gameObject = new GameObject(string.IsNullOrEmpty(name) ? defaultPrefix + "Volume" : name);
            var volume = gameObject.AddComponent(volumeType);
            var global = isGlobal ?? true;
            SetMember(volumeType, volume, "isGlobal", global);
            if (plan.Priority.HasValue) SetMember(volumeType, volume, "priority", plan.Priority.Value);
            if (plan.Weight.HasValue) SetMember(volumeType, volume, "weight", plan.Weight.Value);

            var profile = ScriptableObject.CreateInstance(profileType);
            profile.name = gameObject.name + "Profile";
            AssignProfile(volumeType, volume, profile, errorPrefix);
            var observedObject = GameObjectToolSupport.ResolveUniqueTarget(gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture), "Created Volume could not be resolved.");
            var observedVolume = observedObject.GetComponent(volumeType);
            var observedProfile = observedVolume == null ? null : UrpToolSupport.GetVolumeProfile(observedVolume);
            if (observedVolume == null || observedProfile == null || observedProfile.GetInstanceID() != profile.GetInstanceID())
                throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Created Volume component or profile could not be read back.", "Check the Volume system and retry.");
            if (Convert.ToBoolean(ReadMember(volumeType, observedVolume, "isGlobal") ?? false, CultureInfo.InvariantCulture) != global)
                throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Created Volume global state did not retain the requested value.", "Check the Volume system and retry.");
            var observedPriority = ReadMember(volumeType, observedVolume, "priority");
            var observedWeight = ReadMember(volumeType, observedVolume, "weight");
            if (plan.Priority.HasValue && (observedPriority == null || Convert.ToSingle(observedPriority, CultureInfo.InvariantCulture) != plan.Priority.Value)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Created Volume priority did not retain the requested value.", "Check the Volume system and retry.");
            if (plan.Weight.HasValue && (observedWeight == null || Convert.ToSingle(observedWeight, CultureInfo.InvariantCulture) != plan.Weight.Value)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Created Volume weight did not retain the requested value.", "Check the Volume system and retry.");
            UnityCliSceneMutation.RegisterCreatedObject(gameObject, "UnityCli " + defaultPrefix + " Volume Create");
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            return new CreatedVolume { GameObject = observedObject, IsGlobal = global };
        }

        public static AddedVolumeEffectResult AddEffect(string target, string effectTypeName, string errorPrefix, Func<string, Type> findEffectType)
        {
            RequireTarget(target, "add-effect");
            if (string.IsNullOrEmpty(effectTypeName)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_EFFECT_TYPE_REQUIRED", "add-effect requires effectType.", "Pass a VolumeComponent-derived type name.");
            var gameObject = ResolveVolume(target, errorPrefix);
            var effectType = findEffectType(effectTypeName);
            if (effectType == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_EFFECT_TYPE_NOT_FOUND", "Volume effect type not found: " + effectTypeName, "Ensure the effect type is correct and the render pipeline package is fully imported.");
            var profile = UrpToolSupport.GetVolumeProfile(gameObject.GetComponent(UrpToolSupport.VolumeType)) as VolumeProfile;
            var addMethod = UrpToolSupport.VolumeProfileType.GetMethod("Add", new[] { typeof(Type), typeof(bool) });
            if (profile == null || addMethod == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_PROFILE_NOT_FOUND", "The target Volume has no writable in-memory profile.", "Create a Volume with urp.volume.create or assign a profile before adding an effect.");
            var countBefore = profile.components.Count(component => component != null && effectType.IsInstanceOfType(component));
            if (countBefore != 0) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_EFFECT_EXISTS", "The requested effect is already present: " + effectTypeName, "Use urp.volume.set to modify the existing effect.");
            addMethod.Invoke(profile, new object[] { effectType, true });
            var observedObject = ResolveVolume(gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture), errorPrefix);
            var observedProfile = UrpToolSupport.GetVolumeProfile(observedObject.GetComponent(UrpToolSupport.VolumeType)) as VolumeProfile;
            var observed = observedProfile?.components.FirstOrDefault(component => component != null && effectType.IsInstanceOfType(component));
            var countAfter = observedProfile == null ? 0 : observedProfile.components.Count(component => component != null && effectType.IsInstanceOfType(component));
            if (observedProfile == null || observedProfile.GetInstanceID() != profile.GetInstanceID() || observed == null || countAfter != countBefore + 1) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Added Volume effect could not be independently located in VolumeProfile.components.", "Check the Volume profile and effect type, then retry.");
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            return new AddedVolumeEffectResult { GameObject = gameObject, ObservedEffectType = observed.GetType().FullName };
        }

        // 在提交前完成 target、effect type、profile 和序列化路径解析。
        internal static void ValidateAddEffect(string target, string effectTypeName, string errorPrefix, Func<string, Type> findEffectType)
        {
            RequireTarget(target, "add-effect");
            if (string.IsNullOrEmpty(effectTypeName)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_EFFECT_TYPE_REQUIRED", "add-effect requires effectType.", "Pass a VolumeComponent-derived type name.");
            var effectType = findEffectType(effectTypeName);
            if (effectType == null || effectType == typeof(VolumeComponent) || effectType.IsAbstract || !typeof(VolumeComponent).IsAssignableFrom(effectType) || !IsPipelineEffectType(effectType)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_EFFECT_TYPE_NOT_FOUND", "Concrete URP VolumeComponent effect type not found: " + effectTypeName, "Use a concrete effect from the active URP pipeline.");
            var profile = UrpToolSupport.GetProfileForPlanning(ResolveVolume(target, errorPrefix).GetComponent(UrpToolSupport.VolumeType)) as VolumeProfile;
            var addMethod = UrpToolSupport.VolumeProfileType.GetMethod("Add", new[] { typeof(Type), typeof(bool) });
            if (profile == null || addMethod == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_PROFILE_NOT_FOUND", "The target Volume has no writable Add API.", "Create a Volume with urp.volume.create or assign a profile before adding an effect.");
            if (profile.components.Any(component => component != null && effectType.IsInstanceOfType(component))) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_EFFECT_EXISTS", "The requested effect is already present: " + effectTypeName, "Use urp.volume.set to modify the existing effect.");
        }

        internal sealed class VolumeSetMutationPlan
        {
            internal int GameObjectId { get; set; }
            internal int PlanningProfileId { get; set; }
            internal int SharedProfileId { get; set; }
            internal Type EffectType { get; set; }
            internal string PropertyPath { get; set; }
            internal SerializedPropertyType PropertyType { get; set; }
            internal object ExpectedValue { get; set; }
            internal string ExpectedReadback { get; set; }
        }

        // 在 barrier 前冻结 Volume、profile、effect、属性路径和严格转换后的标量值。
        internal static VolumeSetMutationPlan ValidateSet(string target, string effectTypeName, string propertyPath, string value, string errorPrefix, Func<string, Type> findEffectType)
        {
            RequireTarget(target, "set");
            if (string.IsNullOrEmpty(effectTypeName)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_EFFECT_TYPE_REQUIRED", "set requires effectType.", "Pass a VolumeComponent-derived type name.");
            if (string.IsNullOrEmpty(propertyPath) || value == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_PROPERTY_REQUIRED", "set requires property and value.", "Pass a SerializedProperty path in property and the new value as a string.");
            var gameObject = ResolveVolume(target, errorPrefix);
            var effectType = findEffectType(effectTypeName);
            if (effectType == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_EFFECT_TYPE_NOT_FOUND", "Volume effect type not found: " + effectTypeName, "Ensure the effect type is correct and the render pipeline package is fully imported.");
            var volume = gameObject.GetComponent(UrpToolSupport.VolumeType);
            var profile = UrpToolSupport.GetProfileForPlanning(volume) as VolumeProfile;
            var effect = profile == null ? null : profile.components.FirstOrDefault(component => component != null && component.GetType() == effectType);
            if (effect == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_EFFECT_NOT_FOUND", "The requested effect is not present: " + effectTypeName, "Add the effect with urp.volume.add-effect before setting it.");
            using (var serialized = new SerializedObject(effect))
            {
                var property = serialized.FindProperty(propertyPath);
                if (property == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_PROPERTY_NOT_FOUND", "Volume effect property not found: " + propertyPath, "Pass a valid SerializedProperty path for the effect.");
                var expected = PrepareVolumeScalar(property, value, errorPrefix);
                var shared = volume.GetType().GetProperty("sharedProfile", BindingFlags.Instance | BindingFlags.Public)?.GetValue(volume, null) as UnityEngine.Object;
                return new VolumeSetMutationPlan { GameObjectId = gameObject.GetInstanceID(), PlanningProfileId = profile.GetInstanceID(), SharedProfileId = shared == null ? 0 : shared.GetInstanceID(), EffectType = effectType, PropertyPath = propertyPath, PropertyType = property.propertyType, ExpectedValue = expected, ExpectedReadback = FormatVolumeScalar(property.propertyType, expected, errorPrefix) };
            }
        }

        // barrier 后首次访问 Volume.profile，随后只在该独立 runtime profile 中按冻结类型写入。
        public static SetVolumeResult Set(VolumeSetMutationPlan plan, string errorPrefix)
        {
            if (plan == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_SET_FAILED", "Prepared URP Volume mutation plan is no longer valid.", "Retry the request.");
            var observedObject = ResolveVolume(plan.GameObjectId.ToString(CultureInfo.InvariantCulture), errorPrefix);
            var mutationProfile = UrpToolSupport.GetVolumeProfile(observedObject.GetComponent(UrpToolSupport.VolumeType)) as VolumeProfile;
            if (mutationProfile == null || (plan.SharedProfileId != 0 && mutationProfile.GetInstanceID() == plan.SharedProfileId)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Volume did not provide an independent runtime profile.", "Check the Volume profile and retry.");
            var mutationEffect = mutationProfile.components.FirstOrDefault(component => component != null && component.GetType() == plan.EffectType);
            if (mutationEffect == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Volume effect could not be located in the runtime profile.", "Check the Volume profile and retry.");
            using (var mutationSerialized = new SerializedObject(mutationEffect))
            {
                var mutationProperty = mutationSerialized.FindProperty(plan.PropertyPath);
                if (mutationProperty == null || mutationProperty.propertyType != plan.PropertyType) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Volume effect property no longer matches its prepared type.", "Check the Volume component parameter and retry.");
                ApplyVolumeScalar(mutationProperty, plan.PropertyType, plan.ExpectedValue, errorPrefix);
                mutationSerialized.ApplyModifiedProperties();
            }
            var observedProfile = UrpToolSupport.GetVolumeProfile(observedObject.GetComponent(UrpToolSupport.VolumeType)) as VolumeProfile;
            var observedEffect = observedProfile?.components.FirstOrDefault(component => component != null && component.GetType() == plan.EffectType);
            if (observedProfile == null || observedProfile.GetInstanceID() != mutationProfile.GetInstanceID() || (plan.SharedProfileId != 0 && observedProfile.GetInstanceID() == plan.SharedProfileId) || observedEffect == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Volume runtime profile or effect could not be independently read back.", "Check the Volume profile and retry.");
            string actualValue;
            using (var observedSerialized = new SerializedObject(observedEffect))
            {
                observedSerialized.Update();
                var actualProperty = observedSerialized.FindProperty(plan.PropertyPath);
                if (actualProperty == null || actualProperty.propertyType != plan.PropertyType) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Volume effect property no longer matches its prepared type.", "Check the Volume component parameter and retry.");
                actualValue = ReadSerializedValue(actualProperty, errorPrefix);
            }
            if (!string.Equals(actualValue, plan.ExpectedReadback, StringComparison.Ordinal)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Volume effect property did not retain the requested value: " + plan.PropertyPath, "Check the Volume component parameter and retry.");
            UnityCliSceneMutation.MarkSceneDirty(observedObject.scene);
            return new SetVolumeResult { GameObject = observedObject, ObservedEffectType = observedEffect.GetType().FullName, Value = actualValue };
        }

        private static object PrepareVolumeScalar(SerializedProperty property, string value, string errorPrefix)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float: return UrpToolSupport.ParseFiniteSingle(value, "URP Volume value");
                case SerializedPropertyType.Integer: return int.Parse(value, CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return bool.Parse(value);
                case SerializedPropertyType.Enum: return UrpToolSupport.ParseSerializedEnumIndex(property, value, "URP Volume value");
                case SerializedPropertyType.String: return value;
                default: throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_PROPERTY_TYPE_UNSUPPORTED", "Cannot set Volume property type " + property.propertyType + " via string value.", "Use a scalar Volume parameter.");
            }
        }

        private static void ApplyVolumeScalar(SerializedProperty property, SerializedPropertyType type, object value, string errorPrefix)
        {
            if (property.propertyType != type) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_SET_FAILED", "Prepared Volume property type changed before apply.", "Retry the request.");
            switch (type)
            {
                case SerializedPropertyType.Float: property.floatValue = (float)value; break;
                case SerializedPropertyType.Integer: property.intValue = (int)value; break;
                case SerializedPropertyType.Boolean: property.boolValue = (bool)value; break;
                case SerializedPropertyType.Enum: property.enumValueIndex = (int)value; break;
                case SerializedPropertyType.String: property.stringValue = (string)value; break;
                default: throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_PROPERTY_TYPE_UNSUPPORTED", "Cannot set Volume property type " + type + " via a prepared value.", "Use a scalar Volume parameter.");
            }
        }

        private static string FormatVolumeScalar(SerializedPropertyType type, object value, string errorPrefix)
        {
            if (type == SerializedPropertyType.Float) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (type == SerializedPropertyType.Integer || type == SerializedPropertyType.Enum) return Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            if (type == SerializedPropertyType.Boolean) return (bool)value ? "true" : "false";
            if (type == SerializedPropertyType.String) return (string)value;
            throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_PROPERTY_TYPE_UNSUPPORTED", "Cannot format Volume property type " + type + ".", "Use a scalar Volume parameter.");
        }

        // 以序列化参数的实际类型规范化读回值，避免返回原始请求字符串。
        private static string ReadSerializedValue(SerializedProperty property, string errorPrefix)
        {
            if (property == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Volume effect property could not be read back.", "Check the Volume component parameter and retry.");
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float: return property.floatValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.Integer: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Enum: return property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return property.stringValue;
                default: throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Volume effect property type cannot be read back: " + property.propertyType, "Use a scalar Volume parameter.");
            }
        }

        private static GameObject ResolveVolume(string target, string errorPrefix)
        {
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(target, "A Volume target is required.");
            if (UrpToolSupport.VolumeType == null || gameObject.GetComponent(UrpToolSupport.VolumeType) == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_NOT_FOUND", "Volume component not found on target: " + target, "Pass a target GameObject that has a Volume component.");
            return gameObject;
        }

        private static bool FitsSingle(double value) => value >= -float.MaxValue && value <= float.MaxValue;
        private static bool HasWritableMember(Type type, string name, Type expectedType)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return (property != null && property.CanWrite && property.PropertyType == expectedType) || (field != null && !field.IsInitOnly && field.FieldType == expectedType);
        }
        private static void SetMember(Type type, Component component, string name, object value)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite) { property.SetValue(component, value, null); return; }
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field != null && !field.IsInitOnly) { field.SetValue(component, value); return; }
            throw new UnityCliBridge.BridgeCommandException("E_URP_VOLUME_SYSTEM_NOT_FOUND", "Volume member is not writable: " + name, "Use a supported render pipeline version.");
        }

        // 兼容 Core Volume 在不同包版本中使用 public property 或 field。
        internal static object ReadMember(Type type, Component component, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanRead) return property.GetValue(component, null);
            return type.GetField(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(component);
        }
        private static bool IsPipelineEffectType(Type type) => type.Assembly == UrpToolSupport.VolumeType.Assembly || (type.Namespace ?? string.Empty).StartsWith("UnityEngine.Rendering.Universal", StringComparison.Ordinal);

        private static void AssignProfile(Type volumeType, Component volume, UnityEngine.Object profile, string errorPrefix)
        {
            var property = volumeType.GetProperty("profile", BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_PROFILE_ASSIGN_FAILED", "The Volume profile cannot be assigned.", "Use a supported render pipeline version with a writable Volume profile.");
            property.SetValue(volume, profile, null);
        }
    }

    public sealed class UrpFeatureInfo
    {
        public long RendererIndex { get; set; }
        public long Index { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public bool Enabled { get; set; }
    }

    public sealed class UrpPlannedEffect
    {
        public string Type { get; set; }
        public string FeatureType { get; set; }
        public string EffectType { get; set; }
        public string Property { get; set; }
        public string Value { get; set; }
        public string Name { get; set; }
        public string Target { get; set; }
        public long? Index { get; set; }
        public long? RendererIndex { get; set; }
        public string SideEffects { get; set; }
        public bool? Enabled { get; set; }
    }
}
#endif
