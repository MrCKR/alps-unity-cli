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
            RegisterCommand(registry, "vfx.parameter", "vfx", false, VfxParameter);
            RegisterCommand(registry, "vfx.control", "vfx", false, VfxControl);
            RegisterCommand(registry, "vfx.event", "vfx", false, VfxEvent);
            RegisterCommand(registry, "vfx.template", "vfx", true, VfxTemplate);
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
                return ErrorJson("E_VFX_GRAPH_NOT_FOUND", "VFX Graph asset not found: " + path, "Check the path or create one with unity_cli tools run vfx.graph --input '{\"path\":\"Assets/VFX/Effect.vfx\",\"create\":true}'.");
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
                    return ErrorJson("E_VFX_GRAPH_NOT_FOUND", "VFX Graph asset not found: " + graphPath, "Pass an existing Assets/*.vfx path or create one with unity_cli tools run vfx.graph --input '{\"path\":\"Assets/VFX/Effect.vfx\",\"create\":true}'.");
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
                throw new BridgeCommandException("E_VFX_GRAPH_PATH_REQUIRED", "Pass an Assets/*.vfx path.", "Use unity_cli tools run vfx.graph --input '{\"path\":\"Assets/VFX/Effect.vfx\"}'.");
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
                throw new BridgeCommandException("E_VFX_PACKAGE_MISSING", "Visual Effect Graph package is not installed in this Unity project.", "Install com.unity.visualeffectgraph manually via Unity Package Manager or Packages/manifest.json, then run unity_cli extensions install vfx-graph --project <path>.");
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

        // ── VFX Parameter ──────────────────────────────────────────────────────
        // Critically references unity-mcp VfxGraphWrite.cs: SetFloat, SetInt, SetBool,
        //   SetVector2, SetVector3, SetVector4, SetGradient, SetTexture, SetMesh,
        //   SetAnimationCurve methods on UnityEngine.VFX.VisualEffect component.
        //
        // Adapted for alps-unity-cli: uses reflection-based method invocation
        //   (FindType + GetMethod + Invoke) since we have no compile-time VFX dependency.
        //   Parameter type is passed explicitly via --type to avoid guesswork.

private static string VfxParameter(BridgeRequest request)
        {
            RequireVfxPackageInstalled();
            var visualEffectType = RequireVisualEffectType();
            var go = ResolveUniqueTarget(request.StringArg("target"), "VFX parameter requires --target <go>.");
            var component = go.GetComponent(visualEffectType);
            if (component == null)
                return ErrorJson("E_VFX_COMPONENT_NOT_FOUND", "VisualEffect component not found on target: " + HierarchyPath(go), "Pass --add to add one or choose a target with VisualEffect.");
            var paramName = request.StringArg("name");
            if (string.IsNullOrEmpty(paramName))
                return ErrorJson("E_VFX_PARAMETER_NAME_REQUIRED", "VFX parameter requires --name <param>.", "Pass the exposed parameter name.");
            var paramType = (request.StringArg("type") ?? "float").ToLowerInvariant();
            var effects = new List<string>();

            try
            {
                switch (paramType)
                {
                    case "float":
                        {
                            var value = request.FloatArg("value", 0f);
                            var method = visualEffectType.GetMethod("SetFloat", new[] { typeof(string), typeof(float) });
                            if (method != null) method.Invoke(component, new object[] { paramName, value });
                            effects.Add(EffectJson("vfx.parameter", paramName, Number(value)));
                        }
                        break;
                    case "int":
                        {
                            var value = request.IntArg("value", 0);
                            var method = visualEffectType.GetMethod("SetInt", new[] { typeof(string), typeof(int) });
                            if (method != null) method.Invoke(component, new object[] { paramName, value });
                            effects.Add(EffectJson("vfx.parameter", paramName, value.ToString(CultureInfo.InvariantCulture)));
                        }
                        break;
                    case "bool":
                        {
                            var value = request.BoolArg("value", false);
                            var method = visualEffectType.GetMethod("SetBool", new[] { typeof(string), typeof(bool) });
                            if (method != null) method.Invoke(component, new object[] { paramName, value });
                            effects.Add(EffectJson("vfx.parameter", paramName, Bool(value)));
                        }
                        break;
                    case "vector2":
                        {
                            var vec = new Vector2(request.FloatArg("x", 0f), request.FloatArg("y", 0f));
                            var method = visualEffectType.GetMethod("SetVector2", new[] { typeof(string), typeof(Vector2) });
                            if (method != null) method.Invoke(component, new object[] { paramName, vec });
                            effects.Add(EffectJson("vfx.parameter", paramName, vec.ToString()));
                        }
                        break;
                    case "vector3":
                        {
                            var vec = new Vector3(request.FloatArg("x", 0f), request.FloatArg("y", 0f), request.FloatArg("z", 0f));
                            var method = visualEffectType.GetMethod("SetVector3", new[] { typeof(string), typeof(Vector3) });
                            if (method != null) method.Invoke(component, new object[] { paramName, vec });
                            effects.Add(EffectJson("vfx.parameter", paramName, vec.ToString()));
                        }
                        break;
                    case "vector4":
                    case "color":
                        {
                            if (paramType == "color")
                            {
                                var color = new Color(request.FloatArg("r", 0f), request.FloatArg("g", 0f), request.FloatArg("b", 0f), request.FloatArg("a", 1f));
                                var method = visualEffectType.GetMethod("SetVector4", new[] { typeof(string), typeof(Vector4) });
                                if (method != null) method.Invoke(component, new object[] { paramName, (Vector4)color });
                                effects.Add(EffectJson("vfx.parameter", paramName, color.ToString()));
                            }
                            else
                            {
                                var vec = new Vector4(request.FloatArg("x", 0f), request.FloatArg("y", 0f), request.FloatArg("z", 0f), request.FloatArg("w", 0f));
                                var method = visualEffectType.GetMethod("SetVector4", new[] { typeof(string), typeof(Vector4) });
                                if (method != null) method.Invoke(component, new object[] { paramName, vec });
                                effects.Add(EffectJson("vfx.parameter", paramName, vec.ToString()));
                            }
                        }
                        break;
                    case "texture":
                        {
                            var assetPath = request.StringArg("value");
                            var texture = !string.IsNullOrEmpty(assetPath) ? AssetDatabase.LoadAssetAtPath<Texture>(NormalizeAssetPath(assetPath)) : null;
                            if (texture == null && !string.IsNullOrEmpty(assetPath))
                                return ErrorJson("E_VFX_TEXTURE_NOT_FOUND", "Texture not found: " + assetPath, "Pass a valid Assets/ texture path.");
                            var method = visualEffectType.GetMethod("SetTexture", new[] { typeof(string), typeof(Texture) });
                            if (method != null) method.Invoke(component, new object[] { paramName, texture });
                            effects.Add(EffectJson("vfx.parameter", paramName, assetPath ?? "null"));
                        }
                        break;
                    case "mesh":
                        {
                            var assetPath = request.StringArg("value");
                            var mesh = !string.IsNullOrEmpty(assetPath) ? AssetDatabase.LoadAssetAtPath<Mesh>(NormalizeAssetPath(assetPath)) : null;
                            if (mesh == null && !string.IsNullOrEmpty(assetPath))
                                return ErrorJson("E_VFX_MESH_NOT_FOUND", "Mesh not found: " + assetPath, "Pass a valid Assets/ mesh path.");
                            var method = visualEffectType.GetMethod("SetMesh", new[] { typeof(string), typeof(Mesh) });
                            if (method != null) method.Invoke(component, new object[] { paramName, mesh });
                            effects.Add(EffectJson("vfx.parameter", paramName, assetPath ?? "null"));
                        }
                        break;
                    default:
                        return ErrorJson("E_VFX_PARAMETER_TYPE_UNSUPPORTED", "Unsupported VFX parameter type: " + paramType, "Use float, int, bool, vector2, vector3, vector4, color, texture, mesh, gradient, or curve.");
                }
            }
            catch (TargetInvocationException tie)
            {
                return ErrorJson("E_VFX_PARAMETER_SET_FAILED", "Failed to set VFX parameter: " + (tie.InnerException != null ? tie.InnerException.Message : tie.Message), "Check the parameter name and type match the VFX Graph exposed parameters.");
            }

            EditorUtility.SetDirty(component);
            WriteLog("vfx_parameter target=" + HierarchyPath(go) + " name=" + paramName + " type=" + paramType);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(go))
                .Prop("parameterName", paramName)
                .Prop("parameterType", paramType)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── VFX Control ────────────────────────────────────────────────────────
        // Critically references unity-mcp VfxGraphControl.cs: Play(), Stop(),
        //   pause, Reinit(), playRate, startSeed, resetSeedOnPlay.

private static string VfxControl(BridgeRequest request)
        {
            RequireVfxPackageInstalled();
            var visualEffectType = RequireVisualEffectType();
            var go = ResolveUniqueTarget(request.StringArg("target"), "VFX control requires --target <go>.");
            var component = go.GetComponent(visualEffectType);
            if (component == null)
                return ErrorJson("E_VFX_COMPONENT_NOT_FOUND", "VisualEffect component not found on target: " + HierarchyPath(go), "Pass a target with VisualEffect.");
            var action = request.StringArg("action") ?? "play";
            var effects = new List<string>();

            switch (action.ToLowerInvariant())
            {
                case "play":
                    {
                        var method = visualEffectType.GetMethod("Play", Type.EmptyTypes);
                        if (method != null) method.Invoke(component, null);
                        effects.Add(EffectJson("vfx.control", "action", "play"));
                    }
                    break;
                case "stop":
                    {
                        var method = visualEffectType.GetMethod("Stop", Type.EmptyTypes);
                        if (method != null) method.Invoke(component, null);
                        effects.Add(EffectJson("vfx.control", "action", "stop"));
                    }
                    break;
                case "pause":
                    {
                        var pauseProp = visualEffectType.GetProperty("pause", BindingFlags.Instance | BindingFlags.Public);
                        if (pauseProp != null && pauseProp.CanWrite)
                        {
                            var current = pauseProp.GetValue(component, null);
                            pauseProp.SetValue(component, !(bool)current, null);
                        }
                        effects.Add(EffectJson("vfx.control", "action", "pause"));
                    }
                    break;
                case "reinit":
                    {
                        var method = visualEffectType.GetMethod("Reinit", Type.EmptyTypes);
                        if (method != null) method.Invoke(component, null);
                        effects.Add(EffectJson("vfx.control", "action", "reinit"));
                    }
                    break;
                case "rate":
                    {
                        var rate = request.OptionalFloatArg("rate");
                        if (!rate.HasValue) return ErrorJson("E_VFX_RATE_REQUIRED", "Rate action requires --rate <value>.", "Pass a playback rate (1.0 = normal).");
                        var playRateProp = visualEffectType.GetProperty("playRate", BindingFlags.Instance | BindingFlags.Public);
                        if (playRateProp != null && playRateProp.CanWrite) playRateProp.SetValue(component, rate.Value, null);
                        effects.Add(EffectJson("vfx.control", "playRate", Number(rate.Value)));
                    }
                    break;
                case "seed":
                    {
                        var seed = request.OptionalIntArg("seed");
                        if (!seed.HasValue) return ErrorJson("E_VFX_SEED_REQUIRED", "Seed action requires --seed <value>.", "Pass an integer seed.");
                        var startSeedProp = visualEffectType.GetProperty("startSeed", BindingFlags.Instance | BindingFlags.Public);
                        if (startSeedProp != null && startSeedProp.CanWrite) startSeedProp.SetValue(component, seed.Value, null);
                        var resetProp = request.OptionalBoolArg("resetSeedOnPlay");
                        if (resetProp.HasValue)
                        {
                            var resetSeedProp = visualEffectType.GetProperty("resetSeedOnPlay", BindingFlags.Instance | BindingFlags.Public);
                            if (resetSeedProp != null && resetSeedProp.CanWrite) resetSeedProp.SetValue(component, resetProp.Value, null);
                        }
                        effects.Add(EffectJson("vfx.control", "startSeed", seed.Value.ToString(CultureInfo.InvariantCulture)));
                    }
                    break;
                default:
                    return ErrorJson("E_VFX_CONTROL_ACTION_UNSUPPORTED", "Unsupported VFX control action: " + action, "Use play, stop, pause, reinit, rate, or seed.");
            }

            EditorSceneManager.MarkSceneDirty(go.scene);
            WriteLog("vfx_control target=" + HierarchyPath(go) + " action=" + action);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(go))
                .Prop("action", action)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── VFX Event ──────────────────────────────────────────────────────────
        // Critically references unity-mcp VfxGraphWrite.cs: SendEvent(eventName, attr)
        //   and CreateVFXEventAttribute() + attr.SetVector3/SetFloat.

private static string VfxEvent(BridgeRequest request)
        {
            RequireVfxPackageInstalled();
            var visualEffectType = RequireVisualEffectType();
            var go = ResolveUniqueTarget(request.StringArg("target"), "VFX event requires --target <go>.");
            var component = go.GetComponent(visualEffectType);
            if (component == null)
                return ErrorJson("E_VFX_COMPONENT_NOT_FOUND", "VisualEffect component not found on target: " + HierarchyPath(go), "Pass a target with VisualEffect.");
            var eventName = request.StringArg("eventName");
            if (string.IsNullOrEmpty(eventName))
                return ErrorJson("E_VFX_EVENT_NAME_REQUIRED", "VFX event requires --event-name <name>.", "Pass the VFX Graph event name.");

            // Check if there are attribute payloads
            var hasAttrs = request.HasArg("attributeName");
            object eventAttr = null;
            Type vfxEventAttrType = null;

            if (hasAttrs)
            {
                // Create VFXEventAttribute via CreateVFXEventAttribute()
                var createMethod = visualEffectType.GetMethod("CreateVFXEventAttribute", Type.EmptyTypes);
                if (createMethod != null)
                {
                    eventAttr = createMethod.Invoke(component, null);
                    vfxEventAttrType = eventAttr?.GetType();
                }

                if (vfxEventAttrType != null && eventAttr != null)
                {
                    var attrName = request.StringArg("attributeName");
                    if (!string.IsNullOrEmpty(attrName))
                    {
                        // Set Vector3 attributes (position, velocity, etc.)
                        var hasVector = request.HasArg("attributeX") || request.HasArg("attributeY") || request.HasArg("attributeZ");
                        if (hasVector)
                        {
                            var setVec3Method = vfxEventAttrType.GetMethod("SetVector3", new[] { typeof(string), typeof(Vector3) });
                            if (setVec3Method != null)
                            {
                                var vec = new Vector3(request.FloatArg("attributeX", 0f), request.FloatArg("attributeY", 0f), request.FloatArg("attributeZ", 0f));
                                setVec3Method.Invoke(eventAttr, new object[] { attrName, vec });
                            }
                        }
                        // Set float attributes (size, etc.)
                        var hasSize = request.HasArg("attributeSize");
                        if (hasSize)
                        {
                            var setFloatMethod = vfxEventAttrType.GetMethod("SetFloat", new[] { typeof(string), typeof(float) });
                            if (setFloatMethod != null)
                            {
                                setFloatMethod.Invoke(eventAttr, new object[] { attrName, request.FloatArg("attributeSize", 0f) });
                            }
                        }
                    }
                }
            }

            // SendEvent(string eventName, VFXEventAttribute eventAttribute)
            var sendMethod = eventAttr != null
                ? visualEffectType.GetMethod("SendEvent", new[] { typeof(string), vfxEventAttrType ?? typeof(object) })
                : visualEffectType.GetMethod("SendEvent", new[] { typeof(string) });
            if (sendMethod == null)
                sendMethod = visualEffectType.GetMethod("SendEvent", new[] { typeof(string) });
            if (sendMethod != null)
            {
                var args = eventAttr != null ? new object[] { eventName, eventAttr } : new object[] { eventName };
                sendMethod.Invoke(component, args);
            }

            WriteLog("vfx_event target=" + HierarchyPath(go) + " event=" + eventName);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(go))
                .Prop("eventName", eventName)
                .Prop("hasAttributes", hasAttrs)
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "vfx.event")
                    .Prop("eventName", eventName)
                    .Prop("target", HierarchyPath(go))
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── VFX Template ──────────────────────────────────────────────────────
        // Critically references unity-mcp VfxGraphAssets.cs: template search paths
        //   in {packageResolvedPath}/Editor/Templates, {packageResolvedPath}/Samples.
        //   Uses AssetDatabase.CopyAsset for template cloning.

private static string VfxTemplate(BridgeRequest request)
        {
            RequireVfxPackageInstalled();
            var assetType = RequireVfxAssetType();
            var destPath = RequireVfxGraphPath(request.StringArg("path"));
            var templateName = request.StringArg("template");
            if (string.IsNullOrEmpty(templateName))
                return ErrorJson("E_VFX_TEMPLATE_NAME_REQUIRED", "VFX template requires --template <name>.", "Pass a template name (e.g. AR/AugmentedRealityTemplate, SimpleParticle). Use 'vfx template --template list' to discover templates.");

            // List templates
            if (string.Equals(templateName, "list", StringComparison.OrdinalIgnoreCase))
            {
                var templates = DiscoverVfxTemplates();
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropArray("templates", arr => { foreach (var t in templates) arr.Value(t); })
                    .Prop("count", (long)templates.Count)
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }

            var overwrite = request.BoolArg("overwrite", false);
            if (AssetDatabase.LoadAssetAtPath(destPath, assetType) != null && !overwrite)
                return ErrorJson("E_VFX_GRAPH_EXISTS", "VFX Graph already exists: " + destPath, "Pass --overwrite or choose a different path.");

            // Find template
            var templatePath = ResolveVfxTemplatePath(templateName);
            if (string.IsNullOrEmpty(templatePath))
                return ErrorJson("E_VFX_TEMPLATE_NOT_FOUND", "VFX Graph template not found: " + templateName, "Use 'vfx template --template list' to discover available templates.");

            CreateAssetDirectory(destPath);
            if (!AssetDatabase.CopyAsset(templatePath, destPath))
                return ErrorJson("E_VFX_TEMPLATE_COPY_FAILED", "Failed to copy template to: " + destPath, "Check destination path and Unity Console for errors.");

            AssetDatabase.ImportAsset(destPath, ImportAssetOptions.ForceUpdate);
            var created = AssetDatabase.LoadAssetAtPath(destPath, assetType);
            WriteLog("vfx_template_created template=" + templateName + " path=" + destPath);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("graph", VfxGraphJson(destPath, created))
                .Prop("templateName", templateName)
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "vfx.template")
                    .Prop("template", templateName)
                    .Prop("path", destPath)
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static List<string> DiscoverVfxTemplates()
        {
            var results = new List<string>();
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.unity.visualeffectgraph");
            if (packageInfo == null) return results;
            var searchPaths = new[]
            {
                System.IO.Path.Combine(packageInfo.resolvedPath, "Editor", "Templates"),
                System.IO.Path.Combine(packageInfo.resolvedPath, "Samples"),
                System.IO.Path.Combine(packageInfo.resolvedPath, "Samples~")
            };
            foreach (var searchPath in searchPaths)
            {
                if (!Directory.Exists(searchPath)) continue;
                foreach (var file in Directory.GetFiles(searchPath, "*.vfx", SearchOption.AllDirectories))
                {
                    var relative = file.Substring(searchPath.Length).TrimStart(System.IO.Path.DirectorySeparatorChar, '/');
                    var name = System.IO.Path.GetFileNameWithoutExtension(relative);
                    if (!results.Contains(name)) results.Add(name);
                }
            }
            results.Sort();
            return results;
        }

        private static string ResolveVfxTemplatePath(string templateName)
        {
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.unity.visualeffectgraph");
            if (packageInfo == null) return null;
            var searchPaths = new[]
            {
                System.IO.Path.Combine(packageInfo.resolvedPath, "Editor", "Templates"),
                System.IO.Path.Combine(packageInfo.resolvedPath, "Samples"),
                System.IO.Path.Combine(packageInfo.resolvedPath, "Samples~")
            };
            foreach (var searchPath in searchPaths)
            {
                if (!Directory.Exists(searchPath)) continue;
                // Try exact file name match
                var exactFile = System.IO.Path.Combine(searchPath, templateName + ".vfx");
                if (File.Exists(exactFile)) return ToAssetPath(exactFile);
                // Try nested path match
                var nestedFiles = Directory.GetFiles(searchPath, "*.vfx", SearchOption.AllDirectories);
                foreach (var file in nestedFiles)
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(file);
                    if (string.Equals(name, templateName, StringComparison.OrdinalIgnoreCase)) return ToAssetPath(file);
                    var relative = file.Substring(searchPath.Length).TrimStart(System.IO.Path.DirectorySeparatorChar, '/');
                    if (string.Equals(relative, templateName + ".vfx", StringComparison.OrdinalIgnoreCase)) return ToAssetPath(file);
                }
            }
            return null;
        }

        private static string ToAssetPath(string fullPath)
        {
            // Convert absolute path to Unity asset path if under Packages/
            var projectRoot = ProjectRoot();
            if (fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(projectRoot.Length).TrimStart(System.IO.Path.DirectorySeparatorChar, '/').Replace('\\', '/');
            }
            return fullPath.Replace('\\', '/');
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
            ApplyParticleMainFields(ps, request, "vfx.particle-create", effects);
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
            var effects = new List<string>();
            ApplyParticleMainFields(ps, request, "vfx.particle-main", effects);
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
            if (!ApplyParticleModuleFields(ps, request, module, effects))
            {
                return ErrorJson("E_UNKNOWN_MODULE", "Unknown or unsupported particle module: " + module, "Use color, size, rotation, velocity, force, noise, collision, subEmitter, textureAnimation, lights, trails, inheritVelocity, or limitVelocity.");
            }
            EditorUtility.SetDirty(ps);
            EditorSceneManager.MarkSceneDirty(ps.gameObject.scene);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(ps.gameObject))
                .Prop("module", module)
                .PropRaw("particleSystem", ParticleSystemJson(ps))
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
            ApplyLineRendererFields(line, request, "vfx.line-create", effects);
            EditorUtility.SetDirty(line);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            WriteLog("vfx_line_create target=" + HierarchyPath(gameObject));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropRaw("line", LineRendererJson(line))
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
            if (request.HasArg("positions"))
            {
                var positions = ParseVector3ArrayArg(request, "positions");
                line.positionCount = positions.Length;
                line.SetPositions(positions);
                effects.Add(EffectJson("vfx.line-positions", "positions", positions.Length.ToString(CultureInfo.InvariantCulture)));
            }
            var index = request.OptionalIntArg("index");
            if (index.HasValue && request.HasArg("position"))
            {
                if (index.Value < 0 || index.Value >= line.positionCount)
                {
                    return ErrorJson("E_INDEX_OUT_OF_RANGE", "Line position index out of range: " + index.Value, "Use an index between 0 and " + (line.positionCount - 1) + ".");
                }
                var position = ParseVector3Arg(request, "position");
                line.SetPosition(index.Value, position);
                effects.Add(EffectJson("vfx.line-positions", "index" + index.Value.ToString(CultureInfo.InvariantCulture), Vector3Json(position)));
            }
            EditorUtility.SetDirty(line);
            EditorSceneManager.MarkSceneDirty(line.gameObject.scene);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(line.gameObject))
                .PropRaw("line", LineRendererJson(line))
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
                return ErrorJson("E_TRAIL_RENDERER_NOT_FOUND", "TrailRenderer not found on target: " + HierarchyPath(gameObject), "Pass create=true to add one or choose a target with TrailRenderer.");
            }
            ApplyTrailRendererFields(trail, request, effects);
            EditorUtility.SetDirty(trail);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropRaw("trail", TrailRendererJson(trail))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ===== Helpers =====

        private static ParticleSystem ResolveParticleSystem(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                var first = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
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
                var first = UnityEngine.Object.FindObjectsOfType<LineRenderer>();
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
            var all = GameObject.FindObjectsOfType<GameObject>();
            foreach (var obj in all)
            {
                if (HierarchyPath(obj) == target) return obj;
            }
            return null;
        }

        private static void ApplyParticleMainFields(ParticleSystem ps, BridgeRequest request, string effectType, List<string> effects)
        {
            // MainModule 是 struct wrapper；必须每次从 ParticleSystem 获取后直接赋值。
            var main = ps.main;
            if (request.HasArg("duration")) { main.duration = request.OptionalFloatArg("duration").Value; effects.Add(EffectJson(effectType, "duration", Number(main.duration))); }
            if (request.HasArg("startLifetime")) { main.startLifetime = request.OptionalFloatArg("startLifetime").Value; effects.Add(EffectJson(effectType, "startLifetime", Number(main.startLifetime.constant))); }
            if (request.HasArg("startSpeed")) { main.startSpeed = request.OptionalFloatArg("startSpeed").Value; effects.Add(EffectJson(effectType, "startSpeed", Number(main.startSpeed.constant))); }
            if (request.HasArg("startSize")) { main.startSize = request.OptionalFloatArg("startSize").Value; effects.Add(EffectJson(effectType, "startSize", Number(main.startSize.constant))); }
            if (request.HasArg("startColor")) { main.startColor = ParseColor(request.StringArg("startColor")); effects.Add(EffectJson(effectType, "startColor", request.StringArg("startColor"))); }
            if (request.HasArg("startRotation")) { main.startRotation = request.OptionalFloatArg("startRotation").Value * Mathf.Deg2Rad; effects.Add(EffectJson(effectType, "startRotation", Number(main.startRotation.constant * Mathf.Rad2Deg))); }
            if (request.HasArg("gravityModifier")) { main.gravityModifier = request.OptionalFloatArg("gravityModifier").Value; effects.Add(EffectJson(effectType, "gravityModifier", Number(main.gravityModifier.constant))); }
            if (request.HasArg("maxParticles")) { main.maxParticles = request.OptionalIntArg("maxParticles").Value; effects.Add(EffectJson(effectType, "maxParticles", main.maxParticles.ToString(CultureInfo.InvariantCulture))); }
            if (request.HasArg("loop")) { main.loop = request.OptionalBoolArg("loop").Value; effects.Add(EffectJson(effectType, "loop", Bool(main.loop))); }
            if (request.HasArg("playOnAwake")) { main.playOnAwake = request.OptionalBoolArg("playOnAwake").Value; effects.Add(EffectJson(effectType, "playOnAwake", Bool(main.playOnAwake))); }
            if (request.HasArg("simulationSpace")) { main.simulationSpace = ParseEnumArg<ParticleSystemSimulationSpace>(request.StringArg("simulationSpace"), "E_PARTICLE_SIMULATION_SPACE_INVALID", "Use Local, World, or Custom."); effects.Add(EffectJson(effectType, "simulationSpace", main.simulationSpace.ToString())); }
            if (request.HasArg("scalingMode")) { main.scalingMode = ParseEnumArg<ParticleSystemScalingMode>(request.StringArg("scalingMode"), "E_PARTICLE_SCALING_MODE_INVALID", "Use Hierarchy, Local, or Shape."); effects.Add(EffectJson(effectType, "scalingMode", main.scalingMode.ToString())); }
        }

        private static bool ApplyParticleModuleFields(ParticleSystem ps, BridgeRequest request, string module, List<string> effects)
        {
            var key = (module ?? string.Empty).Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            var enabled = request.OptionalBoolArg("enabled");
            switch (key)
            {
                case "coloroverlifetime":
                case "color":
                    var color = ps.colorOverLifetime;
                    if (enabled.HasValue) { color.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "colorOverLifetime.enabled", Bool(color.enabled))); }
                    if (request.HasArg("color")) { color.color = new ParticleSystem.MinMaxGradient(ParseColor(request.StringArg("color"))); effects.Add(EffectJson("vfx.particle-module", "colorOverLifetime.color", request.StringArg("color"))); }
                    return true;
                case "sizeoverlifetime":
                case "size":
                    var size = ps.sizeOverLifetime;
                    if (enabled.HasValue) { size.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "sizeOverLifetime.enabled", Bool(size.enabled))); }
                    if (request.HasArg("size")) { size.size = new ParticleSystem.MinMaxCurve(request.OptionalFloatArg("size").Value); effects.Add(EffectJson("vfx.particle-module", "sizeOverLifetime.size", Number(size.size.constant))); }
                    return true;
                case "rotationoverlifetime":
                case "rotation":
                    var rotation = ps.rotationOverLifetime;
                    if (enabled.HasValue) { rotation.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "rotationOverLifetime.enabled", Bool(rotation.enabled))); }
                    if (request.HasArg("rotation")) { rotation.z = new ParticleSystem.MinMaxCurve(request.OptionalFloatArg("rotation").Value * Mathf.Deg2Rad); effects.Add(EffectJson("vfx.particle-module", "rotationOverLifetime.z", request.StringArg("rotation"))); }
                    return true;
                case "velocityoverlifetime":
                case "velocity":
                    var velocity = ps.velocityOverLifetime;
                    if (enabled.HasValue) { velocity.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "velocityOverLifetime.enabled", Bool(velocity.enabled))); }
                    if (request.HasArg("x")) { velocity.x = new ParticleSystem.MinMaxCurve(request.OptionalFloatArg("x").Value); effects.Add(EffectJson("vfx.particle-module", "velocityOverLifetime.x", Number(velocity.x.constant))); }
                    if (request.HasArg("y")) { velocity.y = new ParticleSystem.MinMaxCurve(request.OptionalFloatArg("y").Value); effects.Add(EffectJson("vfx.particle-module", "velocityOverLifetime.y", Number(velocity.y.constant))); }
                    if (request.HasArg("z")) { velocity.z = new ParticleSystem.MinMaxCurve(request.OptionalFloatArg("z").Value); effects.Add(EffectJson("vfx.particle-module", "velocityOverLifetime.z", Number(velocity.z.constant))); }
                    if (request.HasArg("space")) { velocity.space = ParseEnumArg<ParticleSystemSimulationSpace>(request.StringArg("space"), "E_PARTICLE_SPACE_INVALID", "Use Local or World."); effects.Add(EffectJson("vfx.particle-module", "velocityOverLifetime.space", velocity.space.ToString())); }
                    if (request.HasArg("speedModifier")) { velocity.speedModifier = new ParticleSystem.MinMaxCurve(request.OptionalFloatArg("speedModifier").Value); effects.Add(EffectJson("vfx.particle-module", "velocityOverLifetime.speedModifier", Number(velocity.speedModifier.constant))); }
                    return true;
                case "forceoverlifetime":
                case "force":
                    var force = ps.forceOverLifetime;
                    if (enabled.HasValue) { force.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "forceOverLifetime.enabled", Bool(force.enabled))); }
                    if (request.HasArg("x")) { force.x = new ParticleSystem.MinMaxCurve(request.OptionalFloatArg("x").Value); effects.Add(EffectJson("vfx.particle-module", "forceOverLifetime.x", Number(force.x.constant))); }
                    if (request.HasArg("y")) { force.y = new ParticleSystem.MinMaxCurve(request.OptionalFloatArg("y").Value); effects.Add(EffectJson("vfx.particle-module", "forceOverLifetime.y", Number(force.y.constant))); }
                    if (request.HasArg("z")) { force.z = new ParticleSystem.MinMaxCurve(request.OptionalFloatArg("z").Value); effects.Add(EffectJson("vfx.particle-module", "forceOverLifetime.z", Number(force.z.constant))); }
                    if (request.HasArg("space")) { force.space = ParseEnumArg<ParticleSystemSimulationSpace>(request.StringArg("space"), "E_PARTICLE_SPACE_INVALID", "Use Local or World."); effects.Add(EffectJson("vfx.particle-module", "forceOverLifetime.space", force.space.ToString())); }
                    return true;
                case "noise":
                    var noise = ps.noise;
                    if (enabled.HasValue) { noise.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "noise.enabled", Bool(noise.enabled))); }
                    if (request.HasArg("strength")) { noise.strength = request.OptionalFloatArg("strength").Value; effects.Add(EffectJson("vfx.particle-module", "noise.strength", Number(noise.strength.constant))); }
                    if (request.HasArg("frequency")) { noise.frequency = request.OptionalFloatArg("frequency").Value; effects.Add(EffectJson("vfx.particle-module", "noise.frequency", Number(noise.frequency))); }
                    if (request.HasArg("scrollSpeed")) { noise.scrollSpeed = request.OptionalFloatArg("scrollSpeed").Value; effects.Add(EffectJson("vfx.particle-module", "noise.scrollSpeed", Number(noise.scrollSpeed.constant))); }
                    if (request.HasArg("octaveCount")) { noise.octaveCount = request.OptionalIntArg("octaveCount").Value; effects.Add(EffectJson("vfx.particle-module", "noise.octaveCount", noise.octaveCount.ToString(CultureInfo.InvariantCulture))); }
                    if (request.HasArg("quality")) { noise.quality = ParseEnumArg<ParticleSystemNoiseQuality>(request.StringArg("quality"), "E_PARTICLE_NOISE_QUALITY_INVALID", "Use Low, Medium, or High."); effects.Add(EffectJson("vfx.particle-module", "noise.quality", noise.quality.ToString())); }
                    return true;
                case "collision":
                    var collision = ps.collision;
                    if (enabled.HasValue) { collision.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "collision.enabled", Bool(collision.enabled))); }
                    if (request.HasArg("collisionType")) { collision.type = ParseEnumArg<ParticleSystemCollisionType>(request.StringArg("collisionType"), "E_PARTICLE_COLLISION_TYPE_INVALID", "Use Planes or World."); effects.Add(EffectJson("vfx.particle-module", "collision.type", collision.type.ToString())); }
                    if (request.HasArg("collisionMode")) { collision.mode = ParseEnumArg<ParticleSystemCollisionMode>(request.StringArg("collisionMode"), "E_PARTICLE_COLLISION_MODE_INVALID", "Use Collision3D or Collision2D."); effects.Add(EffectJson("vfx.particle-module", "collision.mode", collision.mode.ToString())); }
                    if (request.HasArg("dampen")) { collision.dampen = request.OptionalFloatArg("dampen").Value; effects.Add(EffectJson("vfx.particle-module", "collision.dampen", Number(collision.dampen.constant))); }
                    if (request.HasArg("bounce")) { collision.bounce = request.OptionalFloatArg("bounce").Value; effects.Add(EffectJson("vfx.particle-module", "collision.bounce", Number(collision.bounce.constant))); }
                    if (request.HasArg("lifetimeLoss")) { collision.lifetimeLoss = request.OptionalFloatArg("lifetimeLoss").Value; effects.Add(EffectJson("vfx.particle-module", "collision.lifetimeLoss", Number(collision.lifetimeLoss.constant))); }
                    if (request.HasArg("minKillSpeed")) { collision.minKillSpeed = request.OptionalFloatArg("minKillSpeed").Value; effects.Add(EffectJson("vfx.particle-module", "collision.minKillSpeed", Number(collision.minKillSpeed))); }
                    if (request.HasArg("maxKillSpeed")) { collision.maxKillSpeed = request.OptionalFloatArg("maxKillSpeed").Value; effects.Add(EffectJson("vfx.particle-module", "collision.maxKillSpeed", Number(collision.maxKillSpeed))); }
                    if (request.HasArg("radiusScale")) { collision.radiusScale = request.OptionalFloatArg("radiusScale").Value; effects.Add(EffectJson("vfx.particle-module", "collision.radiusScale", Number(collision.radiusScale))); }
                    return true;
                case "subemitters":
                case "subemitter":
                case "sub":
                    var sub = ps.subEmitters;
                    if (enabled.HasValue) { sub.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "subEmitters.enabled", Bool(sub.enabled))); }
                    ApplySubEmitterFields(sub, request, effects);
                    return true;
                case "textureanimation":
                case "texturesheetanimation":
                case "texture":
                    var texture = ps.textureSheetAnimation;
                    if (enabled.HasValue) { texture.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "textureSheetAnimation.enabled", Bool(texture.enabled))); }
                    return true;
                case "lights":
                    var lights = ps.lights;
                    if (enabled.HasValue) { lights.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "lights.enabled", Bool(lights.enabled))); }
                    return true;
                case "trails":
                case "trail":
                    var trails = ps.trails;
                    if (enabled.HasValue) { trails.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "trails.enabled", Bool(trails.enabled))); }
                    if (request.HasArg("ratio")) { trails.ratio = request.OptionalFloatArg("ratio").Value; effects.Add(EffectJson("vfx.particle-module", "trails.ratio", Number(trails.ratio))); }
                    if (request.HasArg("lifetime")) { trails.lifetime = request.OptionalFloatArg("lifetime").Value; effects.Add(EffectJson("vfx.particle-module", "trails.lifetime", Number(trails.lifetime.constant))); }
                    if (request.HasArg("width")) { trails.widthOverTrail = new ParticleSystem.MinMaxCurve(request.OptionalFloatArg("width").Value); effects.Add(EffectJson("vfx.particle-module", "trails.width", Number(trails.widthOverTrail.constant))); }
                    if (request.HasArg("color")) { trails.colorOverTrail = new ParticleSystem.MinMaxGradient(ParseColor(request.StringArg("color"))); effects.Add(EffectJson("vfx.particle-module", "trails.color", request.StringArg("color"))); }
                    return true;
                case "inheritvelocity":
                    var inherit = ps.inheritVelocity;
                    if (enabled.HasValue) { inherit.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "inheritVelocity.enabled", Bool(inherit.enabled))); }
                    if (request.HasArg("mode")) { inherit.mode = ParseEnumArg<ParticleSystemInheritVelocityMode>(request.StringArg("mode"), "E_PARTICLE_INHERIT_MODE_INVALID", "Use Initial or Current."); effects.Add(EffectJson("vfx.particle-module", "inheritVelocity.mode", inherit.mode.ToString())); }
                    if (request.HasArg("curve")) { inherit.curve = new ParticleSystem.MinMaxCurve(request.OptionalFloatArg("curve") ?? 0f); effects.Add(EffectJson("vfx.particle-module", "inheritVelocity.curve", Number(inherit.curve.constant))); }
                    return true;
                case "limitvelocityoverlifetime":
                case "limitvelocity":
                    var limit = ps.limitVelocityOverLifetime;
                    if (enabled.HasValue) { limit.enabled = enabled.Value; effects.Add(EffectJson("vfx.particle-module", "limitVelocityOverLifetime.enabled", Bool(limit.enabled))); }
                    if (request.HasArg("speed")) { limit.limit = new ParticleSystem.MinMaxCurve(request.OptionalFloatArg("speed").Value); effects.Add(EffectJson("vfx.particle-module", "limitVelocityOverLifetime.limit", Number(limit.limit.constant))); }
                    if (request.HasArg("dampen")) { limit.dampen = request.OptionalFloatArg("dampen").Value; effects.Add(EffectJson("vfx.particle-module", "limitVelocityOverLifetime.dampen", Number(limit.dampen))); }
                    return true;
                default:
                    return false;
            }
        }

        private static void ApplyLineRendererFields(LineRenderer line, BridgeRequest request, string effectType, List<string> effects)
        {
            if (request.HasArg("startWidth")) { line.startWidth = request.OptionalFloatArg("startWidth").Value; effects.Add(EffectJson(effectType, "startWidth", Number(line.startWidth))); }
            if (request.HasArg("endWidth")) { line.endWidth = request.OptionalFloatArg("endWidth").Value; effects.Add(EffectJson(effectType, "endWidth", Number(line.endWidth))); }
            if (request.HasArg("widthMultiplier")) { line.widthMultiplier = request.OptionalFloatArg("widthMultiplier").Value; effects.Add(EffectJson(effectType, "widthMultiplier", Number(line.widthMultiplier))); }
            if (request.HasArg("widthCurve")) { line.widthCurve = ParseAnimationCurveArg(request, "widthCurve"); effects.Add(EffectJson(effectType, "widthCurve", CurveJson(line.widthCurve))); }
            if (request.HasArg("startColor")) { line.startColor = ParseColor(request.StringArg("startColor")); effects.Add(EffectJson(effectType, "startColor", request.StringArg("startColor"))); }
            if (request.HasArg("endColor")) { line.endColor = ParseColor(request.StringArg("endColor")); effects.Add(EffectJson(effectType, "endColor", request.StringArg("endColor"))); }
            if (request.HasArg("colorGradient")) { line.colorGradient = ParseGradientArg(request, "colorGradient"); effects.Add(EffectJson(effectType, "colorGradient", GradientJson(line.colorGradient))); }
            if (request.HasArg("material")) { line.sharedMaterial = ResolveOptionalMaterial(request.StringArg("material")); effects.Add(EffectJson(effectType, "material", line.sharedMaterial == null ? "null" : AssetDatabase.GetAssetPath(line.sharedMaterial))); }
            if (request.HasArg("positionCount")) { line.positionCount = request.OptionalIntArg("positionCount").Value; effects.Add(EffectJson(effectType, "positionCount", line.positionCount.ToString(CultureInfo.InvariantCulture))); }
            if (request.HasArg("positions")) { var positions = ParseVector3ArrayArg(request, "positions"); line.positionCount = positions.Length; line.SetPositions(positions); effects.Add(EffectJson(effectType, "positions", positions.Length.ToString(CultureInfo.InvariantCulture))); }
            if (request.HasArg("loop")) { line.loop = request.OptionalBoolArg("loop").Value; effects.Add(EffectJson(effectType, "loop", Bool(line.loop))); }
            if (request.HasArg("useWorldSpace")) { line.useWorldSpace = request.OptionalBoolArg("useWorldSpace").Value; effects.Add(EffectJson(effectType, "useWorldSpace", Bool(line.useWorldSpace))); }
            if (request.HasArg("alignment")) { line.alignment = ParseEnumArg<LineAlignment>(request.StringArg("alignment"), "E_LINE_ALIGNMENT_INVALID", "Use View or TransformZ."); effects.Add(EffectJson(effectType, "alignment", line.alignment.ToString())); }
            if (request.HasArg("textureMode")) { line.textureMode = ParseEnumArg<LineTextureMode>(request.StringArg("textureMode"), "E_LINE_TEXTURE_MODE_INVALID", "Use Stretch, Tile, DistributePerSegment, or RepeatPerSegment."); effects.Add(EffectJson(effectType, "textureMode", line.textureMode.ToString())); }
            if (request.HasArg("numCornerVertices")) { line.numCornerVertices = request.OptionalIntArg("numCornerVertices").Value; effects.Add(EffectJson(effectType, "numCornerVertices", line.numCornerVertices.ToString(CultureInfo.InvariantCulture))); }
            if (request.HasArg("numCapVertices")) { line.numCapVertices = request.OptionalIntArg("numCapVertices").Value; effects.Add(EffectJson(effectType, "numCapVertices", line.numCapVertices.ToString(CultureInfo.InvariantCulture))); }
        }

        private static void ApplyTrailRendererFields(TrailRenderer trail, BridgeRequest request, List<string> effects)
        {
            if (request.HasArg("time")) { trail.time = request.OptionalFloatArg("time").Value; effects.Add(EffectJson("vfx.trail", "time", Number(trail.time))); }
            if (request.HasArg("startWidth")) { trail.startWidth = request.OptionalFloatArg("startWidth").Value; effects.Add(EffectJson("vfx.trail", "startWidth", Number(trail.startWidth))); }
            if (request.HasArg("endWidth")) { trail.endWidth = request.OptionalFloatArg("endWidth").Value; effects.Add(EffectJson("vfx.trail", "endWidth", Number(trail.endWidth))); }
            if (request.HasArg("widthMultiplier")) { trail.widthMultiplier = request.OptionalFloatArg("widthMultiplier").Value; effects.Add(EffectJson("vfx.trail", "widthMultiplier", Number(trail.widthMultiplier))); }
            if (request.HasArg("widthCurve")) { trail.widthCurve = ParseAnimationCurveArg(request, "widthCurve"); effects.Add(EffectJson("vfx.trail", "widthCurve", CurveJson(trail.widthCurve))); }
            if (request.HasArg("startColor")) { trail.startColor = ParseColor(request.StringArg("startColor")); effects.Add(EffectJson("vfx.trail", "startColor", request.StringArg("startColor"))); }
            if (request.HasArg("endColor")) { trail.endColor = ParseColor(request.StringArg("endColor")); effects.Add(EffectJson("vfx.trail", "endColor", request.StringArg("endColor"))); }
            if (request.HasArg("colorGradient")) { trail.colorGradient = ParseGradientArg(request, "colorGradient"); effects.Add(EffectJson("vfx.trail", "colorGradient", GradientJson(trail.colorGradient))); }
            if (request.HasArg("material")) { trail.sharedMaterial = ResolveOptionalMaterial(request.StringArg("material")); effects.Add(EffectJson("vfx.trail", "material", trail.sharedMaterial == null ? "null" : AssetDatabase.GetAssetPath(trail.sharedMaterial))); }
            if (request.HasArg("minVertexDistance")) { trail.minVertexDistance = request.OptionalFloatArg("minVertexDistance").Value; effects.Add(EffectJson("vfx.trail", "minVertexDistance", Number(trail.minVertexDistance))); }
            if (request.HasArg("autodestruct")) { trail.autodestruct = request.OptionalBoolArg("autodestruct").Value; effects.Add(EffectJson("vfx.trail", "autodestruct", Bool(trail.autodestruct))); }
            if (request.HasArg("emitting")) { trail.emitting = request.OptionalBoolArg("emitting").Value; effects.Add(EffectJson("vfx.trail", "emitting", Bool(trail.emitting))); }
            if (request.HasArg("alignment")) { trail.alignment = ParseEnumArg<LineAlignment>(request.StringArg("alignment"), "E_TRAIL_ALIGNMENT_INVALID", "Use View or TransformZ."); effects.Add(EffectJson("vfx.trail", "alignment", trail.alignment.ToString())); }
            if (request.HasArg("textureMode")) { trail.textureMode = ParseEnumArg<LineTextureMode>(request.StringArg("textureMode"), "E_TRAIL_TEXTURE_MODE_INVALID", "Use Stretch, Tile, DistributePerSegment, or RepeatPerSegment."); effects.Add(EffectJson("vfx.trail", "textureMode", trail.textureMode.ToString())); }
            if (request.HasArg("numCornerVertices")) { trail.numCornerVertices = request.OptionalIntArg("numCornerVertices").Value; effects.Add(EffectJson("vfx.trail", "numCornerVertices", trail.numCornerVertices.ToString(CultureInfo.InvariantCulture))); }
            if (request.HasArg("numCapVertices")) { trail.numCapVertices = request.OptionalIntArg("numCapVertices").Value; effects.Add(EffectJson("vfx.trail", "numCapVertices", trail.numCapVertices.ToString(CultureInfo.InvariantCulture))); }
        }

        private static void ApplySubEmitterFields(ParticleSystem.SubEmittersModule sub, BridgeRequest request, List<string> effects)
        {
            var action = (request.OptionalStringArg("subEmitterAction") ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrEmpty(action)) return;
            if (action == "clear")
            {
                // Unity 2022.3 没有批量清空 API，倒序删除可避免索引移动。
                for (var i = sub.subEmittersCount - 1; i >= 0; i--)
                {
                    sub.RemoveSubEmitter(i);
                }
                effects.Add(EffectJson("vfx.particle-module", "subEmitters", "cleared"));
                return;
            }
            if (action == "remove")
            {
                var index = request.OptionalIntArg("index") ?? 0;
                if (index < 0 || index >= sub.subEmittersCount) throw new BridgeCommandException("E_SUB_EMITTER_INDEX_INVALID", "Sub-emitter index out of range: " + index, "Pass a valid sub-emitter index.");
                sub.RemoveSubEmitter(index);
                effects.Add(EffectJson("vfx.particle-module", "subEmitters", "removed"));
                return;
            }
            if (action == "add")
            {
                var target = request.StringArg("subEmitterTarget");
                var subPs = ResolveParticleSystem(target);
                if (subPs == null) throw new BridgeCommandException("E_SUB_EMITTER_NOT_FOUND", "Sub-emitter ParticleSystem not found: " + target, "Pass subEmitterTarget as a GameObject with ParticleSystem.");
                var type = request.HasArg("subEmitterType") ? ParseEnumArg<ParticleSystemSubEmitterType>(request.StringArg("subEmitterType"), "E_SUB_EMITTER_TYPE_INVALID", "Use Birth, Collision, Death, Trigger, or Manual.") : ParticleSystemSubEmitterType.Birth;
                var properties = request.HasArg("subEmitterProperties") ? ParseEnumArg<ParticleSystemSubEmitterProperties>(request.StringArg("subEmitterProperties"), "E_SUB_EMITTER_PROPERTIES_INVALID", "Use InheritNothing or InheritEverything.") : ParticleSystemSubEmitterProperties.InheritNothing;
                sub.AddSubEmitter(subPs, type, properties);
                effects.Add(EffectJson("vfx.particle-module", "subEmitters", "added"));
                return;
            }
            throw new BridgeCommandException("E_SUB_EMITTER_ACTION_INVALID", "Unsupported subEmitterAction: " + action, "Use add, remove, or clear.");
        }

        private static string ParticleSystemJson(ParticleSystem ps)
        {
            var main = ps.main;
            var emission = ps.emission;
            var shape = ps.shape;
            var color = ps.colorOverLifetime;
            var size = ps.sizeOverLifetime;
            var rotation = ps.rotationOverLifetime;
            var velocity = ps.velocityOverLifetime;
            var noise = ps.noise;
            var collision = ps.collision;
            var sub = ps.subEmitters;
            var trails = ps.trails;
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
                    .Prop("startRotationDegrees", main.startRotation.constant * Mathf.Rad2Deg)
                    .Prop("maxParticles", (long)main.maxParticles)
                    .Prop("loop", main.loop)
                    .Prop("playOnAwake", main.playOnAwake)
                    .Prop("simulationSpace", main.simulationSpace.ToString())
                    .Prop("scalingMode", main.scalingMode.ToString())
                    .Prop("gravityModifier", main.gravityModifier.constant))
                .PropObject("emission", w => w.Prop("enabled", emission.enabled).Prop("rateOverTime", emission.rateOverTime.constant).Prop("rateOverDistance", emission.rateOverDistance.constant).Prop("burstCount", (long)emission.burstCount))
                .PropObject("shape", w => w.Prop("enabled", shape.enabled).Prop("shapeType", shape.shapeType.ToString()).Prop("radius", shape.radius).Prop("angle", shape.angle).Prop("arc", shape.arc).Prop("length", shape.length).PropRaw("box", Vector3Json(shape.box)))
                .PropObject("modules", w => w
                    .Prop("colorOverLifetime", color.enabled)
                    .Prop("sizeOverLifetime", size.enabled)
                    .Prop("rotationOverLifetime", rotation.enabled)
                    .Prop("velocityOverLifetime", velocity.enabled)
                    .Prop("noise", noise.enabled)
                    .Prop("collision", collision.enabled)
                    .Prop("subEmitters", sub.enabled)
                    .Prop("subEmittersCount", (long)sub.subEmittersCount)
                    .Prop("trails", trails.enabled))
                .EndObject().ToString();
        }

        private static string LineRendererJson(LineRenderer line)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("startWidth", line.startWidth)
                .Prop("endWidth", line.endWidth)
                .Prop("widthMultiplier", line.widthMultiplier)
                .PropRaw("widthCurve", CurveJson(line.widthCurve))
                .PropRaw("startColor", ColorJson(line.startColor))
                .PropRaw("endColor", ColorJson(line.endColor))
                .PropRaw("colorGradient", GradientJson(line.colorGradient))
                .Prop("material", line.sharedMaterial == null ? null : AssetDatabase.GetAssetPath(line.sharedMaterial))
                .Prop("positionCount", (long)line.positionCount)
                .Prop("loop", line.loop)
                .Prop("useWorldSpace", line.useWorldSpace)
                .Prop("alignment", line.alignment.ToString())
                .Prop("textureMode", line.textureMode.ToString())
                .Prop("numCornerVertices", (long)line.numCornerVertices)
                .Prop("numCapVertices", (long)line.numCapVertices)
                .EndObject().ToString();
        }

        private static string TrailRendererJson(TrailRenderer trail)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("time", trail.time)
                .Prop("startWidth", trail.startWidth)
                .Prop("endWidth", trail.endWidth)
                .Prop("widthMultiplier", trail.widthMultiplier)
                .PropRaw("widthCurve", CurveJson(trail.widthCurve))
                .PropRaw("startColor", ColorJson(trail.startColor))
                .PropRaw("endColor", ColorJson(trail.endColor))
                .PropRaw("colorGradient", GradientJson(trail.colorGradient))
                .Prop("material", trail.sharedMaterial == null ? null : AssetDatabase.GetAssetPath(trail.sharedMaterial))
                .Prop("minVertexDistance", trail.minVertexDistance)
                .Prop("emitting", trail.emitting)
                .Prop("autodestruct", trail.autodestruct)
                .Prop("alignment", trail.alignment.ToString())
                .Prop("textureMode", trail.textureMode.ToString())
                .Prop("numCornerVertices", (long)trail.numCornerVertices)
                .Prop("numCapVertices", (long)trail.numCapVertices)
                .EndObject().ToString();
        }

        private static AnimationCurve ParseAnimationCurveArg(BridgeRequest request, string name)
        {
            var raw = request.RawJsonArg(name);
            if (string.IsNullOrEmpty(raw)) return new AnimationCurve();
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("\"", StringComparison.Ordinal)) trimmed = request.StringArg(name);
            var keys = new List<Keyframe>();
            if (!string.IsNullOrEmpty(trimmed) && trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                foreach (var item in JsonArrayItems(trimmed))
                {
                    var time = JsonFloatProperty(item, "time");
                    var value = JsonFloatProperty(item, "value");
                    if (time.HasValue && value.HasValue) keys.Add(new Keyframe(time.Value, value.Value));
                }
            }
            else
            {
                foreach (var part in (trimmed ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var pieces = part.Split(':');
                    if (pieces.Length != 2) continue;
                    keys.Add(new Keyframe(ParseVfxFloat(pieces[0], "time"), ParseVfxFloat(pieces[1], "value")));
                }
            }
            return new AnimationCurve(keys.Count == 0 ? new[] { new Keyframe(0f, 1f), new Keyframe(1f, 1f) } : keys.ToArray());
        }

        private static Gradient ParseGradientArg(BridgeRequest request, string name)
        {
            var raw = request.RawJsonArg(name);
            var trimmed = raw == null ? string.Empty : raw.Trim();
            if (trimmed.StartsWith("\"", StringComparison.Ordinal)) trimmed = request.StringArg(name);
            var colorKeys = new List<GradientColorKey>();
            var alphaKeys = new List<GradientAlphaKey>();
            if (!string.IsNullOrEmpty(trimmed) && trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                foreach (var item in JsonArrayItems(trimmed))
                {
                    var time = JsonFloatProperty(item, "time") ?? 0f;
                    var colorValue = JsonStringProperty(item, "color");
                    if (string.IsNullOrEmpty(colorValue)) continue;
                    var color = ParseColor(colorValue);
                    colorKeys.Add(new GradientColorKey(color, time));
                    alphaKeys.Add(new GradientAlphaKey(color.a, time));
                }
            }
            else
            {
                foreach (var part in (trimmed ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var pieces = part.Split(':');
                    if (pieces.Length != 2) continue;
                    var time = ParseVfxFloat(pieces[0], "time");
                    var color = ParseColor(pieces[1]);
                    colorKeys.Add(new GradientColorKey(color, time));
                    alphaKeys.Add(new GradientAlphaKey(color.a, time));
                }
            }
            if (colorKeys.Count == 0)
            {
                colorKeys.Add(new GradientColorKey(Color.white, 0f));
                colorKeys.Add(new GradientColorKey(Color.white, 1f));
                alphaKeys.Add(new GradientAlphaKey(1f, 0f));
                alphaKeys.Add(new GradientAlphaKey(1f, 1f));
            }
            var gradient = new Gradient();
            gradient.SetKeys(colorKeys.ToArray(), alphaKeys.ToArray());
            return gradient;
        }

        private static Vector3[] ParseVector3ArrayArg(BridgeRequest request, string name)
        {
            var raw = request.RawJsonArg(name);
            if (string.IsNullOrEmpty(raw)) return new Vector3[0];
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                var values = new List<Vector3>();
                foreach (var item in JsonArrayItems(trimmed)) values.Add(ParseVector3JsonObject(item));
                return values.ToArray();
            }
            return ParseVector3Array(request.StringArg(name));
        }

        private static Vector3 ParseVector3Arg(BridgeRequest request, string name)
        {
            var raw = request.RawJsonArg(name);
            if (string.IsNullOrEmpty(raw)) return Vector3.zero;
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal)) return ParseVector3JsonObject(trimmed);
            return ParseVector3(request.StringArg(name));
        }

        private static Vector3 ParseVector3JsonObject(string json)
        {
            return new Vector3(JsonFloatProperty(json, "x") ?? 0f, JsonFloatProperty(json, "y") ?? 0f, JsonFloatProperty(json, "z") ?? 0f);
        }

        private static Material ResolveOptionalMaterial(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath) || string.Equals(rawPath, "null", StringComparison.OrdinalIgnoreCase) || string.Equals(rawPath, "none", StringComparison.OrdinalIgnoreCase)) return null;
            var path = NormalizeAssetPath(rawPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) throw new BridgeCommandException("E_VFX_MATERIAL_NOT_FOUND", "Material not found: " + rawPath, "Pass an existing Assets/*.mat path or omit material.");
            return material;
        }

        private static T ParseEnumArg<T>(string value, string code, string nextAction) where T : struct
        {
            T parsed;
            if (Enum.TryParse<T>(value, true, out parsed)) return parsed;
            throw new BridgeCommandException(code, "Invalid " + typeof(T).Name + " value: " + value, nextAction);
        }

        private static float ParseVfxFloat(string value, string label)
        {
            float parsed;
            if (float.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            throw new BridgeCommandException("E_VFX_NUMBER_INVALID", "Invalid numeric " + label + ": " + value, "Pass a JSON number or invariant-culture numeric string.");
        }

        private static float? JsonFloatProperty(string json, string name)
        {
            var raw = JsonRawProperty(json, name);
            if (string.IsNullOrEmpty(raw)) return null;
            return ParseVfxFloat(raw.Trim().Trim('"'), name);
        }

        private static List<string> JsonArrayItems(string json)
        {
            var items = new List<string>();
            var trimmed = (json ?? string.Empty).Trim();
            if (!trimmed.StartsWith("[", StringComparison.Ordinal) || !trimmed.EndsWith("]", StringComparison.Ordinal)) return items;
            var index = 1;
            while (index < trimmed.Length - 1)
            {
                while (index < trimmed.Length - 1 && (char.IsWhiteSpace(trimmed[index]) || trimmed[index] == ',')) index++;
                if (index >= trimmed.Length - 1) break;
                var end = FindJsonValueEnd(trimmed, index);
                if (end <= index) break;
                items.Add(trimmed.Substring(index, end - index).Trim());
                index = end;
            }
            return items;
        }

        private static string CurveJson(AnimationCurve curve)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropArray("keys", arr =>
                {
                    foreach (var key in curve == null ? new Keyframe[0] : curve.keys)
                    {
                        arr.BeginObject().Prop("time", key.time).Prop("value", key.value).EndObject();
                    }
                })
                .EndObject().ToString();
        }

        private static string GradientJson(Gradient gradient)
        {
            var colors = gradient == null ? new GradientColorKey[0] : gradient.colorKeys;
            var alphas = gradient == null ? new GradientAlphaKey[0] : gradient.alphaKeys;
            return new UnityCliJsonWriter().BeginObject()
                .PropArray("colors", arr =>
                {
                    foreach (var key in colors)
                    {
                        arr.BeginObject().Prop("time", key.time).PropRaw("color", ColorJson(key.color)).EndObject();
                    }
                })
                .PropArray("alphas", arr =>
                {
                    foreach (var key in alphas)
                    {
                        arr.BeginObject().Prop("time", key.time).Prop("alpha", key.alpha).EndObject();
                    }
                })
                .EndObject().ToString();
        }
    }
}
#endif
