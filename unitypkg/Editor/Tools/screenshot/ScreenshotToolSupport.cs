#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace UnityCli
{
    public sealed class ScreenshotBudgetInput
    {
        [UnityCliRange(1L, ScreenshotToolSupport.AbsoluteMaxTotalPixels)] public long? MaxTotalPixels { get; set; }
        [UnityCliRange(1L, ScreenshotToolSupport.AbsoluteMaxTemporaryBytes)] public long? MaxTemporaryBytes { get; set; }
        [UnityCliRange(1024L, ScreenshotToolSupport.AbsoluteMaxArtifactBytes)] public long? MaxArtifactBytes { get; set; }
    }

    /// <summary>首个捕获 effect 前冻结的 aggregate 资源计划。</summary>
    public sealed class ScreenshotCapturePlan
    {
        public long CaptureCount { get; set; }
        public long TotalPixels { get; set; }
        public long PeakTemporaryBytes { get; set; }
        public long ArtifactByteCeiling { get; set; }
        public long PlannedWorstCaseArtifactBytes { get; set; }
    }

    /// <summary>截图域的尺寸、内存、捕获次数和 artifact byte 统一 owner。</summary>
    public static class ScreenshotToolSupport
    {
        public const int MaxImageDimension = 8192;
        public const long MaxPixelsPerImage = 16777216L;
        public const int MaxCaptureCount = 36;
        public const long DefaultMaxTotalPixels = 33554432L;
        public const long AbsoluteMaxTotalPixels = 67108864L;
        public const long DefaultMaxTemporaryBytes = 128L * 1024L * 1024L;
        public const long AbsoluteMaxTemporaryBytes = 256L * 1024L * 1024L;
        public const long DefaultMaxArtifactBytes = 64L * 1024L * 1024L;
        public const long AbsoluteMaxArtifactBytes = 256L * 1024L * 1024L;
        private const int InlineImageMaxBytes = 512 * 1024;

        public static string ProjectRoot() { return Directory.GetParent(Application.dataPath).FullName; }
        public static string AssetFullPath(string assetPath) { return Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar)); }

        public static string ResolveScreenshotOutputPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_PATH_REQUIRED", "Screenshot requires a path.", "Pass a non-empty screenshot output path.");
            var normalized = rawPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(ProjectRoot(), normalized));
        }

        public static void CreateScreenshotDirectory(string outputPath)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(outputPath)); }
            catch (Exception exception) { throw new UnityCliBridge.BridgeCommandException("E_IO_ERROR", "Failed to create screenshot output directory: " + exception.Message); }
        }

        /// <summary>按所有图片合计值冻结计划；任何无效或超限输入都在 caller CommitEffect 前失败。</summary>
        public static ScreenshotCaptureBudget PrepareCaptureBudget(int width, int height, int captureCount, bool contactSheet, ScreenshotBudgetInput input, UnityCliRequestBudget requestBudget)
        {
            input = input ?? new ScreenshotBudgetInput();
            var maxTotalPixels = input.MaxTotalPixels ?? DefaultMaxTotalPixels;
            var maxTemporaryBytes = input.MaxTemporaryBytes ?? DefaultMaxTemporaryBytes;
            var maxArtifactBytes = input.MaxArtifactBytes ?? DefaultMaxArtifactBytes;
            ValidateLimit(maxTotalPixels, 1L, AbsoluteMaxTotalPixels, "maxTotalPixels");
            ValidateLimit(maxTemporaryBytes, 1L, AbsoluteMaxTemporaryBytes, "maxTemporaryBytes");
            ValidateLimit(maxArtifactBytes, 1024L, AbsoluteMaxArtifactBytes, "maxArtifactBytes");
            ValidateDimensions(width, height, "capture");
            if (captureCount < 1 || captureCount > MaxCaptureCount) throw Limit("capture count must be between 1 and 36.");
            var imagePixels = checked((long)width * height);
            var sheetPixels = contactSheet ? checked(imagePixels * 6L) : 0L;
            if (contactSheet) ValidateDimensions(checked(width * 3), checked(height * 2), "contact sheet");
            var totalPixels = checked(imagePixels * captureCount + sheetPixels);
            var peakTemporaryBytes = contactSheet
                ? checked(imagePixels * 8L + sheetPixels * 6L)
                : checked(imagePixels * 8L);
            var worstCaseArtifacts = checked(totalPixels * 4L + (captureCount + (contactSheet ? 1 : 0)) * 65536L);
            if (totalPixels > maxTotalPixels) throw Limit("aggregate totalPixels " + totalPixels.ToString(CultureInfo.InvariantCulture) + " exceeds maxTotalPixels.");
            if (peakTemporaryBytes > maxTemporaryBytes) throw Limit("peak temporary bytes " + peakTemporaryBytes.ToString(CultureInfo.InvariantCulture) + " exceeds maxTemporaryBytes.");
            if (worstCaseArtifacts > maxArtifactBytes) throw Limit("planned worst-case artifact bytes " + worstCaseArtifacts.ToString(CultureInfo.InvariantCulture) + " exceeds maxArtifactBytes.");
            return new ScreenshotCaptureBudget(new ScreenshotCapturePlan
            {
                CaptureCount = captureCount + (contactSheet ? 1 : 0), TotalPixels = totalPixels,
                PeakTemporaryBytes = peakTemporaryBytes, ArtifactByteCeiling = maxArtifactBytes,
                PlannedWorstCaseArtifactBytes = worstCaseArtifacts
            }, requestBudget);
        }

        public static void ValidateDimensions(int width, int height, string owner)
        {
            if (width < 1 || width > MaxImageDimension || height < 1 || height > MaxImageDimension)
                throw Limit(owner + " dimensions must each be between 1 and 8192 pixels.");
            var pixels = checked((long)width * height);
            if (pixels > MaxPixelsPerImage) throw Limit(owner + " image pixels must not exceed 16777216.");
        }

        public static ScreenshotArtifactProof WriteScreenshotBytes(string outputPath, byte[] bytes, ScreenshotCaptureBudget budget)
        {
            if (bytes == null || bytes.Length == 0) throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_ARTIFACT_EMPTY", "Screenshot encoder produced no image data.", "Check the camera and render settings, then retry.");
            budget.ChargeArtifact(bytes.LongLength);
            try { File.WriteAllBytes(outputPath, bytes); }
            catch (Exception exception) { throw new UnityCliBridge.BridgeCommandException("E_IO_ERROR", "Failed to write screenshot output: " + exception.Message); }
            return VerifyScreenshotArtifact(outputPath, Path.GetExtension(outputPath).TrimStart('.'), budget);
        }

        /// <summary>fresh proof 使用 bounded stream header/hash，不再 File.ReadAllBytes 复制整个 artifact。</summary>
        public static ScreenshotArtifactProof VerifyScreenshotArtifact(string outputPath, string format, ScreenshotCaptureBudget budget)
        {
            if (!File.Exists(outputPath)) throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_ARTIFACT_MISSING", "Screenshot artifact was not written: " + outputPath, "Check output permissions before retrying.");
            var info = new FileInfo(outputPath);
            budget.EnsureArtifactSize(info.Length);
            var normalized = NormalizeFormat(format, outputPath);
            byte[] header = new byte[8];
            string hash;
            using (var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var read = stream.Read(header, 0, header.Length);
                var valid = normalized == "png"
                    ? read == 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4e && header[3] == 0x47 && header[4] == 0x0d && header[5] == 0x0a && header[6] == 0x1a && header[7] == 0x0a
                    : read >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff;
                if (!valid) throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_ARTIFACT_INVALID", "Screenshot artifact does not match its requested format: " + outputPath, "Use a .png or .jpg output path and retry.");
                stream.Position = 0L;
                using (var sha = SHA256.Create()) hash = string.Concat(Array.ConvertAll(sha.ComputeHash(stream), value => value.ToString("x2", CultureInfo.InvariantCulture)));
            }
            return new ScreenshotArtifactProof { Path = outputPath, SizeBytes = info.Length, Sha256 = hash, Format = normalized };
        }

        public static string NormalizeFormat(string rawFormat, string outputPath)
        {
            var format = string.IsNullOrEmpty(rawFormat) ? Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant() : rawFormat.ToLowerInvariant();
            if (string.IsNullOrEmpty(format)) format = "png";
            if (format == "jpeg") format = "jpg";
            if (format != "png" && format != "jpg") throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_FORMAT_UNSUPPORTED", "Screenshot format must be png or jpg.");
            return format;
        }

        public static Texture2D RenderScreenshotTexture(Camera camera, int width, int height, ScreenshotCaptureBudget budget)
        {
            budget.BeginCapture(width, height);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            RenderTexture renderTexture = null;
            Texture2D texture = null;
            try
            {
                renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                return texture;
            }
            finally
            {
                RenderTexture.active = previousActive;
                camera.targetTexture = previousTarget;
                if (renderTexture != null) { renderTexture.Release(); UnityEngine.Object.DestroyImmediate(renderTexture); }
            }
        }

        public static Texture2D CreateContactSheet(int tileWidth, int tileHeight, ScreenshotCaptureBudget budget)
        {
            budget.BeginCapture(tileWidth * 3, tileHeight * 2);
            var sheet = new Texture2D(tileWidth * 3, tileHeight * 2, TextureFormat.RGB24, false);
            return sheet;
        }

        /// <summary>逐 tile 写入 sheet 后 caller 立即释放 tile，避免同时持有全部捕获纹理。</summary>
        public static void CopyContactSheetTile(Texture2D sheet, Texture2D tile, int index, int tileWidth, int tileHeight)
        {
            var column = index % 3;
            var row = 1 - index / 3;
            var pixels = tile.GetPixels32();
            sheet.SetPixels32(column * tileWidth, row * tileHeight, tileWidth, tileHeight, pixels);
        }

        public static Bounds TargetRenderBounds(GameObject gameObject)
        {
            var renderers = gameObject.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(gameObject.transform.position, Vector3.one);
            var bounds = renderers[0].bounds;
            for (var index = 1; index < renderers.Length; index++) if (renderers[index] != null && renderers[index].gameObject.activeInHierarchy) bounds.Encapsulate(renderers[index].bounds);
            return bounds;
        }

        public static void MaybeAppendInline(ScreenshotCaptureResult result, byte[] bytes, int width, int height, string mimeType, bool inlineBase64)
        {
            if (!inlineBase64 || bytes == null || bytes.Length > InlineImageMaxBytes) return;
            result.ImageBase64 = Convert.ToBase64String(bytes);
            result.ImageWidth = width;
            result.ImageHeight = height;
            result.ImageMimeType = mimeType;
        }

        private static void ValidateLimit(long value, long minimum, long maximum, string name)
        {
            if (value < minimum || value > maximum) throw Limit(name + " must be between " + minimum.ToString(CultureInfo.InvariantCulture) + " and " + maximum.ToString(CultureInfo.InvariantCulture) + ".");
        }

        private static UnityCliBridge.BridgeCommandException Limit(string message)
        {
            return new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_LIMIT_INVALID", message, "Reduce dimensions/capture count or pass a documented positive aggregate ceiling before retrying.");
        }
    }

    public sealed class ScreenshotCaptureBudget
    {
        private readonly UnityCliRequestBudget requestBudget;
        private long captures;
        private long artifactBytes;

        internal ScreenshotCaptureBudget(ScreenshotCapturePlan plan, UnityCliRequestBudget requestBudget)
        {
            Plan = plan ?? throw new ArgumentNullException("plan");
            this.requestBudget = requestBudget ?? throw new ArgumentNullException("requestBudget");
        }

        public ScreenshotCapturePlan Plan { get; private set; }

        public void BeginCapture(int width, int height)
        {
            requestBudget.CheckpointOrThrow();
            ScreenshotToolSupport.ValidateDimensions(width, height, "capture");
            captures++;
            if (captures > Plan.CaptureCount) throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_LIMIT_EXCEEDED", "Capture count exceeded the frozen aggregate plan.", "Submit a new request with a valid aggregate plan.");
        }

        public void ChargeArtifact(long bytes)
        {
            requestBudget.CheckpointOrThrow();
            if (bytes <= 0L || bytes > Plan.ArtifactByteCeiling - artifactBytes)
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_LIMIT_EXCEEDED", "Encoded artifact bytes exceed the frozen request ceiling.", "Reduce dimensions or capture count.");
            artifactBytes += bytes;
        }

        public void EnsureArtifactSize(long bytes)
        {
            requestBudget.CheckpointOrThrow();
            if (bytes <= 0L || bytes > Plan.ArtifactByteCeiling)
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_LIMIT_EXCEEDED", "Persisted artifact size exceeds the frozen request ceiling.", "Reduce dimensions or capture count.");
        }
    }

    public class ScreenshotCaptureResult
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

    public sealed class ScreenshotArtifactEntry
    {
        public string Source { get; set; }
        public string Path { get; set; }
        public string Camera { get; set; }
        public long Width { get; set; }
        public long Height { get; set; }
        public string Format { get; set; }
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; }
    }

    public sealed class ScreenshotArtifactProof
    {
        public string Path { get; set; }
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; }
        public string Format { get; set; }
    }
}
#endif
