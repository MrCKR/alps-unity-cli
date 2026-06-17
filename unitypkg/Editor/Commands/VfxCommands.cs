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
    }
}
#endif
