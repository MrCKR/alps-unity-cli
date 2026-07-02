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
        private static void RegisterSceneCommands(BridgeCommandRegistry registry)
        {
            // scene.active 已迁移为 IUnityCliTool(unitypkg/Editor/Tools/scene/active.cs)
            RegisterCommand(registry, "scene.hierarchy", "scene", false, SceneHierarchy);
            RegisterCommand(registry, "scene.build-settings", "scene", false, SceneBuildSettings);
            RegisterCommand(registry, "scene.screenshot", "scene", true, SceneScreenshot);
            RegisterCommand(registry, "scene.create", "scene", true, SceneCreate);
            RegisterCommand(registry, "scene.load", "scene", true, SceneLoad);
            RegisterCommand(registry, "scene.save", "scene", true, SceneSave);
            RegisterCommand(registry, "scene.unload", "scene", false, SceneUnload);
            RegisterCommand(registry, "scene.set-active", "scene", true, SceneSetActive);
            RegisterCommand(registry, "scene.list-opened", "scene", false, SceneListOpened);
            RegisterCommand(registry, "scene.data", "scene", false, SceneData);
            RegisterCommand(registry, "scene.move-object", "scene", false, SceneMoveObject);
            RegisterCommand(registry, "scene.validate", "scene", false, SceneValidate);
            RegisterCommand(registry, "scene.view-frame", "scene", true, SceneViewFrame);
            RegisterCommand(registry, "screenshot.capture", "scene", true, SceneScreenshot);
        }

        // SceneActive handler 已迁移为 IUnityCliTool(unitypkg/Editor/Tools/scene/active.cs)

        private static string SceneBuildSettings(BridgeRequest request)
        {
            var scenes = EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0];
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropArray("scenes", arr =>
                {
                    for (var index = 0; index < scenes.Length; index++)
                    {
                        var scene = scenes[index];
                        arr.BeginObject()
                            .Prop("path", NormalizeAssetPath(scene.path))
                            .Prop("enabled", scene.enabled)
                            .Prop("buildIndex", (long)index)
                            .EndObject();
                    }
                })
                .Prop("count", (long)scenes.Length)
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string SceneCreate(BridgeRequest request)
        {
            EnsureEditorNotBusy("scene.create");
            var path = ScenePathFromRequest(request, false);
            if (!string.IsNullOrEmpty(path) && File.Exists(AssetFullPath(path)))
            {
                throw new BridgeCommandException("E_SCENE_OUTPUT_EXISTS", "Scene already exists: " + path, "Choose a different Assets/*.unity path or run scene load/save.");
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!string.IsNullOrEmpty(path))
            {
                CreateAssetDirectory(path);
                if (!EditorSceneManager.SaveScene(scene, path))
                {
                    throw new BridgeCommandException("E_SCENE_SAVE_FAILED", "Failed to save new scene: " + path, "Check destination path and Unity Console diagnostics.");
                }
            }
            return SuccessJson(SceneMutationJson("scene.create", scene, path));
        }

        private static string SceneLoad(BridgeRequest request)
        {
            EnsureEditorNotBusy("scene.load");
            var path = ScenePathFromRequest(request, true);
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            return SuccessJson(SceneMutationJson("scene.load", scene, path));
        }

        private static string SceneSave(BridgeRequest request)
        {
            EnsureEditorNotBusy("scene.save");
            var scene = SceneManager.GetActiveScene();
            var path = ScenePathFromRequest(request, false);
            if (string.IsNullOrEmpty(path))
            {
                path = NormalizeAssetPath(scene.path);
            }
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_SCENE_PATH_REQUIRED", "Scene save requires --path when the active scene has not been saved yet.", "Pass tools run scene.save --input '{\"path\":\"Assets/Scenes/Main.unity\"}'.");
            }
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "scene.save")
                    .Prop("dryRun", true)
                    .PropRaw("scene", ActiveSceneJson())
                    .Prop("path", path)
                    .PropArray("plannedEffects", arr =>
                    {
                        arr.BeginObject()
                            .Prop("type", "scene.save")
                            .Prop("path", path)
                            .Prop("sideEffects", "none")
                            .EndObject();
                    })
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }
            CreateAssetDirectory(path);
            if (!EditorSceneManager.SaveScene(scene, path))
            {
                throw new BridgeCommandException("E_SCENE_SAVE_FAILED", "Failed to save scene: " + path, "Check Unity Console diagnostics and file permissions.");
            }
            return SuccessJson(SceneMutationJson("scene.save", SceneManager.GetActiveScene(), path));
        }

        private static string SceneUnload(BridgeRequest request)
        {
            EnsureEditorNotBusy("scene.unload");
            var path = ScenePathFromRequest(request, true);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (string.Equals(NormalizeAssetPath(scene.path), path, StringComparison.OrdinalIgnoreCase))
                {
                    if (scene == SceneManager.GetActiveScene() && SceneManager.sceneCount == 1)
                    {
                        throw new BridgeCommandException("E_SCENE_UNLOAD_LAST", "Cannot unload the only open scene.", "Open another scene first.");
                    }
                    var opts = request.BoolArg("removeAll", false) ? UnloadSceneOptions.UnloadAllEmbeddedSceneObjects : UnloadSceneOptions.None;
                    var saveDirty = request.BoolArg("save", false);
                    if (request.DryRun)
                    {
                        return SuccessJson(new UnityCliJsonWriter().BeginObject()
                            .Prop("command", "scene.unload").Prop("dryRun", true)
                            .Prop("path", path).Prop("sceneName", scene.name)
                            .Prop("isDirty", scene.isDirty).Prop("willSave", saveDirty && scene.isDirty)
                            .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "scene.unload").Prop("path", path).Prop("saveDirty", saveDirty && scene.isDirty).EndObject())
                            .Prop("logPath", LogPath()).EndObject().ToString());
                    }
                    // dirty 保存策略：save=true 且场景有未保存改动时先保存再卸载，避免丢失改动。
                    if (saveDirty && scene.isDirty && !string.IsNullOrEmpty(scene.path))
                    {
                        EditorSceneManager.SaveScene(scene);
                    }
                    EditorSceneManager.CloseScene(scene, opts == UnloadSceneOptions.UnloadAllEmbeddedSceneObjects);
                    return SuccessJson(SceneMutationJson("scene.unload", SceneManager.GetActiveScene(), path));
                }
            }
            throw new BridgeCommandException("E_SCENE_NOT_OPEN", "Scene is not currently open: " + path, "Run context read scene.opened to list open scenes.");
        }

        private static string SceneSetActive(BridgeRequest request)
        {
            EnsureEditorNotBusy("scene.set-active");
            var path = ScenePathFromRequest(request, true);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (string.Equals(NormalizeAssetPath(scene.path), path, StringComparison.OrdinalIgnoreCase))
                {
                    EditorSceneManager.SetActiveScene(scene);
                    return SuccessJson(SceneMutationJson("scene.set-active", scene, path));
                }
            }
            throw new BridgeCommandException("E_SCENE_NOT_OPEN", "Scene is not currently open: " + path, "Run context read scene.opened, then scene.load if needed.");
        }

        private static string SceneListOpened(BridgeRequest request)
        {
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "scene.list-opened");
            w.PropArray("scenes", arr =>
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    arr.BeginObject()
                        .Prop("name", scene.name)
                        .Prop("path", NormalizeAssetPath(scene.path))
                        .Prop("buildIndex", (long)scene.buildIndex)
                        .Prop("isLoaded", scene.isLoaded)
                        .Prop("isDirty", scene.isDirty)
                        .Prop("isActive", scene == SceneManager.GetActiveScene())
                        .EndObject();
                }
            });
            w.Prop("count", (long)SceneManager.sceneCount);
            w.Prop("activeScene", SceneManager.GetActiveScene().name);
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        /// <summary>参数化 Scene 深读：对齐 Ivan scene-get-data。只读 Tool，不做 Context 主入口。</summary>
        private static string SceneData(BridgeRequest request)
        {
            var maxDepth = Math.Max(1, Math.Min(request.IntArg("maxDepth", 2), 6));
            var maxResults = Math.Max(1, Math.Min(request.IntArg("maxResults", 200), 2000));
            var nameFilter = request.StringArg("nameFilter");
            var includeComponents = request.BoolArg("includeComponents", false);
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            var items = new List<string>();
            var count = 0;
            foreach (var root in roots)
            {
                if (count >= maxResults) break;
                CollectSceneData(root.transform, 0, maxDepth, nameFilter, includeComponents, items, ref count, maxResults);
            }
            var w = new UnityCliJsonWriter().BeginObject();
            w.PropRaw("scene", ActiveSceneJson());
            w.Prop("maxDepth", (long)maxDepth);
            w.Prop("count", (long)count);
            w.PropArray("items", arr => { foreach (var item in items) arr.RawValue(item); });
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        private static void CollectSceneData(Transform t, int depth, int maxDepth, string nameFilter, bool includeComponents, List<string> items, ref int count, int maxResults)
        {
            if (count >= maxResults) return;
            if (string.IsNullOrEmpty(nameFilter) || t.name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var w = new UnityCliJsonWriter().BeginObject();
                w.Prop("name", t.name).Prop("path", TransformPath(t)).Prop("depth", (long)depth).Prop("childCount", (long)t.childCount);
                if (includeComponents)
                {
                    w.PropArray("components", arr => { foreach (var c in t.GetComponents<Component>()) { if (c != null) arr.Value(c.GetType().Name); } });
                }
                w.EndObject();
                items.Add(w.ToString());
                count++;
            }
            if (depth < maxDepth)
            {
                for (var i = 0; i < t.childCount; i++) CollectSceneData(t.GetChild(i), depth + 1, maxDepth, nameFilter, includeComponents, items, ref count, maxResults);
            }
        }

        private static string TransformPath(Transform t)
        {
            var path = t.name;
            var p = t.parent;
            while (p != null) { path = p.name + "/" + path; p = p.parent; }
            return path;
        }

        // 将 GameObject 移动到另一个已加载场景。对齐 CoplayDev manage_scene move_to_scene。
        private static string SceneMoveObject(BridgeRequest request)
        {
            EnsureEditorNotBusy("scene.move-object");
            var target = request.StringArg("target") ?? request.StringArg("path");
            var instanceId = request.OptionalIntArg("instanceId");
            var destScenePath = request.StringArg("scene");
            if (string.IsNullOrEmpty(destScenePath))
            {
                throw new BridgeCommandException("E_SCENE_DESTINATION_REQUIRED", "scene.move-object requires --scene (destination scene path).", "Run context read scene.opened to list loaded scenes.");
            }
            UnityEngine.GameObject go = null;
            if (instanceId.HasValue && instanceId.Value != 0) go = EditorUtility.InstanceIDToObject(instanceId.Value) as UnityEngine.GameObject;
            if (go == null && !string.IsNullOrEmpty(target)) go = ObjectRefResolver.FindGameObjectByPath(target);
            if (go == null)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject not found: " + (target ?? instanceId?.ToString()), "Pass a valid target path or instanceId.");
            }
            var destScene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(destScenePath);
            if (!destScene.IsValid() || !destScene.isLoaded)
            {
                throw new BridgeCommandException("E_SCENE_NOT_OPEN", "Destination scene is not loaded: " + destScenePath, "Load the destination scene first via scene.load, or run context read scene.opened.");
            }
            if (go.scene == destScene)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "scene.move-object").Prop("target", go.name).Prop("scene", destScenePath).Prop("moved", false).Prop("reason", "already in destination scene").Prop("logPath", LogPath()).EndObject().ToString());
            }
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "scene.move-object").Prop("dryRun", true).Prop("target", go.name).Prop("fromScene", go.scene.path).Prop("toScene", destScenePath).PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "scene.move-object").Prop("target", go.name).EndObject()).Prop("logPath", LogPath()).EndObject().ToString());
            }
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, destScene);
            return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "scene.move-object").Prop("target", go.name).Prop("fromScene", go.scene.path).Prop("toScene", destScenePath).Prop("moved", true).Prop("logPath", LogPath()).EndObject().ToString());
        }

        // 校验场景：缺失脚本、null 引用、构建配置。对齐 CoplayDev manage_scene validate。
        private static string SceneValidate(BridgeRequest request)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var path = request.StringArg("path");
            if (!string.IsNullOrEmpty(path))
            {
                scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(path);
                if (!scene.IsValid()) throw new BridgeCommandException("E_SCENE_NOT_OPEN", "Scene is not loaded: " + path, "Load the scene first or run context read scene.opened.");
            }
            var issues = new System.Collections.Generic.List<string>();
            var missingScriptCount = 0;
            var nullReferenceCount = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    foreach (var c in t.GetComponents<Component>())
                    {
                        // 缺失脚本（MonoBehaviour 引用的程序集丢失）表现为 null 组件。
                        if (c == null) { missingScriptCount++; issues.Add(t.gameObject.name + ": missing script component"); continue; }
                    }
                }
            }
            // 构建 scenes 配置校验：当前场景是否在 EditorBuildSettings.scenes。
            var inBuildSettings = false;
            foreach (var bs in EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0])
            {
                if (string.Equals(bs.path, scene.path, StringComparison.OrdinalIgnoreCase)) { inBuildSettings = true; break; }
            }
            if (!string.IsNullOrEmpty(scene.path) && !inBuildSettings)
            {
                issues.Add("scene not in Build Settings scenes: " + scene.path);
            }
            var ok = missingScriptCount == 0 && nullReferenceCount == 0;
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "scene.validate").Prop("scene", scene.name).Prop("path", scene.path).Prop("valid", ok);
            w.Prop("missingScriptCount", (long)missingScriptCount).Prop("nullReferenceCount", (long)nullReferenceCount);
            w.Prop("inBuildSettings", inBuildSettings);
            w.PropArray("issues", arr => { foreach (var i in issues) arr.Value(i); });
            w.PropArray("next_actions", arr => { if (!ok) arr.Value("Fix missing scripts/null references before building."); if (!inBuildSettings && !string.IsNullOrEmpty(scene.path)) arr.Value("Add the scene to Build Settings via build.scenes --action add --path " + scene.path); });
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        private static string SceneViewFrame(BridgeRequest request)
        {
            var target = request.StringArg("target") ?? request.StringArg("path");
            var frameAll = request.BoolArg("frameAll", false);
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                throw new BridgeCommandException("E_SCENE_VIEW_NOT_AVAILABLE", "No active SceneView is open.", "Open a SceneView window in the Unity Editor.");
            }
            if (!string.IsNullOrEmpty(target))
            {
                var go = ObjectRefResolver.FindGameObjectByPath(target);
                if (go != null)
                {
                    Selection.activeGameObject = go;
                    sceneView.Frame(new Bounds(go.transform.position, Vector3.one), false);
                    return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "scene.view-frame").Prop("target", target).Prop("framed", true).Prop("logPath", LogPath()).EndObject().ToString());
                }
            }
            if (frameAll)
            {
                sceneView.Frame(new Bounds(Vector3.zero, new Vector3(10, 10, 10)), true);
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "scene.view-frame").Prop("framed", true).Prop("logPath", LogPath()).EndObject().ToString());
        }

        private static string SceneScreenshot(BridgeRequest request)
        {
            var camera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : Camera.main;
            if (camera == null)
            {
                return ErrorJson("E_SCENE_VIEW_NOT_AVAILABLE", "No SceneView or main Camera is available for screenshot capture.", "Open a SceneView or create an active Camera in the scene.");
            }

            var capture = CaptureCamera(camera, request);
            return SuccessJson(ScreenshotJson(capture, "scene", camera.name));
        }

        private static string ActiveSceneJson()
        {
            var scene = SceneManager.GetActiveScene();
            return new UnityCliJsonWriter().BeginObject()
                .Prop("name", scene.name)
                .Prop("path", NormalizeAssetPath(scene.path))
                .Prop("buildIndex", (long)scene.buildIndex)
                .Prop("isLoaded", scene.isLoaded)
                .Prop("isDirty", scene.isDirty)
                .EndObject().ToString();
        }

        private static string ScenePathFromRequest(BridgeRequest request, bool requireExisting)
        {
            var raw = request.StringArg("path") ?? request.StringArg("name");
            var path = NormalizeAssetPath(raw);
            if (string.IsNullOrEmpty(path))
            {
                if (requireExisting)
                {
                    throw new BridgeCommandException("E_SCENE_PATH_REQUIRED", "Pass an Assets/*.unity scene path.", "Use tools run scene.load --input '{\"path\":\"Assets/Scenes/Main.unity\"}'.");
                }
                return null;
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_SCENE_PATH_INVALID", "Scene path must be under Assets/ and end with .unity: " + path, "Pass a Unity scene asset path such as Assets/Scenes/Main.unity.");
            }
            if (requireExisting && !AssetExists(path))
            {
                throw new BridgeCommandException("E_SCENE_NOT_FOUND", "Scene asset not found: " + path, "Run tools run asset.search --input '{\"filter\":\"t:Scene\"}' or pass an existing Assets/*.unity path.");
            }
            return path;
        }

        private static string SceneMutationJson(string type, Scene scene, string path)
        {
            AssetDatabase.Refresh();
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("scene", ActiveSceneJson())
                .Prop("requestedPath", path)
                .PropArray("plannedEffects", arr =>
                {
                    arr.BeginObject()
                        .Prop("type", type)
                        .Prop("path", NormalizeAssetPath(scene.path) ?? path)
                        .EndObject();
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString();
        }

        /// <summary>读取场景层级摘要：支持 parent、深度、名称过滤、组件预览和分页，避免一次返回整棵大树。</summary>
        private static string SceneHierarchy(BridgeRequest request)
        {
            var scene = SceneManager.GetActiveScene();
            var parent = request.StringArg("parent");
            var includeInactive = request.BoolArg("includeInactive", false);
            var includeComponents = request.BoolArg("includeComponents", false);
            var query = request.StringArg("query") ?? request.StringArg("nameFilter") ?? request.StringArg("filter");
            var componentType = request.StringArg("componentType");
            var maxDepth = Math.Max(0, Math.Min(request.IntArg("maxDepth", 1), 16));
            var cursor = LargeOutputCursor(request);
            var pageSize = LargeOutputPageSize(request, 50, 500);

            var roots = new List<GameObject>();
            if (!string.IsNullOrEmpty(parent))
            {
                var parentObject = ObjectRefResolver.FindGameObjectByPath(parent);
                if (parentObject == null)
                {
                    throw new BridgeCommandException("E_SCENE_HIERARCHY_PARENT_NOT_FOUND", "Parent GameObject not found: " + parent, "Run go.find or scene.hierarchy without parent, then pass a stable hierarchy path or instance id.");
                }
                for (var i = 0; i < parentObject.transform.childCount; i++)
                {
                    roots.Add(parentObject.transform.GetChild(i).gameObject);
                }
            }
            else
            {
                roots.AddRange(scene.GetRootGameObjects());
            }

            var allItems = new List<string>();
            foreach (var root in roots.OrderBy(g => HierarchyPath(g), StringComparer.Ordinal))
            {
                CollectHierarchy(root.transform, allItems, 0, maxDepth, query, includeInactive, includeComponents, componentType);
            }

            var total = allItems.Count;
            var page = allItems.Skip(cursor).Take(pageSize).ToList();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropObject("scene", w => w
                    .Prop("name", scene.name)
                    .Prop("path", scene.path))
                .Prop("parent", parent)
                .Prop("maxDepth", (long)maxDepth)
                .Prop("query", query)
                .Prop("nameFilter", query)
                .Prop("componentType", componentType)
                .Prop("includeInactive", includeInactive)
                .Prop("includeComponents", includeComponents)
                .PropArray("items", arr =>
                {
                    foreach (var item in page)
                    {
                        arr.RawValue(item);
                    }
                })
                .PropRaw("pageInfo", PageInfoJson(pageSize, cursor, total, page.Count))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

    }
}
#endif
