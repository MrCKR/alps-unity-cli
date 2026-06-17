#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterGraphicsCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "graphics.quality", "graphics", true, GraphicsQuality);
            RegisterCommand(registry, "graphics.settings", "graphics", true, GraphicsSettingsCommand);
            RegisterCommand(registry, "graphics.pipeline", "graphics", false, GraphicsPipeline);
            RegisterCommand(registry, "graphics.volume", "graphics", false, GraphicsVolume);
            RegisterCommand(registry, "graphics.screenshot", "graphics", true, GraphicsScreenshot);
        }

        private static string GraphicsQuality(BridgeRequest request)
        {
            var effects = new List<string>();
            var requestedQualityLevel = request.OptionalIntArg("qualityLevel");
            if (requestedQualityLevel.HasValue)
            {
                SetQualityLevel(requestedQualityLevel.Value, effects);
            }
            ApplyQualityInt(request, "vSyncCount", "vSyncCount", delegate(int value) { QualitySettings.vSyncCount = value; }, effects);
            ApplyQualityInt(request, "antiAliasing", "antiAliasing", delegate(int value) { QualitySettings.antiAliasing = value; }, effects);
            ApplyQualityFloat(request, "shadowDistance", "shadowDistance", delegate(float value) { QualitySettings.shadowDistance = value; }, effects);
            ApplyQualityInt(request, "masterTextureLimit", "masterTextureLimit", delegate(int value) { QualitySettings.masterTextureLimit = value; }, effects);
            ApplyQualityFloat(request, "lodBias", "lodBias", delegate(float value) { QualitySettings.lodBias = value; }, effects);
            ApplyQualityInt(request, "maximumLODLevel", "maximumLODLevel", delegate(int value) { QualitySettings.maximumLODLevel = value; }, effects);
            ApplyQualityEnum<AnisotropicFiltering>(request, "anisotropicFiltering", "anisotropicFiltering", delegate(AnisotropicFiltering value) { QualitySettings.anisotropicFiltering = value; }, effects);
            ApplyQualityEnum<ShadowQuality>(request, "shadows", "shadows", delegate(ShadowQuality value) { QualitySettings.shadows = value; }, effects);
            ApplyQualityEnum<ShadowResolution>(request, "shadowResolution", "shadowResolution", delegate(ShadowResolution value) { QualitySettings.shadowResolution = value; }, effects);
            ApplyQualityBool(request, "realtimeReflectionProbes", "realtimeReflectionProbes", delegate(bool value) { QualitySettings.realtimeReflectionProbes = value; }, effects);
            ApplyQualityBool(request, "softParticles", "softParticles", delegate(bool value) { QualitySettings.softParticles = value; }, effects);
            if (effects.Count > 0)
            {
                AssetDatabase.SaveAssets();
                WriteLog("graphics_quality_updated effects=" + effects.Count.ToString(CultureInfo.InvariantCulture));
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("graphics", GraphicsSummaryJson())
                .PropRaw("qualitySettings", QualitySettingsJson())
                .PropArray("plannedEffects", arr => { foreach (var effect in effects) arr.RawValue(effect); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsSettingsCommand(BridgeRequest request)
        {
            var effects = new List<string>();
            ApplyQualityInt(request, "antiAliasing", "antiAliasing", delegate(int value) { QualitySettings.antiAliasing = value; }, effects);
            ApplyQualityFloat(request, "shadowDistance", "shadowDistance", delegate(float value) { QualitySettings.shadowDistance = value; }, effects);
            ApplyQualityInt(request, "vSyncCount", "vSyncCount", delegate(int value) { QualitySettings.vSyncCount = value; }, effects);
            ApplyQualityInt(request, "masterTextureLimit", "masterTextureLimit", delegate(int value) { QualitySettings.masterTextureLimit = value; }, effects);
            ApplyQualityFloat(request, "lodBias", "lodBias", delegate(float value) { QualitySettings.lodBias = value; }, effects);
            ApplyQualityInt(request, "maximumLODLevel", "maximumLODLevel", delegate(int value) { QualitySettings.maximumLODLevel = value; }, effects);
            if (request.HasArg("renderPipeline"))
            {
                var value = request.StringArg("renderPipeline");
                if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "built-in", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(value))
                {
                    GraphicsSettings.defaultRenderPipeline = null;
                }
                else
                {
                    var pipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(NormalizeAssetPath(value));
                    if (pipeline == null)
                    {
                        return ErrorJson("E_RENDER_PIPELINE_NOT_FOUND", "RenderPipelineAsset not found: " + value, "Pass an Assets/*.asset RenderPipelineAsset path or use --render-pipeline built-in.");
                    }
                    GraphicsSettings.defaultRenderPipeline = pipeline;
                }
                effects.Add(EffectJson("graphics.settings", "renderPipeline", value));
            }
            if (request.HasArg("property"))
            {
                ApplyGenericGraphicsSetting(request, effects);
            }
            if (effects.Count > 0)
            {
                AssetDatabase.SaveAssets();
                WriteLog("graphics_settings_updated effects=" + effects.Count.ToString(CultureInfo.InvariantCulture));
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("graphics", GraphicsSummaryJson())
                .PropRaw("qualitySettings", QualitySettingsJson())
                .PropArray("plannedEffects", arr => { foreach (var effect in effects) arr.RawValue(effect); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsPipeline(BridgeRequest request)
        {
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("graphics", GraphicsSummaryJson())
                .PropRaw("qualitySettings", QualitySettingsJson())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsVolume(BridgeRequest request)
        {
            var target = request.StringArg("target");
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("volumes", GraphicsVolumesJson(target))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsScreenshot(BridgeRequest request)
        {
            var camera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : ResolveCamera(null);
            if (camera == null)
            {
                return ErrorJson("E_SCENE_VIEW_NOT_AVAILABLE", "No SceneView or Camera is available for graphics screenshot capture.", "Open a SceneView or create an active Camera in the scene.");
            }
            var capture = CaptureCamera(camera, request);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("diagnostics", GraphicsSummaryJson())
                .PropRaw("qualitySettings", QualitySettingsJson())
                .PropRaw("screenshot", ScreenshotDataJson(capture, "graphics", camera.name))
                .Prop("screenshotPath", capture.Path)
                .Prop("artifactPath", capture.Path)
                .PropArray("plannedEffects", arr => arr.RawValue(EffectJson("graphics.screenshot", "path", capture.Path)))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsVolumesJson(string target)
        {
            var components = new List<Component>();
            var volumeType = FindType("UnityEngine.Rendering.Volume");
            if (volumeType == null)
            {
                return "[]";
            }
            foreach (var component in Resources.FindObjectsOfTypeAll(volumeType))
            {
                var typed = component as Component;
                if (typed == null || typed.gameObject == null) continue;
                if (EditorUtility.IsPersistent(typed.gameObject)) continue;
                if (!string.IsNullOrEmpty(target) && !HierarchyPath(typed.gameObject).Equals(target, StringComparison.OrdinalIgnoreCase) && !typed.gameObject.name.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                components.Add(typed);
            }
            components.Sort(delegate(Component left, Component right) { return string.Compare(HierarchyPath(left.gameObject), HierarchyPath(right.gameObject), StringComparison.OrdinalIgnoreCase); });
            var w = new UnityCliJsonWriter().BeginArray();
            foreach (var component in components)
            {
                w.RawValue(GraphicsVolumeJson(component));
            }
            return w.EndArray().ToString();
        }

        private static string GraphicsVolumeJson(Component component)
        {
            var type = component.GetType();
            var enabledProperty = type.GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            var isGlobalField = type.GetField("isGlobal", BindingFlags.Instance | BindingFlags.Public);
            var priorityField = type.GetField("priority", BindingFlags.Instance | BindingFlags.Public);
            var weightField = type.GetField("weight", BindingFlags.Instance | BindingFlags.Public);
            var profile = MemberValue(type, component, "profile") as UnityEngine.Object;
            var sharedProfile = MemberValue(type, component, "sharedProfile") as UnityEngine.Object;
            var effectiveProfile = profile ?? sharedProfile;
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(component.gameObject))
                .Prop("enabled", enabledProperty == null || Convert.ToBoolean(enabledProperty.GetValue(component, null), CultureInfo.InvariantCulture))
                .Prop("isGlobal", isGlobalField != null && Convert.ToBoolean(isGlobalField.GetValue(component), CultureInfo.InvariantCulture))
                .Prop("priority", priorityField == null ? 0f : Convert.ToSingle(priorityField.GetValue(component), CultureInfo.InvariantCulture))
                .Prop("weight", weightField == null ? 0f : Convert.ToSingle(weightField.GetValue(component), CultureInfo.InvariantCulture))
                .PropRaw("profile", AssetReferenceJson(effectiveProfile))
                .EndObject().ToString();
        }

        private static string GraphicsSummaryJson()
        {
            var pipeline = GraphicsSettings.defaultRenderPipeline;
            var pipelinePath = pipeline == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(pipeline));
            return new UnityCliJsonWriter().BeginObject()
                .PropObject("renderPipeline", obj => obj
                    .Prop("active", RenderPipelineSummaryName())
                    .Prop("defaultAsset", pipeline == null ? null : pipeline.name)
                    .Prop("defaultAssetPath", pipelinePath))
                .Prop("currentQualityLevel", (long)QualitySettings.GetQualityLevel())
                .Prop("qualityLevelName", QualitySettings.names.Length > QualitySettings.GetQualityLevel() ? QualitySettings.names[QualitySettings.GetQualityLevel()] : null)
                .EndObject().ToString();
        }

        private static string RenderPipelineSummaryName()
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                return GraphicsSettings.currentRenderPipeline.GetType().Name;
            }
            return GraphicsSettings.defaultRenderPipeline != null ? GraphicsSettings.defaultRenderPipeline.GetType().Name : "Built-in";
        }

        private static string QualitySettingsJson()
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropArray("names", arr =>
                {
                    foreach (var name in QualitySettings.names)
                    {
                        arr.Value(name);
                    }
                })
                .Prop("currentLevel", (long)QualitySettings.GetQualityLevel())
                .Prop("vSyncCount", (long)QualitySettings.vSyncCount)
                .Prop("antiAliasing", (long)QualitySettings.antiAliasing)
                .Prop("shadowDistance", QualitySettings.shadowDistance)
                .Prop("masterTextureLimit", (long)QualitySettings.masterTextureLimit)
                .Prop("lodBias", QualitySettings.lodBias)
                .Prop("maximumLODLevel", (long)QualitySettings.maximumLODLevel)
                .Prop("anisotropicFiltering", QualitySettings.anisotropicFiltering.ToString())
                .Prop("shadows", QualitySettings.shadows.ToString())
                .Prop("shadowResolution", QualitySettings.shadowResolution.ToString())
                .Prop("realtimeReflectionProbes", QualitySettings.realtimeReflectionProbes)
                .Prop("softParticles", QualitySettings.softParticles)
                .EndObject().ToString();
        }

        private static string PhysicsSettingsJson()
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("mode", "3d+2d")
                .PropRaw("gravity", Vector3Json(Physics.gravity))
                .PropRaw("gravity2d", Vector2Json(Physics2D.gravity))
                .Prop("defaultContactOffset", Physics.defaultContactOffset)
                .Prop("bounceThreshold", Physics.bounceThreshold)
                .Prop("defaultSolverIterations", (long)Physics.defaultSolverIterations)
                .Prop("defaultSolverVelocityIterations", (long)Physics.defaultSolverVelocityIterations)
                .Prop("queriesHitTriggers", Physics.queriesHitTriggers)
                .Prop("queriesHitBackfaces", Physics.queriesHitBackfaces)
                .Prop("queriesStartInColliders", Physics2D.queriesStartInColliders)
                .Prop("autoSimulation", Physics.autoSimulation)
                .EndObject().ToString();
        }

        private static void SetQualityLevel(int level, List<string> effects)
        {
            if (level < 0 || level >= QualitySettings.names.Length)
            {
                throw new BridgeCommandException("E_QUALITY_LEVEL_INVALID", "Quality level is out of range: " + level.ToString(CultureInfo.InvariantCulture), "Run unity_cli graphics quality to inspect available quality level names and indexes.");
            }
            QualitySettings.SetQualityLevel(level, true);
            effects.Add(EffectJson("graphics.quality", "qualityLevel", level.ToString(CultureInfo.InvariantCulture)));
        }

        private static void ApplyGenericGraphicsSetting(BridgeRequest request, List<string> effects)
        {
            var property = request.StringArg("property");
            var value = request.StringArg("value");
            if (property == "vSyncCount")
            {
                QualitySettings.vSyncCount = ParseInt(value, property);
            }
            else if (property == "antiAliasing")
            {
                QualitySettings.antiAliasing = ParseInt(value, property);
            }
            else if (property == "shadowDistance")
            {
                QualitySettings.shadowDistance = ParseFloat(value, property);
            }
            else
            {
                throw new BridgeCommandException("E_GRAPHICS_SETTING_UNSUPPORTED", "Unsupported graphics setting: " + property, "Use named flags such as --anti-aliasing, --shadow-distance or --render-pipeline.");
            }
            effects.Add(EffectJson("graphics.settings", property, value));
        }

        private static void ApplyGenericPhysicsSetting(BridgeRequest request, List<string> effects)
        {
            var property = request.StringArg("property");
            var value = request.StringArg("value");
            if (property == "gravityY")
            {
                Physics.gravity = new Vector3(Physics.gravity.x, ParseFloat(value, property), Physics.gravity.z);
            }
            else if (property == "defaultContactOffset")
            {
                Physics.defaultContactOffset = ParseFloat(value, property);
            }
            else if (property == "queriesHitTriggers")
            {
                Physics.queriesHitTriggers = ParseBool(value, property);
            }
            else
            {
                throw new BridgeCommandException("E_PHYSICS_SETTING_UNSUPPORTED", "Unsupported physics setting: " + property, "Use named flags such as --gravity-y, --default-contact-offset or --queries-hit-triggers.");
            }
            effects.Add(EffectJson("physics.settings", property, value));
        }

        private static void ApplyQualityInt(BridgeRequest request, string arg, string property, Action<int> setter, List<string> effects)
        {
            var value = request.OptionalIntArg(arg);
            if (!value.HasValue) return;
            setter(value.Value);
            effects.Add(EffectJson("graphics.quality", property, value.Value.ToString(CultureInfo.InvariantCulture)));
        }

        private static void ApplyQualityFloat(BridgeRequest request, string arg, string property, Action<float> setter, List<string> effects)
        {
            var value = request.OptionalFloatArg(arg);
            if (!value.HasValue) return;
            setter(value.Value);
            effects.Add(EffectJson("graphics.quality", property, Number(value.Value)));
        }

        private static void ApplyQualityBool(BridgeRequest request, string arg, string property, Action<bool> setter, List<string> effects)
        {
            var value = request.OptionalBoolArg(arg);
            if (!value.HasValue) return;
            setter(value.Value);
            effects.Add(EffectJson("graphics.quality", property, Bool(value.Value)));
        }

        private static void ApplyQualityEnum<T>(BridgeRequest request, string arg, string property, Action<T> setter, List<string> effects) where T : struct
        {
            if (!request.HasArg(arg)) return;
            T parsed;
            if (!Enum.TryParse<T>(request.StringArg(arg), true, out parsed))
            {
                throw new BridgeCommandException("E_GRAPHICS_SETTING_INVALID", "Invalid " + property + " value: " + request.StringArg(arg), "Use a Unity enum value for " + property + ".");
            }
            setter(parsed);
            effects.Add(EffectJson("graphics.quality", property, parsed.ToString()));
        }
    }
}
#endif
