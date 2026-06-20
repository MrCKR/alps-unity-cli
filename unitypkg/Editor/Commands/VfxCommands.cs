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
    }
}
#endif
