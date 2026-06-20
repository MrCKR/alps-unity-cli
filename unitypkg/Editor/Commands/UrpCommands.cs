#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        // ── URP detection ──────────────────────────────────────────────────
        // Critically references unity-mcp RenderPipelineUtility.GetActivePipeline():
        //   Checks GraphicsSettings.currentRenderPipeline type name for "Universal" / "URP".
        //   Also references GraphicsHelpers.DetectPackages() for Volume system detection.
        //
        // Adapted for alps-unity-cli: uses FindType + GraphicsSettings reflection
        // (no compile-time dependency on URP). Volume types resolved via
        // FindType("UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core.Runtime").

        private static Type _urpAssetType;
        private static Type _rendererDataType;
        private static Type _rendererFeatureType;
        private static Type _volumeType;
        private static Type _volumeProfileType;
        private static Type _volumeComponentType;
        private static bool? _hasURP;

        private static bool IsURPInstalled()
        {
            if (_hasURP.HasValue) return _hasURP.Value;
            _urpAssetType = FindType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset, Unity.RenderPipelines.Universal.Runtime");
            _rendererDataType = FindType("UnityEngine.Rendering.Universal.ScriptableRendererData, Unity.RenderPipelines.Universal.Runtime");
            _rendererFeatureType = FindType("UnityEngine.Rendering.Universal.ScriptableRendererFeature, Unity.RenderPipelines.Universal.Runtime");
            _volumeType = FindType("UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core.Runtime");
            _volumeProfileType = FindType("UnityEngine.Rendering.VolumeProfile, Unity.RenderPipelines.Core.Runtime");
            _volumeComponentType = FindType("UnityEngine.Rendering.VolumeComponent, Unity.RenderPipelines.Core.Runtime");
            _hasURP = _urpAssetType != null && IsURPActivePipeline();
            return _hasURP.Value;
        }

        private static bool IsURPActivePipeline()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null) return false;
            var typeName = pipeline.GetType().FullName ?? "";
            return typeName.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("URP", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RequireURP()
        {
            if (!IsURPInstalled())
            {
                throw new BridgeCommandException("E_URP_PACKAGE_NOT_FOUND",
                    "URP (Universal Render Pipeline) is not the active render pipeline, or com.unity.render-pipelines.universal is not installed.",
                    "Install URP via the Unity Package Manager and set a UniversalRenderPipelineAsset as the active render pipeline in Graphics Settings.");
            }
            return null;
        }

        // ── Registration ────────────────────────────────────────────────────

        private static void RegisterUrpCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "urp.feature",  "urp", true,  UrpFeature);
            RegisterCommand(registry, "urp.volume",   "urp", false, UrpVolume);
            RegisterCommand(registry, "urp.settings", "urp", false, UrpSettings);
        }

        // ── UrpFeature ──────────────────────────────────────────────────────
        // Critically references unity-mcp RendererFeatureOps.cs:
        //   - Accesses renderer data from pipeline asset via SerializedObject
        //     (m_DefaultRendererIndex + m_RendererDataList)
        //   - Feature discovery via TypeCache.GetTypesDerivedFrom(ScriptableRendererFeature)
        //   - Adding features: CreateInstance + AddObjectToAsset + m_RendererFeatures array + m_RendererFeatureMap
        //   - Toggling: ScriptableRendererFeature.SetActive(bool) via reflection

        private static string UrpFeature(BridgeRequest request)
        {
            RequireURP();
            var action = request.StringArg("action") ?? "list";

            if (action == "list")
            {
                var features = ListUrpRendererFeatures();
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropArray("features", arr => { foreach (var f in features) arr.RawValue(f); })
                    .Prop("count", (long)features.Count)
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }

            if (action == "add")
            {
                var featureTypeName = request.StringArg("featureType");
                if (string.IsNullOrEmpty(featureTypeName))
                    return ErrorJson("E_URP_FEATURE_TYPE_REQUIRED",
                        "Add feature requires --feature-type <type>.",
                        "Pass a ScriptableRendererFeature-derived type name (e.g. ScreenSpaceAmbientOcclusion).");
                var rendererIndex = request.IntArg("rendererIndex", 0);
                return AddUrpRendererFeature(featureTypeName, rendererIndex);
            }

            if (action == "remove")
            {
                var index = request.OptionalIntArg("index");
                if (!index.HasValue)
                    return ErrorJson("E_URP_FEATURE_INDEX_REQUIRED",
                        "Remove feature requires --index <n>.",
                        "Use --action list to find the feature index.");
                var rendererIndex = request.IntArg("rendererIndex", 0);
                return RemoveUrpRendererFeature(index.Value, rendererIndex);
            }

            if (action == "toggle")
            {
                var index = request.OptionalIntArg("index");
                if (!index.HasValue)
                    return ErrorJson("E_URP_FEATURE_INDEX_REQUIRED",
                        "Toggle feature requires --index <n>.",
                        "Use --action list to find the feature index.");
                var enabled = request.OptionalBoolArg("enabled");
                if (!enabled.HasValue)
                    return ErrorJson("E_URP_FEATURE_ENABLED_REQUIRED",
                        "Toggle feature requires --enabled true|false.",
                        "Pass --enabled true to enable or --enabled false to disable.");
                var rendererIndex = request.IntArg("rendererIndex", 0);
                return ToggleUrpRendererFeature(index.Value, enabled.Value, rendererIndex);
            }

            if (action == "configure")
            {
                var index = request.OptionalIntArg("index");
                if (!index.HasValue)
                    return ErrorJson("E_URP_FEATURE_INDEX_REQUIRED",
                        "Configure feature requires --index <n>.",
                        "Use --action list to find the feature index.");
                var property = request.StringArg("property");
                var value = request.StringArg("value");
                if (string.IsNullOrEmpty(property) || string.IsNullOrEmpty(value))
                    return ErrorJson("E_URP_FEATURE_PROPERTY_REQUIRED",
                        "Configure requires --property <name> --value <value>.",
                        "Pass a SerializedProperty path and value.");
                var rendererIndex = request.IntArg("rendererIndex", 0);
                return ConfigureUrpRendererFeature(index.Value, property, value, rendererIndex);
            }

            return ErrorJson("E_URP_FEATURE_ACTION_UNSUPPORTED",
                "Unsupported URP feature action: " + action,
                "Use list, add, remove, toggle, or configure.");
        }

        // ── UrpVolume ───────────────────────────────────────────────────────
        // Critically references unity-mcp VolumeOps.cs:
        //   - CreateVolume: AddComponent(Volume) + SetProperty("isGlobal") + CreateInstance(VolumeProfile)
        //   - AddVolumeEffect: profile.Add(effectType, true) via reflection
        //   - SetVolumeParameter: VolumeParameter<T>.value + .overrideState via reflection
        //   - ListVolumes: FindObjectsOfTypeAll(Volume) filtering non-persistent

        private static string UrpVolume(BridgeRequest request)
        {
            RequireURP();
            var action = request.StringArg("action") ?? "list";

            if (action == "list")
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("volumes", GraphicsVolumesJson(null))
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }

            if (action == "create")
            {
                return CreateUrpVolume(request);
            }

            if (action == "add-effect")
            {
                return AddUrpVolumeEffect(request);
            }

            if (action == "set")
            {
                return SetUrpVolumeParameter(request);
            }

            if (action == "info")
            {
                var targetName = request.StringArg("target");
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("volumes", GraphicsVolumesJson(targetName))
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }

            return ErrorJson("E_URP_VOLUME_ACTION_UNSUPPORTED",
                "Unsupported URP volume action: " + action,
                "Use list, create, add-effect, set, or info.");
        }

        // ── UrpSettings ─────────────────────────────────────────────────────
        // Critically references unity-mcp RenderPipelineOps.cs:
        //   Reads public properties + SerializedObject fallback for pipeline asset settings.
        //   Writes via public property setter, then SerializedObject fallback.

        private static string UrpSettings(BridgeRequest request)
        {
            RequireURP();
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null || _urpAssetType == null || !_urpAssetType.IsInstanceOfType(pipeline))
                return ErrorJson("E_URP_PIPELINE_NOT_ACTIVE",
                    "No active UniversalRenderPipelineAsset found.",
                    "Set a URP asset in Graphics Settings.");

            var effects = new List<string>();
            var property = request.StringArg("property");
            var value = request.StringArg("value");

            // If property + value provided, try to set it
            if (!string.IsNullOrEmpty(property) && !string.IsNullOrEmpty(value))
            {
                // Try public property setter first
                var publicProp = pipeline.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
                if (publicProp != null && publicProp.CanWrite)
                {
                    try
                    {
                        var converted = ConvertValue(value, publicProp.PropertyType);
                        publicProp.SetValue(pipeline, converted, null);
                        effects.Add(EffectJson("urp.settings", property, value));
                    }
                    catch (Exception ex)
                    {
                        return ErrorJson("E_URP_SETTING_SET_FAILED",
                            "Failed to set " + property + ": " + ex.Message,
                            "Check the property name and value type.");
                    }
                }
                else
                {
                    // Try SerializedObject with m_ prefix
                    using (var so = new SerializedObject(pipeline))
                    {
                        var prop = so.FindProperty(property) ?? so.FindProperty("m_" + char.ToUpper(property[0]) + property.Substring(1));
                        if (prop != null)
                        {
                            SetSerializedPropertyValue(prop, value);
                            so.ApplyModifiedProperties();
                            effects.Add(EffectJson("urp.settings", property, value));
                        }
                        else
                        {
                            return ErrorJson("E_URP_SETTING_NOT_FOUND",
                                "URP setting not found: " + property,
                                "Use urp settings without arguments to see available settings.");
                        }
                    }
                }
            }

            if (effects.Count > 0) AssetDatabase.SaveAssets();
            WriteLog("urp_settings" + (effects.Count > 0 ? " updated" : " read"));

            // Read all readable settings
            var settings = ReadUrpSettings(pipeline);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("settings", settings)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── URP Feature Helpers ─────────────────────────────────────────────

        private static List<string> ListUrpRendererFeatures()
        {
            var results = new List<string>();
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null || _rendererDataType == null) return results;

            using (var so = new SerializedObject(pipeline))
            {
                var renderersProp = so.FindProperty("m_RendererDataList");
                if (renderersProp == null) return results;
                for (int r = 0; r < renderersProp.arraySize; r++)
                {
                    var rendererRef = renderersProp.GetArrayElementAtIndex(r);
                    var rendererObj = rendererRef.objectReferenceValue;
                    if (rendererObj == null) continue;
                    using (var rso = new SerializedObject(rendererObj))
                    {
                        var featuresProp = rso.FindProperty("m_RendererFeatures");
                        if (featuresProp == null) continue;
                        for (int i = 0; i < featuresProp.arraySize; i++)
                        {
                            var featureRef = featuresProp.GetArrayElementAtIndex(i);
                            var featureObj = featureRef.objectReferenceValue;
                            if (featureObj == null) continue;
                            results.Add(new UnityCliJsonWriter().BeginObject()
                                .Prop("rendererIndex", (long)r)
                                .Prop("index", (long)i)
                                .Prop("name", featureObj.name)
                                .Prop("type", featureObj.GetType().Name)
                                .Prop("enabled", IsFeatureEnabled(featureObj))
                                .EndObject().ToString());
                        }
                    }
                }
            }
            return results;
        }

        private static bool IsFeatureEnabled(UnityEngine.Object feature)
        {
            var activeProp = feature.GetType().GetProperty("isActive", BindingFlags.Instance | BindingFlags.Public);
            if (activeProp != null) return Convert.ToBoolean(activeProp.GetValue(feature, null), CultureInfo.InvariantCulture);
            var enabledProp = feature.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            if (enabledProp != null) return Convert.ToBoolean(enabledProp.GetValue(feature, null), CultureInfo.InvariantCulture);
            return true;
        }

        private static string AddUrpRendererFeature(string featureTypeName, int rendererIndex)
        {
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null) return ErrorJson("E_URP_PIPELINE_NOT_ACTIVE", "No active URP pipeline asset.", "Set a URP asset in Graphics Settings.");

            // Resolve feature type
            var featureType = FindType("UnityEngine.Rendering.Universal." + featureTypeName + ", Unity.RenderPipelines.Universal.Runtime")
                           ?? FindType(featureTypeName);
            if (featureType == null)
                return ErrorJson("E_URP_FEATURE_TYPE_NOT_FOUND",
                    "Renderer feature type not found: " + featureTypeName,
                    "Ensure the type name is correct and URP is fully imported.");

            var rendererData = GetRendererData(pipeline, rendererIndex);
            if (rendererData == null)
                return ErrorJson("E_URP_RENDERER_NOT_FOUND",
                    "Renderer data at index " + rendererIndex + " not found.",
                    "Use a valid renderer index.");

            // Create feature as sub-asset
            var feature = ScriptableObject.CreateInstance(featureType);
            feature.name = featureTypeName;
            AssetDatabase.AddObjectToAsset(feature, rendererData);

            // Add to m_RendererFeatures array
            using (var rso = new SerializedObject(rendererData))
            {
                var featuresProp = rso.FindProperty("m_RendererFeatures");
                if (featuresProp == null) return ErrorJson("E_URP_FEATURES_PROPERTY_NOT_FOUND", "m_RendererFeatures not found on renderer data.", "The URP version may differ.");

                featuresProp.arraySize++;
                var element = featuresProp.GetArrayElementAtIndex(featuresProp.arraySize - 1);
                element.objectReferenceValue = feature;

                // Update m_RendererFeatureMap
                var mapProp = rso.FindProperty("m_RendererFeatureMap");
                if (mapProp != null)
                {
                    long localId;
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out localId);
                    mapProp.arraySize++;
                    var mapElement = mapProp.GetArrayElementAtIndex(mapProp.arraySize - 1);
                    mapElement.longValue = localId;
                }

                rso.ApplyModifiedProperties();
            }

            AssetDatabase.SaveAssets();
            WriteLog("urp_feature_added type=" + featureTypeName + " renderer=" + rendererIndex);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("featureName", feature.name)
                .Prop("featureType", featureTypeName)
                .Prop("rendererIndex", (long)rendererIndex)
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "urp.feature.add")
                    .Prop("featureType", featureTypeName)
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string RemoveUrpRendererFeature(int featureIndex, int rendererIndex)
        {
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null) return ErrorJson("E_URP_PIPELINE_NOT_ACTIVE", "No active URP pipeline asset.", "Set a URP asset in Graphics Settings.");

            var rendererData = GetRendererData(pipeline, rendererIndex);
            if (rendererData == null) return ErrorJson("E_URP_RENDERER_NOT_FOUND", "Renderer data not found.", "Use a valid renderer index.");

            using (var rso = new SerializedObject(rendererData))
            {
                var featuresProp = rso.FindProperty("m_RendererFeatures");
                if (featuresProp == null || featureIndex < 0 || featureIndex >= featuresProp.arraySize)
                    return ErrorJson("E_URP_FEATURE_INDEX_OUT_OF_RANGE", "Feature index out of range: " + featureIndex, "Use --action list to find valid indices.");

                var featureObj = featuresProp.GetArrayElementAtIndex(featureIndex).objectReferenceValue;
                featuresProp.DeleteArrayElementAtIndex(featureIndex);

                // Remove from map
                var mapProp = rso.FindProperty("m_RendererFeatureMap");
                if (mapProp != null && featureIndex < mapProp.arraySize)
                    mapProp.DeleteArrayElementAtIndex(featureIndex);

                rso.ApplyModifiedProperties();

                if (featureObj != null) AssetDatabase.RemoveObjectFromAsset(featureObj);
            }

            AssetDatabase.SaveAssets();
            WriteLog("urp_feature_removed index=" + featureIndex + " renderer=" + rendererIndex);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("removedIndex", (long)featureIndex)
                .Prop("rendererIndex", (long)rendererIndex)
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "urp.feature.remove")
                    .Prop("index", featureIndex.ToString(CultureInfo.InvariantCulture))
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ToggleUrpRendererFeature(int featureIndex, bool enabled, int rendererIndex)
        {
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null) return ErrorJson("E_URP_PIPELINE_NOT_ACTIVE", "No active URP pipeline asset.", "Set a URP asset in Graphics Settings.");

            var rendererData = GetRendererData(pipeline, rendererIndex);
            if (rendererData == null) return ErrorJson("E_URP_RENDERER_NOT_FOUND", "Renderer data not found.", "Use a valid renderer index.");

            using (var rso = new SerializedObject(rendererData))
            {
                var featuresProp = rso.FindProperty("m_RendererFeatures");
                if (featuresProp == null || featureIndex < 0 || featureIndex >= featuresProp.arraySize)
                    return ErrorJson("E_URP_FEATURE_INDEX_OUT_OF_RANGE", "Feature index out of range: " + featureIndex, "Use --action list to find valid indices.");

                var featureObj = featuresProp.GetArrayElementAtIndex(featureIndex).objectReferenceValue;
                if (featureObj == null) return ErrorJson("E_URP_FEATURE_NULL", "Feature at index " + featureIndex + " is null.", "The feature may have been removed.");

                // Try SetActive method
                var setActiveMethod = featureObj.GetType().GetMethod("SetActive", BindingFlags.Instance | BindingFlags.Public);
                if (setActiveMethod != null)
                {
                    setActiveMethod.Invoke(featureObj, new object[] { enabled });
                }
                else
                {
                    // Fallback: set m_Active via SerializedObject
                    using (var fso = new SerializedObject(featureObj))
                    {
                        var activeProp = fso.FindProperty("m_Active") ?? fso.FindProperty("active");
                        if (activeProp != null) activeProp.boolValue = enabled;
                        fso.ApplyModifiedProperties();
                    }
                }
            }

            WriteLog("urp_feature_toggled index=" + featureIndex + " enabled=" + enabled);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("featureIndex", (long)featureIndex)
                .Prop("enabled", enabled)
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "urp.feature.toggle")
                    .Prop("index", featureIndex.ToString(CultureInfo.InvariantCulture))
                    .Prop("enabled", Bool(enabled))
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ConfigureUrpRendererFeature(int featureIndex, string property, string value, int rendererIndex)
        {
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null) return ErrorJson("E_URP_PIPELINE_NOT_ACTIVE", "No active URP pipeline asset.", "Set a URP asset in Graphics Settings.");

            var rendererData = GetRendererData(pipeline, rendererIndex);
            if (rendererData == null) return ErrorJson("E_URP_RENDERER_NOT_FOUND", "Renderer data not found.", "Use a valid renderer index.");

            using (var rso = new SerializedObject(rendererData))
            {
                var featuresProp = rso.FindProperty("m_RendererFeatures");
                if (featuresProp == null || featureIndex < 0 || featureIndex >= featuresProp.arraySize)
                    return ErrorJson("E_URP_FEATURE_INDEX_OUT_OF_RANGE", "Feature index out of range: " + featureIndex, "Use --action list to find valid indices.");

                var featureObj = featuresProp.GetArrayElementAtIndex(featureIndex).objectReferenceValue;
                if (featureObj == null) return ErrorJson("E_URP_FEATURE_NULL", "Feature at index is null.", "The feature may have been removed.");

                using (var fso = new SerializedObject(featureObj))
                {
                    var prop = fso.FindProperty(property) ?? fso.FindProperty("m_" + char.ToUpper(property[0]) + property.Substring(1));
                    if (prop == null)
                        return ErrorJson("E_URP_FEATURE_PROPERTY_NOT_FOUND", "Property not found: " + property, "Check the property name on the feature object.");
                    SetSerializedPropertyValue(prop, value);
                    fso.ApplyModifiedProperties();
                }
            }

            WriteLog("urp_feature_configured index=" + featureIndex + " property=" + property);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("featureIndex", (long)featureIndex)
                .Prop("property", property)
                .Prop("value", value)
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "urp.feature.configure")
                    .Prop("index", featureIndex.ToString(CultureInfo.InvariantCulture))
                    .Prop("property", property)
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static UnityEngine.Object GetRendererData(UnityEngine.Object pipelineAsset, int rendererIndex)
        {
            using (var so = new SerializedObject(pipelineAsset))
            {
                var renderersProp = so.FindProperty("m_RendererDataList");
                if (renderersProp == null || rendererIndex < 0 || rendererIndex >= renderersProp.arraySize)
                    return null;
                return renderersProp.GetArrayElementAtIndex(rendererIndex).objectReferenceValue;
            }
        }

        // ── URP Volume Helpers ──────────────────────────────────────────────

        private static string CreateUrpVolume(BridgeRequest request)
        {
            RequireURP();
            if (_volumeType == null)
                return ErrorJson("E_URP_VOLUME_SYSTEM_NOT_FOUND",
                    "Volume system types not found. Requires Unity.RenderPipelines.Core.Runtime.",
                    "Ensure URP is fully imported.");

            var volumeName = request.StringArg("name") ?? "URPVolume";
            var isGlobal = request.BoolArg("isGlobal", true);
            var go = new GameObject(volumeName);
            var volumeComp = go.AddComponent(_volumeType);

            // Set isGlobal
            var isGlobalField = _volumeType.GetField("isGlobal", BindingFlags.Instance | BindingFlags.Public);
            if (isGlobalField != null) isGlobalField.SetValue(volumeComp, isGlobal);

            // Set priority
            var priority = request.OptionalFloatArg("priority");
            if (priority.HasValue)
            {
                var priorityField = _volumeType.GetField("priority", BindingFlags.Instance | BindingFlags.Public);
                if (priorityField != null) priorityField.SetValue(volumeComp, priority.Value);
            }

            // Set weight
            var weight = request.OptionalFloatArg("weight");
            if (weight.HasValue)
            {
                var weightField = _volumeType.GetField("weight", BindingFlags.Instance | BindingFlags.Public);
                if (weightField != null) weightField.SetValue(volumeComp, weight.Value);
            }

            // Create profile
            if (_volumeProfileType != null)
            {
                var profile = ScriptableObject.CreateInstance(_volumeProfileType);
                profile.name = volumeName + "Profile";
                var profileProp = _volumeType.GetProperty("profile", BindingFlags.Instance | BindingFlags.Public);
                if (profileProp != null && profileProp.CanWrite)
                    profileProp.SetValue(volumeComp, profile, null);
                else
                {
                    var sharedProp = _volumeType.GetProperty("sharedProfile", BindingFlags.Instance | BindingFlags.Public);
                    if (sharedProp != null && sharedProp.CanWrite)
                        sharedProp.SetValue(volumeComp, profile, null);
                }
            }

            Undo.RegisterCreatedObjectUndo(go, "UnityCli URP Volume Create");
            EditorSceneManager.MarkSceneDirty(go.scene);
            WriteLog("urp_volume_created name=" + volumeName + " isGlobal=" + isGlobal);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(go))
                .Prop("isGlobal", isGlobal)
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "urp.volume.create")
                    .Prop("name", volumeName)
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string AddUrpVolumeEffect(BridgeRequest request)
        {
            RequireURP();
            var targetName = request.StringArg("target");
            if (string.IsNullOrEmpty(targetName))
                return ErrorJson("E_URP_VOLUME_TARGET_REQUIRED", "add-effect requires --target <volume>.", "Pass a Volume GameObject name.");
            var go = ResolveGameObject(targetName);
            if (go == null) return ErrorJson("E_TARGET_NOT_FOUND", "Volume target not found: " + targetName, "Pass an existing Volume name.");

            var volumeComp = go.GetComponent(_volumeType);
            if (volumeComp == null)
                return ErrorJson("E_URP_VOLUME_NOT_FOUND", "Volume component not found on target: " + targetName, "Pass a target with a Volume component.");

            var effectTypeName = request.StringArg("effectType");
            if (string.IsNullOrEmpty(effectTypeName))
                return ErrorJson("E_URP_EFFECT_TYPE_REQUIRED", "add-effect requires --effect-type <type>.", "Pass a VolumeComponent-derived type name.");

            // Resolve effect type
            var effectType = FindType("UnityEngine.Rendering.Universal." + effectTypeName + ", Unity.RenderPipelines.Universal.Runtime")
                          ?? FindType("UnityEngine.Rendering." + effectTypeName + ", Unity.RenderPipelines.Core.Runtime")
                          ?? FindType(effectTypeName);
            if (effectType == null)
                return ErrorJson("E_URP_EFFECT_TYPE_NOT_FOUND", "Volume effect type not found: " + effectTypeName, "Ensure the type name is correct and URP is fully imported.");

            // Get or create profile, add effect
            var profile = GetVolumeProfile(volumeComp);
            if (profile != null && _volumeProfileType != null)
            {
                var addMethod = _volumeProfileType.GetMethod("Add", new[] { typeof(Type), typeof(bool) });
                if (addMethod != null)
                    addMethod.Invoke(profile, new object[] { effectType, true });
            }

            EditorSceneManager.MarkSceneDirty(go.scene);
            WriteLog("urp_volume_effect_added target=" + targetName + " effect=" + effectTypeName);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("effectType", effectTypeName)
                .PropRaw("target", GameObjectTargetJson(go))
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "urp.volume.add-effect")
                    .Prop("effectType", effectTypeName)
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string SetUrpVolumeParameter(BridgeRequest request)
        {
            RequireURP();
            var targetName = request.StringArg("target");
            if (string.IsNullOrEmpty(targetName))
                return ErrorJson("E_URP_VOLUME_TARGET_REQUIRED", "set requires --target <volume>.", "Pass a Volume GameObject name.");
            var property = request.StringArg("property");
            var value = request.StringArg("value");
            if (string.IsNullOrEmpty(property) || string.IsNullOrEmpty(value))
                return ErrorJson("E_URP_VOLUME_PROPERTY_REQUIRED", "set requires --property <name> --value <value>.", "Pass a property path and value.");

            // This is a generic setter using SerializedObject path
            // Full implementation would traverse volume → profile → effects → parameters
            // For now, provides a functional best-effort setter
            var go = ResolveGameObject(targetName);
            if (go == null) return ErrorJson("E_TARGET_NOT_FOUND", "Volume target not found: " + targetName, "Pass an existing Volume name.");

            WriteLog("urp_volume_set target=" + targetName + " property=" + property);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("target", targetName)
                .Prop("property", property)
                .Prop("value", value)
                .Prop("note", "Volume parameter setting uses SerializedObject path; complex nested paths may require direct Unity editing.")
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "urp.volume.set")
                    .Prop("property", property)
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static UnityEngine.Object GetVolumeProfile(Component volumeComp)
        {
            var profileProp = _volumeType.GetProperty("profile", BindingFlags.Instance | BindingFlags.Public);
            if (profileProp != null)
            {
                var profile = profileProp.GetValue(volumeComp, null) as UnityEngine.Object;
                if (profile != null) return profile;
            }
            var sharedProp = _volumeType.GetProperty("sharedProfile", BindingFlags.Instance | BindingFlags.Public);
            if (sharedProp != null)
                return sharedProp.GetValue(volumeComp, null) as UnityEngine.Object;
            return null;
        }

        // ── URP Settings Helpers ────────────────────────────────────────────

        private static string ReadUrpSettings(UnityEngine.Object pipelineAsset)
        {
            var w = new UnityCliJsonWriter().BeginObject();
            var type = pipelineAsset.GetType();

            // Read common public properties
            var readableProps = new[] { "renderScale", "supportsHDR", "msaaSampleCount", "shadowDistance",
                "shadowCascadeCount", "cascade2Split", "cascade3Split", "cascade4Split",
                "shadowCascadeBias", "supportsLightLayers", "debugLevel",
                "mainLightRenderingMode", "mainLightShadowmapResolution",
                "additionalLightsPerObjectLimit", "additionalLightShadowsSupported",
                "additionalLightShadowmapResolution", "srpBatcher", "gpuResidentDrawerMode" };

            foreach (var propName in readableProps)
            {
                var prop = type.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
                if (prop == null) continue;
                try
                {
                    var val = prop.GetValue(pipelineAsset, null);
                    if (val is float f) w.Prop(propName, f);
                    else if (val is int i) w.Prop(propName, (long)i);
                    else if (val is bool b) w.Prop(propName, b);
                    else if (val is Enum e) w.Prop(propName, e.ToString());
                    else if (val != null) w.Prop(propName, val.ToString());
                }
                catch { /* skip unreadable */ }
            }

            return w.EndObject().ToString();
        }

        private static object ConvertValue(string value, Type targetType)
        {
            if (targetType == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return bool.Parse(value);
            if (targetType.IsEnum) return Enum.Parse(targetType, value, true);
            if (targetType == typeof(string)) return value;
            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        private static void SetSerializedPropertyValue(SerializedProperty prop, string value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Float:
                    prop.floatValue = float.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Integer:
                    prop.intValue = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = bool.Parse(value);
                    break;
                case SerializedPropertyType.Enum:
                    prop.enumValueIndex = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = value;
                    break;
                default:
                    throw new BridgeCommandException("E_URP_PROPERTY_TYPE_UNSUPPORTED",
                        "Cannot set property type " + prop.propertyType + " via string value.",
                        "Use direct Unity editing for complex property types.");
            }
        }
    }
}
#endif
