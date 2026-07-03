#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// scene.screenshot / screenshot.capture — 捕获激活 SceneView(或主 Camera)到 Assets/* 图片。
    /// 两个工具共享同一捕获逻辑(原 SceneScreenshot handler),仅 Name/Group 不同。
    /// screenshot.capture 为 screenshot 域别名;scene.screenshot 为 scene 域兼容入口。
    /// 截图捕获逻辑自包含复刻(不可复用 Camera 域私有 CaptureCamera)。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>

    /// <summary>截图捕获结果,两个 screenshot 工具共享。</summary>
    public sealed class ScreenshotResult
    {
        public string ScreenshotPath { get; set; }
        public string ArtifactPath { get; set; }
        public string Format { get; set; }
        public long Width { get; set; }
        public long Height { get; set; }
        public bool Transparent { get; set; }
        public long Quality { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
        public string ImageBase64 { get; set; }
        public long? ImageWidth { get; set; }
        public long? ImageHeight { get; set; }
        public string ImageMimeType { get; set; }
    }

    /// <summary>scene.screenshot — 捕获激活 SceneView/主 Camera 到 Assets/* 图片(scene 域兼容入口)。</summary>
    public class SceneScreenshotTool : IUnityCliTool<SceneScreenshotTool.Request, ScreenshotResult>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Path { get; set; }
            public string Format { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public bool Transparent { get; set; }
            public int? Quality { get; set; }
            public bool Overwrite { get; set; }
            public bool InlineBase64 { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.screenshot",
            Summary = "Compatibility alias for active scene viewport screenshots; prefer screenshot.scene-view for explicit source.",
            Description = "Compatibility alias that captures the active scene viewport and saves to an Assets/* image path. Prefer screenshot.scene-view for explicit SceneView source control, or screenshot.game-view for Game View output.",
            Group = "scene",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "asset-write", "asset-import" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Capture a scene screenshot", Input = new Request { Path = "Assets/UnityCliAlignmentFixtures/_scene_shot.png" } }
            }
        };

        public ScreenshotResult Execute(Request request, UnityCliProjectToolContext ctx)
        {
            return SceneScreenshotCapture.Run(request.Path, request.Format, request.Width, request.Height, request.Transparent, request.Quality, request.Overwrite, request.InlineBase64);
        }
    }

    /// <summary>screenshot.capture — 捕获激活 SceneView/主 Camera 到 Assets/* 图片(screenshot 域别名)。</summary>
    public class ScreenshotCaptureTool : IUnityCliTool<ScreenshotCaptureTool.Request, ScreenshotResult>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Path { get; set; }
            public string Format { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public bool Transparent { get; set; }
            public int? Quality { get; set; }
            public bool Overwrite { get; set; }
            public bool InlineBase64 { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "screenshot.capture",
            Summary = "Compatibility alias: capture active SceneView or main Camera; prefer scene-view/game-view for explicit source.",
            Description = "Compatibility alias that captures the active SceneView (or main Camera when no SceneView is open) to an Assets/* image path. Prefer screenshot.scene-view or screenshot.game-view when the source matters. Returns the saved artifact path and size.",
            Group = "screenshot",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "asset-write", "asset-import" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Capture the scene view to a PNG", Input = new Request { Path = "Assets/UnityCliAlignmentFixtures/CapturedShot.png" } }
            }
        };

        public ScreenshotResult Execute(Request request, UnityCliProjectToolContext ctx)
        {
            return SceneScreenshotCapture.Run(request.Path, request.Format, request.Width, request.Height, request.Transparent, request.Quality, request.Overwrite, request.InlineBase64);
        }
    }

    /// <summary>
    /// scene 域截图捕获共享实现:渲染 SceneView/主 Camera 到 RenderTexture,编码写文件。
    /// 仅供 scene.screenshot / screenshot.capture 复用;不实现 IUnityCliTool,不参与发现。
    /// </summary>
    internal static class SceneScreenshotCapture
    {
        // 内联 base64 上限,防止图像撑爆 JSON 响应。
        private const int InlineImageMaxBytes = 512 * 1024;

        public static ScreenshotResult Run(string path, string format, int? widthOpt, int? heightOpt, bool transparent, int? qualityOpt, bool overwrite, bool inlineBase64)
        {
            var camera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : Camera.main;
            if (camera == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_VIEW_NOT_AVAILABLE", "No SceneView or main Camera is available for screenshot capture.", "Open a SceneView or create an active Camera in the scene.");
            }

            var outputPath = ResolveScreenshotOutputPath(path);
            var fmt = NormalizeFormat(format, outputPath);
            if (File.Exists(outputPath) && !overwrite)
            {
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + outputPath, "Pass overwrite=true or choose a different output path.");
            }
            CreateScreenshotDirectory(outputPath);
            var width = ClampDimension(widthOpt ?? (camera.pixelWidth > 0 ? camera.pixelWidth : 1280));
            var height = ClampDimension(heightOpt ?? (camera.pixelHeight > 0 ? camera.pixelHeight : 720));
            var quality = Mathf.Clamp(qualityOpt ?? 95, 1, 100);

            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var previousClearFlags = camera.clearFlags;
            var previousBackground = camera.backgroundColor;
            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(width, height, transparent ? TextureFormat.ARGB32 : TextureFormat.RGB24, false);
            byte[] bytes;
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
                bytes = fmt == "jpg" ? texture.EncodeToJPG(quality) : texture.EncodeToPNG();
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

            var result = new ScreenshotResult
            {
                ScreenshotPath = outputPath,
                ArtifactPath = outputPath,
                Format = fmt,
                Width = width,
                Height = height,
                Transparent = transparent,
                Quality = quality,
                Source = "scene",
                Target = camera.name
            };
            if (inlineBase64 && bytes != null && bytes.Length <= InlineImageMaxBytes)
            {
                result.ImageBase64 = Convert.ToBase64String(bytes);
                result.ImageWidth = width;
                result.ImageHeight = height;
                result.ImageMimeType = fmt == "jpg" ? "image/jpeg" : "image/png";
            }
            return result;
        }

        // 解析截图输出绝对路径;空路径抛 E_OUTPUT_PATH_REQUIRED。
        private static string ResolveScreenshotOutputPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
            {
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_PATH_REQUIRED", "Pass --output <path> or a positional screenshot path.");
            }
            var normalized = rawPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(SceneToolSupport.ProjectRoot(), normalized));
        }

        private static void CreateScreenshotDirectory(string outputPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_IO_ERROR", "Failed to create screenshot output directory: " + exception.Message);
            }
        }

        private static void WriteScreenshotBytes(string outputPath, byte[] bytes)
        {
            try
            {
                File.WriteAllBytes(outputPath, bytes);
            }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_IO_ERROR", "Failed to write screenshot output: " + exception.Message);
            }
        }

        // 推断图片格式:显式 format > 路径扩展名 > png;仅接受 png/jpg。
        private static string NormalizeFormat(string rawFormat, string outputPath)
        {
            var fmt = string.IsNullOrEmpty(rawFormat) ? Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant() : rawFormat.ToLowerInvariant();
            if (string.IsNullOrEmpty(fmt))
            {
                fmt = "png";
            }
            if (fmt == "jpeg")
            {
                fmt = "jpg";
            }
            if (fmt != "png" && fmt != "jpg")
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_FORMAT_UNSUPPORTED", "Screenshot format must be png or jpg.");
            }
            return fmt;
        }

        // 截图尺寸必须在 1..8192 像素之间。
        private static int ClampDimension(int value)
        {
            if (value < 1 || value > 8192)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_SIZE_INVALID", "Screenshot dimension must be between 1 and 8192 pixels: " + value.ToString(System.Globalization.CultureInfo.InvariantCulture), "Use a smaller width/height before retrying.");
            }
            return value;
        }
    }
}
#endif
