#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public sealed class UrpFeatureResult
    {
        public long Count { get; set; }
        public long? RendererIndex { get; set; }
        public long? FeatureIndex { get; set; }
        public bool? Enabled { get; set; }
        public string FeatureName { get; set; }
        public string FeatureType { get; set; }
        public string Property { get; set; }
        public string Value { get; set; }
        public bool DryRun { get; set; }
        public List<UrpFeatureInfo> Features { get; set; }
        public List<UrpPlannedEffect> PlannedEffects { get; set; }
        public string LogPath { get; set; }
    }

    /// <summary>URP Renderer Feature 单动作 Tool 的共享资产定位与写入实现。</summary>
    internal static class UrpFeatureToolSupport
    {
        internal static UrpFeatureResult List(string logPath)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.feature.list");
            UrpToolSupport.EnsureUrpAvailable();
            var features = UrpToolSupport.ListRendererFeatures();
            return new UrpFeatureResult { Count = features.Count, Features = features, LogPath = logPath };
        }

        internal static UrpFeatureResult Add(string featureTypeName, int rendererIndex, bool dryRun, string logPath)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.feature.add");
            UrpToolSupport.EnsureUrpAvailable();
            var pipeline = UrpToolSupport.GetActivePipelineAsset();
            var featureType = UrpToolSupport.FindFeatureType(featureTypeName);
            if (featureType == null) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_TYPE_NOT_FOUND", "Renderer feature type not found: " + featureTypeName, "Pass a ScriptableRendererFeature-derived type such as ScreenSpaceAmbientOcclusion.");
            var rendererData = UrpToolSupport.GetRendererData(pipeline, rendererIndex);
            if (rendererData == null) throw RendererNotFound(rendererIndex);
            var effect = new UrpPlannedEffect { Type = "urp.feature.add", FeatureType = featureTypeName, RendererIndex = rendererIndex };
            if (dryRun) return new UrpFeatureResult { FeatureType = featureTypeName, RendererIndex = rendererIndex, DryRun = true, PlannedEffects = new List<UrpPlannedEffect> { effect }, LogPath = logPath };

            var feature = ScriptableObject.CreateInstance(featureType);
            feature.name = featureTypeName;
            AssetDatabase.AddObjectToAsset(feature, rendererData);
            using (var serialized = new SerializedObject(rendererData))
            {
                var features = serialized.FindProperty("m_RendererFeatures");
                if (features == null) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURES_PROPERTY_NOT_FOUND", "m_RendererFeatures was not found on renderer data.", "Check the installed URP package version.");
                features.arraySize += 1;
                features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
                var map = serialized.FindProperty("m_RendererFeatureMap");
                if (map != null)
                {
                    string guid;
                    long localId;
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out guid, out localId);
                    map.arraySize += 1;
                    map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                }
                serialized.ApplyModifiedProperties();
            }
            AssetDatabase.SaveAssets();
            var index = UrpToolSupport.ListRendererFeatures().Count - 1;
            return new UrpFeatureResult { FeatureName = feature.name, FeatureType = featureTypeName, FeatureIndex = index, RendererIndex = rendererIndex, PlannedEffects = new List<UrpPlannedEffect> { effect }, LogPath = logPath };
        }

        internal static UrpFeatureResult Toggle(int featureIndex, int rendererIndex, bool enabled, bool dryRun, string logPath)
        {
            var target = RequireFeature(featureIndex, rendererIndex, "urp.feature.toggle");
            var effect = new UrpPlannedEffect { Type = "urp.feature.toggle", Index = featureIndex, RendererIndex = rendererIndex, Enabled = enabled };
            if (!dryRun)
            {
                var setActive = target.Feature.GetType().GetMethod("SetActive", BindingFlags.Instance | BindingFlags.Public);
                if (setActive != null) setActive.Invoke(target.Feature, new object[] { enabled });
                else
                {
                    using (var serialized = new SerializedObject(target.Feature))
                    {
                        var active = serialized.FindProperty("m_Active") ?? serialized.FindProperty("active");
                        if (active == null) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_ACTIVE_UNSUPPORTED", "Renderer feature active state is unavailable.", "Check the installed URP package version.");
                        active.boolValue = enabled;
                        serialized.ApplyModifiedProperties();
                    }
                }
                AssetDatabase.SaveAssets();
            }
            return new UrpFeatureResult { FeatureIndex = featureIndex, RendererIndex = rendererIndex, Enabled = enabled, DryRun = dryRun, PlannedEffects = new List<UrpPlannedEffect> { effect }, LogPath = logPath };
        }

        internal static UrpFeatureResult Configure(int featureIndex, int rendererIndex, string propertyName, string value, bool dryRun, string logPath)
        {
            var target = RequireFeature(featureIndex, rendererIndex, "urp.feature.configure");
            using (var serialized = new SerializedObject(target.Feature))
            {
                var property = serialized.FindProperty(propertyName) ?? serialized.FindProperty("m_" + char.ToUpper(propertyName[0]) + propertyName.Substring(1));
                if (property == null) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_PROPERTY_NOT_FOUND", "Renderer feature property not found: " + propertyName, "Pass a SerializedProperty path present on the feature object.");
                if (!dryRun)
                {
                    UrpToolSupport.SetSerializedPropertyValue(property, value);
                    serialized.ApplyModifiedProperties();
                    AssetDatabase.SaveAssets();
                }
            }
            var effect = new UrpPlannedEffect { Type = "urp.feature.configure", Index = featureIndex, RendererIndex = rendererIndex, Property = propertyName, Value = value };
            return new UrpFeatureResult { FeatureIndex = featureIndex, RendererIndex = rendererIndex, Property = propertyName, Value = value, DryRun = dryRun, PlannedEffects = new List<UrpPlannedEffect> { effect }, LogPath = logPath };
        }

        internal static UrpFeatureResult Remove(int featureIndex, int rendererIndex, bool dryRun, string logPath)
        {
            var target = RequireFeature(featureIndex, rendererIndex, "urp.feature.remove");
            var effect = new UrpPlannedEffect { Type = "urp.feature.remove", Index = featureIndex, RendererIndex = rendererIndex };
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
                AssetDatabase.SaveAssets();
            }
            return new UrpFeatureResult { FeatureIndex = featureIndex, RendererIndex = rendererIndex, DryRun = dryRun, PlannedEffects = new List<UrpPlannedEffect> { effect }, LogPath = logPath };
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
                    throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_INDEX_OUT_OF_RANGE", "Renderer feature index is out of range: " + featureIndex.ToString(CultureInfo.InvariantCulture), "Run urp.feature.list and pass an existing index.");
                var feature = features.GetArrayElementAtIndex(featureIndex).objectReferenceValue;
                if (feature == null) throw new UnityCliBridge.BridgeCommandException("E_URP_FEATURE_NULL", "Renderer feature is null at index " + featureIndex.ToString(CultureInfo.InvariantCulture) + ".", "Remove the invalid entry or recreate the feature.");
                return new FeatureTarget { RendererData = rendererData, Feature = feature };
            }
        }

        private static UnityCliBridge.BridgeCommandException RendererNotFound(int rendererIndex)
        {
            return new UnityCliBridge.BridgeCommandException("E_URP_RENDERER_NOT_FOUND", "Renderer data was not found at index " + rendererIndex.ToString(CultureInfo.InvariantCulture) + ".", "Pass a rendererIndex exposed by the active URP asset.");
        }

        private sealed class FeatureTarget { public UnityEngine.Object RendererData { get; set; } public UnityEngine.Object Feature { get; set; } }
    }
}
#endif
