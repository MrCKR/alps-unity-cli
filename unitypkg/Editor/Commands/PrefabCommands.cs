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
        private static void RegisterPrefabCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "prefab.info", "prefab", false, PrefabInfo);
            RegisterCommand(registry, "prefab.create", "prefab", true, PrefabCreate);
            RegisterCommand(registry, "prefab.modify", "prefab", false, PrefabModify);
            RegisterCommand(registry, "prefab.stage-open", "prefab", true, PrefabStageOpen);
            RegisterCommand(registry, "prefab.stage-save", "prefab", true, PrefabStageSave);
            RegisterCommand(registry, "prefab.stage-close", "prefab", true, PrefabStageClose);
            RegisterCommand(registry, "prefab.hierarchy", "prefab", false, PrefabHierarchy);
            RegisterCommand(registry, "prefab.instantiate", "prefab", false, PrefabInstantiate);
            RegisterCommand(registry, "prefab.unpack", "prefab", false, PrefabUnpack);
        }

        private static string PrefabInfo(BridgeRequest request)
        {
            var path = RequirePrefabPath(request.StringArg("path"));
            var prefab = LoadPrefabAsset(path);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("prefab", PrefabAssetJson(path, prefab))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString());
        }

        private static string PrefabCreate(BridgeRequest request)
        {
            var source = request.StringArg("source");
            var path = RequirePrefabOutputPath(request.StringArg("path"));
            var overwrite = request.BoolArg("overwrite", false);
            if (string.IsNullOrEmpty(source))
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "Prefab create requires a source GameObject or Prefab asset path.", "Pass a hierarchy path, GameObject name, instance id, or existing Assets/*.prefab path.");
            }
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null && !overwrite)
            {
                return ErrorJson("E_PREFAB_OUTPUT_EXISTS", "Prefab output already exists: " + path, "Pass --overwrite or choose a different Assets/*.prefab path.");
            }

            CreateAssetDirectory(path);
            var sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(NormalizeAssetPath(source));
            if (sourcePrefab != null)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }
                if (!AssetDatabase.CopyAsset(NormalizeAssetPath(source), path))
                {
                    return ErrorJson("E_PREFAB_CREATE_FAILED", "Failed to copy Prefab asset to: " + path, "Check destination path and Unity Console diagnostics.");
                }
                AssetDatabase.SaveAssets();
                WriteLog("prefab_created source=" + source + " path=" + path + " mode=copy");
                return SuccessJson(PrefabCreateJson(source, path, "copy"));
            }

            var gameObject = ResolveGameObject(source);
            if (gameObject == null)
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "Source GameObject not found: " + source, "Run tools run scene.hierarchy --instance <instance> to list objects, or pass a hierarchy path / instance id.");
            }
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }
            var saved = PrefabUtility.SaveAsPrefabAssetAndConnect(gameObject, path, InteractionMode.AutomatedAction);
            if (saved == null)
            {
                return ErrorJson("E_PREFAB_CREATE_FAILED", "Failed to create Prefab at: " + path, "Check destination path, scene object validity and Unity Console diagnostics.");
            }
            AssetDatabase.SaveAssets();
            WriteLog("prefab_created source=" + source + " path=" + path + " mode=scene");
            return SuccessJson(PrefabCreateJson(source, path, "scene"));
        }

        private static string PrefabModify(BridgeRequest request)
        {
            var path = RequirePrefabPath(request.StringArg("path"));
            LoadPrefabAsset(path);
            var root = PrefabUtility.LoadPrefabContents(path);
            var effects = new List<string>();
            try
            {
                var name = request.StringArg("name");
                if (!string.IsNullOrEmpty(name) && root.name != name)
                {
                    root.name = name;
                    effects.Add(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", "prefab.modify")
                        .Prop("path", path)
                        .Prop("property", "name")
                        .EndObject()
                        .ToString());
                }

                var addChild = request.StringArg("addChild");
                if (!string.IsNullOrEmpty(addChild))
                {
                    var existing = root.transform.Find(addChild);
                    if (existing != null && !request.BoolArg("overwrite", false))
                    {
                        throw new BridgeCommandException("E_PREFAB_CHILD_EXISTS", "Prefab child already exists: " + addChild, "Pass --overwrite or choose another child name.");
                    }
                    if (existing != null)
                    {
                        UnityEngine.Object.DestroyImmediate(existing.gameObject);
                    }
                    var child = new GameObject(addChild);
                    child.transform.SetParent(root.transform, false);
                    effects.Add(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", "prefab.modify")
                        .Prop("path", path)
                        .Prop("addChild", addChild)
                        .EndObject()
                        .ToString());
                }

                var removeChild = request.StringArg("removeChild");
                if (!string.IsNullOrEmpty(removeChild))
                {
                    var child = root.transform.Find(removeChild);
                    if (child == null)
                    {
                        throw new BridgeCommandException("E_PREFAB_CHILD_NOT_FOUND", "Prefab child not found: " + removeChild, "Run tools run prefab.hierarchy --input '{\"path\":\"<path>\"}' to inspect child paths.");
                    }
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                    effects.Add(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", "prefab.modify")
                        .Prop("path", path)
                        .Prop("removeChild", removeChild)
                        .EndObject()
                        .ToString());
                }

                if (effects.Count == 0)
                {
                    throw new BridgeCommandException("E_PREFAB_MODIFY_NOOP", "No Prefab modifications were requested.", "Pass --name, --add-child or --remove-child.");
                }

                PrefabUtility.SaveAsPrefabAsset(root, path);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("prefab", PrefabAssetJson(path, root, root))
                    .PropArray("plannedEffects", arr => { foreach (var effect in effects) { arr.RawValue(effect); } })
                    .Prop("logPath", LogPath())
                    .EndObject()
                    .ToString());
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static string PrefabStageOpen(BridgeRequest request)
        {
            var path = RequirePrefabPath(request.StringArg("path"));
            LoadPrefabAsset(path);
            var stage = CurrentPrefabStage();
            if (stage != null && StageIsDirty(stage) && !string.Equals(StageAssetPath(stage), path, StringComparison.OrdinalIgnoreCase))
            {
                return ErrorJson("E_PREFAB_DIRTY_CONFLICT", "Current Prefab Stage has unsaved changes.", "Run tools run prefab.stage-save or close/save the current Prefab Stage in Unity before opening another Prefab.");
            }

            if (!OpenPrefabStage(path))
            {
                return ErrorJson("E_PREFAB_STAGE_OPEN_FAILED", "Failed to open Prefab Stage: " + path, "Check Unity Console and UnityCliBridge.log.");
            }
            WriteLog("prefab_stage_open path=" + path);
            return SuccessJson(PrefabStageJson("open", path, false));
        }

        private static string PrefabStageSave(BridgeRequest request)
        {
            var stage = RequireCurrentPrefabStage(request.StringArg("path"));
            var path = StageAssetPath(stage);
            if (!SavePrefabStage(stage))
            {
                return ErrorJson("E_PREFAB_STAGE_SAVE_FAILED", "Failed to save Prefab Stage: " + path, "Check Unity Console and UnityCliBridge.log.");
            }
            AssetDatabase.SaveAssets();
            WriteLog("prefab_stage_save path=" + path);
            return SuccessJson(PrefabStageJson("save", path, false));
        }

        private static string PrefabStageClose(BridgeRequest request)
        {
            var stage = RequireCurrentPrefabStage(request.StringArg("path"));
            var path = StageAssetPath(stage);
            var save = request.BoolArg("save", false);
            if (StageIsDirty(stage) && !save)
            {
                return ErrorJson("E_PREFAB_DIRTY_CONFLICT", "Prefab Stage has unsaved changes: " + path, "Run tools run prefab.stage-close --input '{\"path\":\"<path>\",\"save\":true}' or tools run prefab.stage-save before closing.");
            }
            if (save && !SavePrefabStage(stage))
            {
                return ErrorJson("E_PREFAB_STAGE_SAVE_FAILED", "Failed to save Prefab Stage before close: " + path, "Check Unity Console and UnityCliBridge.log.");
            }
            if (!ClosePrefabStage())
            {
                return ErrorJson("E_PREFAB_STAGE_CLOSE_FAILED", "Failed to close Prefab Stage: " + path, "Close the Prefab Stage in Unity or check UnityCliBridge.log.");
            }
            WriteLog("prefab_stage_close path=" + path + " save=" + save);
            return SuccessJson(PrefabStageJson("close", path, false));
        }

        private static string PrefabInstantiate(BridgeRequest request)
        {
            EnsureEditorNotBusy("prefab.instantiate");
            var path = RequirePrefabPath(request.StringArg("path"));
            var parentName = request.StringArg("parent");
            var name = request.StringArg("name");
            var posX = request.FloatArg("x", 0f);
            var posY = request.FloatArg("y", 0f);
            var posZ = request.FloatArg("z", 0f);
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "prefab.instantiate").Prop("dryRun", true)
                    .Prop("path", path)
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "prefab.instantiate").Prop("path", path).EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            var prefab = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
            if (prefab == null)
            {
                throw new BridgeCommandException("E_PREFAB_NOT_FOUND", "Prefab asset not found: " + path, "Run asset search --type Prefab.");
            }
            Transform parent = null;
            if (!string.IsNullOrEmpty(parentName))
            {
                var parentGo = ObjectRefResolver.FindGameObjectByPath(parentName);
                if (parentGo == null) throw new BridgeCommandException("E_PARENT_NOT_FOUND", "Parent not found: " + parentName, "Pass a valid path.");
                parent = parentGo.transform;
            }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent != null ? parent.gameObject.scene : SceneManager.GetActiveScene());
            if (parent != null) instance.transform.SetParent(parent, false);
            instance.transform.position = new Vector3(posX, posY, posZ);
            if (!string.IsNullOrEmpty(name)) instance.name = name;
            EditorSceneManager.MarkSceneDirty(instance.scene);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "prefab.instantiate")
                .PropRaw("instance", GameObjectTargetJson(instance))
                .Prop("sourcePath", path)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "prefab.instantiate").Prop("path", path).EndObject())
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        private static string PrefabUnpack(BridgeRequest request)
        {
            EnsureEditorNotBusy("prefab.unpack");
            var gameObject = ObjectRefResolver.FindGameObjectByPath(request.StringArg("target") ?? request.StringArg("path"));
            if (gameObject == null)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject not found for prefab.unpack.", "Pass a prefab instance path or name.");
            }
            if (!PrefabUtility.IsPartOfPrefabInstance(gameObject))
            {
                throw new BridgeCommandException("E_NOT_PREFAB_INSTANCE", gameObject.name + " is not a prefab instance.", "Pass a GameObject that is a prefab instance.");
            }
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "prefab.unpack").Prop("dryRun", true)
                    .PropRaw("target", GameObjectTargetJson(gameObject))
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "prefab.unpack").EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            PrefabUtility.UnpackPrefabInstance(gameObject, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "prefab.unpack")
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "prefab.unpack").EndObject())
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        private static string PrefabHierarchy(BridgeRequest request)
        {
            var path = RequirePrefabPath(request.StringArg("path"));
            LoadPrefabAsset(path);
            var pageSize = Math.Max(1, request.IntArg("pageSize", 100));
            var cursor = Math.Max(0, request.IntArg("cursor", 0));
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var items = new List<string>();
                var total = CountHierarchy(root.transform);
                CollectHierarchy(root.transform, items, cursor, pageSize, new Counter());
                var nextCursor = cursor + items.Count < total ? (cursor + items.Count).ToString(CultureInfo.InvariantCulture) : null;
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("prefab", PrefabAssetJson(path, root, root))
                    .PropArray("items", arr => { foreach (var item in items) { arr.RawValue(item); } })
                    .PropObject("pageInfo", pageInfo => pageInfo
                        .Prop("pageSize", (long)pageSize)
                        .Prop("cursor", (long)cursor)
                        .Prop("nextCursor", nextCursor)
                        .Prop("total", (long)total))
                    .Prop("logPath", LogPath())
                    .EndObject()
                    .ToString());
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static string RequirePrefabPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_PREFAB_PATH_REQUIRED", "Pass an Assets/*.prefab path.", "Use tools run prefab.info --input '{\"path\":\"<path>\"}' or pass an Assets/*.prefab path.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_PREFAB_PATH_INVALID", "Prefab path must be under Assets/ and end with .prefab: " + path, "Pass a Unity asset path such as Assets/Prefabs/Player.prefab.");
            }
            return path;
        }

        private static string RequirePrefabOutputPath(string rawPath)
        {
            return RequirePrefabPath(rawPath);
        }

        private static GameObject LoadPrefabAsset(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                throw new BridgeCommandException("E_PREFAB_NOT_FOUND", "Prefab asset not found: " + path, "Check the path or run tools run asset.search --input '{\"filter\":\"t:Prefab\"}' to discover the path.");
            }
            if (PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.NotAPrefab)
            {
                throw new BridgeCommandException("E_ASSET_NOT_PREFAB", "Asset is not a Prefab: " + path, "Pass an Assets/*.prefab asset path.");
            }
            return prefab;
        }

        private static string NormalizeAssetPath(string rawPath)
        {
            return string.IsNullOrEmpty(rawPath) ? null : rawPath.Replace('\\', '/').Trim();
        }

        private static void CreateAssetDirectory(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(directory) || AssetDatabase.IsValidFolder(directory.Replace('\\', '/')))
            {
                return;
            }
            Directory.CreateDirectory(Path.Combine(ProjectRoot(), directory.Replace('/', Path.DirectorySeparatorChar)));
            AssetDatabase.Refresh();
        }

        private static GameObject ResolveGameObject(string target)
        {
            return ResolveGameObjectTarget(target).Target;
        }

        private static string PrefabStageJson(object prefabStage)
        {
            if (prefabStage == null)
            {
                return "null";
            }
            var type = prefabStage.GetType();
            var assetPath = type.GetProperty("assetPath")?.GetValue(prefabStage)?.ToString();
            var sceneValue = type.GetProperty("scene")?.GetValue(prefabStage);
            var scenePath = sceneValue is Scene scene ? scene.path : null;
            return new UnityCliJsonWriter().BeginObject()
                .Prop("assetPath", assetPath)
                .Prop("scenePath", scenePath)
                .EndObject()
                .ToString();
        }

        private static string PrefabAssetJson(string path, GameObject prefab)
        {
            var dependencies = AssetDatabase.GetDependencies(path, false);
            return PrefabAssetJson(path, prefab, null, dependencies);
        }

        private static string PrefabAssetJson(string path, GameObject prefab, GameObject contentsRoot)
        {
            var dependencies = AssetDatabase.GetDependencies(path, false);
            return PrefabAssetJson(path, prefab, contentsRoot, dependencies);
        }

        private static string PrefabAssetJson(string path, GameObject prefab, GameObject contentsRoot, string[] dependencies)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            var assetType = PrefabUtility.GetPrefabAssetType(prefab).ToString();
            var instanceStatus = PrefabUtility.GetPrefabInstanceStatus(prefab).ToString();
            return new UnityCliJsonWriter().BeginObject()
                .Prop("path", path)
                .Prop("guid", guid)
                .Prop("rootName", prefab.name)
                .Prop("assetType", assetType)
                .Prop("instanceStatus", instanceStatus)
                .Prop("isVariant", assetType == "Variant")
                .PropRaw("variant", PrefabVariantJson(prefab))
                .Prop("dependencyCount", (long)dependencies.Length)
                .PropRaw("dependencies", DependencySummaryJson(dependencies))
                .PropRaw("nestedPrefabs", NestedPrefabReferencesJson(path, contentsRoot))
                .EndObject()
                .ToString();
        }

        private static string PrefabVariantJson(GameObject prefab)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
            var sourcePath = source == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(source));
            var sourceGuid = string.IsNullOrEmpty(sourcePath) ? null : AssetDatabase.AssetPathToGUID(sourcePath);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("isVariant", PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.Variant)
                .Prop("basePath", sourcePath)
                .Prop("baseGuid", sourceGuid)
                .Prop("sourcePath", sourcePath)
                .Prop("sourceGuid", sourceGuid)
                .EndObject()
                .ToString();
        }

        private static string DependencySummaryJson(string[] dependencies)
        {
            var items = new List<string>();
            foreach (var dependency in dependencies)
            {
                var type = AssetDatabase.GetMainAssetTypeAtPath(dependency);
                items.Add(new UnityCliJsonWriter().BeginObject()
                    .Prop("path", NormalizeAssetPath(dependency))
                    .Prop("guid", AssetDatabase.AssetPathToGUID(dependency))
                    .Prop("type", type == null ? null : type.Name)
                    .EndObject()
                    .ToString());
            }
            return new UnityCliJsonWriter().BeginObject()
                .Prop("count", (long)dependencies.Length)
                .PropArray("items", arr => { foreach (var item in items) { arr.RawValue(item); } })
                .EndObject()
                .ToString();
        }

        private static string NestedPrefabReferencesJson(string prefabPath, GameObject contentsRoot)
        {
            if (contentsRoot != null)
            {
                return NestedPrefabReferencesJsonFromRoot(prefabPath, contentsRoot);
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                return NestedPrefabReferencesJsonFromRoot(prefabPath, root);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static string NestedPrefabReferencesJsonFromRoot(string prefabPath, GameObject root)
        {
            var items = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectNestedPrefabReferences(prefabPath, root.transform, root.transform, items, seen);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("count", (long)items.Count)
                .PropArray("items", arr => { foreach (var item in items) { arr.RawValue(item); } })
                .EndObject()
                .ToString();
        }

        private static void CollectNestedPrefabReferences(string prefabPath, Transform root, Transform current, List<string> items, HashSet<string> seen)
        {
            if (current != root && PrefabUtility.IsAnyPrefabInstanceRoot(current.gameObject))
            {
                var sourcePath = NormalizeAssetPath(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(current.gameObject));
                if (!string.IsNullOrEmpty(sourcePath) && !string.Equals(sourcePath, prefabPath, StringComparison.OrdinalIgnoreCase))
                {
                    var key = sourcePath + "|" + HierarchyPath(current.gameObject);
                    if (seen.Add(key))
                    {
                        items.Add(NestedPrefabReferenceJson(current.gameObject, sourcePath));
                    }
                }
            }

            foreach (Transform child in current)
            {
                CollectNestedPrefabReferences(prefabPath, root, child, items, seen);
            }
        }

        private static string NestedPrefabReferenceJson(GameObject gameObject, string sourcePath)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("name", gameObject.name)
                .Prop("path", HierarchyPath(gameObject))
                .Prop("sourcePath", sourcePath)
                .Prop("sourceGuid", AssetDatabase.AssetPathToGUID(sourcePath))
                .Prop("assetType", source == null ? null : PrefabUtility.GetPrefabAssetType(source).ToString())
                .Prop("instanceStatus", PrefabUtility.GetPrefabInstanceStatus(gameObject).ToString())
                .EndObject()
                .ToString();
        }

        private static string PrefabCreateJson(string source, string path, string mode)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("prefab", PrefabAssetJson(path, LoadPrefabAsset(path)))
                .Prop("source", source)
                .Prop("mode", mode)
                .PropArray("plannedEffects", arr => arr
                    .RawValue(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", "prefab.create")
                        .Prop("path", path)
                        .Prop("source", source)
                        .EndObject().ToString()))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
        }

        private static string PrefabStageJson(string action, string path, bool dirty)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropObject("stage", stage => stage
                    .Prop("action", action)
                    .Prop("path", path)
                    .Prop("dirty", dirty))
                .PropArray("plannedEffects", arr => arr
                    .RawValue(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", "prefab.stage-" + action)
                        .Prop("path", path)
                        .EndObject().ToString()))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
        }

        private static object CurrentPrefabStage()
        {
            return InvokePrefabStageUtility("GetCurrentPrefabStage", null);
        }

        private static object RequireCurrentPrefabStage(string requestedPath)
        {
            var stage = CurrentPrefabStage();
            if (stage == null)
            {
                throw new BridgeCommandException("E_PREFAB_STAGE_NOT_OPEN", "No Prefab Stage is currently open.", "Run tools run prefab.stage-open --input '{\"path\":\"<path>\"}' first.");
            }
            var path = StageAssetPath(stage);
            if (!string.IsNullOrEmpty(requestedPath) && !string.Equals(NormalizeAssetPath(requestedPath), path, StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_PREFAB_STAGE_MISMATCH", "Current Prefab Stage is " + path + ", not " + requestedPath, "Run stage-save/stage-close without --path or use the currently open Prefab path.");
            }
            return stage;
        }

        private static bool OpenPrefabStage(string path)
        {
            return InvokePrefabStageUtility("OpenPrefab", new object[] { path }) != null;
        }

        private static bool SavePrefabStage(object stage)
        {
            var method = stage.GetType().GetMethod("SavePrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null)
            {
                method.Invoke(stage, null);
                return true;
            }
            var root = StageRoot(stage);
            var path = StageAssetPath(stage);
            if (root == null || string.IsNullOrEmpty(path))
            {
                return false;
            }
            PrefabUtility.SaveAsPrefabAsset(root, path);
            return true;
        }

        private static bool ClosePrefabStage()
        {
            if (InvokePrefabStageUtilityVoid("CloseCurrentPrefabStage", null))
            {
                return true;
            }
            var method = typeof(StageUtility).GetMethod("GoBackToPreviousStage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                return false;
            }
            method.Invoke(null, null);
            return true;
        }

        private static object InvokePrefabStageUtility(string methodName, object[] args)
        {
            var method = PrefabStageUtilityMethod(methodName, args == null ? 0 : args.Length);
            return method == null ? null : method.Invoke(null, args);
        }

        private static bool InvokePrefabStageUtilityVoid(string methodName, object[] args)
        {
            var method = PrefabStageUtilityMethod(methodName, args == null ? 0 : args.Length);
            if (method == null)
            {
                return false;
            }
            method.Invoke(null, args);
            return true;
        }

        private static MethodInfo PrefabStageUtilityMethod(string methodName, int parameterCount)
        {
            var type = typeof(PrefabStageUtility);
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var method in type.GetMethods(flags))
            {
                if (method.Name == methodName && method.GetParameters().Length == parameterCount)
                {
                    return method;
                }
            }
            return null;
        }

        private static string StageAssetPath(object stage)
        {
            var property = stage.GetType().GetProperty("assetPath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : NormalizeAssetPath(property.GetValue(stage, null) as string);
        }

        private static GameObject StageRoot(object stage)
        {
            var property = stage.GetType().GetProperty("prefabContentsRoot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(stage, null) as GameObject;
        }

        private static bool StageIsDirty(object stage)
        {
            var root = StageRoot(stage);
            return root != null && root.scene.isDirty;
        }
    }
}
#endif
