#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// urp.feature — 列出、添加、移除、切换或配置活动 URP 资产上的 Renderer Feature。
    /// 保持原工具的 action 语义，但改为自包含 IUnityCliTool 形态。
    /// </summary>
    public class UrpFeatureTool : IUnityCliTool<UrpFeatureTool.Request, UrpFeatureTool.Result>
    {
        public sealed class Request
        {
            public string Action { get; set; }
            public string FeatureType { get; set; }
            public int? Index { get; set; }
            public bool? Enabled { get; set; }
            public string Property { get; set; }
            public string Value { get; set; }
            public int? RendererIndex { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string Action { get; set; }
            public long Count { get; set; }
            public long? RendererIndex { get; set; }
            public long? FeatureIndex { get; set; }
            public long? RemovedIndex { get; set; }
            public bool? Enabled { get; set; }
            public string FeatureName { get; set; }
            public string FeatureType { get; set; }
            public string Property { get; set; }
            public string Value { get; set; }
            public List<UrpFeatureInfo> Features { get; set; }
            public List<UrpPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "urp.feature",
            Summary = "List, add, remove, toggle, or configure URP Renderer Features on the active pipeline asset.",
            Description = "List, add, remove, toggle, or configure URP Renderer Features on the active Universal Render Pipeline asset. Requires com.unity.render-pipelines.universal and an active UniversalRenderPipelineAsset in Graphics Settings. action supports list, add, remove, toggle, and configure.",
            Group = "urp",
            Version = "1.0.0",
            DestructiveHint = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" },
            SideEffects = new[] { "asset-write", "renderer-feature-edit" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List renderer features", Input = new Request { Action = "list" } },
                new ToolExample { Description = "Add ScreenSpaceAmbientOcclusion", Input = new Request { Action = "add", FeatureType = "ScreenSpaceAmbientOcclusion" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.feature");
            UrpToolSupport.EnsureUrpAvailable();
            var action = (request.Action ?? "list").Trim().ToLowerInvariant();

            if (action == "list")
            {
                var features = UrpToolSupport.ListRendererFeatures();
                return new Result
                {
                    Command = "urp.feature",
                    Action = "list",
                    Count = features.Count,
                    Features = features,
                    LogPath = ctx.LogPath
                };
            }

            if (action == "add")
            {
                return AddFeature(request, ctx.LogPath);
            }

            if (action == "remove")
            {
                return RemoveFeature(request, ctx.LogPath);
            }

            if (action == "toggle")
            {
                return ToggleFeature(request, ctx.LogPath);
            }

            if (action == "configure")
            {
                return ConfigureFeature(request, ctx.LogPath);
            }

            throw new UnityCliBridge.BridgeCommandException(
                "E_URP_FEATURE_ACTION_UNSUPPORTED",
                "Unsupported URP feature action: " + action,
                "Use list, add, remove, toggle, or configure.");
        }

        private static Result AddFeature(Request request, string logPath)
        {
            if (string.IsNullOrEmpty(request.FeatureType))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_FEATURE_TYPE_REQUIRED",
                    "Add feature requires featureType.",
                    "Pass featureType with a ScriptableRendererFeature-derived type name, such as ScreenSpaceAmbientOcclusion.");
            }

            var rendererIndex = request.RendererIndex ?? 0;
            var pipeline = UrpToolSupport.GetActivePipelineAsset();
            var featureType = UrpToolSupport.FindFeatureType(request.FeatureType);
            if (featureType == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_FEATURE_TYPE_NOT_FOUND",
                    "Renderer feature type not found: " + request.FeatureType,
                    "Ensure the type name is correct and URP is fully imported.");
            }

            var rendererData = UrpToolSupport.GetRendererData(pipeline, rendererIndex);
            if (rendererData == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_RENDERER_NOT_FOUND",
                    "Renderer data at index " + rendererIndex.ToString(CultureInfo.InvariantCulture) + " not found.",
                    "Use a valid rendererIndex.");
            }

            var feature = ScriptableObject.CreateInstance(featureType);
            feature.name = request.FeatureType;
            AssetDatabase.AddObjectToAsset(feature, rendererData);

            using (var rendererSo = new SerializedObject(rendererData))
            {
                var featuresProp = rendererSo.FindProperty("m_RendererFeatures");
                if (featuresProp == null)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_URP_FEATURES_PROPERTY_NOT_FOUND",
                        "m_RendererFeatures not found on renderer data.",
                        "The URP version may differ.");
                }

                featuresProp.arraySize++;
                var element = featuresProp.GetArrayElementAtIndex(featuresProp.arraySize - 1);
                element.objectReferenceValue = feature;

                var mapProp = rendererSo.FindProperty("m_RendererFeatureMap");
                if (mapProp != null)
                {
                    string guid;
                    long localId;
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out guid, out localId);
                    mapProp.arraySize++;
                    mapProp.GetArrayElementAtIndex(mapProp.arraySize - 1).longValue = localId;
                }

                rendererSo.ApplyModifiedProperties();
            }

            AssetDatabase.SaveAssets();
            return new Result
            {
                Command = "urp.feature",
                Action = "add",
                FeatureName = feature.name,
                FeatureType = request.FeatureType,
                RendererIndex = rendererIndex,
                PlannedEffects = new List<UrpPlannedEffect>
                {
                    new UrpPlannedEffect { Type = "urp.feature.add", FeatureType = request.FeatureType, RendererIndex = rendererIndex }
                },
                LogPath = logPath
            };
        }

        private static Result RemoveFeature(Request request, string logPath)
        {
            if (!request.Index.HasValue)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_FEATURE_INDEX_REQUIRED",
                    "Remove feature requires index.",
                    "Use action=list to inspect the current feature indexes.");
            }

            var rendererIndex = request.RendererIndex ?? 0;
            var featureIndex = request.Index.Value;
            var pipeline = UrpToolSupport.GetActivePipelineAsset();
            var rendererData = UrpToolSupport.GetRendererData(pipeline, rendererIndex);
            if (rendererData == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_RENDERER_NOT_FOUND",
                    "Renderer data not found.",
                    "Use a valid rendererIndex.");
            }

            using (var rendererSo = new SerializedObject(rendererData))
            {
                var featuresProp = rendererSo.FindProperty("m_RendererFeatures");
                if (featuresProp == null || featureIndex < 0 || featureIndex >= featuresProp.arraySize)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_URP_FEATURE_INDEX_OUT_OF_RANGE",
                        "Feature index out of range: " + featureIndex.ToString(CultureInfo.InvariantCulture),
                        "Use action=list to inspect the current feature indexes.");
                }

                var featureObject = featuresProp.GetArrayElementAtIndex(featureIndex).objectReferenceValue;
                featuresProp.DeleteArrayElementAtIndex(featureIndex);

                var mapProp = rendererSo.FindProperty("m_RendererFeatureMap");
                if (mapProp != null && featureIndex < mapProp.arraySize)
                {
                    mapProp.DeleteArrayElementAtIndex(featureIndex);
                }

                rendererSo.ApplyModifiedProperties();
                if (featureObject != null)
                {
                    AssetDatabase.RemoveObjectFromAsset(featureObject);
                }
            }

            AssetDatabase.SaveAssets();
            return new Result
            {
                Command = "urp.feature",
                Action = "remove",
                RemovedIndex = featureIndex,
                RendererIndex = rendererIndex,
                PlannedEffects = new List<UrpPlannedEffect>
                {
                    new UrpPlannedEffect { Type = "urp.feature.remove", Index = featureIndex, RendererIndex = rendererIndex }
                },
                LogPath = logPath
            };
        }

        private static Result ToggleFeature(Request request, string logPath)
        {
            if (!request.Index.HasValue)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_FEATURE_INDEX_REQUIRED",
                    "Toggle feature requires index.",
                    "Use action=list to inspect the current feature indexes.");
            }
            if (!request.Enabled.HasValue)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_FEATURE_ENABLED_REQUIRED",
                    "Toggle feature requires enabled=true|false.",
                    "Pass enabled=true to enable or enabled=false to disable the feature.");
            }

            var rendererIndex = request.RendererIndex ?? 0;
            var featureIndex = request.Index.Value;
            var pipeline = UrpToolSupport.GetActivePipelineAsset();
            var rendererData = UrpToolSupport.GetRendererData(pipeline, rendererIndex);
            if (rendererData == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_RENDERER_NOT_FOUND",
                    "Renderer data not found.",
                    "Use a valid rendererIndex.");
            }

            using (var rendererSo = new SerializedObject(rendererData))
            {
                var featuresProp = rendererSo.FindProperty("m_RendererFeatures");
                if (featuresProp == null || featureIndex < 0 || featureIndex >= featuresProp.arraySize)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_URP_FEATURE_INDEX_OUT_OF_RANGE",
                        "Feature index out of range: " + featureIndex.ToString(CultureInfo.InvariantCulture),
                        "Use action=list to inspect the current feature indexes.");
                }

                var featureObject = featuresProp.GetArrayElementAtIndex(featureIndex).objectReferenceValue;
                if (featureObject == null)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_URP_FEATURE_NULL",
                        "Feature at index " + featureIndex.ToString(CultureInfo.InvariantCulture) + " is null.",
                        "The feature may have been removed or the renderer asset is inconsistent.");
                }

                var setActiveMethod = featureObject.GetType().GetMethod("SetActive", BindingFlags.Instance | BindingFlags.Public);
                if (setActiveMethod != null)
                {
                    setActiveMethod.Invoke(featureObject, new object[] { request.Enabled.Value });
                }
                else
                {
                    using (var featureSo = new SerializedObject(featureObject))
                    {
                        var activeProp = featureSo.FindProperty("m_Active") ?? featureSo.FindProperty("active");
                        if (activeProp != null)
                        {
                            activeProp.boolValue = request.Enabled.Value;
                            featureSo.ApplyModifiedProperties();
                        }
                    }
                }
            }

            return new Result
            {
                Command = "urp.feature",
                Action = "toggle",
                FeatureIndex = featureIndex,
                RendererIndex = rendererIndex,
                Enabled = request.Enabled.Value,
                PlannedEffects = new List<UrpPlannedEffect>
                {
                    new UrpPlannedEffect { Type = "urp.feature.toggle", Index = featureIndex, RendererIndex = rendererIndex, Enabled = request.Enabled.Value }
                },
                LogPath = logPath
            };
        }

        private static Result ConfigureFeature(Request request, string logPath)
        {
            if (!request.Index.HasValue)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_FEATURE_INDEX_REQUIRED",
                    "Configure feature requires index.",
                    "Use action=list to inspect the current feature indexes.");
            }
            if (string.IsNullOrEmpty(request.Property) || string.IsNullOrEmpty(request.Value))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_FEATURE_PROPERTY_REQUIRED",
                    "Configure feature requires property and value.",
                    "Pass a SerializedProperty path in property and the new value as a string.");
            }

            var rendererIndex = request.RendererIndex ?? 0;
            var featureIndex = request.Index.Value;
            var pipeline = UrpToolSupport.GetActivePipelineAsset();
            var rendererData = UrpToolSupport.GetRendererData(pipeline, rendererIndex);
            if (rendererData == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_RENDERER_NOT_FOUND",
                    "Renderer data not found.",
                    "Use a valid rendererIndex.");
            }

            using (var rendererSo = new SerializedObject(rendererData))
            {
                var featuresProp = rendererSo.FindProperty("m_RendererFeatures");
                if (featuresProp == null || featureIndex < 0 || featureIndex >= featuresProp.arraySize)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_URP_FEATURE_INDEX_OUT_OF_RANGE",
                        "Feature index out of range: " + featureIndex.ToString(CultureInfo.InvariantCulture),
                        "Use action=list to inspect the current feature indexes.");
                }

                var featureObject = featuresProp.GetArrayElementAtIndex(featureIndex).objectReferenceValue;
                if (featureObject == null)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_URP_FEATURE_NULL",
                        "Feature at index " + featureIndex.ToString(CultureInfo.InvariantCulture) + " is null.",
                        "The feature may have been removed or the renderer asset is inconsistent.");
                }

                using (var featureSo = new SerializedObject(featureObject))
                {
                    var property = featureSo.FindProperty(request.Property)
                        ?? featureSo.FindProperty("m_" + char.ToUpper(request.Property[0]) + request.Property.Substring(1));
                    if (property == null)
                    {
                        throw new UnityCliBridge.BridgeCommandException(
                            "E_URP_FEATURE_PROPERTY_NOT_FOUND",
                            "Property not found: " + request.Property,
                            "Check the SerializedProperty path on the feature object.");
                    }
                    UrpToolSupport.SetSerializedPropertyValue(property, request.Value);
                    featureSo.ApplyModifiedProperties();
                }
            }

            return new Result
            {
                Command = "urp.feature",
                Action = "configure",
                FeatureIndex = featureIndex,
                RendererIndex = rendererIndex,
                Property = request.Property,
                Value = request.Value,
                PlannedEffects = new List<UrpPlannedEffect>
                {
                    new UrpPlannedEffect
                    {
                        Type = "urp.feature.configure",
                        Index = featureIndex,
                        RendererIndex = rendererIndex,
                        Property = request.Property,
                        Value = request.Value
                    }
                },
                LogPath = logPath
            };
        }
    }
}
#endif
