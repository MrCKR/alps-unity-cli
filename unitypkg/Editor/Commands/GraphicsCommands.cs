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
            RegisterCommand(registry, "graphics.bake-start", "graphics", true, GraphicsBakeStart);
            RegisterCommand(registry, "graphics.bake-cancel", "graphics", true, GraphicsBakeCancel);
            RegisterCommand(registry, "graphics.bake-status", "graphics", false, GraphicsBakeStatus);
            RegisterCommand(registry, "graphics.bake-clear", "graphics", true, GraphicsBakeClear);
            RegisterCommand(registry, "graphics.bake-reflection-probe", "graphics", true, GraphicsBakeReflectionProbe);
            RegisterCommand(registry, "graphics.bake-settings", "graphics", false, GraphicsBakeSettings);
            RegisterCommand(registry, "graphics.light-probe-group", "graphics", true, GraphicsLightProbeGroup);
            RegisterCommand(registry, "graphics.reflection-probe", "graphics", true, GraphicsReflectionProbe);
            RegisterCommand(registry, "graphics.probe-positions", "graphics", true, GraphicsProbePositions);
            RegisterCommand(registry, "graphics.stats", "graphics", false, GraphicsStats);
            RegisterCommand(registry, "graphics.stats-counters", "graphics", false, GraphicsStatsCounters);
            RegisterCommand(registry, "graphics.stats-memory", "graphics", false, GraphicsStatsMemory);
            RegisterCommand(registry, "graphics.scene-debug", "graphics", true, GraphicsSceneDebug);
            RegisterCommand(registry, "graphics.skybox-get", "graphics", false, GraphicsSkyboxGet);
            RegisterCommand(registry, "graphics.skybox-set-material", "graphics", true, GraphicsSkyboxSetMaterial);
            RegisterCommand(registry, "graphics.skybox-set-properties", "graphics", true, GraphicsSkyboxSetProperties);
            RegisterCommand(registry, "graphics.skybox-set-ambient", "graphics", true, GraphicsSkyboxSetAmbient);
            RegisterCommand(registry, "graphics.skybox-set-fog", "graphics", true, GraphicsSkyboxSetFog);
            RegisterCommand(registry, "graphics.skybox-set-reflection", "graphics", true, GraphicsSkyboxSetReflection);
            RegisterCommand(registry, "graphics.skybox-set-sun", "graphics", true, GraphicsSkyboxSetSun);
            RegisterCommand(registry, "graphics.pipeline-settings", "graphics", false, GraphicsPipelineSettings);
            RegisterCommand(registry, "graphics.pipeline-quality", "graphics", true, GraphicsPipelineQuality);
            RegisterCommand(registry, "graphics.pipeline-info", "graphics", false, GraphicsPipelineInfo);
        }

        private static string GraphicsQuality(BridgeRequest request)
        {
            var effects = new List<string>();
            var requestedQualityLevel = request.RawJsonArg("qualityLevel");
            if (!string.IsNullOrEmpty(requestedQualityLevel))
            {
                SetQualityLevel(requestedQualityLevel, effects);
            }
            ApplyQualityInt(request, "vSyncCount", "vSyncCount", delegate(int value) { QualitySettings.vSyncCount = value; }, effects);
            ApplyQualityInt(request, "antiAliasing", "antiAliasing", delegate(int value) { QualitySettings.antiAliasing = value; }, effects);
            ApplyQualityFloat(request, "shadowDistance", "shadowDistance", delegate(float value) { QualitySettings.shadowDistance = value; }, effects);
            ApplyQualityInt(request, "masterTextureLimit", "masterTextureLimit", delegate(int value) { QualitySettings.globalTextureMipmapLimit = value; }, effects);
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
            ApplyQualityInt(request, "masterTextureLimit", "masterTextureLimit", delegate(int value) { QualitySettings.globalTextureMipmapLimit = value; }, effects);
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
                .Prop("masterTextureLimit", (long)QualitySettings.globalTextureMipmapLimit)
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

        private static void SetQualityLevel(string rawLevel, List<string> effects)
        {
            var level = ResolveQualityLevel(rawLevel);
            QualitySettings.SetQualityLevel(level, true);
            effects.Add(EffectJson("graphics.quality", "qualityLevel", level.ToString(CultureInfo.InvariantCulture)));
        }

        private static int ResolveQualityLevel(string rawLevel)
        {
            var value = JsonValueToString(rawLevel);
            int level;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out level))
            {
                if (level >= 0 && level < QualitySettings.names.Length) return level;
            }
            for (var index = 0; index < QualitySettings.names.Length; index++)
            {
                if (string.Equals(QualitySettings.names[index], value, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
            throw new BridgeCommandException("E_QUALITY_LEVEL_INVALID", "Quality level is invalid: " + value, "Run tools run graphics.quality --input '{}' to inspect available quality level names and indexes.");
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

        // ===== Light Baking =====

        private static string GraphicsBakeStart(BridgeRequest request)
        {
            var baking = Lightmapping.isRunning;
            if (baking)
            {
                return ErrorJson("E_BAKE_ALREADY_RUNNING", "Lightmapping bake is already running.", "Run tools run graphics.bake-status --input '{}' to inspect progress or tools run graphics.bake-cancel --input '{}' to stop it.");
            }
            Lightmapping.BakeAsync();
            WriteLog("graphics_bake_started");
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("baking", Lightmapping.isRunning)
                .PropArray("plannedEffects", arr => arr.RawValue(EffectJson("graphics.bake-start", "bake", "async")))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsBakeCancel(BridgeRequest request)
        {
            var wasRunning = Lightmapping.isRunning;
            if (wasRunning)
            {
                Lightmapping.Cancel();
            }
            WriteLog("graphics_bake_cancelled wasRunning=" + Bool(wasRunning));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("baking", Lightmapping.isRunning)
                .Prop("cancelled", wasRunning)
                .PropArray("plannedEffects", arr => arr.RawValue(EffectJson("graphics.bake-cancel", "bake", "cancelled")))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsBakeStatus(BridgeRequest request)
        {
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("baking", Lightmapping.isRunning)
#if UNITY_2023_2_OR_NEWER
                .Prop("progress", Lightmapping.bakeProgress)
#else
                .Prop("progress", Lightmapping.buildProgress)
#endif
                .PropRaw("mode", BakeModeJson())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string BakeModeJson()
        {
#if UNITY_2023_2_OR_NEWER
            var mode = Lightmapping.lightingMode;
            string modeStr;
            switch (mode)
            {
                case LightingMode.Baked: modeStr = "baked"; break;
                case LightingMode.Mixed: modeStr = "mixed"; break;
                case LightingMode.Realtime: modeStr = "realtime"; break;
                default: modeStr = mode.ToString(); break;
            }
#else
            // Unity 2022.3: Lightmapping.lightingMode does not exist.
            // Derive mode from the active scene's lighting settings.
            var modeStr = "unknown";
            var activeScene = SceneManager.GetActiveScene();
            var ls = Lightmapping.GetLightingSettingsForScene(activeScene);
            if (ls != null)
            {
                modeStr = ls.mixedBakeMode.ToString();
            }
#endif
            return new UnityCliJsonWriter().BeginObject()
                .Prop("lightingMode", modeStr)
                .Prop("giWorkflowMode", (long)Lightmapping.giWorkflowMode)
                .EndObject().ToString();
        }

        private static string GraphicsBakeClear(BridgeRequest request)
        {
            var clearedBaked = request.OptionalBoolArg("bakedData") ?? true;
            var clearedRealtime = request.OptionalBoolArg("realtimeData") ?? true;
            if (clearedBaked)
            {
                Lightmapping.ClearLightingDataAsset();
            }
            if (clearedRealtime)
            {
                Lightmapping.Clear();
            }
            AssetDatabase.SaveAssets();
            WriteLog("graphics_bake_cleared bakedData=" + Bool(clearedBaked) + " realtimeData=" + Bool(clearedRealtime));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("clearedBakedData", clearedBaked)
                .Prop("clearedRealtimeData", clearedRealtime)
                .PropArray("plannedEffects", arr => arr.RawValue(EffectJson("graphics.bake-clear", "lightingData", "cleared")))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsBakeReflectionProbe(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var probe = ResolveReflectionProbe(target);
            if (probe == null)
            {
                return ErrorJson("E_REFLECTION_PROBE_NOT_FOUND", "ReflectionProbe not found: " + target, "Run tools run graphics.reflection-probe --input '{}' to inspect probes or pass a GameObject name / hierarchy path with a ReflectionProbe component.");
            }
            var probePath = request.StringArg("path");
            if (string.IsNullOrEmpty(probePath))
            {
                probePath = "Assets/" + probe.gameObject.name + "_reflection.exr";
            }
            var baked = Lightmapping.BakeReflectionProbe(probe, probePath);
            WriteLog("graphics_bake_reflection_probe target=" + HierarchyPath(probe.gameObject) + " baked=" + Bool(baked));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("probe", ReflectionProbeJson(probe))
                .Prop("baked", baked)
                .PropArray("plannedEffects", arr => arr.RawValue(EffectJson("graphics.bake-reflection-probe", "probe", HierarchyPath(probe.gameObject))))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsBakeSettings(BridgeRequest request)
        {
            var scene = ResolveLightingScene(request);
            var lightingSettings = Lightmapping.GetLightingSettingsForScene(scene);
            var effects = new List<string>();
            var changed = false;
            var hasWriteArgs = HasBakeSettingsWriteArgs(request);
            if (lightingSettings == null && hasWriteArgs)
            {
                lightingSettings = new LightingSettings();
                changed = true;
                effects.Add(EffectJson("graphics.bake-settings", "lightingSettings", "created"));
            }
            if (lightingSettings == null)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("settings", LightingSettingsJson(lightingSettings))
                    .Prop("scene", scene.path)
                    .PropArray("plannedEffects", arr => { })
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }
            if (request.HasArg("lightingMode"))
            {
                var value = request.StringArg("lightingMode");
#if UNITY_2023_2_OR_NEWER
                if (Enum.TryParse<LightingMode>(value, true, out var parsed))
                {
                    lightingSettings.lightingMode = parsed;
                    changed = true;
                    effects.Add(EffectJson("graphics.bake-settings", "lightingMode", value));
                }
#else
                if (Enum.TryParse<MixedLightingMode>(value, true, out var parsed))
                {
                    lightingSettings.mixedBakeMode = parsed;
                    changed = true;
                    effects.Add(EffectJson("graphics.bake-settings", "lightingMode", value));
                }
#endif
                else
                {
                    throw new BridgeCommandException("E_BAKE_SETTING_INVALID", "Invalid lightingMode: " + value, "Use baked, mixed, or realtime.");
                }
            }
            if (request.HasArg("bakedGI"))
            {
                lightingSettings.bakedGI = request.OptionalBoolArg("bakedGI").Value;
                changed = true;
                effects.Add(EffectJson("graphics.bake-settings", "bakedGI", Bool(lightingSettings.bakedGI)));
            }
            if (request.HasArg("realtimeGI"))
            {
                lightingSettings.realtimeGI = request.OptionalBoolArg("realtimeGI").Value;
                changed = true;
                effects.Add(EffectJson("graphics.bake-settings", "realtimeGI", Bool(lightingSettings.realtimeGI)));
            }
            if (request.HasArg("directSamples"))
            {
#if UNITY_2023_2_OR_NEWER
                lightingSettings.directSamples = request.OptionalIntArg("directSamples").Value;
#else
                lightingSettings.directSampleCount = request.OptionalIntArg("directSamples").Value;
#endif
                changed = true;
            }
            if (request.HasArg("indirectSamples"))
            {
#if UNITY_2023_2_OR_NEWER
                lightingSettings.indirectSamples = request.OptionalIntArg("indirectSamples").Value;
#else
                lightingSettings.indirectSampleCount = request.OptionalIntArg("indirectSamples").Value;
#endif
                changed = true;
            }
            if (request.HasArg("environmentSamples"))
            {
#if UNITY_2023_2_OR_NEWER
                lightingSettings.environmentSamples = request.OptionalIntArg("environmentSamples").Value;
#else
                lightingSettings.environmentSampleCount = request.OptionalIntArg("environmentSamples").Value;
#endif
                changed = true;
            }
            if (request.HasArg("bounces"))
            {
#if UNITY_2023_2_OR_NEWER
                lightingSettings.bounces = request.OptionalIntArg("bounces").Value;
#else
                lightingSettings.maxBounces = request.OptionalIntArg("bounces").Value;
#endif
                changed = true;
            }
            if (request.HasArg("filtering"))
            {
                var value = request.StringArg("filtering");
                if (Enum.TryParse<LightingSettings.FilterType>(value, true, out var parsed))
                {
#if UNITY_2023_2_OR_NEWER
                    lightingSettings.filtering = parsed;
#else
                    lightingSettings.filterTypeDirect = parsed;
                    lightingSettings.filterTypeIndirect = parsed;
                    lightingSettings.filterTypeAO = parsed;
#endif
                    changed = true;
                    effects.Add(EffectJson("graphics.bake-settings", "filtering", value));
                }
            }
            if (changed)
            {
                Lightmapping.SetLightingSettingsForScene(scene, lightingSettings);
                EditorSceneManager.MarkSceneDirty(scene);
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("settings", LightingSettingsJson(lightingSettings))
                .Prop("scene", scene.path)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        /// <summary>判断 bake-settings 是否包含会写入 LightingSettings 的字段。</summary>
        private static bool HasBakeSettingsWriteArgs(BridgeRequest request)
        {
            return request.HasArg("lightingMode")
                || request.HasArg("bakedGI")
                || request.HasArg("realtimeGI")
                || request.HasArg("directSamples")
                || request.HasArg("indirectSamples")
                || request.HasArg("environmentSamples")
                || request.HasArg("bounces")
                || request.HasArg("filtering");
        }

        private static Scene ResolveLightingScene(BridgeRequest request)
        {
            var path = request.StringArg("scene");
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (string.IsNullOrEmpty(path) || scene.path == path || scene.name == path)
                {
                    return scene;
                }
            }
            return SceneManager.GetActiveScene();
        }

        private static string LightingSettingsJson(LightingSettings settings)
        {
            if (settings == null)
            {
                return new UnityCliJsonWriter().BeginObject().EndObject().ToString();
            }
#if UNITY_2023_2_OR_NEWER
            return new UnityCliJsonWriter().BeginObject()
                .Prop("lightingMode", settings.lightingMode.ToString())
                .Prop("bakedGI", settings.bakedGI)
                .Prop("realtimeGI", settings.realtimeGI)
                .Prop("directSamples", (long)settings.directSamples)
                .Prop("indirectSamples", (long)settings.indirectSamples)
                .Prop("environmentSamples", (long)settings.environmentSamples)
                .Prop("bounces", (long)settings.bounces)
                .Prop("filtering", settings.filtering.ToString())
                .Prop("indirectResolution", (long)settings.indirectResolution)
                .EndObject().ToString();
#else
            return new UnityCliJsonWriter().BeginObject()
                .Prop("lightingMode", settings.mixedBakeMode.ToString())
                .Prop("bakedGI", settings.bakedGI)
                .Prop("realtimeGI", settings.realtimeGI)
                .Prop("directSamples", (long)settings.directSampleCount)
                .Prop("indirectSamples", (long)settings.indirectSampleCount)
                .Prop("environmentSamples", (long)settings.environmentSampleCount)
                .Prop("bounces", (long)settings.maxBounces)
                .Prop("filtering", settings.filterTypeDirect.ToString())
                .Prop("indirectResolution", (long)settings.indirectResolution)
                .EndObject().ToString();
#endif
        }

        private static string GraphicsLightProbeGroup(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var gameObject = string.IsNullOrEmpty(target) ? CreateGameObject("LightProbeGroup", null) : ResolveUniqueTarget(target, "graphics.light-probe-group requires --target <go> or create a new one with --create.");
            var probe = gameObject.GetComponent<LightProbeGroup>();
            var effects = new List<string>();
            if (probe == null && request.OptionalBoolArg("create") == true)
            {
                probe = gameObject.AddComponent<LightProbeGroup>();
                effects.Add(EffectJson("graphics.light-probe-group", "component", "added"));
            }
            if (probe == null)
            {
                return ErrorJson("E_LIGHT_PROBE_GROUP_NOT_FOUND", "LightProbeGroup component not found on target: " + HierarchyPath(gameObject), "Pass --create to add one or choose a target with LightProbeGroup.");
            }
            var positionsArg = request.StringArg("positions");
            if (!string.IsNullOrEmpty(positionsArg))
            {
                var positions = ParseVector3Array(positionsArg);
                probe.probePositions = positions;
                EditorUtility.SetDirty(probe);
                effects.Add(EffectJson("graphics.light-probe-group", "probePositions", positions.Length.ToString(CultureInfo.InvariantCulture)));
            }
            AssetDatabase.SaveAssets();
            WriteLog("graphics_light_probe_group target=" + HierarchyPath(gameObject) + " effects=" + effects.Count.ToString(CultureInfo.InvariantCulture));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .Prop("probeCount", (long)(probe.probePositions == null ? 0 : probe.probePositions.Length))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsReflectionProbe(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var gameObject = string.IsNullOrEmpty(target) ? CreateGameObject("ReflectionProbe", null) : ResolveUniqueTarget(target, "graphics.reflection-probe requires --target <go> or create a new one with --create.");
            var probe = gameObject.GetComponent<ReflectionProbe>();
            var effects = new List<string>();
            if (probe == null && request.OptionalBoolArg("create") == true)
            {
                probe = gameObject.AddComponent<ReflectionProbe>();
                effects.Add(EffectJson("graphics.reflection-probe", "component", "added"));
            }
            if (probe == null)
            {
                return ErrorJson("E_REFLECTION_PROBE_NOT_FOUND", "ReflectionProbe component not found on target: " + HierarchyPath(gameObject), "Pass --create to add one or choose a target with ReflectionProbe.");
            }
            if (request.HasArg("mode"))
            {
                var value = request.StringArg("mode");
                if (Enum.TryParse<ReflectionProbeMode>(value, true, out var parsed))
                {
                    probe.mode = parsed;
                    effects.Add(EffectJson("graphics.reflection-probe", "mode", value));
                }
            }
            if (request.HasArg("resolution"))
            {
                probe.resolution = request.OptionalIntArg("resolution").Value;
                effects.Add(EffectJson("graphics.reflection-probe", "resolution", probe.resolution.ToString(CultureInfo.InvariantCulture)));
            }
            if (request.HasArg("bounces"))
            {
                // ReflectionProbe.bounces only exists in Unity 2023.2+.
                // In Unity 2022.3, reflection bounces are set globally via RenderSettings.reflectionBounces.
#if UNITY_2023_2_OR_NEWER
                probe.bounces = request.OptionalIntArg("bounces").Value;
                effects.Add(EffectJson("graphics.reflection-probe", "bounces", probe.bounces.ToString(CultureInfo.InvariantCulture)));
#else
                RenderSettings.reflectionBounces = request.OptionalIntArg("bounces").Value;
                effects.Add(EffectJson("graphics.reflection-probe", "bounces", RenderSettings.reflectionBounces.ToString(CultureInfo.InvariantCulture)));
#endif
            }
            if (request.HasArg("hdr"))
            {
                probe.hdr = request.OptionalBoolArg("hdr").Value;
                effects.Add(EffectJson("graphics.reflection-probe", "hdr", Bool(probe.hdr)));
            }
            if (request.HasArg("box-size"))
            {
                probe.size = ParseVector3(request.StringArg("box-size"));
                effects.Add(EffectJson("graphics.reflection-probe", "boxSize", request.StringArg("box-size")));
            }
            if (request.HasArg("box-offset"))
            {
                probe.center = ParseVector3(request.StringArg("box-offset"));
                effects.Add(EffectJson("graphics.reflection-probe", "boxOffset", request.StringArg("box-offset")));
            }
            EditorUtility.SetDirty(probe);
            AssetDatabase.SaveAssets();
            WriteLog("graphics_reflection_probe target=" + HierarchyPath(gameObject) + " effects=" + effects.Count.ToString(CultureInfo.InvariantCulture));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropRaw("probe", ReflectionProbeJson(probe))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ReflectionProbeJson(ReflectionProbe probe)
        {
#if UNITY_2023_2_OR_NEWER
            return new UnityCliJsonWriter().BeginObject()
                .Prop("mode", probe.mode.ToString())
                .Prop("resolution", (long)probe.resolution)
                .Prop("bounces", (long)probe.bounces)
                .Prop("hdr", probe.hdr)
                .Prop("importance", (long)probe.importance)
                .PropRaw("size", Vector3Json(probe.size))
                .PropRaw("center", Vector3Json(probe.center))
                .EndObject().ToString();
#else
            return new UnityCliJsonWriter().BeginObject()
                .Prop("mode", probe.mode.ToString())
                .Prop("resolution", (long)probe.resolution)
                .Prop("bounces", (long)RenderSettings.reflectionBounces)
                .Prop("hdr", probe.hdr)
                .Prop("importance", (long)probe.importance)
                .PropRaw("size", Vector3Json(probe.size))
                .PropRaw("center", Vector3Json(probe.center))
                .EndObject().ToString();
#endif
        }

        private static ReflectionProbe ResolveReflectionProbe(string target)
        {
            var probes = UnityEngine.Object.FindObjectsOfType<ReflectionProbe>();
            if (string.IsNullOrEmpty(target))
            {
                return probes.Length > 0 ? probes[0] : null;
            }
            foreach (var probe in probes)
            {
                if (HierarchyPath(probe.gameObject) == target || probe.gameObject.name == target || probe.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target)
                {
                    return probe;
                }
            }
            return null;
        }

        private static string GraphicsProbePositions(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var probeType = request.OptionalStringArg("type") ?? "reflection";
            Component component = null;
            if (string.Equals(probeType, "reflection", StringComparison.OrdinalIgnoreCase))
            {
                component = ResolveReflectionProbeComponent(target);
            }
            else
            {
                component = ResolveLightProbeGroupComponent(target);
            }
            if (component == null)
            {
                return ErrorJson("E_PROBE_NOT_FOUND", "Probe component not found: " + target + " (type=" + probeType + ")", "Pass a GameObject name / hierarchy path with a ReflectionProbe or LightProbeGroup component.");
            }
            var effects = new List<string>();
            var positionsArg = request.StringArg("positions");
            if (!string.IsNullOrEmpty(positionsArg))
            {
                var positions = ParseVector3Array(positionsArg);
                if (component is ReflectionProbe rp)
                {
                    rp.center = positions.Length > 0 ? positions[0] : rp.center;
                    effects.Add(EffectJson("graphics.probe-positions", "reflectionCenter", positions.Length.ToString(CultureInfo.InvariantCulture)));
                }
                else if (component is LightProbeGroup lpg)
                {
                    lpg.probePositions = positions;
                    effects.Add(EffectJson("graphics.probe-positions", "lightProbePositions", positions.Length.ToString(CultureInfo.InvariantCulture)));
                }
                EditorUtility.SetDirty(component);
                AssetDatabase.SaveAssets();
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(component.gameObject))
                .Prop("probeType", probeType)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static Component ResolveReflectionProbeComponent(string target)
        {
            var probe = ResolveReflectionProbe(target);
            return probe;
        }

        private static Component ResolveLightProbeGroupComponent(string target)
        {
            var groups = UnityEngine.Object.FindObjectsOfType<LightProbeGroup>();
            if (string.IsNullOrEmpty(target))
            {
                return groups.Length > 0 ? groups[0] : null;
            }
            foreach (var group in groups)
            {
                if (HierarchyPath(group.gameObject) == target || group.gameObject.name == target || group.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target)
                {
                    return group;
                }
            }
            return null;
        }

        // ===== Render Stats =====

        private static string GraphicsStats(BridgeRequest request)
        {
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("stats", RenderStatsJson())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string RenderStatsJson()
        {
            var stats = CaptureRenderStats();
            return new UnityCliJsonWriter().BeginObject()
                .Prop("drawCalls", stats.DrawCalls)
                .Prop("batchedDrawCalls", stats.BatchedDrawCalls)
                .Prop("batches", stats.Batches)
                .Prop("savedByBatching", stats.SavedByBatches)
                .Prop("triangles", stats.Triangles)
                .Prop("vertices", stats.Vertices)
                .Prop("screenRes", stats.ScreenRes)
                .Prop("usedTextureMemory", stats.UsedTextureMemory)
                .Prop("renderTextureCount", stats.RenderTextureCount)
                .Prop("renderTextureBytes", stats.RenderTextureBytes)
                .Prop("usedVertexBuffer", stats.UsedVertexBuffer)
                .Prop("usedVertexBufferVRAM", stats.UsedVertexBufferVRAM)
                .Prop("setPassCalls", stats.SetPassCalls)
                .EndObject().ToString();
        }

        // ── Version-adaptive render-stats capture ──
        // UnityEngine.Rendering.RenderStats exists only on Unity 2023.2+ (Unity 6).
        // On Unity 2022.3 LTS the equivalent data is exposed by UnityEditor.UnityStats.
        // CaptureRenderStats unifies both sources into a snapshot so callers do not
        // need to branch on Unity version.
        private struct RenderStatsSnapshot
        {
            public long DrawCalls;
            public long BatchedDrawCalls;
            public long Batches;
            public long SavedByBatches;
            public long Triangles;
            public long Vertices;
            public string ScreenRes;
            public long UsedTextureMemory;
            public long RenderTextureCount;
            public long RenderTextureBytes;
            public long UsedVertexBuffer;
            public long UsedVertexBufferVRAM;
            public long SetPassCalls;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RenderStatsSnapshot CaptureRenderStats()
        {
            return CaptureRenderStats_Static();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RenderStatsSnapshot CaptureRenderStats_Static()
        {
#if UNITY_2023_2_OR_NEWER
            var stats = UnityEngine.Rendering.RenderStats.current;
            return new RenderStatsSnapshot
            {
                DrawCalls = (long)stats.drawCalls,
                BatchedDrawCalls = (long)stats.batchedDrawCalls,
                Batches = (long)stats.batches,
                SavedByBatches = (long)stats.savedByBatches,
                Triangles = (long)stats.triangles,
                Vertices = (long)stats.vertices,
                ScreenRes = stats.screenRes,
                UsedTextureMemory = (long)stats.usedTextureMemory,
                RenderTextureCount = stats.renderTextureCount,
                RenderTextureBytes = (long)stats.renderTextureBytes,
                UsedVertexBuffer = (long)stats.usedVertexBuffer,
                UsedVertexBufferVRAM = (long)stats.usedVertexBufferVRAM,
                SetPassCalls = stats.setPass,
            };
#else
            // Unity 2022.3 LTS: UnityEditor.UnityStats (public static class in UnityEditor).
            // Property names differ from RenderStats; batched draw calls and
            // saved-by-batching are derived because UnityStats exposes them per-category.
            var drawCalls = UnityEditor.UnityStats.drawCalls;
            var batches = UnityEditor.UnityStats.batches;
            var batchedDrawCalls = UnityEditor.UnityStats.dynamicBatchedDrawCalls
                + UnityEditor.UnityStats.staticBatchedDrawCalls
                + UnityEditor.UnityStats.instancedBatchedDrawCalls;
            return new RenderStatsSnapshot
            {
                DrawCalls = drawCalls,
                BatchedDrawCalls = batchedDrawCalls,
                Batches = batches,
                SavedByBatches = drawCalls - batches,
                Triangles = UnityEditor.UnityStats.triangles,
                Vertices = UnityEditor.UnityStats.vertices,
                ScreenRes = UnityEditor.UnityStats.screenRes,
                UsedTextureMemory = UnityEditor.UnityStats.usedTextureMemorySize,
                RenderTextureCount = UnityEditor.UnityStats.renderTextureCount,
                RenderTextureBytes = UnityEditor.UnityStats.renderTextureBytes,
                UsedVertexBuffer = UnityEditor.UnityStats.vboTotalBytes,
                UsedVertexBufferVRAM = 0,
                SetPassCalls = UnityEditor.UnityStats.setPassCalls,
            };
#endif
        }

        private static string GraphicsStatsCounters(BridgeRequest request)
        {
            var stats = CaptureRenderStats();
            var counters = new List<string>();
            counters.Add(EffectJson("stats", "drawCalls", stats.DrawCalls.ToString(CultureInfo.InvariantCulture)));
            counters.Add(EffectJson("stats", "batches", stats.Batches.ToString(CultureInfo.InvariantCulture)));
            counters.Add(EffectJson("stats", "triangles", Number((float)stats.Triangles)));
            counters.Add(EffectJson("stats", "vertices", Number((float)stats.Vertices)));
            counters.Add(EffectJson("stats", "setPassCalls", stats.SetPassCalls.ToString(CultureInfo.InvariantCulture)));
            counters.Add(EffectJson("stats", "renderTextureCount", stats.RenderTextureCount.ToString(CultureInfo.InvariantCulture)));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropArray("counters", arr => { foreach (var c in counters) arr.RawValue(c); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsStatsMemory(BridgeRequest request)
        {
            var stats = CaptureRenderStats();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("usedTextureMemory", stats.UsedTextureMemory)
                .Prop("renderTextureBytes", stats.RenderTextureBytes)
                .Prop("usedVertexBuffer", stats.UsedVertexBuffer)
                .Prop("usedVertexBufferVRAM", stats.UsedVertexBufferVRAM)
                .Prop("renderTextureCount", stats.RenderTextureCount)
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsSceneDebug(BridgeRequest request)
        {
            var effects = new List<string>();
            if (request.HasArg("shadingMode"))
            {
                var value = request.StringArg("shadingMode");
#if UNITY_2023_2_OR_NEWER
                if (Enum.TryParse<SceneView.ShadingMode>(value, true, out var parsed))
                {
                    if (SceneView.lastActiveSceneView != null)
                    {
                        SceneView.lastActiveSceneView.SetSceneViewMode(parsed, true);
                        effects.Add(EffectJson("graphics.scene-debug", "shadingMode", value));
                    }
                }
#else
                // Unity 2022.3: SceneView.ShadingMode does not exist.
                // Use DrawCameraMode as a fallback for common shading modes.
                // CameraMode constructor is internal; use GetBuiltinCameraMode to obtain mode.
                if (Enum.TryParse<DrawCameraMode>(value, true, out var drawMode))
                {
                    if (SceneView.lastActiveSceneView != null)
                    {
                        SceneView.lastActiveSceneView.cameraMode = SceneView.GetBuiltinCameraMode(drawMode);
                        effects.Add(EffectJson("graphics.scene-debug", "shadingMode", value));
                    }
                }
#endif
            }
            if (request.HasArg("drawMode"))
            {
                var value = request.StringArg("drawMode");
                if (Enum.TryParse<DrawCameraMode>(value, true, out var parsed))
                {
                    if (SceneView.lastActiveSceneView != null)
                    {
                        SceneView.lastActiveSceneView.cameraMode = SceneView.GetBuiltinCameraMode(parsed);
                        effects.Add(EffectJson("graphics.scene-debug", "drawMode", value));
                    }
                }
            }
            var currentMode = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.cameraMode.name : null;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("sceneViewAvailable", SceneView.lastActiveSceneView != null)
                .Prop("currentDrawMode", currentMode)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ===== Skybox / Environment =====

        private static string GraphicsSkyboxGet(BridgeRequest request)
        {
            var skybox = RenderSettings.skybox;
            var sun = RenderSettings.sun;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropObject("skybox", w => w
                    .Prop("name", skybox == null ? null : skybox.name)
                    .Prop("path", skybox == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(skybox)))
                    .Prop("guid", skybox == null ? null : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(skybox))))
                .PropObject("sun", w => w
                    .Prop("name", sun == null ? null : sun.name)
                    .Prop("path", sun == null ? null : HierarchyPath(sun.gameObject)))
                .PropObject("ambient", w => w
                    .Prop("ambientMode", RenderSettings.ambientMode.ToString())
                    .Prop("ambientIntensity", RenderSettings.ambientIntensity)
                    .PropRaw("ambientLight", ColorJson(RenderSettings.ambientLight)))
                .PropObject("fog", w => w
                    .Prop("enabled", RenderSettings.fog)
                    .Prop("fogMode", RenderSettings.fogMode.ToString())
                    .Prop("fogColor", ColorHtml(RenderSettings.fogColor))
                    .Prop("fogDensity", RenderSettings.fogDensity)
                    .Prop("fogStartDistance", RenderSettings.fogStartDistance)
                    .Prop("fogEndDistance", RenderSettings.fogEndDistance))
                .PropObject("reflection", w => w
                    .Prop("ambientMode", RenderSettings.defaultReflectionMode.ToString())
                    .Prop("reflectionBounces", (long)RenderSettings.reflectionBounces)
                    .Prop("reflectionIntensity", RenderSettings.reflectionIntensity))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsSkyboxSetMaterial(BridgeRequest request)
        {
            var path = request.StringArg("material");
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_MATERIAL_PATH_REQUIRED", "Pass --material <Assets/*.mat path>.", "Use tools run graphics.skybox-set-material --input '{\"material\":\"Assets/Skyboxes/Day.mat\"}'.");
            }
            var material = ResolveSkyboxMaterial(path);
            if (material == null)
            {
                return ErrorJson("E_MATERIAL_NOT_FOUND", "Skybox material not found: " + path, "Pass an existing Assets/*.mat path.");
            }
            RenderSettings.skybox = material;
            EditorUtility.SetDirty(RenderSettings.skybox);
            AssetDatabase.SaveAssets();
            WriteLog("graphics_skybox_set_material path=" + path);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("skybox", material.name)
                .PropArray("plannedEffects", arr => arr.RawValue(EffectJson("graphics.skybox-set-material", "skybox", path)))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        /// <summary>解析 skybox 材质路径，并允许恢复 Unity 内置 Default-Skybox。</summary>
        private static Material ResolveSkyboxMaterial(string path)
        {
            if (string.Equals(path, "builtin:Default-Skybox", StringComparison.OrdinalIgnoreCase) || string.Equals(path, "Default-Skybox", StringComparison.OrdinalIgnoreCase))
            {
                return AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
            }
            return AssetDatabase.LoadAssetAtPath<Material>(NormalizeAssetPath(path));
        }

        private static string GraphicsSkyboxSetProperties(BridgeRequest request)
        {
            var skybox = RenderSettings.skybox;
            if (skybox == null)
            {
                return ErrorJson("E_SKYBOX_NOT_SET", "No skybox material is assigned to RenderSettings.", "Use tools run graphics.skybox-set-material --input '{\"material\":\"Assets/Skyboxes/Day.mat\"}' first.");
            }
            var effects = new List<string>();
            var property = request.StringArg("property");
            var value = request.StringArg("value");
            if (!string.IsNullOrEmpty(property) && !string.IsNullOrEmpty(value))
            {
                ApplySkyboxMaterialProperty(skybox, property, value, effects);
            }
            EditorUtility.SetDirty(skybox);
            AssetDatabase.SaveAssets();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("skybox", skybox.name)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static void ApplySkyboxMaterialProperty(Material material, string property, string value, List<string> effects)
        {
            if (!material.HasProperty(property)) return;

            // Material.GetPropertyType / UnityEngine.Rendering.ShaderPropertyType only exist in Unity 2023.1+.
            // Unity 2022.3 uses ShaderUtil.ShaderPropertyType from the UnityEditor namespace.
#if UNITY_2023_2_OR_NEWER
            var propType = material.GetPropertyType(property);
            if (propType == UnityEngine.Rendering.ShaderPropertyType.Float || propType == UnityEngine.Rendering.ShaderPropertyType.Range)
            {
                material.SetFloat(property, ParseFloat(value, property));
            }
            else if (propType == UnityEngine.Rendering.ShaderPropertyType.Color)
            {
                material.SetColor(property, ParseColor(value));
            }
            else
            {
                material.SetFloat(property, ParseFloat(value, property));
            }
#else
            // Unity 2022.3: use ShaderUtil to get the property type by index.
            var shader = material.shader;
            var propIndex = shader.FindPropertyIndex(property);
            var propType = ShaderUtil.GetPropertyType(shader, propIndex);
            if (propType == ShaderUtil.ShaderPropertyType.Float || propType == ShaderUtil.ShaderPropertyType.Range)
            {
                material.SetFloat(property, ParseFloat(value, property));
            }
            else if (propType == ShaderUtil.ShaderPropertyType.Color)
            {
                material.SetColor(property, ParseColor(value));
            }
            else
            {
                material.SetFloat(property, ParseFloat(value, property));
            }
#endif
            effects.Add(EffectJson("graphics.skybox-set-properties", property, value));
        }

        private static string GraphicsSkyboxSetAmbient(BridgeRequest request)
        {
            var effects = new List<string>();
            if (request.HasArg("mode"))
            {
                var value = request.StringArg("mode");
                if (Enum.TryParse<AmbientMode>(value, true, out var parsed))
                {
                    RenderSettings.ambientMode = parsed;
                    effects.Add(EffectJson("graphics.skybox-set-ambient", "ambientMode", value));
                }
            }
            if (request.HasArg("intensity"))
            {
                RenderSettings.ambientIntensity = request.OptionalFloatArg("intensity").Value;
                effects.Add(EffectJson("graphics.skybox-set-ambient", "ambientIntensity", Number(RenderSettings.ambientIntensity)));
            }
            if (request.HasArg("color"))
            {
                var color = ParseColor(request.StringArg("color"));
                RenderSettings.ambientLight = color;
                effects.Add(EffectJson("graphics.skybox-set-ambient", "ambientLight", ColorHtml(color)));
            }
            if (request.HasArg("ambientEquatorColor"))
            {
                RenderSettings.ambientEquatorColor = ParseColor(request.StringArg("ambientEquatorColor"));
            }
            if (request.HasArg("ambientGroundColor"))
            {
                RenderSettings.ambientGroundColor = ParseColor(request.StringArg("ambientGroundColor"));
            }
            if (request.HasArg("ambientSkyColor"))
            {
                RenderSettings.ambientSkyColor = ParseColor(request.StringArg("ambientSkyColor"));
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("ambientMode", RenderSettings.ambientMode.ToString())
                .Prop("ambientIntensity", RenderSettings.ambientIntensity)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsSkyboxSetFog(BridgeRequest request)
        {
            var effects = new List<string>();
            if (request.HasArg("enabled"))
            {
                RenderSettings.fog = request.OptionalBoolArg("enabled").Value;
                effects.Add(EffectJson("graphics.skybox-set-fog", "fog", Bool(RenderSettings.fog)));
            }
            if (request.HasArg("mode"))
            {
                var value = request.StringArg("mode");
                if (Enum.TryParse<FogMode>(value, true, out var parsed))
                {
                    RenderSettings.fogMode = parsed;
                    effects.Add(EffectJson("graphics.skybox-set-fog", "fogMode", value));
                }
            }
            if (request.HasArg("color"))
            {
                var color = ParseColor(request.StringArg("color"));
                RenderSettings.fogColor = color;
                effects.Add(EffectJson("graphics.skybox-set-fog", "fogColor", ColorHtml(color)));
            }
            if (request.HasArg("density"))
            {
                RenderSettings.fogDensity = request.OptionalFloatArg("density").Value;
                effects.Add(EffectJson("graphics.skybox-set-fog", "fogDensity", Number(RenderSettings.fogDensity)));
            }
            if (request.HasArg("startDistance"))
            {
                RenderSettings.fogStartDistance = request.OptionalFloatArg("startDistance").Value;
                effects.Add(EffectJson("graphics.skybox-set-fog", "fogStartDistance", Number(RenderSettings.fogStartDistance)));
            }
            if (request.HasArg("endDistance"))
            {
                RenderSettings.fogEndDistance = request.OptionalFloatArg("endDistance").Value;
                effects.Add(EffectJson("graphics.skybox-set-fog", "fogEndDistance", Number(RenderSettings.fogEndDistance)));
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("fog", RenderSettings.fog)
                .Prop("fogMode", RenderSettings.fogMode.ToString())
                .Prop("fogColor", ColorHtml(RenderSettings.fogColor))
                .Prop("fogDensity", RenderSettings.fogDensity)
                .Prop("fogStartDistance", RenderSettings.fogStartDistance)
                .Prop("fogEndDistance", RenderSettings.fogEndDistance)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsSkyboxSetReflection(BridgeRequest request)
        {
            var effects = new List<string>();
            if (request.HasArg("mode"))
            {
                var value = request.StringArg("mode");
                if (Enum.TryParse<DefaultReflectionMode>(value, true, out var parsed))
                {
                    RenderSettings.defaultReflectionMode = parsed;
                    effects.Add(EffectJson("graphics.skybox-set-reflection", "defaultReflectionMode", value));
                }
            }
            if (request.HasArg("bounces"))
            {
                RenderSettings.reflectionBounces = request.OptionalIntArg("bounces").Value;
                effects.Add(EffectJson("graphics.skybox-set-reflection", "reflectionBounces", RenderSettings.reflectionBounces.ToString(CultureInfo.InvariantCulture)));
            }
            if (request.HasArg("intensity"))
            {
                RenderSettings.reflectionIntensity = request.OptionalFloatArg("intensity").Value;
                effects.Add(EffectJson("graphics.skybox-set-reflection", "reflectionIntensity", Number(RenderSettings.reflectionIntensity)));
            }
            if (request.HasArg("resolution"))
            {
                var value = request.StringArg("resolution");
#if UNITY_2023_2_OR_NEWER
                if (Enum.TryParse<DefaultReflectionResolution>(value, true, out var parsed))
                {
                    RenderSettings.defaultReflectionResolution = parsed;
                    effects.Add(EffectJson("graphics.skybox-set-reflection", "defaultReflectionResolution", value));
                }
#else
                // Unity 2022.3: RenderSettings.defaultReflectionResolution is int, not an enum.
                // Parse as integer (common values: 16, 32, 64, 128, 256, 512, 1024, 2048).
                int parsed;
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    RenderSettings.defaultReflectionResolution = parsed;
                    effects.Add(EffectJson("graphics.skybox-set-reflection", "defaultReflectionResolution", value));
                }
#endif
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("defaultReflectionMode", RenderSettings.defaultReflectionMode.ToString())
                .Prop("reflectionBounces", (long)RenderSettings.reflectionBounces)
                .Prop("reflectionIntensity", RenderSettings.reflectionIntensity)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsSkyboxSetSun(BridgeRequest request)
        {
            var target = request.StringArg("target");
            Light sun = null;
            if (!string.IsNullOrEmpty(target))
            {
                var gameObject = ResolveUniqueTarget(target, "graphics.skybox-set-sun requires --target <go>.");
                sun = gameObject.GetComponent<Light>();
                if (sun == null)
                {
                    return ErrorJson("E_LIGHT_NOT_FOUND", "No Light component on target: " + HierarchyPath(gameObject), "Pass a GameObject with a directional Light component.");
                }
            }
            else
            {
                sun = RenderSettings.sun;
            }
            RenderSettings.sun = sun;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("sun", sun == null ? null : HierarchyPath(sun.gameObject))
                .PropArray("plannedEffects", arr => arr.RawValue(EffectJson("graphics.skybox-set-sun", "sun", sun == null ? "null" : HierarchyPath(sun.gameObject))))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ===== Pipeline =====

        private static string GraphicsPipelineSettings(BridgeRequest request)
        {
            var effects = new List<string>();
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
                effects.Add(EffectJson("graphics.pipeline-settings", "renderPipeline", value));
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("graphics", GraphicsSummaryJson())
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsPipelineQuality(BridgeRequest request)
        {
            var effects = new List<string>();
            var level = request.RawJsonArg("qualityLevel");
            if (!string.IsNullOrEmpty(level))
            {
                SetQualityLevel(level, effects);
                AssetDatabase.SaveAssets();
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("qualitySettings", QualitySettingsJson())
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string GraphicsPipelineInfo(BridgeRequest request)
        {
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("graphics", GraphicsSummaryJson())
                .PropRaw("qualitySettings", QualitySettingsJson())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ===== Helpers =====

        private static string ColorHtml(Color color)
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", (int)(color.r * 255f), (int)(color.g * 255f), (int)(color.b * 255f), (int)(color.a * 255f));
        }

        private static Vector3[] ParseVector3Array(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return new Vector3[0];
            }
            var items = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<Vector3>();
            foreach (var item in items)
            {
                var trimmed = item.Trim(new[] { '[', ']', '(', ')' });
                if (!string.IsNullOrEmpty(trimmed))
                {
                    list.Add(ParseVector3(trimmed));
                }
            }
            return list.ToArray();
        }

        private static GameObject CreateGameObject(string name, Transform parent)
        {
            var gameObject = new GameObject(name);
            if (parent != null)
            {
                gameObject.transform.SetParent(parent, false);
            }
            EditorUtility.SetDirty(gameObject);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return gameObject;
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
