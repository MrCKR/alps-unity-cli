#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterScreenshotCommands(BridgeCommandRegistry registry)
        {
            // 对齐 Ivan screenshot game-view/scene-view/isolated + CoplayDev orbit/surround
            RegisterCommand(registry, "screenshot.game-view", "screenshot", true, ScreenshotGameView);
            RegisterCommand(registry, "screenshot.scene-view", "screenshot", true, ScreenshotSceneView);
            RegisterCommand(registry, "screenshot.isolated-object", "screenshot", true, ScreenshotIsolatedObject);
            RegisterCommand(registry, "screenshot.orbit", "screenshot", true, ScreenshotOrbit);
        }

        /// <summary>Game View 截图：对齐 Ivan game-view。可带 diagnostics。</summary>
        private static string ScreenshotGameView(BridgeRequest request)
        {
            var outputPath = ResolveScreenshotOutputPath(request.StringArg("path"));
            var overwrite = request.BoolArg("overwrite", false);
            if (File.Exists(outputPath) && !overwrite)
            {
                throw new BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + outputPath);
            }
            CreateScreenshotDirectory(outputPath);
            var width = Math.Max(1, request.IntArg("width", 1280));
            var height = Math.Max(1, request.IntArg("height", 720));
            var includeDiagnostics = request.BoolArg("includeDiagnostics", false);
            // 使用临时 RenderTexture 渲染所有相机模拟 Game View
            var rt = new RenderTexture(width, height, 24);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var cameras = Camera.allCamerasCount > 0 ? Camera.allCameras : new Camera[0];
                foreach (var cam in cameras)
                {
                    if (!cam.gameObject.activeInHierarchy) continue;
                    var prevTarget = cam.targetTexture;
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.targetTexture = prevTarget;
                }
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                RenderTexture.active = prev;
                var bytes = outputPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? texture.EncodeToJPG(95) : texture.EncodeToPNG();
                WriteScreenshotBytes(outputPath, bytes);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "screenshot.game-view");
            w.PropRaw("artifact", ScreenshotArtifactJson("game-view", outputPath, width, height, "Camera.main/allCameras"));
            if (includeDiagnostics)
            {
                w.PropRaw("diagnostics", GraphicsStatsSummaryJson());
            }
            w.Prop("logPath", LogPath());
            return w.EndObject().ToString();
        }

        /// <summary>SceneView 截图：对齐 Ivan scene-view。</summary>
        private static string ScreenshotSceneView(BridgeRequest request)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null || sceneView.camera == null)
            {
                throw new BridgeCommandException("E_SCENE_VIEW_NOT_AVAILABLE", "No active SceneView camera.", "Open a SceneView window.");
            }
            var capture = CaptureCamera(sceneView.camera, request);
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "screenshot.scene-view");
            w.PropRaw("artifact", ScreenshotArtifactJson("scene-view", capture.Path, capture.Width, capture.Height, "SceneView.camera"));
            w.Prop("logPath", LogPath());
            return w.EndObject().ToString();
        }

        /// <summary>隔离对象截图：对齐 Ivan isolated。在临时场景渲染单个对象。</summary>
        private static string ScreenshotIsolatedObject(BridgeRequest request)
        {
            var target = request.StringArg("target") ?? request.StringArg("path");
            var go = string.IsNullOrEmpty(target) ? null : ObjectRefResolver.FindGameObjectByPath(target);
            if (go == null)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "screenshot.isolated-object requires a target GameObject.", "Pass --target <path>.");
            }
            var outputPath = ResolveScreenshotOutputPath(request.StringArg("path"));
            var overwrite = request.BoolArg("overwrite", false);
            if (File.Exists(outputPath) && !overwrite)
            {
                throw new BridgeCommandException("E_OUTPUT_EXISTS", "Output exists: " + outputPath);
            }
            CreateScreenshotDirectory(outputPath);
            var width = Math.Max(1, request.IntArg("width", 512));
            var height = Math.Max(1, request.IntArg("height", 512));
            // 创建临时相机渲染对象
            var tempGo = new GameObject("__unity_cli_isolated_cam__");
            var cam = tempGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 1f);
            cam.orthographic = request.BoolArg("orthographic", true);
            cam.orthographicSize = request.FloatArg("size", 2f);
            var bounds = go.GetComponent<Renderer>() != null ? go.GetComponent<Renderer>().bounds : new Bounds(go.transform.position, Vector3.one);
            tempGo.transform.position = bounds.center + new Vector3(0, 0, -5);
            tempGo.transform.LookAt(bounds.center);
            var prevActive = go.activeSelf;
            // 渲染
            var rt = new RenderTexture(width, height, 24);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                RenderTexture.active = prev;
                var bytes = outputPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? texture.EncodeToJPG(95) : texture.EncodeToPNG();
                WriteScreenshotBytes(outputPath, bytes);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tempGo);
            }
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "screenshot.isolated-object");
            w.PropRaw("artifact", ScreenshotArtifactJson("isolated-object", outputPath, width, height, "isolated:" + go.name));
            w.Prop("target", go.name);
            w.Prop("logPath", LogPath());
            return w.EndObject().ToString();
        }

        /// <summary>环绕截图：对齐 CoplayDev orbit/surround。从多个角度截图同一对象。</summary>
        private static string ScreenshotOrbit(BridgeRequest request)
        {
            var target = request.StringArg("target") ?? request.StringArg("path");
            var go = string.IsNullOrEmpty(target) ? null : ObjectRefResolver.FindGameObjectByPath(target);
            if (go == null)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "screenshot.orbit requires a target GameObject.", "Pass --target <path>.");
            }
            var outputDir = request.StringArg("output-dir") ?? request.StringArg("dir") ?? "Assets/Screenshots";
            var angleCount = Math.Max(2, Math.Min(request.IntArg("count", 8), 36));
            var radius = request.FloatArg("radius", 5f);
            var width = Math.Max(1, request.IntArg("width", 512));
            var height = Math.Max(1, request.IntArg("height", 512));
            var center = go.transform.position;
            var artifacts = new List<string>();
            // 复用主相机
            var cam = Camera.main;
            if (cam == null)
            {
                var tempGo = new GameObject("__unity_cli_orbit_cam__");
                cam = tempGo.AddComponent<Camera>();
            }
            var tempCreated = cam.gameObject.name == "__unity_cli_orbit_cam__";
            try
            {
                for (var i = 0; i < angleCount; i++)
                {
                    var angle = (360f / angleCount) * i;
                    var rad = angle * Mathf.Deg2Rad;
                    cam.transform.position = center + new Vector3(Mathf.Cos(rad) * radius, 0, Mathf.Sin(rad) * radius);
                    cam.transform.LookAt(center);
                    var path = Path.Combine(AssetFullPath(outputDir), "orbit_" + i.ToString("00", CultureInfo.InvariantCulture) + ".png");
                    CreateScreenshotDirectory(path);
                    var rt = new RenderTexture(width, height, 24);
                    var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                    var prevTarget = cam.targetTexture;
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.targetTexture = prevTarget;
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    texture.Apply();
                    RenderTexture.active = prev;
                    WriteScreenshotBytes(path, texture.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(texture);
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                    artifacts.Add(ScreenshotArtifactJson("orbit", path, width, height, "orbit:" + angle.ToString("F0", CultureInfo.InvariantCulture)));
                }
            }
            finally
            {
                if (tempCreated) UnityEngine.Object.DestroyImmediate(cam.gameObject);
            }
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "screenshot.orbit");
            w.Prop("target", go.name);
            w.Prop("count", (long)artifacts.Count);
            w.PropArray("artifacts", arr => { foreach (var a in artifacts) arr.RawValue(a); });
            w.Prop("logPath", LogPath());
            return w.EndObject().ToString();
        }

        private static string ScreenshotArtifactJson(string source, string path, int width, int height, string camera)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("source", source)
                .Prop("path", path)
                .Prop("camera", camera)
                .Prop("width", (long)width)
                .Prop("height", (long)height)
                .Prop("format", path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png")
                .EndObject().ToString();
        }

        private static string GraphicsStatsSummaryJson()
        {
            var stats = UnityStatsType();
            if (stats == null) return "{}";
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("batches", StringMemberValue(stats, null, "batches"));
            w.Prop("triangles", StringMemberValue(stats, null, "triangles"));
            w.Prop("vertices", StringMemberValue(stats, null, "vertices"));
            w.Prop("setPassCalls", StringMemberValue(stats, null, "setPassCalls"));
            return w.EndObject().ToString();
        }

        private static Type UnityStatsType()
        {
            var statsType = FindType("UnityEditor.UnityStats");
            if (statsType == null) return null;
            var prop = statsType.GetProperty("drawCalls");
            return prop != null ? statsType : null;
        }
    }
}
#endif
