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

        // 对外接受 CM3 组件名称；CM2 项目自动落到语义等价的 pipeline component。
        private static readonly Dictionary<string, string[]> ComponentTypeAliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "CinemachineFollow", new[] { "CinemachineTransposer" } },
            { "CinemachineRotationComposer", new[] { "CinemachineComposer" } },
            { "CinemachineThirdPersonFollow", new[] { "Cinemachine3rdPersonFollow" } },
            { "CinemachineOrbitalFollow", new[] { "CinemachineOrbitalTransposer" } },
            { "CinemachineSplineDolly", new[] { "CinemachineTrackedDolly" } },
            { "CinemachinePositionComposer", new[] { "CinemachineFramingTransposer" } }
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

            var details = UnityCliSerializer.Serialize(new Dictionary<string, object>
            {
                { "packageName", "com.unity.cinemachine" },
                { "available", false }
            });
            throw new UnityCliBridge.BridgeCommandException(
                "E_CINEMACHINE_PACKAGE_NOT_FOUND",
                "Cinemachine package (com.unity.cinemachine) is not installed.",
                details,
                new[]
                {
                    "Install com.unity.cinemachine via Unity Package Manager or Packages/manifest.json.",
                    "Run unitycli extensions install cinemachine --project <path> after the package is available."
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
            };
        }

        public static GameObject ResolveRequiredTarget(string target, string message)
        {
            return GameObjectToolSupport.ResolveUniqueTarget(target, message);
        }

        /// <summary>解析可选请求字段；字段提供时必须唯一命中，不能静默丢弃请求。</summary>
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

            if (matches.Count == 0)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_TARGET_NOT_FOUND",
                    "GameObject not found: " + target,
                    "Pass an existing GameObject name, hierarchy path, or instance id.");
            }
            return matches[0];
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
                "Use cinemachine.brain.ensure to add one.");
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
                "Use cinemachine.brain.ensure to add one.");
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

        /// <summary>在创建相机前确认当前 Cinemachine 版本提供可写 priority contract。</summary>
        public static void RequirePriorityWritable(Type ownerType)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var direct = ownerType?.GetProperty("Priority", flags);
            if (direct != null && direct.CanWrite && direct.PropertyType == typeof(int)) return;
            var rootField = ownerType?.GetField("Priority", flags) ?? ownerType?.GetField("m_Priority", flags);
            var rootProperty = direct;
            var priorityType = rootField?.FieldType ?? rootProperty?.PropertyType;
            if (priorityType != null)
            {
                var valueField = priorityType.GetField("Value", flags) ?? priorityType.GetField("m_Value", flags);
                var valueProperty = priorityType.GetProperty("Value", flags) ?? priorityType.GetProperty("m_Value", flags);
                if ((valueField != null && !valueField.IsInitOnly && valueField.FieldType == typeof(int)) || (valueProperty != null && valueProperty.CanWrite && valueProperty.PropertyType == typeof(int))) return;
            }
            throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_PRIORITY_PROPERTY_NOT_FOUND", "Cinemachine priority is not writable for the installed package version.", "Use a supported Cinemachine package version.");
        }

        public static void SetPriority(Component camera, int priority)
        {
            using (var serializedObject = new SerializedObject(camera))
            {
                var priorityProperty = serializedObject.FindProperty("Priority") ?? serializedObject.FindProperty("m_Priority");
                var applied = false;
                if (priorityProperty != null)
                {
                    if (priorityProperty.propertyType == SerializedPropertyType.Integer)
                    {
                        priorityProperty.intValue = priority;
                        applied = true;
                    }
                    else
                    {
                        var valueProperty = priorityProperty.FindPropertyRelative("Value") ?? priorityProperty.FindPropertyRelative("m_Value");
                        if (valueProperty != null && valueProperty.propertyType == SerializedPropertyType.Integer)
                        {
                            valueProperty.intValue = priority;
                            applied = true;
                        }
                    }
                }
                if (!applied)
                {
                    var directProperty = camera.GetType().GetProperty("Priority", BindingFlags.Instance | BindingFlags.Public);
                    if (directProperty != null && directProperty.CanWrite && directProperty.PropertyType == typeof(int))
                    {
                        directProperty.SetValue(camera, priority, null);
                        applied = true;
                    }
                }
                if (!applied) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_PRIORITY_PROPERTY_NOT_FOUND", "Cinemachine priority is not writable.", "Use a supported Cinemachine package version.");
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

        public static void RequirePriorityEnabledWritable(Component camera)
        {
            using (var serializedObject = new SerializedObject(camera))
            {
                var priority = serializedObject.FindProperty("Priority") ?? serializedObject.FindProperty("m_Priority");
                var enabledProperty = priority?.FindPropertyRelative("Enabled") ?? priority?.FindPropertyRelative("m_Enabled");
                if (enabledProperty == null || enabledProperty.propertyType != SerializedPropertyType.Boolean)
                    throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_PRIORITY_PROPERTY_NOT_FOUND", "Cinemachine priority enabled flag is not writable for the installed package version.", "The enabled option requires Cinemachine 3 PrioritySettings support.");
            }
        }

        public static void SwapPipelineComponent(GameObject gameObject, Component camera, string stage, Type newType)
        {
            RequirePipelineComponentStage(newType, stage);
            var existing = GetPipelineComponent(camera, stage);
            if (existing != null && existing.GetType() == newType) return;
            RemovePipelineComponent(camera, existing);

            // Cinemachine 2.x 通过泛型 pipeline API 管理隐藏子对象；直接 AddComponent 会破坏其它 stage。
            var cameraMethods = camera.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var add = cameraMethods.FirstOrDefault(method => method.Name == "AddCinemachineComponent" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
            if (add != null) add.MakeGenericMethod(newType).Invoke(camera, null);
            else UnityCliSceneMutation.AddComponent(gameObject, newType);
        }

        // Prefer actual Cinemachine stage metadata. CM2 exposes no usable static metadata for all
        // components, so its known legal types are restricted by assembly and fully-qualified name.
        public static void RequirePipelineComponentStage(Type componentType, string stage)
        {
            if (componentType == null || !HasPipelineStage(componentType, stage))
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_COMPONENT_STAGE_MISMATCH", "Cinemachine component type does not belong to the requested " + stage + " stage: " + (componentType?.FullName ?? "<null>"), "Use a supported Body, Aim, or Noise pipeline component for the installed Cinemachine version.");
        }

        private static bool HasPipelineStage(Type componentType, string stage)
        {
            var stageProperty = componentType.GetProperty("Stage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (stageProperty != null && stageProperty.CanRead)
            {
                var declaredStage = stageProperty.GetValue(null, null);
                if (declaredStage != null) return string.Equals(declaredStage.ToString(), stage, StringComparison.Ordinal);
            }
            var assemblyName = componentType.Assembly.GetName().Name ?? string.Empty;
            if (assemblyName.IndexOf("Cinemachine", StringComparison.OrdinalIgnoreCase) < 0) return false;
            var legal = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "Body", new[] { "Unity.Cinemachine.CinemachineFollow", "Unity.Cinemachine.CinemachineThirdPersonFollow", "Unity.Cinemachine.CinemachineOrbitalFollow", "Unity.Cinemachine.CinemachineSplineDolly", "Unity.Cinemachine.CinemachinePositionComposer", "Cinemachine.CinemachineTransposer", "Cinemachine.Cinemachine3rdPersonFollow", "Cinemachine.CinemachineOrbitalTransposer", "Cinemachine.CinemachineTrackedDolly", "Cinemachine.CinemachineFramingTransposer", "Cinemachine.CinemachineHardLockToTarget", "Cinemachine.CinemachineHardLockToTargetWithWorldUp", "Cinemachine.CinemachineSameAsFollowTarget" } },
                { "Aim", new[] { "Unity.Cinemachine.CinemachineRotationComposer", "Unity.Cinemachine.CinemachineHardLookAt", "Cinemachine.CinemachineComposer", "Cinemachine.CinemachineHardLookAt", "Cinemachine.CinemachinePOV", "Cinemachine.CinemachineSameAsFollowTarget", "Cinemachine.CinemachineGroupComposer" } },
                { "Noise", new[] { "Unity.Cinemachine.CinemachineBasicMultiChannelPerlin", "Cinemachine.CinemachineBasicMultiChannelPerlin" } }
            };
            return legal.TryGetValue(stage, out var allowed) && allowed.Contains(componentType.FullName);
        }

        /// <summary>通过 Cinemachine pipeline API 删除 stage component，并为无该 API 的版本提供普通组件路径。</summary>
        public static void RemovePipelineComponent(Component camera, Component existing)
        {
            if (existing == null) return;
            var cameraMethods = camera.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var destroy = cameraMethods.FirstOrDefault(method => method.Name == "DestroyCinemachineComponent" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
            if (destroy != null) destroy.MakeGenericMethod(existing.GetType()).Invoke(camera, null);
            else UnityCliSceneMutation.DestroyObject(existing);
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

            if (!Enum.IsDefined(stageEnumType, stage))
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_STAGE_NOT_FOUND", "Cinemachine stage is unavailable: " + stage, "Use Body, Aim, or Noise when supported by the installed package version.");
            var stageValue = Enum.Parse(stageEnumType, stage);
            var method = FindPublicInstanceMethod(camera.GetType(), "GetCinemachineComponent", stageEnumType);
            return method == null ? null : method.Invoke(camera, new[] { stageValue }) as Component;
        }

        internal sealed class ComponentMemberPlan
        {
            internal string RequestedName { get; set; }
            internal PropertyInfo Property { get; set; }
            internal FieldInfo Field { get; set; }
            internal object ConvertedValue { get; set; }
        }

        /// <summary>在 effect 前解析最终组件类型、可写 member 与 typed value。</summary>
        public static ComponentMemberPlan PrepareComponentMember(Type componentType, string propertyName, string value)
        {
            if (componentType == null || string.IsNullOrWhiteSpace(propertyName) || value == null)
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_COMPONENT_PROPERTY_REQUIRED", "Component type, property and value are required.", "Pass property and value together.");
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var names = new[] { propertyName, "m_" + char.ToUpperInvariant(propertyName[0]) + propertyName.Substring(1) };
            foreach (var name in names)
            {
                var property = componentType.GetProperty(name, flags);
                if (property != null && property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                    return ConvertMemberPlan(propertyName, property, null, value, property.PropertyType);
                var field = componentType.GetField(name, flags);
                if (field != null && !field.IsInitOnly)
                    return ConvertMemberPlan(propertyName, null, field, value, field.FieldType);
            }
            throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_COMPONENT_PROPERTY_NOT_FOUND", "Writable Cinemachine component property not found: " + propertyName, "Pass a public or serialized field exposed by the installed Cinemachine component type.");
        }

        /// <summary>按版本别名依次解析第一个可写 member；所有转换仍在 effect 前完成。</summary>
        public static ComponentMemberPlan PrepareFirstComponentMember(Type componentType, string value, params string[] names)
        {
            foreach (var name in names ?? new string[0])
            {
                try { return PrepareComponentMember(componentType, name, value); }
                catch (UnityCliBridge.BridgeCommandException exception) when (exception.Code == "E_CINEMACHINE_COMPONENT_PROPERTY_NOT_FOUND") { }
            }
            throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_COMPONENT_PROPERTY_NOT_FOUND", "No compatible Cinemachine member exists for the installed package version.", "Use a supported Cinemachine package version.");
        }

        /// <summary>为 UnityEngine.Object 等非字符串字段准备 typed member plan。</summary>
        public static ComponentMemberPlan PrepareFirstComponentObjectMember(Type componentType, object value, params string[] names)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var name in names ?? new string[0])
            {
                var property = componentType.GetProperty(name, flags);
                if (property != null && property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0 && (value == null || property.PropertyType.IsInstanceOfType(value)))
                    return new ComponentMemberPlan { RequestedName = name, Property = property, ConvertedValue = value };
                var field = componentType.GetField(name, flags);
                if (field != null && !field.IsInitOnly && (value == null || field.FieldType.IsInstanceOfType(value)))
                    return new ComponentMemberPlan { RequestedName = name, Field = field, ConvertedValue = value };
            }
            throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_COMPONENT_PROPERTY_NOT_FOUND", "No compatible Cinemachine object member exists for the installed package version.", "Use a supported Cinemachine package version.");
        }

        /// <summary>为相机目标准备公开属性或序列化字段的可写 member plan。</summary>
        internal static ComponentMemberPlan PrepareComponentObjectMember(Type componentType, string memberName, object value, string errorCode)
        {
            try
            {
                return PrepareFirstComponentObjectMember(componentType, value, memberName, "m_" + memberName);
            }
            catch (UnityCliBridge.BridgeCommandException exception) when (exception.Code == "E_CINEMACHINE_COMPONENT_PROPERTY_NOT_FOUND")
            {
                throw new UnityCliBridge.BridgeCommandException(errorCode, "Cinemachine " + memberName + " cannot be written.", "Use a supported Cinemachine camera version.");
            }
        }

        /// <summary>执行已验证 member plan；此方法不再做业务输入解析。</summary>
        public static void ApplyComponentMember(Component component, ComponentMemberPlan plan)
        {
            if (component == null || plan == null)
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_COMPONENT_NOT_FOUND", "The prepared Cinemachine component is unavailable during apply.", "Inspect the target virtual camera before retrying.");
            if (plan.Property != null) plan.Property.SetValue(component, plan.ConvertedValue, null);
            else plan.Field.SetValue(component, plan.ConvertedValue);
        }

        /// <summary>从 fresh component 读取实际 member，逐值证明请求终态。</summary>
        public static void VerifyComponentMember(Component component, ComponentMemberPlan plan, string errorCode)
        {
            if (component == null || plan == null)
                throw new UnityCliBridge.BridgeCommandException(errorCode, "Cinemachine component readback is unavailable.", "Inspect the target virtual camera before retrying.");
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object actual;
            if (plan.Property != null)
            {
                var observed = component.GetType().GetProperty(plan.Property.Name, flags);
                actual = observed == null || !observed.CanRead ? null : observed.GetValue(component, null);
            }
            else
            {
                var observed = component.GetType().GetField(plan.Field.Name, flags);
                actual = observed == null ? null : observed.GetValue(component);
            }
            if (!MemberValuesEqual(actual, plan.ConvertedValue))
                throw new UnityCliBridge.BridgeCommandException(errorCode, "Cinemachine component member did not retain the requested value: " + plan.RequestedName, "Inspect the target component before retrying.");
        }

        private static ComponentMemberPlan ConvertMemberPlan(string requestedName, PropertyInfo property, FieldInfo field, string rawValue, Type valueType)
        {
            try
            {
                return new ComponentMemberPlan { RequestedName = requestedName, Property = property, Field = field, ConvertedValue = ConvertStringValue(rawValue, valueType) };
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_COMPONENT_VALUE_INVALID", "Invalid value for " + requestedName + ": " + exception.Message, "Pass a value compatible with " + valueType.Name + ".");
            }
        }

        private static bool MemberValuesEqual(object actual, object expected)
        {
            if (actual is float actualFloat && expected is float expectedFloat) return Mathf.Approximately(actualFloat, expectedFloat);
            if (actual is double actualDouble && expected is double expectedDouble) return Math.Abs(actualDouble - expectedDouble) <= 0.000001d;
            if (actual is Vector2 actualVector2 && expected is Vector2 expectedVector2) return (actualVector2 - expectedVector2).sqrMagnitude <= 0.0000001f;
            if (actual is Vector3 actualVector3 && expected is Vector3 expectedVector3) return (actualVector3 - expectedVector3).sqrMagnitude <= 0.0000001f;
            return Equals(actual, expected);
        }

        public static Type ResolveComponentType(string typeName)
        {
            EnsureDetected();
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            var direct = FindComponentType(typeName);
            if (direct != null)
            {
                return direct;
            }

            if (ComponentTypeAliases.TryGetValue(typeName, out var aliases))
            {
                foreach (var alias in aliases)
                {
                    var compatible = FindComponentType(alias);
                    if (compatible != null)
                    {
                        return compatible;
                    }
                }
            }
            return null;
        }

        /// <summary>在当前 Cinemachine assembly 与已加载程序集内解析一个具体组件类型。</summary>
        private static Type FindComponentType(string typeName)
        {
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

        public static void RequireLensFloat(SerializedProperty parent, string propertyName)
        {
            var property = parent.FindPropertyRelative(propertyName) ?? parent.FindPropertyRelative("m_" + propertyName);
            if (property == null || property.propertyType != SerializedPropertyType.Float)
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_LENS_PROPERTY_NOT_FOUND", "Writable lens property not found: " + propertyName, "Use a supported Cinemachine camera version.");
        }

        public static void SetLensFloat(SerializedProperty parent, string propertyName, float value)
        {
            RequireLensFloat(parent, propertyName);
            (parent.FindPropertyRelative(propertyName) ?? parent.FindPropertyRelative("m_" + propertyName)).floatValue = value;
        }

        public static void RequireFinite(double value, string field)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_VALUE_INVALID", "Invalid " + field + " value.", "Pass a finite numeric value.");
            if (value < -float.MaxValue || value > float.MaxValue) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_VALUE_INVALID", "Invalid " + field + " value.", "Pass a finite value representable as a single-precision float.");
        }

        public static void VerifyFloat(SerializedObject serializedObject, string pathA, string pathB, float expected, string errorCode)
        {
            serializedObject.Update();
            var property = serializedObject.FindProperty(pathA) ?? serializedObject.FindProperty(pathB);
            if (property == null || property.propertyType != SerializedPropertyType.Float || !Mathf.Approximately(property.floatValue, expected))
                throw new UnityCliBridge.BridgeCommandException(errorCode, "Cinemachine value did not retain the requested value.", "Check the target component and retry.");
        }

        public static void VerifyLensFloat(SerializedObject serializedObject, string propertyName, float expected)
        {
            serializedObject.Update();
            var lens = serializedObject.FindProperty("Lens") ?? serializedObject.FindProperty("m_Lens");
            var property = lens == null ? null : lens.FindPropertyRelative(propertyName) ?? lens.FindPropertyRelative("m_" + propertyName);
            if (property == null || property.propertyType != SerializedPropertyType.Float || !Mathf.Approximately(property.floatValue, expected))
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_LENS_VERIFY_FAILED", "Cinemachine lens property did not retain the requested value: " + propertyName, "Check the camera and retry.");
        }

        public static void MarkDirty(GameObject gameObject)
        {
            if (gameObject.scene.IsValid())
            {
                UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
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
            if (targetType == typeof(float))
            {
                var parsed = double.Parse(rawValue, CultureInfo.InvariantCulture);
                RequireFinite(parsed, "component");
                return (float)parsed;
            }
            if (targetType == typeof(double))
            {
                var parsed = double.Parse(rawValue, CultureInfo.InvariantCulture);
                if (double.IsNaN(parsed) || double.IsInfinity(parsed))
                    throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_COMPONENT_VALUE_INVALID", "Cinemachine component values must be finite.", "Pass a finite numeric value.");
                return parsed;
            }
            if (targetType == typeof(Vector2)) return GameObjectToolSupport.ParseVector2(rawValue);
            if (targetType == typeof(Vector3)) return GameObjectToolSupport.ParseVector3(rawValue);
            if (targetType.IsEnum)
            {
                var parsed = Enum.Parse(targetType, rawValue, true);
                if (!Enum.IsDefined(targetType, parsed))
                    throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_COMPONENT_VALUE_INVALID", "Cinemachine enum value is not defined: " + rawValue, "Pass a defined enum member name or value.");
                return parsed;
            }
            throw new UnityCliBridge.BridgeCommandException(
                "E_CINEMACHINE_COMPONENT_VALUE_UNSUPPORTED",
                "Unsupported Cinemachine component value type: " + targetType.Name,
                "Use string, bool, int, float, double, enum, Vector2, or Vector3 values.");
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
