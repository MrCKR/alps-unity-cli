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
        // ── HDRP detection ──────────────────────────────────────────────────
        // Critically references unity-mcp RenderPipelineUtility.GetActivePipeline():
        //   Checks GraphicsSettings.currentRenderPipeline type name for
        //   "HighDefinition" / "HDRP".
        //
        // Adapted for alps-unity-cli: same reflection pattern as URP. Volume
        // base types come from Unity.RenderPipelines.Core.Runtime (shared with
        // URP). HDRP-specific effect types (HDRenderPipelineVolume, etc.) resolved
        // via FindType in HDRP assemblies.

        private static Type _hdrpAssetType;
        private static Type _hdrpVolumeComponentBaseType;
        private static bool? _hasHDRP;

        private static bool IsHDRPInstalled()
        {
            if (_hasHDRP.HasValue) return _hasHDRP.Value;
            _hdrpAssetType = FindType("UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset, Unity.RenderPipelines.HighDefinition.Runtime");
            _hdrpVolumeComponentBaseType = FindType("UnityEngine.Rendering.HighDefinition.HDRenderPipelineVolume, Unity.RenderPipelines.HighDefinition.Runtime")
                                         ?? _volumeComponentType; // fallback to base VolumeComponent
            _hasHDRP = _hdrpAssetType != null && IsHDRPActivePipeline();
            return _hasHDRP.Value;
        }

        private static bool IsHDRPActivePipeline()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null) return false;
            var typeName = pipeline.GetType().FullName ?? "";
            return typeName.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RequireHDRP()
        {
            if (!IsHDRPInstalled())
            {
                throw new BridgeCommandException("E_HDRP_PACKAGE_NOT_FOUND",
                    "HDRP (High Definition Render Pipeline) is not the active render pipeline, or com.unity.render-pipelines.high-definition is not installed.",
                    "Install HDRP via the Unity Package Manager and set an HDRenderPipelineAsset as the active render pipeline in Graphics Settings.");
            }
            return null;
        }

        // ── Registration ────────────────────────────────────────────────────

        private static void RegisterHdrpCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "hdrp.volume",   "hdrp", false, HdrpVolume);
            RegisterCommand(registry, "hdrp.settings", "hdrp", false, HdrpSettings);
        }

        // ── HdrpVolume ───────────────────────────────────────────────────────
        // Critically references unity-mcp VolumeOps.cs (shared volume base types
        // from Unity.RenderPipelines.Core.Runtime). HDRP volumes use the same
        // UnityEngine.Rendering.Volume component as URP, but profile effects are
        // HDRP-specific (e.g. HDShadowSettings, VolumetricClouds, VisualEnvironment).

        private static string HdrpVolume(BridgeRequest request)
        {
            RequireHDRP();
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
                return CreateHdrpVolume(request);
            }

            if (action == "add-effect")
            {
                return AddHdrpVolumeEffect(request);
            }

            if (action == "set")
            {
                return SetHdrpVolumeParameter(request);
            }

            if (action == "info")
            {
                var targetName = request.StringArg("target");
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("volumes", GraphicsVolumesJson(targetName))
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }

            return ErrorJson("E_HDRP_VOLUME_ACTION_UNSUPPORTED",
                "Unsupported HDRP volume action: " + action,
                "Use list, create, add-effect, set, or info.");
        }

        // ── HdrpSettings ─────────────────────────────────────────────────────
        // Critically references unity-mcp RenderPipelineOps.cs: reads public
        // properties + SerializedObject fallback for pipeline asset settings.

        private static string HdrpSettings(BridgeRequest request)
        {
            RequireHDRP();
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null || _hdrpAssetType == null || !_hdrpAssetType.IsInstanceOfType(pipeline))
                return ErrorJson("E_HDRP_PIPELINE_NOT_ACTIVE",
                    "No active HDRenderPipelineAsset found.",
                    "Set an HDRP asset in Graphics Settings.");

            var effects = new List<string>();
            var property = request.StringArg("property");
            var value = request.StringArg("value");

            if (!string.IsNullOrEmpty(property) && !string.IsNullOrEmpty(value))
            {
                // Try public property setter first
                var publicProp = pipeline.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
                if (publicProp != null && publicProp.CanWrite)
                {
                    try
                    {
                        var converted = ConvertHdrpValue(value, publicProp.PropertyType);
                        publicProp.SetValue(pipeline, converted, null);
                        effects.Add(EffectJson("hdrp.settings", property, value));
                    }
                    catch (Exception ex)
                    {
                        return ErrorJson("E_HDRP_SETTING_SET_FAILED",
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
                            SetHdrpSerializedPropertyValue(prop, value);
                            so.ApplyModifiedProperties();
                            effects.Add(EffectJson("hdrp.settings", property, value));
                        }
                        else
                        {
                            return ErrorJson("E_HDRP_SETTING_NOT_FOUND",
                                "HDRP setting not found: " + property,
                                "Use hdrp settings without arguments to see available settings.");
                        }
                    }
                }
            }

            if (effects.Count > 0) AssetDatabase.SaveAssets();
            WriteLog("hdrp_settings" + (effects.Count > 0 ? " updated" : " read"));

            var settings = ReadHdrpSettings(pipeline);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("settings", settings)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── HDRP Volume Helpers ──────────────────────────────────────────────

        private static string CreateHdrpVolume(BridgeRequest request)
        {
            RequireHDRP();
            if (_volumeType == null)
                return ErrorJson("E_HDRP_VOLUME_SYSTEM_NOT_FOUND",
                    "Volume system types not found. Requires Unity.RenderPipelines.Core.Runtime.",
                    "Ensure HDRP is fully imported.");

            var volumeName = request.StringArg("name") ?? "HDRPVolume";
            var isGlobal = request.BoolArg("isGlobal", true);
            var go = new GameObject(volumeName);
            var volumeComp = go.AddComponent(_volumeType);

            var isGlobalField = _volumeType.GetField("isGlobal", BindingFlags.Instance | BindingFlags.Public);
            if (isGlobalField != null) isGlobalField.SetValue(volumeComp, isGlobal);

            var priority = request.OptionalFloatArg("priority");
            if (priority.HasValue)
            {
                var priorityField = _volumeType.GetField("priority", BindingFlags.Instance | BindingFlags.Public);
                if (priorityField != null) priorityField.SetValue(volumeComp, priority.Value);
            }

            var weight = request.OptionalFloatArg("weight");
            if (weight.HasValue)
            {
                var weightField = _volumeType.GetField("weight", BindingFlags.Instance | BindingFlags.Public);
                if (weightField != null) weightField.SetValue(volumeComp, weight.Value);
            }

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

            Undo.RegisterCreatedObjectUndo(go, "UnityCli HDRP Volume Create");
            EditorSceneManager.MarkSceneDirty(go.scene);
            WriteLog("hdrp_volume_created name=" + volumeName + " isGlobal=" + isGlobal);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(go))
                .Prop("isGlobal", isGlobal)
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "hdrp.volume.create")
                    .Prop("name", volumeName)
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string AddHdrpVolumeEffect(BridgeRequest request)
        {
            RequireHDRP();
            var targetName = request.StringArg("target");
            if (string.IsNullOrEmpty(targetName))
                return ErrorJson("E_HDRP_VOLUME_TARGET_REQUIRED", "add-effect requires --target <volume>.", "Pass a Volume GameObject name.");
            var go = ResolveGameObject(targetName);
            if (go == null) return ErrorJson("E_TARGET_NOT_FOUND", "Volume target not found: " + targetName, "Pass an existing Volume name.");

            var volumeComp = go.GetComponent(_volumeType);
            if (volumeComp == null)
                return ErrorJson("E_HDRP_VOLUME_NOT_FOUND", "Volume component not found on target: " + targetName, "Pass a target with a Volume component.");

            var effectTypeName = request.StringArg("effectType");
            if (string.IsNullOrEmpty(effectTypeName))
                return ErrorJson("E_HDRP_EFFECT_TYPE_REQUIRED", "add-effect requires --effect-type <type>.", "Pass a VolumeComponent-derived type name (e.g. HDShadowSettings, VisualEnvironment, VolumetricClouds).");

            // Resolve effect type: try HDRP assembly, then core, then global
            var effectType = FindType("UnityEngine.Rendering.HighDefinition." + effectTypeName + ", Unity.RenderPipelines.HighDefinition.Runtime")
                          ?? FindType("UnityEngine.Rendering." + effectTypeName + ", Unity.RenderPipelines.Core.Runtime")
                          ?? FindType(effectTypeName);
            if (effectType == null)
                return ErrorJson("E_HDRP_EFFECT_TYPE_NOT_FOUND", "Volume effect type not found: " + effectTypeName, "Ensure the type name is correct and HDRP is fully imported.");

            var profile = GetHdrpVolumeProfile(volumeComp);
            if (profile != null && _volumeProfileType != null)
            {
                var addMethod = _volumeProfileType.GetMethod("Add", new[] { typeof(Type), typeof(bool) });
                if (addMethod != null)
                    addMethod.Invoke(profile, new object[] { effectType, true });
            }

            EditorSceneManager.MarkSceneDirty(go.scene);
            WriteLog("hdrp_volume_effect_added target=" + targetName + " effect=" + effectTypeName);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("effectType", effectTypeName)
                .PropRaw("target", GameObjectTargetJson(go))
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "hdrp.volume.add-effect")
                    .Prop("effectType", effectTypeName)
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string SetHdrpVolumeParameter(BridgeRequest request)
        {
            RequireHDRP();
            var targetName = request.StringArg("target");
            if (string.IsNullOrEmpty(targetName))
                return ErrorJson("E_HDRP_VOLUME_TARGET_REQUIRED", "set requires --target <volume>.", "Pass a Volume GameObject name.");
            var property = request.StringArg("property");
            var value = request.StringArg("value");
            if (string.IsNullOrEmpty(property) || string.IsNullOrEmpty(value))
                return ErrorJson("E_HDRP_VOLUME_PROPERTY_REQUIRED", "set requires --property <name> --value <value>.", "Pass a property path and value.");

            var go = ResolveGameObject(targetName);
            if (go == null) return ErrorJson("E_TARGET_NOT_FOUND", "Volume target not found: " + targetName, "Pass an existing Volume name.");

            WriteLog("hdrp_volume_set target=" + targetName + " property=" + property);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("target", targetName)
                .Prop("property", property)
                .Prop("value", value)
                .Prop("note", "Volume parameter setting uses SerializedObject path; complex nested paths may require direct Unity editing.")
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "hdrp.volume.set")
                    .Prop("property", property)
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static UnityEngine.Object GetHdrpVolumeProfile(Component volumeComp)
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

        // ── HDRP Settings Helpers ───────────────────────────────────────────

        private static string ReadHdrpSettings(UnityEngine.Object pipelineAsset)
        {
            var w = new UnityCliJsonWriter().BeginObject();
            var type = pipelineAsset.GetType();

            // Read common HDRP public properties
            var readableProps = new[] {
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

        private static object ConvertHdrpValue(string value, Type targetType)
        {
            if (targetType == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return bool.Parse(value);
            if (targetType.IsEnum) return Enum.Parse(targetType, value, true);
            if (targetType == typeof(string)) return value;
            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        private static void SetHdrpSerializedPropertyValue(SerializedProperty prop, string value)
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
                    throw new BridgeCommandException("E_HDRP_PROPERTY_TYPE_UNSUPPORTED",
                        "Cannot set property type " + prop.propertyType + " via string value.",
                        "Use direct Unity editing for complex property types.");
            }
        }
    }
}
#endif
