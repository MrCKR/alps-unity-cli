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
            RegisterCommand(registry, "scene.active", "scene", false, SceneActive);
            RegisterCommand(registry, "scene.hierarchy", "scene", false, SceneHierarchy);
            RegisterCommand(registry, "scene.build-settings", "scene", false, SceneBuildSettings);
            RegisterCommand(registry, "scene.screenshot", "scene", true, SceneScreenshot);
            RegisterCommand(registry, "scene.create", "scene", true, SceneCreate);
            RegisterCommand(registry, "scene.load", "scene", true, SceneLoad);
            RegisterCommand(registry, "scene.save", "scene", true, SceneSave);
            RegisterCommand(registry, "scene.unload", "scene", true, SceneUnload);
            RegisterCommand(registry, "scene.set-active", "scene", true, SceneSetActive);
            RegisterCommand(registry, "scene.list-opened", "scene", false, SceneListOpened);
            RegisterCommand(registry, "scene.data", "scene", false, SceneData);
            RegisterCommand(registry, "scene.view-frame", "scene", true, SceneViewFrame);
            RegisterCommand(registry, "screenshot.capture", "scene", true, SceneScreenshot);
        }

        private static string SceneActive(BridgeRequest request)
        {
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("scene", ActiveSceneJson())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

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
                throw new BridgeCommandException("E_SCENE_PATH_REQUIRED", "Scene save requires --path when the active scene has not been saved yet.", "Pass unity_cli scene save Assets/Scenes/Main.unity.");
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
                    if (request.DryRun)
                    {
                        return SuccessJson(new UnityCliJsonWriter().BeginObject()
                            .Prop("command", "scene.unload").Prop("dryRun", true)
                            .Prop("path", path).Prop("sceneName", scene.name)
                            .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "scene.unload").Prop("path", path).EndObject())
                            .Prop("logPath", LogPath()).EndObject().ToString());
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
            return w.EndObject().ToString();
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
            return w.EndObject().ToString();
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
                    throw new BridgeCommandException("E_SCENE_PATH_REQUIRED", "Pass an Assets/*.unity scene path.", "Use unity_cli scene load Assets/Scenes/Main.unity.");
                }
                return null;
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_SCENE_PATH_INVALID", "Scene path must be under Assets/ and end with .unity: " + path, "Pass a Unity scene asset path such as Assets/Scenes/Main.unity.");
            }
            if (requireExisting && !AssetExists(path))
            {
                throw new BridgeCommandException("E_SCENE_NOT_FOUND", "Scene asset not found: " + path, "Run unity_cli asset search --type Scene or pass an existing Assets/*.unity path.");
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

        private static string SceneHierarchy(BridgeRequest request)
        {
            var roots = SceneManager.GetActiveScene().GetRootGameObjects();
            var total = 0;
            foreach (var root in roots)
            {
                total += CountHierarchy(root.transform);
            }
            var cursor = Math.Max(0, request.IntArg("cursor", 0));
            var pageSize = Math.Max(1, Math.Min(request.IntArg("pageSize", 100), 500));
            var items = new List<string>();
            var counter = new Counter();
            foreach (var root in roots)
            {
                if (items.Count >= pageSize)
                {
                    break;
                }
                CollectHierarchy(root.transform, items, cursor, pageSize, counter);
            }
            var nextCursor = cursor + items.Count < total ? (cursor + items.Count).ToString(CultureInfo.InvariantCulture) : null;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropObject("scene", w => w
                    .Prop("name", SceneManager.GetActiveScene().name)
                    .Prop("path", SceneManager.GetActiveScene().path))
                .PropArray("items", arr =>
                {
                    foreach (var item in items)
                    {
                        arr.RawValue(item);
                    }
                })
                .PropObject("pageInfo", w => w
                    .Prop("pageSize", (long)pageSize)
                    .Prop("cursor", (long)cursor)
                    .Prop("nextCursor", nextCursor)
                    .Prop("total", (long)total))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

    }
}
#endif
