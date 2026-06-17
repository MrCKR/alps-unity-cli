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
        }

        private static string CameraInfo(BridgeRequest request)
        {
            var camera = ResolveCamera(request.StringArg("target"));
            if (camera == null)
            {
                return ErrorJson("E_CAMERA_NOT_FOUND", "Camera not found: " + request.StringArg("target"), "Run unity_cli camera list to inspect available cameras.");
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
                return ErrorJson("E_TARGET_NOT_FOUND", "Camera screenshot requires a target Camera.", "Pass a Camera name, hierarchy path or instance id. Run unity_cli camera list to inspect available cameras.");
            }

            var camera = ResolveCamera(target);
            if (camera == null)
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "Camera not found: " + target, "Run unity_cli camera list to inspect available cameras.");
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
                throw new BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + outputPath);
            }

            CreateScreenshotDirectory(outputPath);
            var width = Math.Max(1, request.IntArg("width", camera.pixelWidth > 0 ? camera.pixelWidth : 1280));
            var height = Math.Max(1, request.IntArg("height", camera.pixelHeight > 0 ? camera.pixelHeight : 720));
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

            WriteLog("screenshot_written path=" + outputPath + " width=" + width + " height=" + height + " format=" + format);
            return new ScreenshotResult(outputPath, format, width, height, transparent, quality);
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
            return new UnityCliJsonWriter().BeginObject()
                .Prop("screenshotPath", capture.Path)
                .Prop("artifactPath", capture.Path)
                .Prop("format", capture.Format)
                .Prop("width", (long)capture.Width)
                .Prop("height", (long)capture.Height)
                .Prop("transparent", capture.Transparent)
                .Prop("quality", (long)capture.Quality)
                .Prop("source", source)
                .Prop("target", target)
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
        }

        private static string ScreenshotDataJson(ScreenshotResult capture, string source, string target)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("screenshotPath", capture.Path)
                .Prop("artifactPath", capture.Path)
                .Prop("format", capture.Format)
                .Prop("width", (long)capture.Width)
                .Prop("height", (long)capture.Height)
                .Prop("transparent", capture.Transparent)
                .Prop("quality", (long)capture.Quality)
                .Prop("source", source)
                .Prop("target", target)
                .EndObject()
                .ToString();
        }
    }
}
#endif
