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
            RegisterCommand(registry, "screenshot.surround", "screenshot", true, ScreenshotSurround);
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
            byte[] bytes;
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
                bytes = outputPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? texture.EncodeToJPG(95) : texture.EncodeToPNG();
                WriteScreenshotBytes(outputPath, bytes);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
            var format = outputPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";
            var capture = new ScreenshotResult(outputPath, format, width, height, false, 95, null, bytes, request.BoolArg("inlineBase64", false));
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "screenshot.game-view");
            w.PropRaw("artifact", ScreenshotDataJson(capture, "game-view", "Camera.main/allCameras"));
            w.Prop("screenshotPath", capture.Path)
                .Prop("artifactPath", capture.Path)
                .Prop("path", capture.Path)
                .Prop("format", capture.Format)
                .Prop("width", (long)capture.Width)
                .Prop("height", (long)capture.Height)
                .Prop("transparent", capture.Transparent)
                .Prop("quality", (long)capture.Quality)
                .Prop("source", "game-view")
                .Prop("target", "Camera.main/allCameras");
            AppendCameraInlineScreenshot(w, capture);
            if (includeDiagnostics)
            {
                w.PropRaw("diagnostics", GraphicsStatsSummaryJson());
            }
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
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
            w.PropRaw("artifact", ScreenshotDataJson(capture, "scene-view", "SceneView.camera"));
            w.Prop("screenshotPath", capture.Path)
                .Prop("artifactPath", capture.Path)
                .Prop("path", capture.Path)
                .Prop("format", capture.Format)
                .Prop("width", (long)capture.Width)
                .Prop("height", (long)capture.Height)
                .Prop("transparent", capture.Transparent)
                .Prop("quality", (long)capture.Quality)
                .Prop("source", "scene-view")
                .Prop("target", "SceneView.camera");
            AppendCameraInlineScreenshot(w, capture);
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
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
            byte[] bytes;
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
                bytes = outputPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? texture.EncodeToJPG(95) : texture.EncodeToPNG();
                WriteScreenshotBytes(outputPath, bytes);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tempGo);
            }
            var format = outputPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";
            var capture = new ScreenshotResult(outputPath, format, width, height, false, 95, null, bytes, request.BoolArg("inlineBase64", false));
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "screenshot.isolated-object");
            w.PropRaw("artifact", ScreenshotDataJson(capture, "isolated-object", "isolated:" + go.name));
            w.Prop("screenshotPath", capture.Path)
                .Prop("artifactPath", capture.Path)
                .Prop("path", capture.Path)
                .Prop("format", capture.Format)
                .Prop("width", (long)capture.Width)
                .Prop("height", (long)capture.Height)
                .Prop("transparent", capture.Transparent)
                .Prop("quality", (long)capture.Quality)
                .Prop("source", "isolated-object")
                .Prop("target", go.name);
            AppendCameraInlineScreenshot(w, capture);
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
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
            return SuccessJson(w.EndObject().ToString());
        }

        /// <summary>6 角度环绕截图并生成 contact sheet。用于 AI 快速检查对象外观。</summary>
        private static string ScreenshotSurround(BridgeRequest request)
        {
            var target = request.StringArg("target") ?? request.StringArg("path");
            var go = string.IsNullOrEmpty(target) ? null : ObjectRefResolver.FindGameObjectByPath(target);
            if (go == null)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "screenshot.surround requires a target GameObject.", "Pass --target <path>.");
            }
            var outputDir = request.StringArg("output-dir") ?? request.StringArg("dir") ?? "Assets/Screenshots/Surround";
            var outputFullDir = AssetFullPath(outputDir);
            var contactSheetRaw = request.StringArg("contactSheetPath") ?? request.StringArg("contact-sheet") ?? Path.Combine(outputDir, "surround_contact_sheet.png");
            var contactSheetPath = ResolveScreenshotOutputPath(contactSheetRaw);
            var overwrite = request.BoolArg("overwrite", false);
            var width = Math.Max(1, request.IntArg("width", 512));
            var height = Math.Max(1, request.IntArg("height", 512));
            var bounds = TargetRenderBounds(go);
            var center = bounds.center;
            var radius = request.OptionalFloatArg("radius") ?? Mathf.Max(bounds.extents.magnitude * 2.5f, 5f);
            var labels = new[] { "front", "back", "left", "right", "top", "bird_eye" };
            var positions = new[]
            {
                center + new Vector3(0, 0, -radius),
                center + new Vector3(0, 0, radius),
                center + new Vector3(-radius, 0, 0),
                center + new Vector3(radius, 0, 0),
                center + new Vector3(0, radius, 0),
                center + new Vector3(radius * 0.7f, radius * 0.7f, -radius * 0.7f)
            };
            var shotPaths = new List<string>();
            for (var i = 0; i < labels.Length; i++)
            {
                var path = Path.Combine(outputFullDir, "surround_" + labels[i] + ".png");
                if (File.Exists(path) && !overwrite)
                {
                    throw new BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + path, "Pass overwrite=true or choose a different output-dir.");
                }
                shotPaths.Add(path);
            }
            if (File.Exists(contactSheetPath) && !overwrite)
            {
                throw new BridgeCommandException("E_OUTPUT_EXISTS", "Contact sheet output already exists: " + contactSheetPath, "Pass overwrite=true or choose a different contactSheetPath.");
            }
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "screenshot.surround")
                    .Prop("dryRun", true)
                    .Prop("target", go.name)
                    .PropRaw("center", Vector3Json(center))
                    .Prop("radius", radius)
                    .PropRaw("contactSheet", ScreenshotArtifactJson("surround-contact-sheet", contactSheetPath, width * 3, height * 2, "surround"))
                    .PropArray("plannedArtifacts", arr =>
                    {
                        for (var i = 0; i < labels.Length; i++) arr.RawValue(SurroundShotJson(labels[i], shotPaths[i], width, height, positions[i]));
                    })
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }
            Directory.CreateDirectory(outputFullDir);
            CreateScreenshotDirectory(contactSheetPath);
            var tempGo = new GameObject("__unity_cli_surround_cam__");
            var cam = tempGo.AddComponent<Camera>();
            cam.fieldOfView = Mathf.Clamp(request.FloatArg("fov", 60f), 10f, 120f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = Mathf.Max(radius * 6f, 100f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);
            var tiles = new List<Texture2D>();
            var artifacts = new List<string>();
            byte[] contactBytes;
            try
            {
                for (var i = 0; i < labels.Length; i++)
                {
                    cam.transform.position = positions[i];
                    cam.transform.LookAt(center);
                    var tile = RenderScreenshotTexture(cam, width, height);
                    tiles.Add(tile);
                    WriteScreenshotBytes(shotPaths[i], tile.EncodeToPNG());
                    artifacts.Add(SurroundShotJson(labels[i], shotPaths[i], width, height, positions[i]));
                }
                var contact = ComposeScreenshotContactSheet(tiles, width, height);
                try
                {
                    contactBytes = contact.EncodeToPNG();
                    WriteScreenshotBytes(contactSheetPath, contactBytes);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(contact);
                }
            }
            finally
            {
                foreach (var tile in tiles) UnityEngine.Object.DestroyImmediate(tile);
                UnityEngine.Object.DestroyImmediate(tempGo);
            }
            var w = new UnityCliJsonWriter().BeginObject()
                .Prop("command", "screenshot.surround")
                .Prop("target", go.name)
                .PropRaw("center", Vector3Json(center))
                .Prop("radius", radius)
                .Prop("count", 6L)
                .PropRaw("contactSheet", ScreenshotArtifactJson("surround-contact-sheet", contactSheetPath, width * 3, height * 2, "surround"))
                .PropArray("artifacts", arr => { foreach (var artifact in artifacts) arr.RawValue(artifact); });
            if (request.BoolArg("inlineBase64", false)) AppendInlineScreenshot(w, Convert.ToBase64String(contactBytes), width * 3, height * 2);
            return SuccessJson(w.Prop("logPath", LogPath()).EndObject().ToString());
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

        private static void AppendInlineScreenshot(UnityCliJsonWriter writer, string imageBase64, int width, int height, string mimeType = "image/png")
        {
            if (!ShouldInlineScreenshotBase64(writer, imageBase64)) return;
            writer.Prop("imageBase64", imageBase64)
                .Prop("imageWidth", (long)width)
                .Prop("imageHeight", (long)height)
                .Prop("imageMimeType", mimeType);
        }

        private static string SurroundShotJson(string label, string path, int width, int height, Vector3 position)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("angle", label)
                .PropRaw("artifact", ScreenshotArtifactJson("surround", path, width, height, "surround:" + label))
                .PropRaw("cameraPosition", Vector3Json(position))
                .EndObject().ToString();
        }

        private static Bounds TargetRenderBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                if (renderers[i] != null && renderers[i].gameObject.activeInHierarchy) bounds.Encapsulate(renderers[i].bounds);
            }
            return bounds;
        }

        private static Texture2D RenderScreenshotTexture(Camera camera, int width, int height)
        {
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                return texture;
            }
            finally
            {
                RenderTexture.active = previousActive;
                camera.targetTexture = previousTarget;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        private static Texture2D ComposeScreenshotContactSheet(List<Texture2D> tiles, int tileWidth, int tileHeight)
        {
            var columns = 3;
            var rows = 2;
            var sheet = new Texture2D(tileWidth * columns, tileHeight * rows, TextureFormat.RGB24, false);
            var fill = Enumerable.Repeat(new Color(0.05f, 0.05f, 0.05f, 1f), sheet.width * sheet.height).ToArray();
            sheet.SetPixels(fill);
            for (var i = 0; i < tiles.Count; i++)
            {
                var column = i % columns;
                var row = rows - 1 - (i / columns);
                sheet.SetPixels(column * tileWidth, row * tileHeight, tileWidth, tileHeight, tiles[i].GetPixels());
            }
            sheet.Apply();
            return sheet;
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
