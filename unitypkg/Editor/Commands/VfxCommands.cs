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
        private static void RegisterVfxCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "vfx.graph", "vfx", true, VfxGraph);
            RegisterCommand(registry, "vfx.effect", "vfx", true, VfxEffect);
            RegisterCommand(registry, "vfx.particle-create", "vfx", true, VfxParticleCreate);
            RegisterCommand(registry, "vfx.particle-info", "vfx", false, VfxParticleInfo);
            RegisterCommand(registry, "vfx.particle-main", "vfx", true, VfxParticleMain);
            RegisterCommand(registry, "vfx.particle-emission", "vfx", true, VfxParticleEmission);
            RegisterCommand(registry, "vfx.particle-shape", "vfx", true, VfxParticleShape);
            RegisterCommand(registry, "vfx.particle-play", "vfx", true, VfxParticlePlay);
            RegisterCommand(registry, "vfx.particle-module", "vfx", true, VfxParticleModule);
            RegisterCommand(registry, "vfx.line-create", "vfx", true, VfxLineCreate);
            RegisterCommand(registry, "vfx.line-positions", "vfx", true, VfxLinePositions);
            RegisterCommand(registry, "vfx.trail", "vfx", true, VfxTrail);
        }

        private static string VfxGraph(BridgeRequest request)
        {
            var action = string.Equals(request.StringArg("action"), "create", StringComparison.OrdinalIgnoreCase) ? "create" : "info";
            var path = RequireVfxGraphPath(request.StringArg("path"));
            var assetType = RequireVfxAssetType();
            if (action == "create")
            {
                var overwrite = request.BoolArg("overwrite", false);
                if (AssetDatabase.LoadAssetAtPath(path, assetType) != null && !overwrite)
                {
                    return ErrorJson("E_VFX_GRAPH_EXISTS", "VFX Graph already exists: " + path, "Pass --overwrite or choose a different Assets/*.vfx path.");
                }
                CreateAssetDirectory(path);
                if (AssetDatabase.LoadAssetAtPath(path, assetType) != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }
                var created = CreateVfxGraphAsset(path, assetType);
                if (created == null)
                {
                    return ErrorJson("E_VFX_GRAPH_CREATE_FAILED", "VFX Graph asset creation did not produce a readable VisualEffectAsset: " + path, "Check Unity Console and UnityCliBridge.log; the Visual Effect Graph package may have changed its internal authoring API.");
                }
                WriteLog("vfx_graph_created path=" + path);
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("graph", VfxGraphJson(path, created))
                    .PropArray("plannedEffects", arr => arr.RawValue(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", "vfx.graph.create")
                        .Prop("path", path)
                        .EndObject().ToString()))
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }
            var asset = AssetDatabase.LoadAssetAtPath(path, assetType);
            if (asset == null)
            {
                return ErrorJson("E_VFX_GRAPH_NOT_FOUND", "VFX Graph asset not found: " + path, "Check the path or create one with unity_cli vfx graph <path> --create.");
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("graph", VfxGraphJson(path, asset))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string VfxEffect(BridgeRequest request)
        {
            var visualEffectType = RequireVisualEffectType();
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "VFX effect requires --target <go>.");
            var component = gameObject.GetComponent(visualEffectType);
            var effects = new List<string>();
            if (component == null && request.BoolArg("add", false))
            {
                component = gameObject.AddComponent(visualEffectType);
                effects.Add(new UnityCliJsonWriter().BeginObject()
                    .Prop("type", "vfx.effect.add")
                    .Prop("target", HierarchyPath(gameObject))
                    .EndObject().ToString());
            }
            if (component == null)
            {
                return ErrorJson("E_VFX_COMPONENT_NOT_FOUND", "VisualEffect component not found on target: " + HierarchyPath(gameObject), "Pass --add to add one or choose a target with VisualEffect.");
            }
            var graphPath = request.StringArg("graph");
            if (!string.IsNullOrEmpty(graphPath))
            {
                var assetType = RequireVfxAssetType();
                var graph = AssetDatabase.LoadAssetAtPath(NormalizeAssetPath(graphPath), assetType);
                if (graph == null)
                {
                    return ErrorJson("E_VFX_GRAPH_NOT_FOUND", "VFX Graph asset not found: " + graphPath, "Pass an existing Assets/*.vfx path or create one with unity_cli vfx graph <path> --create.");
                }
                var property = visualEffectType.GetProperty("visualEffectAsset", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || !property.CanWrite)
                {
                    return ErrorJson("E_VFX_ASSIGN_UNSUPPORTED", "VisualEffect asset property is not writable in this Unity version.", "Assign the VFX Graph in Unity or upgrade Visual Effect Graph package.");
                }
                property.SetValue(component, graph, null);
                effects.Add(new UnityCliJsonWriter().BeginObject()
                    .Prop("type", "vfx.effect.assign")
                    .Prop("target", HierarchyPath(gameObject))
                    .Prop("graph", NormalizeAssetPath(graphPath))
                    .EndObject().ToString());
            }
            EditorUtility.SetDirty(component);
            AssetDatabase.SaveAssets();
            WriteLog("vfx_effect target=" + HierarchyPath(gameObject) + " effects=" + effects.Count.ToString(CultureInfo.InvariantCulture));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropRaw("effect", VfxEffectJson(component))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string RequireVfxGraphPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_VFX_GRAPH_PATH_REQUIRED", "Pass an Assets/*.vfx path.", "Use unity_cli vfx graph <path> or pass --path Assets/VFX/Effect.vfx.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_VFX_GRAPH_PATH_INVALID", "VFX Graph path must be under Assets/ and end with .vfx: " + path, "Pass a Unity asset path such as Assets/VFX/Explosion.vfx.");
            }
            return path;
        }

        private static UnityEngine.Object CreateVfxGraphAsset(string path, Type assetType)
        {
            try
            {
                var created = CreateVfxGraphAssetWithUnityUtility(path, assetType);
                if (created != null)
                {
                    return created;
                }

                WriteMinimalVfxGraphYaml(path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();
                return AssetDatabase.LoadAssetAtPath(path, assetType);
            }
            catch (BridgeCommandException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_VFX_GRAPH_CREATE_FAILED", "Failed to create VFX Graph asset: " + exception.GetType().Name + ": " + exception.Message, "Check Unity Console and UnityCliBridge.log; the Visual Effect Graph package import pipeline may have changed.");
            }
        }

        private static UnityEngine.Object CreateVfxGraphAssetWithUnityUtility(string path, Type assetType)
        {
            var utilityType = FindType("UnityEditor.VisualEffectAssetEditorUtility");
            if (utilityType == null)
            {
                return null;
            }
            var createMethod = utilityType.GetMethod("CreateNewAsset", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (createMethod == null)
            {
                return null;
            }
            var created = createMethod.Invoke(null, new object[] { path }) as UnityEngine.Object;
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            return created == null || !assetType.IsInstanceOfType(created) ? AssetDatabase.LoadAssetAtPath(path, assetType) : created;
        }

        private static void WriteMinimalVfxGraphYaml(string path)
        {
            var fullPath = Path.Combine(ProjectRoot(), path.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(fullPath,
                "%YAML 1.1\n"
                + "%TAG !u! tag:unity3d.com,2011:\n"
                + "--- !u!114 &114350483966674976\n"
                + "MonoBehaviour:\n"
                + "  m_Script: {fileID: 11500000, guid: 7d4c867f6b72b714dbb5fd1780afe208, type: 3}\n"
                + "--- !u!2058629511 &1\n"
                + "VisualEffectResource:\n"
                + "  m_Graph: {fileID: 114350483966674976}\n");
        }

        private static Type RequireVfxAssetType()
        {
            RequireVfxPackageInstalled();
            var type = FindType("UnityEngine.VFX.VisualEffectAsset");
            if (type == null)
            {
                throw new BridgeCommandException("E_VFX_PACKAGE_MISSING", "Visual Effect Graph package is installed in the manifest but its editor/runtime assemblies are not loaded.", "Wait for Unity PackageManager resolve/import to finish, then retry.");
            }
            return type;
        }

        private static Type RequireVisualEffectType()
        {
            RequireVfxPackageInstalled();
            var type = FindType("UnityEngine.VFX.VisualEffect");
            if (type == null)
            {
                throw new BridgeCommandException("E_VFX_PACKAGE_MISSING", "Visual Effect Graph package is installed in the manifest but its runtime assembly is not loaded.", "Wait for Unity PackageManager resolve/import to finish, then retry.");
            }
            return type;
        }

        private static void RequireVfxPackageInstalled()
        {
            if (!PackageManifestContains("com.unity.visualeffectgraph") && !PackageLockContains("com.unity.visualeffectgraph"))
            {
                throw new BridgeCommandException("E_VFX_PACKAGE_MISSING", "Visual Effect Graph package is not installed in this Unity project.", "Install com.unity.visualeffectgraph with unity_cli package install com.unity.visualeffectgraph, run package resolve, then retry.");
            }
        }

        private static bool PackageManifestContains(string packageName)
        {
            return FileContains(Path.Combine(ProjectRoot(), "Packages", "manifest.json"), "\"" + packageName + "\"");
        }

        private static bool PackageLockContains(string packageName)
        {
            return FileContains(Path.Combine(ProjectRoot(), "Packages", "packages-lock.json"), "\"" + packageName + "\"");
        }

        private static bool FileContains(string path, string value)
        {
            try
            {
                return File.Exists(path) && File.ReadAllText(path).IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }


        private static string VfxGraphJson(string path, UnityEngine.Object asset)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("path", path)
                .Prop("guid", AssetDatabase.AssetPathToGUID(path))
                .Prop("name", asset == null ? null : asset.name)
                .Prop("type", asset == null ? null : asset.GetType().FullName)
                .EndObject().ToString();
        }

        private static string VfxEffectJson(Component component)
        {
            var property = component.GetType().GetProperty("visualEffectAsset", BindingFlags.Instance | BindingFlags.Public);
            var graph = property == null ? null : property.GetValue(component, null) as UnityEngine.Object;
            var graphPath = graph == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(graph));
            return new UnityCliJsonWriter().BeginObject()
                .Prop("componentType", component.GetType().FullName)
                .Prop("enabled", ComponentEnabled(component))
                .PropObject("graph", w => w
                    .Prop("name", graph == null ? null : graph.name)
                    .Prop("path", graphPath)
                    .Prop("guid", string.IsNullOrEmpty(graphPath) ? null : AssetDatabase.AssetPathToGUID(graphPath)))
                .EndObject().ToString();
        }

        // ===== ParticleSystem =====

        private static string VfxParticleCreate(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var name = request.OptionalStringArg("name") ?? "ParticleSystem";
            var gameObject = string.IsNullOrEmpty(target) ? CreateGameObject(name, null) : ResolveUniqueTarget(target, "vfx.particle-create requires --target <go>.");
            var ps = gameObject.GetComponent<ParticleSystem>();
            var effects = new List<string>();
            if (ps == null)
            {
                ps = gameObject.AddComponent<ParticleSystem>();
                var renderer = gameObject.GetComponent<ParticleSystemRenderer>();
                if (renderer == null)
                {
                    gameObject.AddComponent<ParticleSystemRenderer>();
                }
                effects.Add(EffectJson("vfx.particle-create", "component", "added"));
            }
            if (request.HasArg("duration"))
            {
                var main = ps.main;
                main.duration = request.OptionalFloatArg("duration").Value;
            }
            if (request.HasArg("startLifetime"))
            {
                var main = ps.main;
                main.startLifetime = request.OptionalFloatArg("startLifetime").Value;
            }
            if (request.HasArg("startSpeed"))
            {
                var main = ps.main;
                main.startSpeed = request.OptionalFloatArg("startSpeed").Value;
            }
            if (request.HasArg("maxParticles"))
            {
                var main = ps.main;
                main.maxParticles = request.OptionalIntArg("maxParticles").Value;
            }
            if (request.HasArg("loop"))
            {
                var main = ps.main;
                main.loop = request.OptionalBoolArg("loop").Value;
            }
            EditorUtility.SetDirty(ps);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            WriteLog("vfx_particle_create target=" + HierarchyPath(gameObject));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropRaw("particleSystem", ParticleSystemJson(ps))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string VfxParticleInfo(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var ps = ResolveParticleSystem(target);
            if (ps == null)
            {
                return ErrorJson("E_PARTICLE_SYSTEM_NOT_FOUND", "ParticleSystem not found: " + target, "Pass a GameObject name / hierarchy path with a ParticleSystem component.");
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(ps.gameObject))
                .PropRaw("particleSystem", ParticleSystemJson(ps))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string VfxParticleMain(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var ps = ResolveParticleSystem(target);
            if (ps == null)
            {
                return ErrorJson("E_PARTICLE_SYSTEM_NOT_FOUND", "ParticleSystem not found: " + target, "Pass a GameObject name / hierarchy path with a ParticleSystem component.");
            }
            var main = ps.main;
            var effects = new List<string>();
            if (request.HasArg("duration")) { main.duration = request.OptionalFloatArg("duration").Value; effects.Add(EffectJson("vfx.particle-main", "duration", Number(main.duration))); }
            if (request.HasArg("startLifetime")) { main.startLifetime = request.OptionalFloatArg("startLifetime").Value; effects.Add(EffectJson("vfx.particle-main", "startLifetime", Number(main.startLifetime.constant))); }
            if (request.HasArg("startSpeed")) { main.startSpeed = request.OptionalFloatArg("startSpeed").Value; effects.Add(EffectJson("vfx.particle-main", "startSpeed", Number(main.startSpeed.constant))); }
            if (request.HasArg("startSize")) { main.startSize = request.OptionalFloatArg("startSize").Value; effects.Add(EffectJson("vfx.particle-main", "startSize", Number(main.startSize.constant))); }
            if (request.HasArg("startColor")) { main.startColor = ParseColor(request.StringArg("startColor")); effects.Add(EffectJson("vfx.particle-main", "startColor", request.StringArg("startColor"))); }
            if (request.HasArg("startRotation")) { main.startRotation = request.OptionalFloatArg("startRotation").Value * Mathf.Deg2Rad; effects.Add(EffectJson("vfx.particle-main", "startRotation", request.StringArg("startRotation"))); }
            if (request.HasArg("gravityModifier")) { main.gravityModifier = request.OptionalFloatArg("gravityModifier").Value; effects.Add(EffectJson("vfx.particle-main", "gravityModifier", Number(main.gravityModifier.constant))); }
            if (request.HasArg("maxParticles")) { main.maxParticles = request.OptionalIntArg("maxParticles").Value; effects.Add(EffectJson("vfx.particle-main", "maxParticles", main.maxParticles.ToString(CultureInfo.InvariantCulture))); }
            if (request.HasArg("loop")) { main.loop = request.OptionalBoolArg("loop").Value; effects.Add(EffectJson("vfx.particle-main", "loop", Bool(main.loop))); }
            if (request.HasArg("playOnAwake")) { main.playOnAwake = request.OptionalBoolArg("playOnAwake").Value; effects.Add(EffectJson("vfx.particle-main", "playOnAwake", Bool(main.playOnAwake))); }
            if (request.HasArg("simulationSpace"))
            {
                var value = request.StringArg("simulationSpace");
                if (Enum.TryParse<ParticleSystemSimulationSpace>(value, true, out var parsed))
                {
                    main.simulationSpace = parsed;
                    effects.Add(EffectJson("vfx.particle-main", "simulationSpace", value));
                }
            }
            if (request.HasArg("scalingMode"))
            {
                var value = request.StringArg("scalingMode");
                if (Enum.TryParse<ParticleSystemScalingMode>(value, true, out var parsed))
                {
                    main.scalingMode = parsed;
                    effects.Add(EffectJson("vfx.particle-main", "scalingMode", value));
                }
            }
            EditorUtility.SetDirty(ps);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(ps.gameObject))
                .PropRaw("particleSystem", ParticleSystemJson(ps))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string VfxParticleEmission(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var ps = ResolveParticleSystem(target);
            if (ps == null)
            {
                return ErrorJson("E_PARTICLE_SYSTEM_NOT_FOUND", "ParticleSystem not found: " + target, "Pass a GameObject name / hierarchy path with a ParticleSystem component.");
            }
            var emission = ps.emission;
            var effects = new List<string>();
            if (request.HasArg("enabled")) { emission.enabled = request.OptionalBoolArg("enabled").Value; effects.Add(EffectJson("vfx.particle-emission", "enabled", Bool(emission.enabled))); }
            if (request.HasArg("rateOverTime")) { emission.rateOverTime = request.OptionalFloatArg("rateOverTime").Value; effects.Add(EffectJson("vfx.particle-emission", "rateOverTime", Number(emission.rateOverTime.constant))); }
            if (request.HasArg("rateOverDistance")) { emission.rateOverDistance = request.OptionalFloatArg("rateOverDistance").Value; effects.Add(EffectJson("vfx.particle-emission", "rateOverDistance", Number(emission.rateOverDistance.constant))); }
            if (request.HasArg("burstCount"))
            {
                var burstCount = request.OptionalIntArg("burstCount").Value;
                emission.SetBursts(new ParticleSystem.Burst[0]);
                for (var i = 0; i < burstCount; i++)
                {
                    var time = request.OptionalFloatArg("burst" + i + "Time") ?? 0f;
                    var count = request.OptionalIntArg("burst" + i + "Count") ?? 10;
                    var cycleCount = request.OptionalIntArg("burst" + i + "Cycles") ?? 1;
                    var repeatInterval = request.OptionalFloatArg("burst" + i + "RepeatInterval") ?? 0.01f;
                    emission.SetBurst(i, new ParticleSystem.Burst(time, (short)count, (short)cycleCount, repeatInterval));
                }
                effects.Add(EffectJson("vfx.particle-emission", "burstCount", burstCount.ToString(CultureInfo.InvariantCulture)));
            }
            EditorUtility.SetDirty(ps);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(ps.gameObject))
                .PropObject("emission", w => w
                    .Prop("enabled", emission.enabled)
                    .Prop("rateOverTime", emission.rateOverTime.constant)
                    .Prop("rateOverDistance", emission.rateOverDistance.constant)
                    .Prop("burstCount", (long)emission.burstCount))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string VfxParticleShape(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var ps = ResolveParticleSystem(target);
            if (ps == null)
            {
                return ErrorJson("E_PARTICLE_SYSTEM_NOT_FOUND", "ParticleSystem not found: " + target, "Pass a GameObject name / hierarchy path with a ParticleSystem component.");
            }
            var shape = ps.shape;
            var effects = new List<string>();
            if (request.HasArg("enabled")) { shape.enabled = request.OptionalBoolArg("enabled").Value; effects.Add(EffectJson("vfx.particle-shape", "enabled", Bool(shape.enabled))); }
            if (request.HasArg("shapeType"))
            {
                var value = request.StringArg("shapeType");
                if (Enum.TryParse<ParticleSystemShapeType>(value, true, out var parsed))
                {
                    shape.shapeType = parsed;
                    effects.Add(EffectJson("vfx.particle-shape", "shapeType", value));
                }
            }
            if (request.HasArg("radius")) { shape.radius = request.OptionalFloatArg("radius").Value; effects.Add(EffectJson("vfx.particle-shape", "radius", Number(shape.radius))); }
            if (request.HasArg("angle")) { shape.angle = request.OptionalFloatArg("angle").Value; effects.Add(EffectJson("vfx.particle-shape", "angle", Number(shape.angle))); }
            if (request.HasArg("arc")) { shape.arc = request.OptionalFloatArg("arc").Value; effects.Add(EffectJson("vfx.particle-shape", "arc", Number(shape.arc))); }
            if (request.HasArg("length")) { shape.length = request.OptionalFloatArg("length").Value; effects.Add(EffectJson("vfx.particle-shape", "length", Number(shape.length))); }
            if (request.HasArg("boxX")) { shape.box = new Vector3(request.OptionalFloatArg("boxX").Value, shape.box.y, shape.box.z); }
            if (request.HasArg("boxY")) { shape.box = new Vector3(shape.box.x, request.OptionalFloatArg("boxY").Value, shape.box.z); }
            if (request.HasArg("boxZ")) { shape.box = new Vector3(shape.box.x, shape.box.y, request.OptionalFloatArg("boxZ").Value); }
            if (request.HasArg("randomDirection")) { shape.randomDirection = request.OptionalBoolArg("randomDirection").Value; effects.Add(EffectJson("vfx.particle-shape", "randomDirection", Bool(shape.randomDirection))); }
            EditorUtility.SetDirty(ps);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(ps.gameObject))
                .PropObject("shape", w => w
                    .Prop("enabled", shape.enabled)
                    .Prop("shapeType", shape.shapeType.ToString())
                    .Prop("radius", shape.radius)
                    .Prop("angle", shape.angle)
                    .Prop("arc", shape.arc)
                    .Prop("length", shape.length)
                    .PropRaw("box", Vector3Json(shape.box))
                    .Prop("randomDirection", shape.randomDirection))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string VfxParticlePlay(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var ps = ResolveParticleSystem(target);
            if (ps == null)
            {
                return ErrorJson("E_PARTICLE_SYSTEM_NOT_FOUND", "ParticleSystem not found: " + target, "Pass a GameObject name / hierarchy path with a ParticleSystem component.");
            }
            var action = request.OptionalStringArg("action") ?? "play";
            var effects = new List<string>();
            switch (action.ToLowerInvariant())
            {
                case "play":
                    ps.Play(request.OptionalBoolArg("withChildren") ?? true);
                    effects.Add(EffectJson("vfx.particle-play", "action", "play"));
                    break;
                case "stop":
                    var stopBehavior = ParticleSystemStopBehavior.StopEmitting;
                    if (request.HasArg("stopBehavior"))
                    {
                        Enum.TryParse<ParticleSystemStopBehavior>(request.StringArg("stopBehavior"), true, out stopBehavior);
                    }
                    ps.Stop(request.OptionalBoolArg("withChildren") ?? true, stopBehavior);
                    effects.Add(EffectJson("vfx.particle-play", "action", "stop"));
                    break;
                case "pause":
                    ps.Pause(request.OptionalBoolArg("withChildren") ?? true);
                    effects.Add(EffectJson("vfx.particle-play", "action", "pause"));
                    break;
                case "clear":
                    ps.Clear(request.OptionalBoolArg("withChildren") ?? true);
                    effects.Add(EffectJson("vfx.particle-play", "action", "clear"));
                    break;
                default:
                    return ErrorJson("E_INVALID_ACTION", "Invalid particle action: " + action, "Use play, stop, pause, or clear.");
            }
            WriteLog("vfx_particle_play target=" + HierarchyPath(ps.gameObject) + " action=" + action);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(ps.gameObject))
                .Prop("isPlaying", ps.isPlaying)
                .Prop("isEmitting", ps.isEmitting)
                .Prop("isPaused", ps.isPaused)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string VfxParticleModule(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var ps = ResolveParticleSystem(target);
            if (ps == null)
            {
                return ErrorJson("E_PARTICLE_SYSTEM_NOT_FOUND", "ParticleSystem not found: " + target, "Pass a GameObject name / hierarchy path with a ParticleSystem component.");
            }
            var module = request.OptionalStringArg("module") ?? "";
            var effects = new List<string>();
            var enabled = request.OptionalBoolArg("enabled");
            switch (module.ToLowerInvariant())
            {
                case "coloroverlifetime":
                case "color":
                    var col = ps.colorOverLifetime;
                    if (enabled.HasValue) { col.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "colorOverLifetime.enabled", Bool(col.enabled))); }
                    break;
                case "sizeoverlifetime":
                case "size":
                    var sol = ps.sizeOverLifetime;
                    if (enabled.HasValue) { sol.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "sizeOverLifetime.enabled", Bool(sol.enabled))); }
                    break;
                case "rotationoverlifetime":
                case "rotation":
                    var rol = ps.rotationOverLifetime;
                    if (enabled.HasValue) { rol.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "rotationOverLifetime.enabled", Bool(rol.enabled))); }
                    break;
                case "velocityoverlifetime":
                case "velocity":
                    var vol = ps.velocityOverLifetime;
                    if (enabled.HasValue) { vol.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "velocityOverLifetime.enabled", Bool(vol.enabled))); }
                    break;
                case "forceoverlifetime":
                case "force":
                    var fol = ps.forceOverLifetime;
                    if (enabled.HasValue) { fol.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "forceOverLifetime.enabled", Bool(fol.enabled))); }
                    break;
                case "noise":
                    var noise = ps.noise;
                    if (enabled.HasValue) { noise.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "noise.enabled", Bool(noise.enabled))); }
                    break;
                case "collision":
                    var collision = ps.collision;
                    if (enabled.HasValue) { collision.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "collision.enabled", Bool(collision.enabled))); }
                    break;
                case "subemitter":
                case "sub":
                    var sub = ps.subEmitters;
                    if (enabled.HasValue) { sub.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "subEmitters.enabled", Bool(sub.enabled))); }
                    break;
                case "textureanimation":
                case "texture":
                    var tex = ps.textureSheetAnimation;
                    if (enabled.HasValue) { tex.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "textureSheetAnimation.enabled", Bool(tex.enabled))); }
                    break;
                case "lights":
                    var lights = ps.lights;
                    if (enabled.HasValue) { lights.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "lights.enabled", Bool(lights.enabled))); }
                    break;
                case "trail":
                    var trail = ps.trails;
                    if (enabled.HasValue) { trail.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "trails.enabled", Bool(trail.enabled))); }
                    break;
                case "inheritvelocity":
                    var inherit = ps.inheritVelocity;
                    if (enabled.HasValue) { inherit.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "inheritVelocity.enabled", Bool(inherit.enabled))); }
                    break;
                case "limitvelocityoverlifetime":
                case "limitvelocity":
                    var limit = ps.limitVelocityOverLifetime;
                    if (enabled.HasValue) { limit.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "limitVelocityOverLifetime.enabled", Bool(limit.enabled))); }
                    break;
                default:
                    return ErrorJson("E_UNKNOWN_MODULE", "Unknown or unsupported particle module: " + module, "Use color, size, rotation, velocity, force, noise, collision, subEmitter, textureAnimation, lights, trail, inheritVelocity, or limitVelocity.");
            }
            EditorUtility.SetDirty(ps);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(ps.gameObject))
                .Prop("module", module)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ===== Line / Trail =====

        private static string VfxLineCreate(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var name = request.OptionalStringArg("name") ?? "Line";
            var gameObject = string.IsNullOrEmpty(target) ? CreateGameObject(name, null) : ResolveUniqueTarget(target, "vfx.line-create requires --target <go>.");
            var line = gameObject.GetComponent<LineRenderer>();
            var effects = new List<string>();
            if (line == null)
            {
                line = gameObject.AddComponent<LineRenderer>();
                effects.Add(EffectJson("vfx.line-create", "component", "added"));
            }
            if (request.HasArg("startWidth")) { line.startWidth = request.OptionalFloatArg("startWidth").Value; effects.Add(EffectJson("vfx.line-create", "startWidth", Number(line.startWidth))); }
            if (request.HasArg("endWidth")) { line.endWidth = request.OptionalFloatArg("endWidth").Value; effects.Add(EffectJson("vfx.line-create", "endWidth", Number(line.endWidth))); }
            if (request.HasArg("startColor")) { line.startColor = ParseColor(request.StringArg("startColor")); effects.Add(EffectJson("vfx.line-create", "startColor", request.StringArg("startColor"))); }
            if (request.HasArg("endColor")) { line.endColor = ParseColor(request.StringArg("endColor")); effects.Add(EffectJson("vfx.line-create", "endColor", request.StringArg("endColor"))); }
            if (request.HasArg("positionCount")) { line.positionCount = request.OptionalIntArg("positionCount").Value; effects.Add(EffectJson("vfx.line-create", "positionCount", line.positionCount.ToString(CultureInfo.InvariantCulture))); }
            if (request.HasArg("loop")) { line.loop = request.OptionalBoolArg("loop").Value; effects.Add(EffectJson("vfx.line-create", "loop", Bool(line.loop))); }
            if (request.HasArg("useWorldSpace")) { line.useWorldSpace = request.OptionalBoolArg("useWorldSpace").Value; effects.Add(EffectJson("vfx.line-create", "useWorldSpace", Bool(line.useWorldSpace))); }
            EditorUtility.SetDirty(line);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            WriteLog("vfx_line_create target=" + HierarchyPath(gameObject));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropObject("line", w => w
                    .Prop("startWidth", line.startWidth)
                    .Prop("endWidth", line.endWidth)
                    .Prop("positionCount", (long)line.positionCount)
                    .Prop("loop", line.loop)
                    .Prop("useWorldSpace", line.useWorldSpace))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string VfxLinePositions(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var line = ResolveLineRenderer(target);
            if (line == null)
            {
                return ErrorJson("E_LINE_RENDERER_NOT_FOUND", "LineRenderer not found: " + target, "Pass a GameObject name / hierarchy path with a LineRenderer component.");
            }
            var effects = new List<string>();
            var positionsArg = request.StringArg("positions");
            if (!string.IsNullOrEmpty(positionsArg))
            {
                var positions = ParseVector3Array(positionsArg);
                line.positionCount = positions.Length;
                line.SetPositions(positions);
                effects.Add(EffectJson("vfx.line-positions", "positions", positions.Length.ToString(CultureInfo.InvariantCulture)));
            }
            var index = request.OptionalIntArg("index");
            var position = request.StringArg("position");
            if (index.HasValue && !string.IsNullOrEmpty(position))
            {
                if (index.Value < 0 || index.Value >= line.positionCount)
                {
                    return ErrorJson("E_INDEX_OUT_OF_RANGE", "Line position index out of range: " + index.Value, "Use an index between 0 and " + (line.positionCount - 1) + ".");
                }
                line.SetPosition(index.Value, ParseVector3(position));
                effects.Add(EffectJson("vfx.line-positions", "index" + index.Value.ToString(CultureInfo.InvariantCulture), position));
            }
            EditorUtility.SetDirty(line);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(line.gameObject))
                .Prop("positionCount", (long)line.positionCount)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string VfxTrail(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var name = request.OptionalStringArg("name") ?? "Trail";
            var gameObject = string.IsNullOrEmpty(target) ? CreateGameObject(name, null) : ResolveUniqueTarget(target, "vfx.trail requires --target <go>.");
            var trail = gameObject.GetComponent<TrailRenderer>();
            var effects = new List<string>();
            if (trail == null && request.OptionalBoolArg("create") == true)
            {
                trail = gameObject.AddComponent<TrailRenderer>();
                effects.Add(EffectJson("vfx.trail", "component", "added"));
            }
            if (trail == null)
            {
                return ErrorJson("E_TRAIL_RENDERER_NOT_FOUND", "TrailRenderer not found on target: " + HierarchyPath(gameObject), "Pass --create to add one or choose a target with TrailRenderer.");
            }
            if (request.HasArg("time")) { trail.time = request.OptionalFloatArg("time").Value; effects.Add(EffectJson("vfx.trail", "time", Number(trail.time))); }
            if (request.HasArg("startWidth")) { trail.startWidth = request.OptionalFloatArg("startWidth").Value; effects.Add(EffectJson("vfx.trail", "startWidth", Number(trail.startWidth))); }
            if (request.HasArg("endWidth")) { trail.endWidth = request.OptionalFloatArg("endWidth").Value; effects.Add(EffectJson("vfx.trail", "endWidth", Number(trail.endWidth))); }
            if (request.HasArg("minVertexDistance")) { trail.minVertexDistance = request.OptionalFloatArg("minVertexDistance").Value; effects.Add(EffectJson("vfx.trail", "minVertexDistance", Number(trail.minVertexDistance))); }
            if (request.HasArg("autodestruct")) { trail.autodestruct = request.OptionalBoolArg("autodestruct").Value; effects.Add(EffectJson("vfx.trail", "autodestruct", Bool(trail.autodestruct))); }
            if (request.HasArg("emitting")) { trail.emitting = request.OptionalBoolArg("emitting").Value; effects.Add(EffectJson("vfx.trail", "emitting", Bool(trail.emitting))); }
            EditorUtility.SetDirty(trail);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropObject("trail", w => w
                    .Prop("time", trail.time)
                    .Prop("startWidth", trail.startWidth)
                    .Prop("endWidth", trail.endWidth)
                    .Prop("minVertexDistance", trail.minVertexDistance)
                    .Prop("emitting", trail.emitting)
                    .Prop("autodestruct", trail.autodestruct))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ===== Helpers =====

        private static ParticleSystem ResolveParticleSystem(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                var first = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
                return first.Length > 0 ? first[0] : null;
            }
            var go = FindGameObjectByTarget(target);
            if (go != null)
            {
                return go.GetComponent<ParticleSystem>();
            }
            return null;
        }

        private static LineRenderer ResolveLineRenderer(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                var first = UnityEngine.Object.FindObjectsByType<LineRenderer>(FindObjectsSortMode.None);
                return first.Length > 0 ? first[0] : null;
            }
            var go = FindGameObjectByTarget(target);
            if (go != null)
            {
                return go.GetComponent<LineRenderer>();
            }
            return null;
        }

        private static GameObject FindGameObjectByTarget(string target)
        {
            var go = GameObject.Find(target);
            if (go != null) return go;
            var all = GameObject.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            foreach (var obj in all)
            {
                if (HierarchyPath(obj) == target) return obj;
            }
            return null;
        }

        private static string ParticleSystemJson(ParticleSystem ps)
        {
            var main = ps.main;
            return new UnityCliJsonWriter().BeginObject()
                .Prop("isPlaying", ps.isPlaying)
                .Prop("isPaused", ps.isPaused)
                .Prop("isEmitting", ps.isEmitting)
                .Prop("particleCount", (long)ps.particleCount)
                .PropObject("main", w => w
                    .Prop("duration", main.duration)
                    .Prop("startLifetime", main.startLifetime.constant)
                    .Prop("startSpeed", main.startSpeed.constant)
                    .Prop("startSize", main.startSize.constant)
                    .Prop("maxParticles", (long)main.maxParticles)
                    .Prop("loop", main.loop)
                    .Prop("playOnAwake", main.playOnAwake)
                    .Prop("simulationSpace", main.simulationSpace.ToString())
                    .Prop("gravityModifier", main.gravityModifier.constant))
                .EndObject().ToString();
        }
    }
}
#endif
