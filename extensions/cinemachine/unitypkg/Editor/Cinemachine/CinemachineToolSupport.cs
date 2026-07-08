#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.Cinemachine
{
    /// <summary>
    /// Cinemachine 工具共享支持层：包探测、目标解析、反射访问、DTO 构造与常见错误。
    /// 不参与工具发现，只服务于各个自包含 IUnityCliTool 实现。
    /// </summary>
    internal static class CinemachineToolSupport
    {
        private static readonly Dictionary<string, string[]> PresetMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "follow",        new[] { "CinemachineFollow",            "CinemachineRotationComposer" } },
            { "third_person",  new[] { "CinemachineThirdPersonFollow", "CinemachineRotationComposer" } },
            { "freelook",      new[] { "CinemachineOrbitalFollow",     "CinemachineRotationComposer" } },
            { "dolly",         new[] { "CinemachineSplineDolly",       "CinemachineRotationComposer" } },
            { "static",        new[] { null,                            "CinemachineHardLookAt" } },
            { "top_down",      new[] { "CinemachineFollow",            null } },
            { "side_scroller", new[] { "CinemachinePositionComposer",  null } }
        };

        private static Type cameraType;
        private static Type brainType;
        private static Type extensionBaseType;
        private static Type coreType;
        private static bool? hasCinemachine;

        public static IReadOnlyDictionary<string, string[]> Presets => PresetMap;
        public static Type CameraType => EnsureDetected().CameraType;
        public static Type BrainType => EnsureDetected().BrainType;
        public static Type ExtensionBaseType => EnsureDetected().ExtensionBaseType;
        public static Type CoreType => EnsureDetected().CoreType;

        public static void EnsureEditorReady(string toolName)
        {
            GameObjectToolSupport.EnsureEditorNotBusy(toolName);
        }

        /// <summary>
        /// 运行时探测 Cinemachine 类型，避免 core 对可选包建立编译期依赖。
        /// </summary>
        public static DetectionState EnsureDetected()
        {
            if (hasCinemachine.HasValue)
            {
                return BuildDetectionState();
            }

            cameraType = GameObjectToolSupport.FindType("Unity.Cinemachine.CinemachineCamera")
                ?? GameObjectToolSupport.FindType("Cinemachine.CinemachineVirtualCamera");
            brainType = GameObjectToolSupport.FindType("Unity.Cinemachine.CinemachineBrain")
                ?? GameObjectToolSupport.FindType("Cinemachine.CinemachineBrain");
            extensionBaseType = GameObjectToolSupport.FindType("Unity.Cinemachine.CinemachineExtension")
                ?? GameObjectToolSupport.FindType("Cinemachine.CinemachineExtension");
            coreType = GameObjectToolSupport.FindType("Unity.Cinemachine.CinemachineCore")
                ?? GameObjectToolSupport.FindType("Cinemachine.CinemachineCore");
            hasCinemachine = cameraType != null && brainType != null;
            return BuildDetectionState();
        }

        public static void EnsureCinemachineAvailable()
        {
            var detection = EnsureDetected();
            if (detection.Installed)
            {
                return;
            }

            var details = new UnityCliJsonWriter().BeginObject()
                .Prop("packageName", "com.unity.cinemachine")
                .Prop("available", false)
                .EndObject().ToString();
            throw new UnityCliBridge.BridgeCommandException(
                "E_CINEMACHINE_PACKAGE_NOT_FOUND",
                "Cinemachine package (com.unity.cinemachine) is not installed.",
                details,
                new[]
                {
                    "Install com.unity.cinemachine via Unity Package Manager or Packages/manifest.json.",
                    "Run unity_cli extensions install cinemachine --project <path> after the package is available."
                });
        }

        public static CinemachinePackageStatus BuildPackageStatus()
        {
            var detection = EnsureDetected();
            return new CinemachinePackageStatus
            {
                Installed = detection.Installed,
                CmCameraTypeAvailable = detection.CameraType != null,
                CmBrainTypeAvailable = detection.BrainType != null,
                CmExtensionTypeAvailable = detection.ExtensionBaseType != null,
                ForceOverrideAvailable = HasOverrideApi()
            };
        }

        public static GameObject ResolveRequiredTarget(string target, string message)
        {
            return GameObjectToolSupport.ResolveUniqueTarget(target, message);
        }

        /// <summary>
        /// create 工具对 parent/follow/lookAt 保持旧语义：找不到时忽略，但名称歧义仍报错。
        /// </summary>
        public static GameObject ResolveOptionalTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            var matches = new List<GameObject>();
            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
            {
                if (gameObject.name == target
                    || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target
                    || GameObjectToolSupport.HierarchyPath(gameObject) == target)
                {
                    matches.Add(gameObject);
                }
            }

            if (matches.Count > 1)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_TARGET_AMBIGUOUS",
                    "Multiple GameObjects matched: " + target,
                    "Pass a hierarchy path or instance id.");
            }

            return matches.Count == 0 ? null : matches[0];
        }

        public static Component RequireCameraComponent(GameObject target)
        {
            var component = target.GetComponent(CameraType);
            if (component != null)
            {
                return component;
            }

            throw new UnityCliBridge.BridgeCommandException(
                "E_CINEMACHINE_COMPONENT_NOT_FOUND",
                "GameObject does not have a CinemachineCamera component: " + GameObjectToolSupport.HierarchyPath(target),
                "Pass a target with a CinemachineCamera component or create one with cinemachine.create.");
        }

        public static Component RequireBrainComponent(GameObject target)
        {
            var component = target.GetComponent(BrainType);
            if (component != null)
            {
                return component;
            }

            throw new UnityCliBridge.BridgeCommandException(
                "E_CINEMACHINE_BRAIN_NOT_FOUND",
                "GameObject does not have CinemachineBrain: " + GameObjectToolSupport.HierarchyPath(target),
                "Use cinemachine.brain with action=ensure to add one.");
        }

        public static GameObject ResolveBrainOwner(string target)
        {
            if (!string.IsNullOrWhiteSpace(target))
            {
                return ResolveRequiredTarget(target, "Pass a GameObject name, hierarchy path or instance id.");
            }

            var owner = FindBrainOwner();
            if (owner != null)
            {
                return owner;
            }

            throw new UnityCliBridge.BridgeCommandException(
                "E_CINEMACHINE_BRAIN_NOT_FOUND",
                "No CinemachineBrain found in the scene.",
                "Use cinemachine.brain with action=ensure to add one.");
        }

        public static GameObject FindOrCreateBrainOwnerCandidate()
        {
            var existing = FindBrainOwner();
            if (existing != null)
            {
                return existing;
            }

            var mainCamera = Camera.main;
            return mainCamera != null ? mainCamera.gameObject : null;
        }

        public static GameObject FindBrainOwner()
        {
            EnsureDetected();
            if (brainType == null)
            {
                return null;
            }

            foreach (var item in Resources.FindObjectsOfTypeAll(brainType))
            {
                var component = item as Component;
                if (component == null || component.gameObject == null || EditorUtility.IsPersistent(component.gameObject))
                {
                    continue;
                }
                return component.gameObject;
            }

            return null;
        }

        public static void SetFollowTarget(Component camera, Transform target)
        {
            var property = camera.GetType().GetProperty("Follow", BindingFlags.Instance | BindingFlags.Public)
                ?? camera.GetType().GetProperty("m_Follow", BindingFlags.Instance | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                property.SetValue(camera, target, null);
            }
        }

        public static void SetLookAtTarget(Component camera, Transform target)
        {
            var property = camera.GetType().GetProperty("LookAt", BindingFlags.Instance | BindingFlags.Public)
                ?? camera.GetType().GetProperty("m_LookAt", BindingFlags.Instance | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                property.SetValue(camera, target, null);
            }
        }

        public static void SetPriority(Component camera, int priority)
        {
            using (var serializedObject = new SerializedObject(camera))
            {
                var priorityProperty = serializedObject.FindProperty("Priority") ?? serializedObject.FindProperty("m_Priority");
                if (priorityProperty != null)
                {
                    var valueProperty = priorityProperty.FindPropertyRelative("Value") ?? priorityProperty.FindPropertyRelative("m_Value");
                    if (valueProperty != null)
                    {
                        valueProperty.intValue = priority;
                    }
                }
                else
                {
                    var directProperty = camera.GetType().GetProperty("Priority", BindingFlags.Instance | BindingFlags.Public);
                    if (directProperty != null && directProperty.CanWrite)
                    {
                        directProperty.SetValue(camera, priority, null);
                    }
                }
                serializedObject.ApplyModifiedProperties();
            }
        }

        public static void SetPriorityEnabled(Component camera, bool enabled)
        {
            using (var serializedObject = new SerializedObject(camera))
            {
                var priorityProperty = serializedObject.FindProperty("Priority") ?? serializedObject.FindProperty("m_Priority");
                if (priorityProperty != null)
                {
                    var enabledProperty = priorityProperty.FindPropertyRelative("Enabled") ?? priorityProperty.FindPropertyRelative("m_Enabled");
                    if (enabledProperty != null)
                    {
                        enabledProperty.boolValue = enabled;
                    }
                }
                serializedObject.ApplyModifiedProperties();
            }
        }

        public static void SwapPipelineComponent(GameObject gameObject, Component camera, string stage, Type newType)
        {
            var existing = GetPipelineComponent(camera, stage);
            if (existing != null && existing.GetType() == newType)
            {
                return;
            }
            if (existing != null)
            {
                Undo.DestroyObjectImmediate(existing);
            }
            Undo.AddComponent(gameObject, newType);
        }

        public static Component GetPipelineComponent(Component camera, string stage)
        {
            EnsureDetected();
            if (coreType == null || cameraType == null)
            {
                return null;
            }

            var stageEnumType = coreType.GetNestedType("Stage");
            if (stageEnumType == null)
            {
                return null;
            }

            var stageValue = Enum.Parse(stageEnumType, string.Equals(stage, "Body", StringComparison.Ordinal) ? "Body" : "Aim");
            var method = FindPublicInstanceMethod(cameraType, "GetCinemachineComponent", stageEnumType);
            return method == null ? null : method.Invoke(camera, new[] { stageValue }) as Component;
        }

        public static void ConfigurePipelineComponent(Component component, string propertyName, string value)
        {
            if (component == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return;
            }

            var type = component.GetType();
            var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite)
            {
                property.SetValue(component, ConvertStringValue(value, property.PropertyType), null);
                return;
            }

            var field = type.GetField(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (field != null)
            {
                field.SetValue(component, ConvertStringValue(value, field.FieldType));
                return;
            }

            using (var serializedObject = new SerializedObject(component))
            {
                var serializedProperty = serializedObject.FindProperty(propertyName)
                    ?? serializedObject.FindProperty("m_" + char.ToUpperInvariant(propertyName[0]) + propertyName.Substring(1));
                if (serializedProperty == null)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_CINEMACHINE_COMPONENT_PROPERTY_NOT_FOUND",
                        "Writable Cinemachine component property not found: " + propertyName,
                        "Check the component type and SerializedProperty path.");
                }
                AssignSerializedPropertyValue(serializedProperty, value);
                serializedObject.ApplyModifiedProperties();
            }
        }

        public static Type ResolveComponentType(string typeName)
        {
            EnsureDetected();
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            if (cameraType != null)
            {
                var assemblyType = cameraType.Assembly.GetType("Unity.Cinemachine." + typeName)
                    ?? cameraType.Assembly.GetType("Cinemachine." + typeName);
                if (assemblyType != null)
                {
                    return assemblyType;
                }
            }

            return GameObjectToolSupport.FindType("Unity.Cinemachine." + typeName)
                ?? GameObjectToolSupport.FindType("Cinemachine." + typeName)
                ?? GameObjectToolSupport.FindType(typeName);
        }

        public static Type RequireComponentType(string typeName, string typeLabel)
        {
            var type = ResolveComponentType(typeName);
            if (type != null)
            {
                return type;
            }

            throw new UnityCliBridge.BridgeCommandException(
                "E_CINEMACHINE_COMPONENT_TYPE_NOT_FOUND",
                typeLabel + " component type not found in loaded assemblies: " + typeName,
                "Check the Cinemachine package is fully imported and the type name is correct.");
        }

        public static UnityEngine.Object FindNoiseProfile(string profileName)
        {
            var guids = AssetDatabase.FindAssets(profileName + " t:NoiseSettings");
            if (guids.Length == 0)
            {
                guids = AssetDatabase.FindAssets(profileName + " t:ScriptableObject");
            }

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (asset != null)
                {
                    return asset;
                }
            }

            return null;
        }

        public static MethodInfo FindSetCameraOverrideMethod(Type ownerType)
        {
            return FindPublicInstanceMethod(ownerType, "SetCameraOverride", typeof(int), null, null, typeof(float), typeof(float));
        }

        public static MethodInfo FindReleaseCameraOverrideMethod(Type ownerType)
        {
            return FindPublicInstanceMethod(ownerType, "ReleaseCameraOverride", typeof(int));
        }

        public static bool HasOverrideApi()
        {
            EnsureDetected();
            return brainType != null
                && FindSetCameraOverrideMethod(brainType) != null
                && FindReleaseCameraOverrideMethod(brainType) != null;
        }

        /// <summary>
        /// force-camera 默认使用 Brain 当前输出虚拟相机作为 source；CM2/CM3 名称差异通过反射兜底。
        /// </summary>
        public static Component GetActiveVirtualCameraComponent(Component brain)
        {
            if (brain == null)
            {
                return null;
            }

            var activeProperty = brain.GetType().GetProperty("ActiveVirtualCamera", BindingFlags.Instance | BindingFlags.Public);
            var activeCamera = activeProperty != null ? activeProperty.GetValue(brain, null) : null;
            if (activeCamera == null)
            {
                return null;
            }

            var component = activeCamera as Component;
            if (component != null)
            {
                return component;
            }

            var gameObjectProperty = activeCamera.GetType().GetProperty("VirtualCameraGameObject", BindingFlags.Instance | BindingFlags.Public);
            var gameObject = gameObjectProperty != null ? gameObjectProperty.GetValue(activeCamera, null) as GameObject : null;
            if (gameObject != null)
            {
                return gameObject.GetComponent(CameraType);
            }

            var nameProperty = activeCamera.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
            var name = nameProperty != null ? nameProperty.GetValue(activeCamera, null) as string : null;
            var fallback = ResolveOptionalTarget(name);
            return fallback != null ? fallback.GetComponent(CameraType) : null;
        }

        public static int? BlendStyleToEnumIndex(string styleName)
        {
            if (string.IsNullOrWhiteSpace(styleName))
            {
                return null;
            }

            var normalized = styleName.Replace("-", "_").ToLowerInvariant();
            var map = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "cut", 0 },
                { "ease_in_out", 1 },
                { "ease_in", 2 },
                { "ease_out", 3 },
                { "hard_in", 4 },
                { "hard_out", 5 },
                { "linear", 6 },
                { "soft_in", 7 },
                { "soft_out", 8 }
            };
            int index;
            return map.TryGetValue(normalized, out index) ? (int?)index : null;
        }

        public static void SetLensFloat(SerializedProperty parent, string propertyName, float value)
        {
            var property = parent.FindPropertyRelative(propertyName) ?? parent.FindPropertyRelative("m_" + propertyName);
            if (property != null)
            {
                property.floatValue = value;
            }
        }

        public static void MarkDirty(GameObject gameObject)
        {
            if (gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
        }

        public static CinemachineObjectInfo BuildObjectInfo(GameObject gameObject, string preset = null)
        {
            var camera = CameraType != null ? gameObject.GetComponent(CameraType) : null;
            var extensions = new List<string>();
            if (camera != null && ExtensionBaseType != null)
            {
                foreach (var extension in gameObject.GetComponents(ExtensionBaseType))
                {
                    extensions.Add(extension.GetType().Name);
                }
            }

            return new CinemachineObjectInfo
            {
                Id = gameObject.GetInstanceID(),
                Name = gameObject.name,
                Path = GameObjectToolSupport.HierarchyPath(gameObject),
                Preset = preset,
                Position = new Vector3Data(gameObject.transform.position),
                HasCinemachineCamera = camera != null,
                Extensions = extensions
            };
        }

        public static CinemachineBrainInfo BuildBrainInfo(Component brain)
        {
            var type = brain.GetType();
            var activeProperty = type.GetProperty("ActiveVirtualCamera", BindingFlags.Instance | BindingFlags.Public);
            var blendingProperty = type.GetProperty("IsBlending", BindingFlags.Instance | BindingFlags.Public);
            var activeCamera = activeProperty != null ? activeProperty.GetValue(brain, null) : null;
            var activeName = activeCamera != null ? (activeCamera is Component component ? component.gameObject.name : activeCamera.ToString()) : null;
            var isBlending = blendingProperty != null && Convert.ToBoolean(blendingProperty.GetValue(brain, null), CultureInfo.InvariantCulture);

            return new CinemachineBrainInfo
            {
                Enabled = GameObjectToolSupport.ComponentEnabled(brain),
                HasBrain = true,
                ActiveVirtualCamera = activeName,
                IsBlending = isBlending,
                ComponentType = type.Name
            };
        }

        public static CinemachinePlannedEffect CreateEffect(string type)
        {
            return new CinemachinePlannedEffect { Type = type };
        }

        private static DetectionState BuildDetectionState()
        {
            return new DetectionState
            {
                Installed = hasCinemachine.GetValueOrDefault(false),
                CameraType = cameraType,
                BrainType = brainType,
                ExtensionBaseType = extensionBaseType,
                CoreType = coreType
            };
        }

        private static MethodInfo FindPublicInstanceMethod(Type ownerType, string methodName, params Type[] parameterTypes)
        {
            if (ownerType == null || string.IsNullOrWhiteSpace(methodName))
            {
                return null;
            }

            foreach (var method in ownerType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) || method.IsGenericMethodDefinition)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != parameterTypes.Length)
                {
                    continue;
                }

                var matched = true;
                for (var index = 0; index < parameters.Length; index++)
                {
                    var expected = parameterTypes[index];
                    if (expected != null && parameters[index].ParameterType != expected)
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    return method;
                }
            }

            return null;
        }

        private static object ConvertStringValue(string rawValue, Type targetType)
        {
            if (Nullable.GetUnderlyingType(targetType) != null)
            {
                targetType = Nullable.GetUnderlyingType(targetType);
            }

            if (targetType == typeof(string)) return rawValue;
            if (targetType == typeof(bool)) return bool.Parse(rawValue);
            if (targetType == typeof(int)) return int.Parse(rawValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(long)) return long.Parse(rawValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(float)) return float.Parse(rawValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(double)) return double.Parse(rawValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(Vector2)) return GameObjectToolSupport.ParseVector2(rawValue);
            if (targetType == typeof(Vector3)) return GameObjectToolSupport.ParseVector3(rawValue);
            if (targetType.IsEnum) return Enum.Parse(targetType, rawValue, true);
            throw new UnityCliBridge.BridgeCommandException(
                "E_CINEMACHINE_COMPONENT_VALUE_UNSUPPORTED",
                "Unsupported Cinemachine component value type: " + targetType.Name,
                "Use string, bool, int, float, double, enum, Vector2, or Vector3 values.");
        }

        private static void AssignSerializedPropertyValue(SerializedProperty property, string rawValue)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    property.boolValue = bool.Parse(rawValue);
                    break;
                case SerializedPropertyType.Integer:
                    property.intValue = int.Parse(rawValue, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Float:
                    property.floatValue = float.Parse(rawValue, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.String:
                    property.stringValue = rawValue;
                    break;
                case SerializedPropertyType.Enum:
                    property.enumValueIndex = int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var enumIndex)
                        ? enumIndex
                        : Array.IndexOf(property.enumDisplayNames, rawValue);
                    break;
                case SerializedPropertyType.Vector2:
                    property.vector2Value = GameObjectToolSupport.ParseVector2(rawValue);
                    break;
                case SerializedPropertyType.Vector3:
                    property.vector3Value = GameObjectToolSupport.ParseVector3(rawValue);
                    break;
                default:
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_CINEMACHINE_COMPONENT_PROPERTY_TYPE_UNSUPPORTED",
                        "Unsupported SerializedProperty type: " + property.propertyType,
                        "Use simple scalar or vector properties for Cinemachine pipeline configuration.");
            }
        }

        internal sealed class DetectionState
        {
            public bool Installed { get; set; }
            public Type CameraType { get; set; }
            public Type BrainType { get; set; }
            public Type ExtensionBaseType { get; set; }
            public Type CoreType { get; set; }
        }
    }

    public sealed class CinemachinePackageStatus
    {
        public bool Installed { get; set; }
        public bool CmCameraTypeAvailable { get; set; }
        public bool CmBrainTypeAvailable { get; set; }
        public bool CmExtensionTypeAvailable { get; set; }
        public bool ForceOverrideAvailable { get; set; }
    }

    public sealed class CinemachineObjectInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Preset { get; set; }
        public Vector3Data Position { get; set; }
        public bool HasCinemachineCamera { get; set; }
        public List<string> Extensions { get; set; }
    }

    public sealed class CinemachineBrainInfo
    {
        public bool Enabled { get; set; }
        public bool HasBrain { get; set; }
        public string ActiveVirtualCamera { get; set; }
        public bool IsBlending { get; set; }
        public string ComponentType { get; set; }
    }

    public sealed class CinemachinePlannedEffect
    {
        public string Type { get; set; }
        public string Target { get; set; }
        public string Field { get; set; }
        public string Value { get; set; }
        public string Preset { get; set; }
        public string BodyType { get; set; }
        public string AimType { get; set; }
        public string ExtensionType { get; set; }
        public string Brain { get; set; }
        public long? OverrideId { get; set; }
        public string SideEffects { get; set; }
    }
}
#endif
