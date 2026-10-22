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
        public long MaxPixelsPerCapture { get; set; }
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
            catch (Exception exception) { throw new UnityCliBridge.BridgeCommandException("E_SHELL_IO_ERROR", "Failed to create screenshot output directory: " + exception.Message); }
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
            // 同时覆盖 RenderTexture/depth、readable texture、编码缓冲与 contact-sheet copy 缓冲。
            var peakTemporaryBytes = contactSheet
                ? checked(imagePixels * 12L + sheetPixels * 8L + 65536L)
                : checked(imagePixels * 12L + 65536L);
            var worstCaseArtifacts = checked(totalPixels * 4L + (captureCount + (contactSheet ? 1 : 0)) * 65536L);
            if (totalPixels > maxTotalPixels) throw Limit("aggregate totalPixels " + totalPixels.ToString(CultureInfo.InvariantCulture) + " exceeds maxTotalPixels.");
            if (peakTemporaryBytes > maxTemporaryBytes) throw Limit("peak temporary bytes " + peakTemporaryBytes.ToString(CultureInfo.InvariantCulture) + " exceeds maxTemporaryBytes.");
            if (worstCaseArtifacts > maxArtifactBytes) throw Limit("planned worst-case artifact bytes " + worstCaseArtifacts.ToString(CultureInfo.InvariantCulture) + " exceeds maxArtifactBytes.");
            return new ScreenshotCaptureBudget(new ScreenshotCapturePlan
            {
                CaptureCount = captureCount + (contactSheet ? 1 : 0), TotalPixels = totalPixels,
                PeakTemporaryBytes = peakTemporaryBytes, ArtifactByteCeiling = maxArtifactBytes,
                PlannedWorstCaseArtifactBytes = worstCaseArtifacts,
                MaxPixelsPerCapture = contactSheet ? sheetPixels : imagePixels
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
            try
            {
                using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    const int chunkSize = 64 * 1024;
                    for (var offset = 0; offset < bytes.Length; offset += chunkSize)
                    {
                        budget.Checkpoint();
                        var count = Math.Min(chunkSize, bytes.Length - offset);
                        stream.Write(bytes, offset, count);
                    }
                    stream.Flush(true);
                }
                var proof = VerifyScreenshotArtifact(outputPath, Path.GetExtension(outputPath).TrimStart('.'), budget);
                if (proof.SizeBytes != bytes.LongLength)
                    throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_ARTIFACT_INVALID", "Persisted screenshot size differs from the encoded artifact.", "Inspect output storage before retrying.");
                return proof;
            }
            catch (UnityCliBridge.BridgeCommandException) { DeleteArtifactNoThrow(outputPath); throw; }
            catch (UnityCliRequestBudgetExceededException) { DeleteArtifactNoThrow(outputPath); throw; }
            catch (OperationCanceledException) { DeleteArtifactNoThrow(outputPath); throw; }
            catch (Exception exception)
            {
                DeleteArtifactNoThrow(outputPath);
                throw new UnityCliBridge.BridgeCommandException("E_SHELL_IO_ERROR", "Failed to write screenshot output: " + exception.Message);
            }
        }

        /// <summary>fresh proof 使用 bounded stream header/hash，并在每个固定块检查同一 deadline/cancel。</summary>
        public static ScreenshotArtifactProof VerifyScreenshotArtifact(string outputPath, string format, ScreenshotCaptureBudget budget)
        {
            if (!File.Exists(outputPath)) throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_ARTIFACT_MISSING", "Screenshot artifact was not written: " + outputPath, "Check output permissions before retrying.");
            var info = new FileInfo(outputPath);
            budget.EnsureArtifactSize(info.Length);
            var normalized = NormalizeFormat(format, outputPath);
            var header = new byte[8];
            string hash;
            using (var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                budget.Checkpoint();
                var read = stream.Read(header, 0, header.Length);
                var valid = normalized == "png"
                    ? read == 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4e && header[3] == 0x47 && header[4] == 0x0d && header[5] == 0x0a && header[6] == 0x1a && header[7] == 0x0a
                    : read >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff;
                if (!valid) throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_ARTIFACT_INVALID", "Screenshot artifact does not match its requested format: " + outputPath, "Use a .png or .jpg output path and retry.");
                stream.Position = 0L;
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[64 * 1024];
                    int count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        budget.Checkpoint();
                        sha.TransformBlock(buffer, 0, count, buffer, 0);
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    hash = string.Concat(Array.ConvertAll(sha.Hash, value => value.ToString("x2", CultureInfo.InvariantCulture)));
                }
            }
            budget.Checkpoint();
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
            try
            {
                renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                return CreateTextureWithOwnership(
                    () => new Texture2D(width, height, TextureFormat.RGB24, false),
                    texture =>
                    {
                        texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                        texture.Apply();
                    },
                    texture => UnityEngine.Object.DestroyImmediate(texture));
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

        /// <summary>只有完整populate成功才转移texture ownership；任何异常都同步销毁新资源。</summary>
        internal static Texture2D CreateTextureWithOwnership(
            Func<Texture2D> create,
            Action<Texture2D> populate,
            Action<Texture2D> destroy)
        {
            if (create == null) throw new ArgumentNullException("create");
            if (populate == null) throw new ArgumentNullException("populate");
            if (destroy == null) throw new ArgumentNullException("destroy");
            var texture = create();
            var ownershipTransferred = false;
            try
            {
                populate(texture);
                ownershipTransferred = true;
                return texture;
            }
            finally
            {
                if (!ownershipTransferred && texture != null) destroy(texture);
            }
        }

        /// <summary>逐 tile 写入 sheet 后 caller 立即释放 tile，避免同时持有全部捕获纹理。</summary>
        public static void CopyContactSheetTile(Texture2D sheet, Texture2D tile, int index, int tileWidth, int tileHeight)
        {
            var column = index % 3;
            var row = 1 - index / 3;
            var pixels = tile.GetPixels32();
            sheet.SetPixels32(column * tileWidth, row * tileHeight, tileWidth, tileHeight, pixels);
        }

        /// <summary>逐节点和逐child读取渲染边界，组件数组仅在已验证cardinality与剩余预算后创建。</summary>
        public static Bounds TargetRenderBounds(GameObject gameObject, UnityCliRequestBudget budget)
        {
            if (!budget.TryConsumeGetters(2)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var root = gameObject.transform;
            var stack = new Stack<TransformTraversalFrame>();
            stack.Push(new TransformTraversalFrame(root));
            var found = false;
            var bounds = new Bounds(root.position, Vector3.one);
            while (stack.Count > 0)
            {
                var frame = stack.Peek();
                if (!frame.Entered)
                {
                    if (!budget.TryCheckpoint() || !budget.TryConsumeScannedRecord() || !budget.TryConsumeNode() || !budget.TryConsumeGetters(2))
                        throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    frame.Entered = true;
                    frame.ChildCount = frame.Transform.childCount;
                    var components = UnityCliBridge.ObjectRefResolver.BoundedComponents(frame.Transform.gameObject, budget);
                    foreach (var component in components)
                    {
                        var renderer = component as Renderer;
                        if (renderer == null) continue;
                        if (!budget.TryConsumeGetters(2)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                        if (!renderer.gameObject.activeInHierarchy) continue;
                        var rendererBounds = renderer.bounds;
                        if (!found) { bounds = rendererBounds; found = true; }
                        else bounds.Encapsulate(rendererBounds);
                    }
                }
                if (frame.NextChildIndex >= frame.ChildCount)
                {
                    stack.Pop();
                    continue;
                }
                if (!budget.TryCheckpoint() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                stack.Push(new TransformTraversalFrame(frame.Transform.GetChild(frame.NextChildIndex++)));
            }
            return bounds;
        }

        private sealed class TransformTraversalFrame
        {
            internal readonly Transform Transform;
            internal bool Entered;
            internal int ChildCount;
            internal int NextChildIndex;

            internal TransformTraversalFrame(Transform transform)
            {
                Transform = transform;
            }
        }

        public static void MaybeAppendInline(ScreenshotCaptureResult result, byte[] bytes, int width, int height, string mimeType, bool inlineBase64, ScreenshotCaptureBudget budget)
        {
            if (!inlineBase64 || bytes == null || bytes.Length > InlineImageMaxBytes) return;
            string encoded;
            if (!budget.TryCreateInlineBase64(bytes, out encoded)) return;
            result.ImageBase64 = encoded;
            result.ImageWidth = width;
            result.ImageHeight = height;
            result.ImageMimeType = mimeType;
        }

        private static void DeleteArtifactNoThrow(string outputPath)
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
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
        private long capturedPixels;
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
            var pixels = checked((long)width * height);
            captures++;
            capturedPixels = checked(capturedPixels + pixels);
            if (captures > Plan.CaptureCount || pixels > Plan.MaxPixelsPerCapture || capturedPixels > Plan.TotalPixels)
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_LIMIT_EXCEEDED", "Capture count or pixels exceeded the frozen aggregate plan.", "Submit a new request with a valid aggregate plan.");
        }

        public void Checkpoint()
        {
            requestBudget.CheckpointOrThrow();
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

        public bool TryCreateInlineBase64(byte[] bytes, out string value)
        {
            value = null;
            if (bytes == null) return false;
            var encodedBytes = checked(((bytes.LongLength + 2L) / 3L) * 4L);
            if (!requestBudget.TryReserveSerializedUtf8Bytes(encodedBytes)) return false;
            value = Convert.ToBase64String(bytes);
            return true;
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
