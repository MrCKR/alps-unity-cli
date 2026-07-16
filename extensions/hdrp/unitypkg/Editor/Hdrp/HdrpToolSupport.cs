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

            // 优先读取 sharedProfile，避免 profile getter 创建的运行时 clone 在下一轮 fixture 删除源资产后变成 MissingReference。
            var sharedProp = s_volumeType.GetProperty("sharedProfile", BindingFlags.Instance | BindingFlags.Public);
            if (sharedProp != null)
            {
                var sharedProfile = sharedProp.GetValue(volumeComponent, null) as UnityEngine.Object;
                if (sharedProfile != null) return sharedProfile;
            }

            var profileProp = s_volumeType.GetProperty("profile", BindingFlags.Instance | BindingFlags.Public);
            return profileProp != null ? profileProp.GetValue(volumeComponent, null) as UnityEngine.Object : null;
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
                var isGlobalField = type.GetField("isGlobal", BindingFlags.Instance | BindingFlags.Public);
                var priorityField = type.GetField("priority", BindingFlags.Instance | BindingFlags.Public);
                var weightField = type.GetField("weight", BindingFlags.Instance | BindingFlags.Public);
                var profile = GetVolumeProfile(typedComponent);
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
                    IsGlobal = isGlobalField != null && Convert.ToBoolean(isGlobalField.GetValue(typedComponent), CultureInfo.InvariantCulture),
                    Priority = priorityField == null ? 0d : Convert.ToDouble(priorityField.GetValue(typedComponent), CultureInfo.InvariantCulture),
                    Weight = weightField == null ? 0d : Convert.ToDouble(weightField.GetValue(typedComponent), CultureInfo.InvariantCulture),
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
            if (targetType == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(long)) return long.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return bool.Parse(value);
            if (targetType.IsEnum) return Enum.Parse(targetType, value, true);
            if (targetType == typeof(string)) return value;
            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        public static void SetSerializedPropertyValue(SerializedProperty property, string value)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float:
                    property.floatValue = float.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Integer:
                    property.intValue = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Boolean:
                    property.boolValue = bool.Parse(value);
                    break;
                case SerializedPropertyType.Enum:
                    property.enumValueIndex = int.Parse(value, CultureInfo.InvariantCulture);
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

        public static void RequireTarget(string target, string operation)
        {
            if (string.IsNullOrEmpty(target)) throw new UnityCliBridge.BridgeCommandException("E_HDRP_VOLUME_TARGET_REQUIRED", operation + " requires a target.", "Pass the target Volume GameObject name, hierarchy path, or instance id.");
        }

        public static CreatedVolume Create(string name, bool? isGlobal, double? priority, double? weight, string defaultPrefix, string errorPrefix)
        {
            var volumeType = HdrpToolSupport.VolumeType;
            var profileType = HdrpToolSupport.VolumeProfileType;
            if (volumeType == null || profileType == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_SYSTEM_NOT_FOUND", "Volume system types not found.", "Ensure the render pipeline package is fully imported.");
            var gameObject = new GameObject(string.IsNullOrEmpty(name) ? defaultPrefix + "Volume" : name);
            var volume = gameObject.AddComponent(volumeType);
            var global = isGlobal ?? true;
            SetField(volumeType, volume, "isGlobal", global);
            if (priority.HasValue) SetField(volumeType, volume, "priority", (float)priority.Value);
            if (weight.HasValue) SetField(volumeType, volume, "weight", (float)weight.Value);
            var profile = ScriptableObject.CreateInstance(profileType);
            profile.name = gameObject.name + "Profile";
            AssignProfile(volumeType, volume, profile, errorPrefix);
            UnityCliSceneMutation.RegisterCreatedObject(gameObject, "UnityCli " + defaultPrefix + " Volume Create");
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            return new CreatedVolume { GameObject = gameObject, IsGlobal = global };
        }

        public static GameObject AddEffect(string target, string effectTypeName, string errorPrefix, Func<string, Type> findEffectType)
        {
            RequireTarget(target, "add-effect");
            if (string.IsNullOrEmpty(effectTypeName)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_EFFECT_TYPE_REQUIRED", "add-effect requires effectType.", "Pass a VolumeComponent-derived type name.");
            var gameObject = ResolveVolume(target, errorPrefix);
            var effectType = findEffectType(effectTypeName);
            if (effectType == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_EFFECT_TYPE_NOT_FOUND", "Volume effect type not found: " + effectTypeName, "Ensure the effect type is correct and the render pipeline package is fully imported.");
            var profile = HdrpToolSupport.GetVolumeProfile(gameObject.GetComponent(HdrpToolSupport.VolumeType));
            var addMethod = HdrpToolSupport.VolumeProfileType.GetMethod("Add", new[] { typeof(Type), typeof(bool) });
            if (profile == null || addMethod == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_PROFILE_NOT_FOUND", "The target Volume has no writable in-memory profile.", "Create a Volume with hdrp.volume.create or assign a profile before adding an effect.");
            addMethod.Invoke(profile, new object[] { effectType, true });
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            return gameObject;
        }

        public static GameObject Set(string target, string effectTypeName, string propertyPath, string value, string errorPrefix, Func<string, Type> findEffectType, Action<SerializedProperty, string> setValue)
        {
            RequireTarget(target, "set");
            if (string.IsNullOrEmpty(effectTypeName)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_EFFECT_TYPE_REQUIRED", "set requires effectType.", "Pass a VolumeComponent-derived type name.");
            if (string.IsNullOrEmpty(propertyPath) || string.IsNullOrEmpty(value)) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_PROPERTY_REQUIRED", "set requires property and value.", "Pass a SerializedProperty path in property and the new value as a string.");
            var gameObject = ResolveVolume(target, errorPrefix);
            var effectType = findEffectType(effectTypeName);
            if (effectType == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_EFFECT_TYPE_NOT_FOUND", "Volume effect type not found: " + effectTypeName, "Ensure the effect type is correct and the render pipeline package is fully imported.");
            var profile = HdrpToolSupport.GetVolumeProfile(gameObject.GetComponent(HdrpToolSupport.VolumeType)) as VolumeProfile;
            if (profile == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_PROFILE_NOT_FOUND", "The target Volume has no readable in-memory profile.", "Create a Volume and add the requested effect before setting it.");
            // Unity 2022 通过运行时 effect type 在 VolumeProfile.components 中定位实例。
            var effect = profile.components.FirstOrDefault(component => component != null && effectType.IsInstanceOfType(component));
            if (effect == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_EFFECT_NOT_FOUND", "The requested effect is not present: " + effectTypeName, "Add the effect with hdrp.volume.add-effect before setting it.");
            using (var serialized = new SerializedObject(effect))
            {
                var property = serialized.FindProperty(propertyPath);
                if (property == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_PROPERTY_NOT_FOUND", "Volume effect property not found: " + propertyPath, "Pass a valid SerializedProperty path for the effect.");
                setValue(property, value);
                serialized.ApplyModifiedProperties();
            }
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            return gameObject;
        }

        private static GameObject ResolveVolume(string target, string errorPrefix)
        {
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(target, "A Volume target is required.");
            if (HdrpToolSupport.VolumeType == null || gameObject.GetComponent(HdrpToolSupport.VolumeType) == null) throw new UnityCliBridge.BridgeCommandException(errorPrefix + "_VOLUME_NOT_FOUND", "Volume component not found on target: " + target, "Pass a target GameObject that has a Volume component.");
            return gameObject;
        }

        private static void SetField(Type type, Component component, string name, object value)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field != null) field.SetValue(component, value);
        }

        private static void AssignProfile(Type volumeType, Component volume, UnityEngine.Object profile, string errorPrefix)
        {
            var property = volumeType.GetProperty("profile", BindingFlags.Instance | BindingFlags.Public) ?? volumeType.GetProperty("sharedProfile", BindingFlags.Instance | BindingFlags.Public);
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
