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
    /// HDRP 工具共享 helper：负责活动管线检测、Volume 枚举与设置读写。
    /// 保持扩展自包含，不依赖旧 Commands partial bridge 方法。
    /// </summary>
    internal static class HdrpToolSupport
    {
        private static bool s_typesInitialized;
        private static Type s_hdrpAssetType;
        private static Type s_volumeType;
        private static Type s_volumeProfileType;

        public static void EnsureHdrpAvailable()
        {
            InitializeTypes();
            if (s_hdrpAssetType == null || !IsHdrpActivePipeline())
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_HDRP_PACKAGE_NOT_FOUND",
                    "HDRP (High Definition Render Pipeline) is not the active render pipeline, or com.unity.render-pipelines.high-definition is not installed.",
                    "Install HDRP via the Unity Package Manager and set an HDRenderPipelineAsset as the active render pipeline in Graphics Settings.");
            }
        }

        public static UnityEngine.Object GetActivePipelineAsset()
        {
            EnsureHdrpAvailable();
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null || !s_hdrpAssetType.IsInstanceOfType(pipeline))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_HDRP_PIPELINE_NOT_ACTIVE",
                    "No active HDRenderPipelineAsset found.",
                    "Set an HDRP asset in Graphics Settings.");
            }
            return pipeline;
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

        public static Type FindVolumeEffectType(string effectTypeName)
        {
            InitializeTypes();
            return GameObjectToolSupport.FindType("UnityEngine.Rendering.HighDefinition." + effectTypeName + ", Unity.RenderPipelines.HighDefinition.Runtime")
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
                    IsGlobal = Convert.ToBoolean(HdrpVolumeOperations.ReadMember(type, typedComponent, "isGlobal") ?? false, CultureInfo.InvariantCulture),
                    Priority = Convert.ToDouble(HdrpVolumeOperations.ReadMember(type, typedComponent, "priority") ?? 0f, CultureInfo.InvariantCulture),
                    Weight = Convert.ToDouble(HdrpVolumeOperations.ReadMember(type, typedComponent, "weight") ?? 0f, CultureInfo.InvariantCulture),
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
                "renderScale", "msaaSampleCount", "shadowDistance",
                "shadowCascadeCount", "shadowSliceCount",
                "supportedShadowQuality", "supportedShadows",
                "shadowAtlasSize", "contactShadows",
                "volumetricClouds", "volumetricFog",
                "supportWater", "waterSimulationResolution",
                "transparentLayerMask", "disableColorGrading",
                "hdrBufferPrecision", "maxPixelLights",
                "supportVolumetrics", "supportSubsurfaceScattering"
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
            if (targetType == typeof(float)) return ParseFiniteSingle(value, "HDRP setting");
            if (targetType == typeof(double)) return ParseFiniteDouble(value, "HDRP setting");
            if (targetType == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(long)) return long.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return bool.Parse(value);
            if (targetType.IsEnum) return ParseDefinedEnum(targetType, value, "HDRP setting");
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

        internal static SettingMutationPlan PrepareSetting(UnityEngine.Object pipeline, string propertyName, string value)
        {
            if (pipeline == null || string.IsNullOrWhiteSpace(propertyName) || value == null) throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_NOT_FOUND", "HDRP setting property and value are required.", "Pass a writable property and a compatible value.");
            var publicProperty = pipeline.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (publicProperty != null && publicProperty.CanWrite && publicProperty.GetIndexParameters().Length == 0) return new SettingMutationPlan { Pipeline = pipeline, PropertyName = propertyName, PublicProperty = publicProperty, ExpectedValue = ConvertValue(value, publicProperty.PropertyType) };
            var serialized = new SerializedObject(pipeline);
            var property = serialized.FindProperty(propertyName) ?? serialized.FindProperty("m_" + char.ToUpper(propertyName[0]) + propertyName.Substring(1));
            if (property == null) { serialized.Dispose(); throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_NOT_FOUND", "HDRP setting not found: " + propertyName, "Run hdrp.settings-get to inspect readable settings."); }
            var expected = ConvertScalar(property, value);
            return new SettingMutationPlan { Pipeline = pipeline, PropertyName = propertyName, SerializedObject = serialized, SerializedProperty = property, SerializedPropertyPath = property.propertyPath, SerializedPropertyType = property.propertyType, ExpectedValue = expected, ExpectedReadback = FormatScalar(property.propertyType, expected) };
        }

        internal static void ApplySetting(SettingMutationPlan plan)
        {
            if (plan == null) throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_SET_FAILED", "HDRP setting plan is unavailable.", "Retry the request.");
            if (plan.PublicProperty != null) { plan.PublicProperty.SetValue(plan.Pipeline, plan.ExpectedValue, null); return; }
            if (plan.SerializedObject == null || plan.SerializedProperty == null || plan.SerializedProperty.propertyType != plan.SerializedPropertyType) throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_SET_FAILED", "HDRP serialized setting plan is no longer valid.", "Retry the request.");
            ApplyScalar(plan.SerializedProperty, plan.SerializedPropertyType, plan.ExpectedValue);
            plan.SerializedObject.ApplyModifiedProperties();
        }

        internal static string VerifySettingReadback(SettingMutationPlan plan, UnityEngine.Object pipeline)
        {
            if (plan == null || pipeline == null) throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_VERIFY_FAILED", "HDRP pipeline asset could not be independently reloaded.", "Check the active pipeline asset and retry.");
            if (plan.PublicProperty != null) { var property = pipeline.GetType().GetProperty(plan.PublicProperty.Name, BindingFlags.Instance | BindingFlags.Public); var actual = property == null || !property.CanRead || property.PropertyType != plan.PublicProperty.PropertyType ? null : property.GetValue(pipeline, null); if (!Equals(actual, plan.ExpectedValue)) throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_VERIFY_FAILED", "HDRP setting did not retain the requested value: " + plan.PropertyName, "Check the pipeline asset and retry."); return Convert.ToString(actual, CultureInfo.InvariantCulture); }
            using (var serialized = new SerializedObject(pipeline)) { serialized.Update(); var property = serialized.FindProperty(plan.SerializedPropertyPath); var actual = property == null || property.propertyType != plan.SerializedPropertyType ? null : ReadSerializedPropertyValue(property); if (!string.Equals(actual, plan.ExpectedReadback, StringComparison.Ordinal)) throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_VERIFY_FAILED", "HDRP setting did not retain the requested value: " + plan.PropertyName, "Check the pipeline asset and retry."); return actual; }
        }

        private static object ConvertScalar(SerializedProperty property, string value)
        {
            switch (property.propertyType) { case SerializedPropertyType.Float: return ParseFiniteSingle(value, "HDRP setting"); case SerializedPropertyType.Integer: return int.Parse(value, CultureInfo.InvariantCulture); case SerializedPropertyType.Boolean: return bool.Parse(value); case SerializedPropertyType.Enum: return ParseSerializedEnumIndex(property, value, "HDRP setting"); case SerializedPropertyType.String: return value; default: throw new UnityCliBridge.BridgeCommandException("E_HDRP_PROPERTY_TYPE_UNSUPPORTED", "Cannot set property type " + property.propertyType + " via string value.", "Use a scalar pipeline setting."); }
        }
        private static void ApplyScalar(SerializedProperty property, SerializedPropertyType type, object value)
        {
            if (property == null || property.propertyType != type) throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_SET_FAILED", "HDRP property no longer matches its prepared plan.", "Retry the request.");
            switch (type) { case SerializedPropertyType.Float: property.floatValue = (float)value; break; case SerializedPropertyType.Integer: property.intValue = (int)value; break; case SerializedPropertyType.Boolean: property.boolValue = (bool)value; break; case SerializedPropertyType.Enum: property.enumValueIndex = (int)value; break; case SerializedPropertyType.String: property.stringValue = (string)value; break; default: throw new UnityCliBridge.BridgeCommandException("E_HDRP_PROPERTY_TYPE_UNSUPPORTED", "Cannot set property type " + type + " via a prepared value.", "Use a scalar pipeline setting."); }
        }
        private static string FormatScalar(SerializedPropertyType type, object value)
        {
            if (type == SerializedPropertyType.Float) return ((float)value).ToString("R", CultureInfo.InvariantCulture); if (type == SerializedPropertyType.Integer || type == SerializedPropertyType.Enum) return Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture); if (type == SerializedPropertyType.Boolean) return (bool)value ? "true" : "false"; if (type == SerializedPropertyType.String) return (string)value; throw new UnityCliBridge.BridgeCommandException("E_HDRP_PROPERTY_TYPE_UNSUPPORTED", "Cannot format property type " + type + ".", "Use a scalar pipeline setting.");
        }

        public static void RequireFinite(double value, string field)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new UnityCliBridge.BridgeCommandException("E_HDRP_VALUE_INVALID", "Invalid " + field + " value.", "Pass a finite numeric value.");
        }

        public static void ValidateSerializedPropertyValue(SerializedProperty property, string value)
        {
            if (property == null) throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_NOT_FOUND", "HDRP setting not found.", "Run hdrp.settings-get to inspect readable settings.");
            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Float: ParseFiniteSingle(value, "HDRP setting"); break;
                    case SerializedPropertyType.Integer: int.Parse(value, CultureInfo.InvariantCulture); break;
                    case SerializedPropertyType.Boolean: bool.Parse(value); break;
                    case SerializedPropertyType.Enum: ParseSerializedEnumIndex(property, value, "HDRP setting"); break;
                    case SerializedPropertyType.String: break;
                    default: throw new UnityCliBridge.BridgeCommandException("E_HDRP_PROPERTY_TYPE_UNSUPPORTED", "Cannot set property type " + property.propertyType + " via string value.", "Use direct Unity editing for complex property types.");
                }
            }
            catch (FormatException) { throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_SET_FAILED", "Invalid value for HDRP setting.", "Pass a value compatible with the setting type."); }
        }

        public static void SetSerializedPropertyValue(SerializedProperty property, string value)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float:
                    property.floatValue = ParseFiniteSingle(value, "HDRP setting");
                    break;
                case SerializedPropertyType.Integer:
                    property.intValue = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Boolean:
                    property.boolValue = bool.Parse(value);
                    break;
                case SerializedPropertyType.Enum:
                    property.enumValueIndex = ParseSerializedEnumIndex(property, value, "HDRP setting");
                    break;
                case SerializedPropertyType.String:
                    property.stringValue = value;
                    break;
                default:
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_HDRP_PROPERTY_TYPE_UNSUPPORTED",
                        "Cannot set property type " + property.propertyType + " via string value.",
                        "Use direct Unity editing for complex property types.");
            }
        }

        // 仅允许可稳定比较的标量值作为写入完成证明。
        public static string ReadSerializedPropertyValue(SerializedProperty property)
        {
            if (property == null) throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_VERIFY_FAILED", "HDRP setting could not be read back.", "Check the pipeline asset and retry.");
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float: return property.floatValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.Integer: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Enum: return property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return property.stringValue;
                default: throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_VERIFY_FAILED", "HDRP setting type cannot be read back: " + property.propertyType, "Use a scalar pipeline setting.");
            }
        }

        internal static float ParseFiniteSingle(string value, string field)
        {
            var parsed = double.Parse(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < -float.MaxValue || parsed > float.MaxValue)
                throw new UnityCliBridge.BridgeCommandException("E_HDRP_VALUE_INVALID", field + " must be finite and representable as a single-precision float.", "Pass a finite value in the single-precision range.");
            return (float)parsed;
        }

        internal static double ParseFiniteDouble(string value, string field)
        {
            var parsed = double.Parse(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(parsed) || double.IsInfinity(parsed))
                throw new UnityCliBridge.BridgeCommandException("E_HDRP_VALUE_INVALID", field + " must be finite.", "Pass a finite numeric value.");
            return parsed;
        }

        internal static object ParseDefinedEnum(Type enumType, string value, string field)
        {
            var parsed = Enum.Parse(enumType, value, true);
            if (!Enum.IsDefined(enumType, parsed))
                throw new UnityCliBridge.BridgeCommandException("E_HDRP_VALUE_INVALID", field + " must be a defined enum value.", "Pass a defined enum member name or value.");
            return parsed;
        }

        internal static int ParseSerializedEnumIndex(SerializedProperty property, string value, string field)
        {
            var index = int.Parse(value, CultureInfo.InvariantCulture);
            if (index < 0 || index >= property.enumDisplayNames.Length)
                throw new UnityCliBridge.BridgeCommandException("E_HDRP_VALUE_INVALID", field + " must be a defined enum value.", "Pass an enum index exposed by the target property.");
            return index;
        }

        private static void InitializeTypes()
        {
            if (s_typesInitialized) return;
            s_typesInitialized = true;
            s_hdrpAssetType = GameObjectToolSupport.FindType("UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset, Unity.RenderPipelines.HighDefinition.Runtime");
            s_volumeType = GameObjectToolSupport.FindType("UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core.Runtime");
            s_volumeProfileType = GameObjectToolSupport.FindType("UnityEngine.Rendering.VolumeProfile, Unity.RenderPipelines.Core.Runtime");
        }

        private static bool IsHdrpActivePipeline()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null) return false;
            var typeName = pipeline.GetType().FullName ?? string.Empty;
            return typeName.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal static class HdrpVolumeOperations
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

        internal sealed class VolumeCreateMutationPlan { internal float? Priority { get; set; } internal float? Weight { get; set; } }

        public static VolumeCreateMutationPlan ValidateCreate(double? priority, double? weight, string errorPrefix)
        {
            if (priority.HasValue) HdrpToolSupport.RequireFinite(priority.Value, "priority");
            if (weight.HasValue) HdrpToolSupport.RequireFinite(weight.Value, "weight");
            if (weight.HasValue && (weight.Value < 0d || weight.Value > 1d)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_WEIGHT_OUT_OF_RANGE", "Volume weight must be between 0 and 1.", "Pass a weight in the inclusive range [0, 1].");
            var volumeType = HdrpToolSupport.VolumeType;
            var profileProperty = volumeType?.GetProperty("profile", BindingFlags.Instance | BindingFlags.Public);
            if (volumeType == null || HdrpToolSupport.VolumeProfileType == null || !HasWritableMember(volumeType, "isGlobal", typeof(bool)) || (priority.HasValue && !HasWritableMember(volumeType, "priority", typeof(float))) || (weight.HasValue && !HasWritableMember(volumeType, "weight", typeof(float))) || profileProperty == null || !profileProperty.CanWrite) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_SYSTEM_NOT_FOUND", "Volume create contract is unavailable.", "Ensure the render pipeline package provides writable Volume members and profile.");
            if (priority.HasValue && !FitsSingle(priority.Value) || weight.HasValue && !FitsSingle(weight.Value)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VALUE_INVALID", "Volume numeric values must be representable as finite float values.", "Pass finite values in the single-precision range.");
            return new VolumeCreateMutationPlan { Priority = priority.HasValue ? (float)priority.Value : null, Weight = weight.HasValue ? (float)weight.Value : null };
        }

        public static void ValidateAddEffect(string target, string effectTypeName, string errorPrefix, Func<string, Type> findEffectType)
        {
            RequireTarget(target, "add-effect");
            if (string.IsNullOrEmpty(effectTypeName)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_EFFECT_TYPE_REQUIRED", "add-effect requires effectType.", "Pass a VolumeComponent-derived type name.");
            var volume = ResolveVolume(target, errorPrefix);
            var effectType = findEffectType(effectTypeName);
            if (effectType == null || effectType == typeof(VolumeComponent) || effectType.IsAbstract || !typeof(VolumeComponent).IsAssignableFrom(effectType) || !IsPipelineEffectType(effectType)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_EFFECT_TYPE_NOT_FOUND", "Concrete HDRP VolumeComponent effect type not found: " + effectTypeName, "Use a concrete effect from the active HDRP pipeline.");
            var profile = HdrpToolSupport.GetProfileForPlanning(volume.GetComponent(HdrpToolSupport.VolumeType)) as VolumeProfile;
            var addMethod = HdrpToolSupport.VolumeProfileType.GetMethod("Add", new[] { typeof(Type), typeof(bool) });
            if (profile == null || addMethod == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_PROFILE_NOT_FOUND", "The target Volume has no writable Add API.", "Create a Volume or assign a profile before adding an effect.");
            if (profile.components.Any(component => component != null && effectType.IsInstanceOfType(component))) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_EFFECT_EXISTS", "The requested effect is already present: " + effectTypeName, "Use hdrp.volume-set to modify the existing effect.");
        }

        internal sealed class VolumeSetMutationPlan { internal int GameObjectId { get; set; } internal int PlanningProfileId { get; set; } internal int SharedProfileId { get; set; } internal Type EffectType { get; set; } internal string PropertyPath { get; set; } internal SerializedPropertyType PropertyType { get; set; } internal object ExpectedValue { get; set; } internal string ExpectedReadback { get; set; } }

        public static VolumeSetMutationPlan ValidateSet(string target, string effectTypeName, string propertyPath, string value, string errorPrefix, Func<string, Type> findEffectType)
        {
            RequireTarget(target, "set");
            if (string.IsNullOrEmpty(effectTypeName)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_EFFECT_TYPE_REQUIRED", "set requires effectType.", "Pass a VolumeComponent-derived type name.");
            if (string.IsNullOrEmpty(propertyPath) || value == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_PROPERTY_REQUIRED", "set requires property and value.", "Pass a SerializedProperty path and value.");
            var volume = ResolveVolume(target, errorPrefix);
            var effectType = findEffectType(effectTypeName);
            if (effectType == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_EFFECT_TYPE_NOT_FOUND", "Volume effect type not found: " + effectTypeName, "Ensure the effect type is correct and imported.");
            var volumeComponent = volume.GetComponent(HdrpToolSupport.VolumeType);
            var profile = HdrpToolSupport.GetProfileForPlanning(volumeComponent) as VolumeProfile;
            var effect = profile == null ? null : profile.components.FirstOrDefault(component => component != null && component.GetType() == effectType);
            if (effect == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_EFFECT_NOT_FOUND", "The requested effect is not present: " + effectTypeName, "Add the effect before setting it.");
            using (var serialized = new SerializedObject(effect))
            {
                var property = serialized.FindProperty(propertyPath);
                if (property == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_PROPERTY_NOT_FOUND", "Volume effect property not found: " + propertyPath, "Pass a valid SerializedProperty path.");
                var expected = PrepareScalar(property, value, errorPrefix);
                var shared = volumeComponent.GetType().GetProperty("sharedProfile", BindingFlags.Instance | BindingFlags.Public)?.GetValue(volumeComponent, null) as UnityEngine.Object;
                return new VolumeSetMutationPlan { GameObjectId = volume.GetInstanceID(), PlanningProfileId = profile.GetInstanceID(), SharedProfileId = shared == null ? 0 : shared.GetInstanceID(), EffectType = effectType, PropertyPath = propertyPath, PropertyType = property.propertyType, ExpectedValue = expected, ExpectedReadback = FormatScalar(property.propertyType, expected) };
            }
        }

        public static void RequireTarget(string target, string operation)
        {
            if (string.IsNullOrEmpty(target)) throw new UnityCliBridge.BridgeCommandException("E_HDRP_VOLUME_TARGET_REQUIRED", operation + " requires a target.", "Pass the target Volume GameObject name, hierarchy path, or instance id.");
        }

        public static CreatedVolume Create(string name, bool? isGlobal, VolumeCreateMutationPlan plan, string defaultPrefix, string errorPrefix)
        {
            var volumeType = HdrpToolSupport.VolumeType;
            var profileType = HdrpToolSupport.VolumeProfileType;
            if (volumeType == null || profileType == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_SYSTEM_NOT_FOUND", "Volume system types not found.", "Ensure the render pipeline package is fully imported.");
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
            var observedProfile = observedVolume == null ? null : HdrpToolSupport.GetVolumeProfile(observedVolume);
            if (observedVolume == null || observedProfile == null || observedProfile.GetInstanceID() != profile.GetInstanceID() || observedProfile.GetType() != profileType)
                throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Created Volume component or profile could not be independently read back.", "Check the Volume system and retry.");
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
            var profile = HdrpToolSupport.GetVolumeProfile(gameObject.GetComponent(HdrpToolSupport.VolumeType)) as VolumeProfile;
            var addMethod = HdrpToolSupport.VolumeProfileType.GetMethod("Add", new[] { typeof(Type), typeof(bool) });
            if (profile == null || addMethod == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_PROFILE_NOT_FOUND", "The target Volume has no writable in-memory profile.", "Create a Volume with hdrp.volume-create or assign a profile before adding an effect.");
            var countBefore = profile.components.Count(component => component != null && effectType.IsInstanceOfType(component));
            if (countBefore != 0) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_EFFECT_EXISTS", "The requested effect is already present: " + effectTypeName, "Use hdrp.volume-set to modify the existing effect.");
            addMethod.Invoke(profile, new object[] { effectType, true });
            var observedObject = ResolveVolume(gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture), errorPrefix);
            var observedProfile = HdrpToolSupport.GetVolumeProfile(observedObject.GetComponent(HdrpToolSupport.VolumeType)) as VolumeProfile;
            var observed = observedProfile?.components.FirstOrDefault(component => component != null && effectType.IsInstanceOfType(component));
            var countAfter = observedProfile == null ? 0 : observedProfile.components.Count(component => component != null && effectType.IsInstanceOfType(component));
            if (observedProfile == null || observedProfile.GetInstanceID() != profile.GetInstanceID() || observed == null || countAfter != countBefore + 1) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Added Volume effect could not be independently located in VolumeProfile.components.", "Check the Volume profile and effect type, then retry.");
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            return new AddedVolumeEffectResult { GameObject = gameObject, ObservedEffectType = observed.GetType().FullName };
        }

        // barrier 后首次访问 Volume.profile，随后只在该独立 runtime profile 中按冻结类型写入。
        public static SetVolumeResult Set(VolumeSetMutationPlan plan, string errorPrefix)
        {
            if (plan == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_SET_FAILED", "Prepared HDRP Volume mutation plan is no longer valid.", "Retry the request.");
            var observedObject = ResolveVolume(plan.GameObjectId.ToString(CultureInfo.InvariantCulture), errorPrefix);
            var mutationProfile = HdrpToolSupport.GetVolumeProfile(observedObject.GetComponent(HdrpToolSupport.VolumeType)) as VolumeProfile;
            if (mutationProfile == null || (plan.SharedProfileId != 0 && mutationProfile.GetInstanceID() == plan.SharedProfileId)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Volume did not provide an independent runtime profile.", "Check the Volume profile and retry.");
            var mutationEffect = mutationProfile.components.FirstOrDefault(component => component != null && component.GetType() == plan.EffectType);
            if (mutationEffect == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Volume effect could not be located in the runtime profile.", "Check the Volume profile and retry.");
            using (var mutationSerialized = new SerializedObject(mutationEffect))
            {
                var mutationProperty = mutationSerialized.FindProperty(plan.PropertyPath);
                if (mutationProperty == null || mutationProperty.propertyType != plan.PropertyType) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Volume property no longer matches its prepared type.", "Check the Volume component parameter and retry.");
                ApplyPreparedScalar(mutationProperty, plan.PropertyType, plan.ExpectedValue, errorPrefix);
                mutationSerialized.ApplyModifiedProperties();
            }
            var observedProfile = HdrpToolSupport.GetVolumeProfile(observedObject.GetComponent(HdrpToolSupport.VolumeType)) as VolumeProfile;
            var observedEffect = observedProfile?.components.FirstOrDefault(component => component != null && component.GetType() == plan.EffectType);
            if (observedProfile == null || observedProfile.GetInstanceID() != mutationProfile.GetInstanceID() || (plan.SharedProfileId != 0 && observedProfile.GetInstanceID() == plan.SharedProfileId) || observedEffect == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Volume runtime profile or effect could not be independently read back.", "Check the Volume profile and retry.");
            string actual;
            using (var observed = new SerializedObject(observedEffect)) { observed.Update(); var property = observed.FindProperty(plan.PropertyPath); if (property == null || property.propertyType != plan.PropertyType) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Volume property no longer matches its prepared type.", "Check the Volume component parameter and retry."); actual = ReadSerializedValue(property, errorPrefix); }
            if (!string.Equals(actual, plan.ExpectedReadback, StringComparison.Ordinal)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_VERIFY_FAILED", "Volume effect property did not retain the requested value: " + plan.PropertyPath, "Check the Volume component parameter and retry.");
            UnityCliSceneMutation.MarkSceneDirty(observedObject.scene);
            return new SetVolumeResult { GameObject = observedObject, ObservedEffectType = observedEffect.GetType().FullName, Value = actual };
        }

        private static object PrepareScalar(SerializedProperty property, string value, string errorPrefix)
        {
            switch (property.propertyType) { case SerializedPropertyType.Float: return HdrpToolSupport.ParseFiniteSingle(value, "HDRP Volume value"); case SerializedPropertyType.Integer: return int.Parse(value, CultureInfo.InvariantCulture); case SerializedPropertyType.Boolean: return bool.Parse(value); case SerializedPropertyType.Enum: return HdrpToolSupport.ParseSerializedEnumIndex(property, value, "HDRP Volume value"); case SerializedPropertyType.String: return value; default: throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_PROPERTY_TYPE_UNSUPPORTED", "Cannot set Volume property type " + property.propertyType + " via string value.", "Use a scalar Volume parameter."); }
        }
        private static void ApplyPreparedScalar(SerializedProperty property, SerializedPropertyType type, object value, string errorPrefix)
        {
            if (property.propertyType != type) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_SET_FAILED", "Prepared Volume property type changed before apply.", "Retry the request.");
            switch (type) { case SerializedPropertyType.Float: property.floatValue = (float)value; break; case SerializedPropertyType.Integer: property.intValue = (int)value; break; case SerializedPropertyType.Boolean: property.boolValue = (bool)value; break; case SerializedPropertyType.Enum: property.enumValueIndex = (int)value; break; case SerializedPropertyType.String: property.stringValue = (string)value; break; default: throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_PROPERTY_TYPE_UNSUPPORTED", "Cannot set Volume property type " + type + " via a prepared value.", "Use a scalar Volume parameter."); }
        }
        private static string FormatScalar(SerializedPropertyType type, object value)
        {
            if (type == SerializedPropertyType.Float) return ((float)value).ToString("R", CultureInfo.InvariantCulture); if (type == SerializedPropertyType.Integer || type == SerializedPropertyType.Enum) return Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture); if (type == SerializedPropertyType.Boolean) return (bool)value ? "true" : "false"; if (type == SerializedPropertyType.String) return (string)value; throw new UnityCliBridge.BridgeCommandException("E_HDRP_PROPERTY_TYPE_UNSUPPORTED", "Cannot format Volume property type " + type + ".", "Use a scalar Volume parameter.");
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
            if (HdrpToolSupport.VolumeType == null || gameObject.GetComponent(HdrpToolSupport.VolumeType) == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_NOT_FOUND", "Volume component not found on target: " + target, "Pass a target GameObject that has a Volume component.");
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
            throw new UnityCliBridge.BridgeCommandException("E_HDRP_VOLUME_SYSTEM_NOT_FOUND", "Volume member is not writable: " + name, "Use a supported render pipeline version.");
        }

        // 兼容 Core Volume 在不同包版本中使用 public property 或 field。
        internal static object ReadMember(Type type, Component component, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanRead) return property.GetValue(component, null);
            return type.GetField(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(component);
        }
        private static bool IsPipelineEffectType(Type type) => type.Assembly == HdrpToolSupport.VolumeType.Assembly || (type.Namespace ?? string.Empty).StartsWith("UnityEngine.Rendering.HighDefinition", StringComparison.Ordinal);

        private static void AssignProfile(Type volumeType, Component volume, UnityEngine.Object profile, string errorPrefix)
        {
            var property = volumeType.GetProperty("profile", BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_PROFILE_ASSIGN_FAILED", "The Volume profile cannot be assigned.", "Use a supported render pipeline version with a writable Volume profile.");
            property.SetValue(volume, profile, null);
        }
    }

    public sealed class HdrpPlannedEffect
    {
        public string Type { get; set; }
        public string EffectType { get; set; }
        public string Property { get; set; }
        public string Value { get; set; }
        public string Name { get; set; }
        public string Target { get; set; }
    }
}
#endif
