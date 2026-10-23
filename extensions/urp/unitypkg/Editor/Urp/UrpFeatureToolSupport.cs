#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public sealed class UrpFeatureListResult : UrpFeatureResult
    {
        public bool Available { get; set; }
        public string Renderer { get; set; }
        public string Diagnostics { get; set; }
        public string[] NextActions { get; set; }
    }

    public class UrpFeatureResult
    {
        public long Count { get; set; }
        public long? FeatureIndex { get; set; }
        public string FeatureName { get; set; }
        public bool DryRun { get; set; }
        public List<UrpFeatureInfo> Features { get; set; }
        public List<UrpPlannedEffect> PlannedEffects { get; set; }
    }

    /// <summary>URP Renderer Feature 单动作 Tool 的共享资产定位与写入实现。</summary>
    internal static class UrpFeatureToolSupport
    {
        /// <summary>读取 active URP renderer feature，并把旧读取入口的可用性诊断并入 canonical Tool。</summary>
        internal static UrpFeatureListResult List(string logPath)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.feature-list");
            try
            {
                var pipeline = UrpToolSupport.GetActivePipelineAsset();
                var features = UrpToolSupport.ListRendererFeatures();
                return new UrpFeatureListResult
                {
                    Available = true,
                    Renderer = pipeline.name,
                    Count = features.Count,
                    Features = features
                };
            }
            catch (UnityCliBridge.BridgeCommandException exception) when (exception.Code == "E_URP_PACKAGE_NOT_FOUND" || exception.Code == "E_URP_PIPELINE_NOT_ACTIVE")
            {
                return new UrpFeatureListResult
                {
                    Available = false,
                    Features = new List<UrpFeatureInfo>(),
                    Diagnostics = exception.Message,
                    NextActions = new List<string>(exception.NextActions).ToArray()
                };
            }
        }

        // 提交前冻结新增所需的资产、类型和可写序列化字段，避免子资产已创建后才暴露版本不兼容。
        internal static AddMutationPlan ValidateAdd(string featureTypeName, int rendererIndex)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.feature-add");
            UrpToolSupport.EnsureUrpAvailable();
            var featureType = UrpToolSupport.FindFeatureType(featureTypeName);
            if (featureType == null || featureType.IsAbstract || UrpToolSupport.RendererFeatureType == null || !UrpToolSupport.RendererFeatureType.IsAssignableFrom(featureType)) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_TYPE_NOT_FOUND", "Concrete ScriptableRendererFeature type not found: " + featureTypeName, "Pass a concrete ScriptableRendererFeature-derived type such as ScreenSpaceAmbientOcclusion.");
            var rendererData = UrpToolSupport.GetRendererData(UrpToolSupport.GetActivePipelineAsset(), rendererIndex);
            if (rendererData == null) throw RendererNotFound(rendererIndex);
            RequireStableRendererAsset(rendererData);
            using (var serialized = new SerializedObject(rendererData))
            {
                ValidateFeatureCollectionForAdd(serialized);
            }
            return new AddMutationPlan { RendererData = rendererData, FeatureType = featureType, FeatureTypeName = featureTypeName, RendererIndex = rendererIndex };
        }

        internal static void ValidateFeature(int featureIndex, int rendererIndex, string toolName)
        {
            RequireFeature(featureIndex, rendererIndex, toolName);
        }

        // 提交前选择唯一的 active 写入机制；提交后不得重新解析反射或 SerializedProperty。
        internal static ToggleMutationPlan ValidateToggle(int featureIndex, int rendererIndex)
        {
            var target = RequireFeature(featureIndex, rendererIndex, "urp.feature-toggle");
            RequireStableRendererAsset(target.RendererData);
            var setActive = target.Feature.GetType().GetMethod("SetActive", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool) }, null);
            var activePropertyPath = (string)null;
            if (setActive == null)
            {
                using (var serialized = new SerializedObject(target.Feature))
                {
                    var active = serialized.FindProperty("m_Active") ?? serialized.FindProperty("active");
                    if (active == null || active.propertyType != SerializedPropertyType.Boolean || !active.editable)
                        throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_ACTIVE_UNSUPPORTED", "Renderer feature active state is unavailable as an editable boolean value.", "Check the installed URP package version.");
                    activePropertyPath = active.propertyPath;
                }
            }
            string guid;
            long localId;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target.Feature, out guid, out localId) || string.IsNullOrEmpty(guid))
                throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_VERIFY_FAILED", "Renderer feature has no stable asset identity.", "Use an imported renderer data asset.");
            return new ToggleMutationPlan { RendererData = target.RendererData, Feature = target.Feature, FeatureIndex = featureIndex, RendererIndex = rendererIndex, SetActive = setActive, ActivePropertyPath = activePropertyPath, FeatureGuid = guid, FeatureLocalId = localId, FeatureType = target.Feature.GetType() };
        }

        internal static void ValidateConfigure(int featureIndex, int rendererIndex, string propertyName, string value)
        {
            var target = RequireFeature(featureIndex, rendererIndex, "urp.feature-configure");
            using (var serialized = new SerializedObject(target.Feature))
            {
                var property = serialized.FindProperty(propertyName) ?? serialized.FindProperty("m_" + char.ToUpper(propertyName[0]) + propertyName.Substring(1));
                if (property == null) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_PROPERTY_NOT_FOUND", "Renderer feature property not found: " + propertyName, "Pass a SerializedProperty path present on the feature object.");
                UrpToolSupport.ValidateSerializedPropertyValue(property, value);
            }
        }

        internal static UrpFeatureResult Add(AddMutationPlan plan, bool dryRun, string logPath)
        {
            var effect = new UrpPlannedEffect { Type = "urp.feature-add", FeatureType = plan.FeatureTypeName, RendererIndex = plan.RendererIndex };
            if (dryRun) return new UrpFeatureResult { DryRun = true, PlannedEffects = new List<UrpPlannedEffect> { effect }};

            var feature = ScriptableObject.CreateInstance(plan.FeatureType);
            feature.name = plan.FeatureTypeName;
            AssetDatabase.AddObjectToAsset(feature, plan.RendererData);
            string featureGuid;
            long featureLocalId;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out featureGuid, out featureLocalId) || string.IsNullOrEmpty(featureGuid))
                throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_VERIFY_FAILED", "Added renderer feature has no stable asset identity.", "Check the renderer feature and retry.");
            using (var serialized = new SerializedObject(plan.RendererData))
            {
                var features = serialized.FindProperty("m_RendererFeatures");
                features.arraySize += 1;
                features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
                var map = serialized.FindProperty("m_RendererFeatureMap");
                map.arraySize += 1;
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = featureLocalId;
                serialized.ApplyModifiedProperties();
            }
            var rendererData = PersistRendererData(plan.RendererData);
            var index = FindFeatureIndex(rendererData, featureGuid, featureLocalId);
            if (index < 0) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_VERIFY_FAILED", "Added renderer feature could not be located in the re-read feature collection.", "Check the renderer feature and retry.");
            var observed = ReadFeature(rendererData, index);
            if (!IsExactFeature(observed, featureGuid, featureLocalId, plan.FeatureType)) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_VERIFY_FAILED", "Added renderer feature could not be read exactly from the reloaded renderer data.", "Check the renderer feature and retry.");
            return new UrpFeatureResult { FeatureName = observed.name, FeatureIndex = index, PlannedEffects = new List<UrpPlannedEffect> { effect }};
        }

        internal static UrpFeatureResult Toggle(ToggleMutationPlan plan, bool enabled, bool dryRun, string logPath)
        {
            var effect = new UrpPlannedEffect { Type = "urp.feature-toggle", Index = plan.FeatureIndex, RendererIndex = plan.RendererIndex, Enabled = enabled };
            if (dryRun) return new UrpFeatureResult { FeatureIndex = plan.FeatureIndex, DryRun = true, PlannedEffects = new List<UrpPlannedEffect> { effect }};
            if (plan.SetActive != null) plan.SetActive.Invoke(plan.Feature, new object[] { enabled });
            else
            {
                using (var serialized = new SerializedObject(plan.Feature))
                {
                    var active = serialized.FindProperty(plan.ActivePropertyPath);
                    active.boolValue = enabled;
                    serialized.ApplyModifiedProperties();
                }
            }
            var reloadedRendererData = PersistRendererData(plan.RendererData);
            var observed = ReadFeature(reloadedRendererData, plan.FeatureIndex);
            if (!IsExactFeature(observed, plan.FeatureGuid, plan.FeatureLocalId, plan.FeatureType) || UrpToolSupport.IsFeatureEnabled(observed) != enabled)
                throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_VERIFY_FAILED", "Renderer feature active state did not retain the requested value.", "Check the renderer feature and retry.");
            return new UrpFeatureResult { FeatureIndex = plan.FeatureIndex, PlannedEffects = new List<UrpPlannedEffect> { effect }};
        }

        internal static UrpFeatureResult Configure(int featureIndex, int rendererIndex, string propertyName, string value, bool dryRun, string logPath)
        {
            var target = RequireFeature(featureIndex, rendererIndex, "urp.feature-configure");
            using (var serialized = new SerializedObject(target.Feature))
            {
                var property = serialized.FindProperty(propertyName) ?? serialized.FindProperty("m_" + char.ToUpper(propertyName[0]) + propertyName.Substring(1));
                if (property == null) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_PROPERTY_NOT_FOUND", "Renderer feature property not found: " + propertyName, "Pass a SerializedProperty path present on the feature object.");
                if (!dryRun)
                {
                    UrpToolSupport.SetSerializedPropertyValue(property, value);
                    var expected = ReadSerializedPropertyValue(property);
                    serialized.ApplyModifiedProperties();
                    serialized.Update();
                    var actualProperty = serialized.FindProperty(propertyName) ?? serialized.FindProperty("m_" + char.ToUpper(propertyName[0]) + propertyName.Substring(1));
                    var actual = ReadSerializedPropertyValue(actualProperty);
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_VERIFY_FAILED", "Renderer feature property did not retain the requested value: " + propertyName, "Check the renderer feature and retry.");
                    value = actual;
                    target = new FeatureTarget { RendererData = PersistRendererData(target.RendererData), Feature = null };
                    target.Feature = ReadFeature(target.RendererData, featureIndex);
                    using (var observedSerialized = new SerializedObject(target.Feature))
                    {
                        var observedProperty = observedSerialized.FindProperty(propertyName) ?? observedSerialized.FindProperty("m_" + char.ToUpper(propertyName[0]) + propertyName.Substring(1));
                        if (!string.Equals(ReadSerializedPropertyValue(observedProperty), expected, StringComparison.Ordinal)) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_VERIFY_FAILED", "Renderer feature property did not retain the requested value after persistence: " + propertyName, "Check the renderer feature and retry.");
                    }
                }
            }
            var effect = new UrpPlannedEffect { Type = "urp.feature-configure", Index = featureIndex, RendererIndex = rendererIndex, Property = propertyName, Value = value };
            return new UrpFeatureResult { FeatureIndex = featureIndex, DryRun = dryRun, PlannedEffects = new List<UrpPlannedEffect> { effect }};
        }

        internal static UrpFeatureResult Remove(int featureIndex, int rendererIndex, bool dryRun, string logPath)
        {
            var target = RequireFeature(featureIndex, rendererIndex, "urp.feature-remove");
            var effect = new UrpPlannedEffect { Type = "urp.feature-remove", Index = featureIndex, RendererIndex = rendererIndex };
            if (!dryRun)
            {
                using (var serialized = new SerializedObject(target.RendererData))
                {
                    var features = serialized.FindProperty("m_RendererFeatures");
                    features.DeleteArrayElementAtIndex(featureIndex);
                    var map = serialized.FindProperty("m_RendererFeatureMap");
                    if (map != null && featureIndex < map.arraySize) map.DeleteArrayElementAtIndex(featureIndex);
                    serialized.ApplyModifiedProperties();
                }
                AssetDatabase.RemoveObjectFromAsset(target.Feature);
                UnityEngine.Object.DestroyImmediate(target.Feature, true);
                var reloadedRendererData = PersistRendererData(target.RendererData);
                if (ContainsFeature(reloadedRendererData, target.Feature))
                    throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_VERIFY_FAILED", "Renderer feature remained in the renderer feature collection after removal.", "Check the renderer feature and retry.");
            }
            return new UrpFeatureResult { FeatureIndex = featureIndex, DryRun = dryRun, PlannedEffects = new List<UrpPlannedEffect> { effect }};
        }

        // 重新读取 renderer feature collection，按持久化身份确认新增实例已经在集合中。
        private static int FindFeatureIndex(UnityEngine.Object rendererData, string featureGuid, long featureLocalId)
        {
            using (var serialized = new SerializedObject(rendererData))
            {
                var features = serialized.FindProperty("m_RendererFeatures");
                if (features == null) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURES_PROPERTY_NOT_FOUND", "m_RendererFeatures was not found on renderer data.", "Check the installed URP package version.");
                for (var index = 0; index < features.arraySize; index++) if (IsExactFeature(features.GetArrayElementAtIndex(index).objectReferenceValue, featureGuid, featureLocalId, null)) return index;
                return -1;
            }
        }

        private static void ValidateFeatureCollectionForAdd(SerializedObject serialized)
        {
            var features = serialized.FindProperty("m_RendererFeatures");
            if (features == null || !features.isArray || !features.editable || features.propertyType != SerializedPropertyType.Generic)
                throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURES_PROPERTY_NOT_FOUND", "m_RendererFeatures is not an editable serialized feature collection.", "Check the installed URP package version.");
            var map = serialized.FindProperty("m_RendererFeatureMap");
            if (map == null || !map.isArray || !map.editable || map.propertyType != SerializedPropertyType.Generic || !string.Equals(map.arrayElementType, "long", StringComparison.OrdinalIgnoreCase))
                throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_MAP_UNSUPPORTED", "m_RendererFeatureMap is not an editable long serialized collection.", "Check the installed URP package version.");
        }

        private static void RequireStableRendererAsset(UnityEngine.Object rendererData)
        {
            var path = AssetDatabase.GetAssetPath(rendererData);
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_VERIFY_FAILED", "Renderer data has no stable asset path and GUID.", "Use an imported renderer data asset.");
        }

        private static bool IsExactFeature(UnityEngine.Object feature, string expectedGuid, long expectedLocalId, Type expectedType)
        {
            if (feature == null || (expectedType != null && feature.GetType() != expectedType)) return false;
            string guid;
            long localId;
            return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out guid, out localId)
                && string.Equals(guid, expectedGuid, StringComparison.Ordinal)
                && localId == expectedLocalId;
        }

        private static bool ContainsFeature(UnityEngine.Object rendererData, UnityEngine.Object feature)
        {
            using (var serialized = new SerializedObject(rendererData))
            {
                var features = serialized.FindProperty("m_RendererFeatures");
                if (features == null) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURES_PROPERTY_NOT_FOUND", "m_RendererFeatures was not found on renderer data.", "Check the installed URP package version.");
                for (var index = 0; index < features.arraySize; index++) if (features.GetArrayElementAtIndex(index).objectReferenceValue == feature) return true;
                return false;
            }
        }

        private static UnityEngine.Object PersistRendererData(UnityEngine.Object rendererData)
        {
            var path = AssetDatabase.GetAssetPath(rendererData);
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(guid)) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_VERIFY_FAILED", "Renderer data has no stable asset path and GUID.", "Use an imported renderer data asset.");
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var reloaded = GraphicsToolSupport.ReloadAsset<UnityEngine.Object>(path, guid);
            if (reloaded == null) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_VERIFY_FAILED", "Renderer data could not be reloaded after persistence.", "Check the renderer asset and retry.");
            return reloaded;
        }

        private static UnityEngine.Object ReadFeature(UnityEngine.Object rendererData, int featureIndex)
        {
            using (var serialized = new SerializedObject(rendererData))
            {
                var features = serialized.FindProperty("m_RendererFeatures");
                if (features == null || featureIndex < 0 || featureIndex >= features.arraySize) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_VERIFY_FAILED", "Renderer feature could not be read from the reloaded collection.", "Check the renderer feature and retry.");
                return features.GetArrayElementAtIndex(featureIndex).objectReferenceValue;
            }
        }

        private static string ReadSerializedPropertyValue(SerializedProperty property)
        {
            if (property == null) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_VERIFY_FAILED", "Renderer feature property could not be read back.", "Check the renderer feature and retry.");
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float: return property.floatValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.Integer: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Enum: return property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return property.stringValue;
                default: throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_VERIFY_FAILED", "Renderer feature property type cannot be read back: " + property.propertyType, "Use a scalar renderer feature property.");
            }
        }

        // 解析一个现有 feature，并统一 index/renderer 错误。
        private static FeatureTarget RequireFeature(int featureIndex, int rendererIndex, string toolName)
        {
            GameObjectToolSupport.EnsureEditorNotBusy(toolName);
            UrpToolSupport.EnsureUrpAvailable();
            var rendererData = UrpToolSupport.GetRendererData(UrpToolSupport.GetActivePipelineAsset(), rendererIndex);
            if (rendererData == null) throw RendererNotFound(rendererIndex);
            using (var serialized = new SerializedObject(rendererData))
            {
                var features = serialized.FindProperty("m_RendererFeatures");
                if (features == null || featureIndex < 0 || featureIndex >= features.arraySize)
                    throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_INDEX_OUT_OF_RANGE", "Renderer feature index is out of range: " + featureIndex.ToString(CultureInfo.InvariantCulture), "Run urp.feature-list and pass an existing index.");
                var feature = features.GetArrayElementAtIndex(featureIndex).objectReferenceValue;
                if (feature == null) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_NULL", "Renderer feature is null at index " + featureIndex.ToString(CultureInfo.InvariantCulture) + ".", "Remove the invalid entry or recreate the feature.");
                return new FeatureTarget { RendererData = rendererData, Feature = feature };
            }
        }

        private static UnityCliBridge.BridgeCommandException RendererNotFound(int rendererIndex)
        {
            return new UnityCliBridge.BridgeCommandException("E_URP_RENDERER_NOT_FOUND", "Renderer data was not found at index " + rendererIndex.ToString(CultureInfo.InvariantCulture) + ".", "Pass a rendererIndex exposed by the active URP asset.");
        }

        internal sealed class AddMutationPlan { internal UnityEngine.Object RendererData { get; set; } internal Type FeatureType { get; set; } internal string FeatureTypeName { get; set; } internal int RendererIndex { get; set; } }
        internal sealed class ToggleMutationPlan { internal UnityEngine.Object RendererData { get; set; } internal UnityEngine.Object Feature { get; set; } internal int FeatureIndex { get; set; } internal int RendererIndex { get; set; } internal MethodInfo SetActive { get; set; } internal string ActivePropertyPath { get; set; } internal string FeatureGuid { get; set; } internal long FeatureLocalId { get; set; } internal Type FeatureType { get; set; } }
        private sealed class FeatureTarget { public UnityEngine.Object RendererData { get; set; } public UnityEngine.Object Feature { get; set; } }
    }
}
#endif
