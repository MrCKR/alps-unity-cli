#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// camera 域 IUnityCliTool 共享的基础设施 helper:相机解析、相机信息构造、
    /// 截图捕获、清屏标志解析、忙状态拦截。仅供 camera 域自包含工具复用;
    /// 不实现 IUnityCliTool,不参与工具发现。
    /// </summary>
    public static class CameraToolSupport
    {
        /// <summary>拦截编译/导入/构建/PlayMode 过渡期的写入请求,避免脏状态写入。</summary>
        public static void EnsureEditorNotBusy(string command)
        {
            if (EditorApplication.isCompiling)
            {
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_COMPILING", "Unity Editor is compiling scripts.", "Wait for Unity to finish compiling and retry.", transient: true, retryAfterMs: 5000);
            }
            if (EditorApplication.isUpdating)
            {
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_IMPORTING", "Unity Editor is importing assets.", "Wait for Unity to finish importing and retry.", transient: true, retryAfterMs: 5000);
            }
            if (UnityCliBridge.IsPlayModeChanging())
            {
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_PLAY_MODE", "Unity Editor is entering or exiting Play mode.", "Wait for Play mode transition to complete and retry.", transient: true, retryAfterMs: 3000);
            }
            if (BuildPipeline.isBuildingPlayer)
            {
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_BUSY", "Unity Editor is building the player.", "Wait for the build to complete and retry.", transient: true, retryAfterMs: 10000);
            }
        }

        /// <summary>解析 Camera:空 target 返回主相机或第一个相机;否则按名称/实例ID/层级路径匹配。</summary>
        public static Camera ResolveCamera(string target)
        {
            var cameras = Camera.allCameras;
            if (string.IsNullOrEmpty(target))
            {
                return Camera.main ?? (cameras.Length > 0 ? cameras[0] : null);
            }
            foreach (var camera in cameras)
            {
                if (camera.name == target
                    || camera.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target
                    || HierarchyPath(camera.gameObject) == target)
                {
                    return camera;
                }
            }
            return null;
        }

        /// <summary>构造 Camera 摘要信息 DTO。</summary>
        public static CameraInfo BuildCameraInfo(Camera camera)
        {
            return new CameraInfo
            {
                Id = camera.GetInstanceID(),
                Name = camera.name,
                Path = HierarchyPath(camera.gameObject),
                Tag = camera.tag,
                Enabled = camera.enabled,
                ClearFlags = camera.clearFlags.ToString(),
                Projection = camera.orthographic ? "orthographic" : "perspective",
                FieldOfView = camera.fieldOfView,
                OrthographicSize = camera.orthographicSize,
                Depth = camera.depth,
                TargetTexture = camera.targetTexture != null ? camera.targetTexture.name : null,
                PixelWidth = camera.pixelWidth,
                PixelHeight = camera.pixelHeight
            };
        }

        /// <summary>解析 CameraClearFlags 枚举值(大小写不敏感)。</summary>
        public static CameraClearFlags ParseCameraClearFlags(string value)
        {
            CameraClearFlags parsed;
            if (!Enum.TryParse<CameraClearFlags>(value, true, out parsed))
            {
                throw new UnityCliBridge.BridgeCommandException("E_CAMERA_CLEAR_FLAGS_INVALID", "Unsupported Camera clearFlags value: " + value, "Use Skybox, SolidColor, Depth, or Nothing.");
            }
            return parsed;
        }

        /// <summary>截图尺寸必须在 1..8192 像素之间。</summary>
        public static int ClampScreenshotDimension(int value, string name)
        {
            if (value < 1 || value > 8192)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_SIZE_INVALID", "Camera screenshot " + name + " must be between 1 and 8192 pixels: " + value.ToString(CultureInfo.InvariantCulture), "Use a smaller width/height before retrying.");
            }
            return value;
        }

        /// <summary>解析截图输出绝对路径;相对路径解析到 Unity 工程根目录下。</summary>
        public static string ResolveScreenshotOutputPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
            {
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_PATH_REQUIRED", "Camera screenshot requires a path.", "Pass --output <path> or a positional screenshot path.");
            }
            var normalized = rawPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(ProjectRoot(), normalized));
        }

        /// <summary>推断图片格式:显式 format > 路径扩展名 > png;仅接受 png/jpg。</summary>
        public static string NormalizeFormat(string rawFormat, string outputPath)
        {
            var fmt = string.IsNullOrEmpty(rawFormat) ? Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant() : rawFormat.ToLowerInvariant();
            if (string.IsNullOrEmpty(fmt)) fmt = "png";
            if (fmt == "jpeg") fmt = "jpg";
            if (fmt != "png" && fmt != "jpg")
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_FORMAT_UNSUPPORTED", "Screenshot format must be png or jpg.");
            }
            return fmt;
        }

        /// <summary>创建截图输出目录(缺失时)。</summary>
        public static void CreateScreenshotDirectory(string outputPath)
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

        /// <summary>写入截图字节到文件。</summary>
        public static void WriteScreenshotBytes(string outputPath, byte[] bytes)
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

        /// <summary>渲染指定 Camera 到 RenderTexture 并编码为图片字节,返回捕获结果。捕获后恢复相机状态。</summary>
        public static CameraCapture CaptureCamera(Camera camera, int width, int height, bool transparent, int quality, string outputPath, string format, bool overwrite, bool inlineBase64)
        {
            if (File.Exists(outputPath) && !overwrite)
            {
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + outputPath, "Pass overwrite=true or choose a different output path.");
            }
            CreateScreenshotDirectory(outputPath);
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
                var capture = new CameraCapture
                {
                    ScreenshotPath = outputPath,
                    ArtifactPath = outputPath,
                    Format = format,
                    Width = width,
                    Height = height,
                    Transparent = transparent,
                    Quality = quality,
                    Source = "camera",
                    Target = camera.name
                };
                // inlineBase64 仅在图片字节小于内联上限时附带,避免大 base64 撑爆 JSON 响应
                const int InlineImageMaxBytes = 512 * 1024;
                if (inlineBase64 && bytes != null && bytes.Length <= InlineImageMaxBytes)
                {
                    capture.ImageBase64 = Convert.ToBase64String(bytes);
                    capture.ImageWidth = width;
                    capture.ImageHeight = height;
                    capture.ImageMimeType = format == "jpg" ? "image/jpeg" : "image/png";
                }
                return capture;
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

        /// <summary>Unity 工程根目录(Assets 的父目录)。</summary>
        public static string ProjectRoot()
        {
            return Directory.GetParent(UnityEngine.Application.dataPath).FullName;
        }

        /// <summary>构造 GameObject 层级路径(A/B/C)。</summary>
        public static string HierarchyPath(GameObject gameObject)
        {
            var names = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", names.ToArray());
        }
    }

    /// <summary>Camera 摘要信息,camera 域工具 Result 共享。</summary>
    public sealed class CameraInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Tag { get; set; }
        public bool Enabled { get; set; }
        public string ClearFlags { get; set; }
        public string Projection { get; set; }
        public double FieldOfView { get; set; }
        public double OrthographicSize { get; set; }
        public double Depth { get; set; }
        public string TargetTexture { get; set; }
        public long PixelWidth { get; set; }
        public long PixelHeight { get; set; }
    }

    /// <summary>Camera 截图捕获结果,camera 域 screenshot/multiview 工具共享。</summary>
    public sealed class CameraCapture
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

    /// <summary>计划副作用项,camera 域 mutation 工具 Result 共享。</summary>
    public sealed class CameraPlannedEffect
    {
        public string Type { get; set; }
        public string Target { get; set; }
        public string Path { get; set; }
        public string Property { get; set; }
        public string Value { get; set; }
    }
}
#endif
