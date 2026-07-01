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
        private static void RegisterCameraCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "camera.list", "camera", false, delegate(BridgeRequest request) { return SuccessJson(CameraListJson()); });
            RegisterCommand(registry, "camera.info", "camera", false, CameraInfo);
            RegisterCommand(registry, "camera.screenshot", "camera", true, CameraScreenshot);
            RegisterCommand(registry, "camera.create", "camera", true, CameraCreate);
            RegisterCommand(registry, "camera.target", "camera", true, CameraTarget);
            RegisterCommand(registry, "camera.lens", "camera", true, CameraLens);
            RegisterCommand(registry, "camera.multiview", "camera", true, CameraMultiview);
        }

        private static string CameraInfo(BridgeRequest request)
        {
            var camera = ResolveCamera(request.StringArg("target"));
            if (camera == null)
            {
                return ErrorJson("E_CAMERA_NOT_FOUND", "Camera not found: " + request.StringArg("target"), "Run tools run camera.list, then retry with a camera name, hierarchy path, or instance id from the result.");
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("camera", CameraJson(camera))
                .EndObject()
                .ToString());
        }

        private static string CameraScreenshot(BridgeRequest request)
        {
            var target = request.StringArg("target");
            if (string.IsNullOrEmpty(target))
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "Camera screenshot requires a target Camera.", "Pass a Camera name, hierarchy path or instance id. Run tools run camera.list before retrying.");
            }

            var camera = ResolveCamera(target);
            if (camera == null)
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "Camera not found: " + target, "Run tools run camera.list, then retry with a camera name, hierarchy path, or instance id from the result.");
            }

            var capture = CaptureCamera(camera, request);
            return SuccessJson(ScreenshotJson(capture, "camera", camera.name));
        }

        private static ScreenshotResult CaptureCamera(Camera camera, BridgeRequest request)
        {
            var outputPath = ResolveScreenshotOutputPath(request.StringArg("path"));
            var format = NormalizeFormat(request.StringArg("format"), outputPath);
            var overwrite = request.BoolArg("overwrite", false);
            if (File.Exists(outputPath) && !overwrite)
            {
                throw new BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + outputPath, "Pass overwrite=true or choose a different output path.");
            }

            CreateScreenshotDirectory(outputPath);
            var width = ClampScreenshotDimension(request.IntArg("width", camera.pixelWidth > 0 ? camera.pixelWidth : 1280), "width");
            var height = ClampScreenshotDimension(request.IntArg("height", camera.pixelHeight > 0 ? camera.pixelHeight : 720), "height");
            var transparent = request.BoolArg("transparent", false);
            var quality = Mathf.Clamp(request.IntArg("quality", 95), 1, 100);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var previousClearFlags = camera.clearFlags;
            var previousBackground = camera.backgroundColor;
            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(width, height, transparent ? TextureFormat.ARGB32 : TextureFormat.RGB24, false);

            try
            {
                camera.targetTexture = renderTexture;
                if (transparent)
                {
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                }
                camera.Render();
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                var bytes = format == "jpg" ? texture.EncodeToJPG(quality) : texture.EncodeToPNG();
                WriteScreenshotBytes(outputPath, bytes);
                var inlineRequested = request.BoolArg("inlineBase64", false);
                WriteLog("screenshot_written path=" + outputPath + " width=" + width + " height=" + height + " format=" + format);
                return new ScreenshotResult(outputPath, format, width, height, transparent, quality, null, bytes, inlineRequested);
            }
            finally
            {
                RenderTexture.active = previousActive;
                camera.targetTexture = previousTarget;
                camera.clearFlags = previousClearFlags;
                camera.backgroundColor = previousBackground;
                UnityEngine.Object.DestroyImmediate(texture);
                renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }

        }

        private static Camera ResolveCamera(string target)
        {
            var cameras = Camera.allCameras;
            if (string.IsNullOrEmpty(target))
            {
                return Camera.main ?? (cameras.Length > 0 ? cameras[0] : null);
            }

            foreach (var camera in cameras)
            {
                if (camera.name == target || camera.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target || HierarchyPath(camera.gameObject) == target)
                {
                    return camera;
                }
            }

            return null;
        }

        private static string CameraListJson()
        {
            var cameras = Camera.allCameras;
            return new UnityCliJsonWriter().BeginObject()
                .PropArray("cameras", arr => { foreach (var camera in cameras) { arr.RawValue(CameraJson(camera)); } })
                .Prop("count", (long)cameras.Length)
                .EndObject()
                .ToString();
        }

        private static string CameraJson(Camera camera)
        {
            var targetTexture = camera.targetTexture != null ? camera.targetTexture.name : null;
            return new UnityCliJsonWriter().BeginObject()
                .Prop("id", (long)camera.GetInstanceID())
                .Prop("name", camera.name)
                .Prop("path", HierarchyPath(camera.gameObject))
                .Prop("tag", camera.tag)
                .Prop("enabled", camera.enabled)
                .Prop("clearFlags", camera.clearFlags.ToString())
                .Prop("projection", camera.orthographic ? "orthographic" : "perspective")
                .Prop("fieldOfView", camera.fieldOfView)
                .Prop("orthographicSize", camera.orthographicSize)
                .Prop("depth", camera.depth)
                .Prop("targetTexture", targetTexture)
                .Prop("pixelWidth", (long)camera.pixelWidth)
                .Prop("pixelHeight", (long)camera.pixelHeight)
                .EndObject()
                .ToString();
        }

        private static string ScreenshotJson(ScreenshotResult capture, string source, string target)
        {
            var w = new UnityCliJsonWriter().BeginObject()
                .Prop("screenshotPath", capture.Path)
                .Prop("artifactPath", capture.Path)
                .Prop("format", capture.Format)
                .Prop("width", (long)capture.Width)
                .Prop("height", (long)capture.Height)
                .Prop("transparent", capture.Transparent)
                .Prop("quality", (long)capture.Quality)
                .Prop("source", source)
                .Prop("target", target);
            AppendCameraInlineScreenshot(w, capture);
            return w.Prop("logPath", LogPath()).EndObject().ToString();
        }

        private static string ScreenshotDataJson(ScreenshotResult capture, string source, string target)
        {
            var w = new UnityCliJsonWriter().BeginObject()
                .Prop("screenshotPath", capture.Path)
                .Prop("artifactPath", capture.Path)
                .Prop("format", capture.Format)
                .Prop("width", (long)capture.Width)
                .Prop("height", (long)capture.Height)
                .Prop("transparent", capture.Transparent)
                .Prop("quality", (long)capture.Quality)
                .Prop("source", source)
                .Prop("target", target);
            AppendCameraInlineScreenshot(w, capture);
            return w.EndObject().ToString();
        }

        /// <summary>按图片字节上限控制相机截图内联，避免大 base64 进入响应。</summary>
        private static void AppendCameraInlineScreenshot(UnityCliJsonWriter writer, ScreenshotResult capture)
        {
            if (capture == null || !capture.InlineBase64Requested) return;
            var mimeType = capture.Format == "jpg" ? "image/jpeg" : "image/png";
            if (capture.ImageBytes != null)
            {
                AppendInlineScreenshotBytes(writer, BridgeRequest.Parse("{\"inlineBase64\":true}"), capture.ImageBytes, capture.Width, capture.Height, mimeType);
                return;
            }
            if (ShouldInlineScreenshotBase64(writer, capture.ImageBase64))
            {
                AppendInlineScreenshot(writer, capture.ImageBase64, capture.Width, capture.Height, mimeType);
            }
        }

        private static string CameraCreate(BridgeRequest request)
        {
            var name = request.OptionalStringArg("name") ?? "Camera";
            var parent = request.StringArg("parent");
            var parentGo = string.IsNullOrEmpty(parent) ? null : ResolveUniqueTarget(parent, "camera.create --parent <go> not found.");
            var position = request.HasArg("position") ? (Vector3?)ParseVector3(request.StringArg("position")) : null;
            var rotation = request.HasArg("rotation") ? (Vector3?)ParseVector3(request.StringArg("rotation")) : null;
            var clearFlags = request.HasArg("clearFlags") ? (CameraClearFlags?)ParseCameraClearFlags(request.StringArg("clearFlags")) : null;

            var gameObject = new GameObject(name);
            if (parentGo != null)
            {
                gameObject.transform.SetParent(parentGo.transform, false);
            }
            var camera = gameObject.AddComponent<Camera>();
            if (position.HasValue)
            {
                gameObject.transform.position = position.Value;
            }
            if (rotation.HasValue)
            {
                gameObject.transform.eulerAngles = rotation.Value;
            }
            if (request.HasArg("fieldOfView"))
            {
                camera.fieldOfView = request.OptionalFloatArg("fieldOfView").Value;
            }
            if (request.HasArg("orthographic"))
            {
                camera.orthographic = request.OptionalBoolArg("orthographic").Value;
            }
            if (request.HasArg("orthographicSize"))
            {
                camera.orthographicSize = request.OptionalFloatArg("orthographicSize").Value;
            }
            if (clearFlags.HasValue)
            {
                camera.clearFlags = clearFlags.Value;
            }
            if (request.HasArg("depth"))
            {
                camera.depth = request.OptionalFloatArg("depth").Value;
            }
            if (request.HasArg("tag"))
            {
                var tag = request.StringArg("tag");
                if (tag == "MainCamera")
                {
                    gameObject.tag = "MainCamera";
                }
            }
            EditorUtility.SetDirty(gameObject);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            WriteLog("camera_create name=" + name);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("camera", CameraJson(camera))
                .PropArray("plannedEffects", arr => arr.RawValue(EffectJson("camera.create", "name", name)))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string CameraTarget(BridgeRequest request)
        {
            var target = request.StringArg("target");
            if (string.IsNullOrEmpty(target))
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "camera.target requires target.", "Run tools run camera.list, then pass a camera name, hierarchy path, or instance id.");
            }
            var camera = ResolveCamera(target);
            if (camera == null)
            {
                return ErrorJson("E_CAMERA_NOT_FOUND", "Camera not found: " + target, "Run tools run camera.list, then retry with a camera name, hierarchy path, or instance id from the result.");
            }
            var lookAtGo = request.HasArg("lookAt") ? ResolveUniqueTarget(request.StringArg("lookAt"), "camera.target --look-at <go> not found.") : null;
            var position = request.HasArg("position") ? (Vector3?)ParseVector3(request.StringArg("position")) : null;
            var rotation = request.HasArg("rotation") ? (Vector3?)ParseVector3(request.StringArg("rotation")) : null;
            var effects = new List<string>();
            if (lookAtGo != null)
            {
                camera.transform.LookAt(lookAtGo.transform);
                effects.Add(EffectJson("camera.target", "lookAt", HierarchyPath(lookAtGo)));
            }
            if (position.HasValue)
            {
                camera.transform.position = position.Value;
                effects.Add(EffectJson("camera.target", "position", request.StringArg("position")));
            }
            if (rotation.HasValue)
            {
                camera.transform.eulerAngles = rotation.Value;
                effects.Add(EffectJson("camera.target", "rotation", request.StringArg("rotation")));
            }
            EditorUtility.SetDirty(camera);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("camera", CameraJson(camera))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string CameraLens(BridgeRequest request)
        {
            var target = request.StringArg("target");
            if (string.IsNullOrEmpty(target))
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "camera.lens requires target.", "Run tools run camera.list, then pass a camera name, hierarchy path, or instance id.");
            }
            var camera = ResolveCamera(target);
            if (camera == null)
            {
                return ErrorJson("E_CAMERA_NOT_FOUND", "Camera not found: " + target, "Run tools run camera.list, then retry with a camera name, hierarchy path, or instance id from the result.");
            }
            var clearFlags = request.HasArg("clearFlags") ? (CameraClearFlags?)ParseCameraClearFlags(request.StringArg("clearFlags")) : null;
            var effects = new List<string>();
            if (request.HasArg("fieldOfView"))
            {
                camera.fieldOfView = request.OptionalFloatArg("fieldOfView").Value;
                effects.Add(EffectJson("camera.lens", "fieldOfView", Number(camera.fieldOfView)));
            }
            if (request.HasArg("orthographic"))
            {
                camera.orthographic = request.OptionalBoolArg("orthographic").Value;
                effects.Add(EffectJson("camera.lens", "orthographic", Bool(camera.orthographic)));
            }
            if (request.HasArg("orthographicSize"))
            {
                camera.orthographicSize = request.OptionalFloatArg("orthographicSize").Value;
                effects.Add(EffectJson("camera.lens", "orthographicSize", Number(camera.orthographicSize)));
            }
            if (request.HasArg("nearClipPlane"))
            {
                camera.nearClipPlane = request.OptionalFloatArg("nearClipPlane").Value;
                effects.Add(EffectJson("camera.lens", "nearClipPlane", Number(camera.nearClipPlane)));
            }
            if (request.HasArg("farClipPlane"))
            {
                camera.farClipPlane = request.OptionalFloatArg("farClipPlane").Value;
                effects.Add(EffectJson("camera.lens", "farClipPlane", Number(camera.farClipPlane)));
            }
            if (clearFlags.HasValue)
            {
                camera.clearFlags = clearFlags.Value;
                effects.Add(EffectJson("camera.lens", "clearFlags", request.StringArg("clearFlags")));
            }
            if (request.HasArg("depth"))
            {
                camera.depth = request.OptionalFloatArg("depth").Value;
                effects.Add(EffectJson("camera.lens", "depth", Number(camera.depth)));
            }
            EditorUtility.SetDirty(camera);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("camera", CameraJson(camera))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static int ClampScreenshotDimension(int value, string name)
        {
            if (value < 1 || value > 8192)
            {
                throw new BridgeCommandException("E_SCREENSHOT_SIZE_INVALID", "Camera screenshot " + name + " must be between 1 and 8192 pixels: " + value.ToString(CultureInfo.InvariantCulture), "Use a smaller width/height before retrying.");
            }
            return value;
        }

        private static CameraClearFlags ParseCameraClearFlags(string value)
        {
            CameraClearFlags parsed;
            if (!Enum.TryParse<CameraClearFlags>(value, true, out parsed))
            {
                throw new BridgeCommandException("E_CAMERA_CLEAR_FLAGS_INVALID", "Unsupported Camera clearFlags value: " + value, "Use Skybox, SolidColor, Depth, or Nothing.");
            }
            return parsed;
        }

        private static string CameraMultiview(BridgeRequest request)
        {
            var targets = request.StringArg("targets");
            if (string.IsNullOrEmpty(targets))
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "camera.multiview requires targets.", "Run tools run camera.list, then pass comma-separated camera names, hierarchy paths, or instance ids.");
            }
            var names = targets.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var cameras = new List<Camera>();
            foreach (var name in names)
            {
                var camera = ResolveCamera(name.Trim());
                if (camera == null)
                {
                    return ErrorJson("E_CAMERA_NOT_FOUND", "Camera not found: " + name.Trim(), "Run tools run camera.list, then retry with camera names, hierarchy paths, or instance ids from the result.");
                }
                cameras.Add(camera);
            }
            var outputDir = request.StringArg("output") ?? "Screenshots";
            var width = request.OptionalIntArg("width") ?? 1280;
            var height = request.OptionalIntArg("height") ?? 720;
            var format = request.OptionalStringArg("format") ?? "png";
            var captures = new List<string>();
            var effects = new List<string>();
            foreach (var camera in cameras)
            {
                var outputPath = System.IO.Path.Combine(outputDir, camera.name + "." + format);
                var captureBody = "{\"path\":\"" + outputPath.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    + "\",\"width\":" + width.ToString(CultureInfo.InvariantCulture)
                    + ",\"height\":" + height.ToString(CultureInfo.InvariantCulture)
                    + ",\"format\":\"" + format + "\""
                    + ",\"overwrite\":true" + "}";
                var capture = CaptureCamera(camera, BridgeRequest.Parse(captureBody));
                captures.Add(ScreenshotDataJson(capture, "camera.multiview", camera.name));
                effects.Add(EffectJson("camera.multiview", "capture", outputPath));
            }
            WriteLog("camera_multiview captures=" + captures.Count.ToString(CultureInfo.InvariantCulture));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropArray("captures", arr => { foreach (var c in captures) arr.RawValue(c); })
                .Prop("count", (long)captures.Count)
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }
    }
}
#endif
