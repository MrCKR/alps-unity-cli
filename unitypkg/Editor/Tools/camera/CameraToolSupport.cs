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

        /// <summary>读取型截图解析复用一次 Camera snapshot，并把每个候选计入请求 scan/getter 预算。</summary>
        public static Camera ResolveCamera(string target, UnityCliRequestBudget budget)
        {
            if (budget == null) throw new ArgumentNullException("budget");
            if (string.IsNullOrEmpty(target))
            {
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var main = Camera.main;
                if (main != null) return main;
            }
            var cameras = Camera.allCameras;
            foreach (var camera in cameras)
            {
                if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                if (string.IsNullOrEmpty(target) || camera.name == target || camera.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target) return camera;
                var path = UnityCliHierarchyPager.Path(camera.gameObject, budget);
                if (path == target) return camera;
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

        public static void ValidateFloatRange(double? value, string field, double min, double max)
        {
            if (!value.HasValue) return;
            if (double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < min || value.Value > max || value.Value < -float.MaxValue || value.Value > float.MaxValue)
                throw new UnityCliBridge.BridgeCommandException("E_CAMERA_VALUE_INVALID", field + " must be finite and representable as a Unity float.", "Pass a finite value in the documented range.");
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

        /// <summary>按冻结 aggregate plan 渲染、编码与证明单张 Camera artifact，并在 finally 恢复纹理状态。</summary>
        public static CameraCapture CaptureCamera(Camera camera, int width, int height, bool transparent, int quality, string outputPath, string format, bool overwrite, bool inlineBase64, ScreenshotCaptureBudget budget)
        {
            if (File.Exists(outputPath) && !overwrite)
            {
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + outputPath, "Pass overwrite=true or choose a different output path.");
            }
            budget.BeginCapture(width, height);
            CreateScreenshotDirectory(outputPath);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var previousClearFlags = camera.clearFlags;
            var previousBackground = camera.backgroundColor;
            RenderTexture renderTexture = null;
            Texture2D texture = null;
            try
            {
                renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                texture = new Texture2D(width, height, transparent ? TextureFormat.ARGB32 : TextureFormat.RGB24, false);
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
                var artifact = ScreenshotToolSupport.WriteScreenshotBytes(outputPath, bytes, budget);
                var capture = new CameraCapture
                {
                    ScreenshotPath = outputPath,
                    ArtifactPath = outputPath,
                    Source = "camera",
                    SizeBytes = artifact.SizeBytes,
                    Sha256 = artifact.Sha256,
                    CapturePlan = budget.Plan
                };
                // inlineBase64 同时受固定图片上限与请求级 UTF-8 预算约束。
                const int InlineImageMaxBytes = 512 * 1024;
                string encoded;
                if (inlineBase64 && bytes != null && bytes.Length <= InlineImageMaxBytes && budget.TryCreateInlineBase64(bytes, out encoded))
                {
                    capture.ImageBase64 = encoded;
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
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                if (renderTexture != null) { renderTexture.Release(); UnityEngine.Object.DestroyImmediate(renderTexture); }
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
        public string Source { get; set; }
        public string ImageBase64 { get; set; }
        public long? ImageWidth { get; set; }
        public long? ImageHeight { get; set; }
        public string ImageMimeType { get; set; }
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; }
        public ScreenshotCapturePlan CapturePlan { get; set; }
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
