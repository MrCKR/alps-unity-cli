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
        // camera 域工具已全部迁移为 IUnityCliTool 自包含形态(unitypkg/Editor/Tools/camera/*.cs)。
        // 此处仅保留被其他域(Graphics/Screenshot/Context)复用的私有 helper 与空注册方法。
        private static void RegisterCameraCommands(BridgeCommandRegistry registry)
        {
        }

        // ─── 以下 helper 仍被 GraphicsCommands/ScreenshotCommands/ContextCommands 复用,保留 ───

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
    }
}
#endif
