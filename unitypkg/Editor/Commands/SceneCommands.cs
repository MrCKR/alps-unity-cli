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
