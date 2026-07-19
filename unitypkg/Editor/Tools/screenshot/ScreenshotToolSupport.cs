#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// screenshot 域 IUnityCliTool 共享的基础设施 helper:渲染纹理、contact sheet、内联 base64。
    /// 仅供 screenshot 域自包含工具复用;复用 CameraToolSupport 的路径/格式/捕获 helper。
    /// </summary>
    public static class ScreenshotToolSupport
    {
        /// <summary>Unity 工程根目录。</summary>
        public static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        /// <summary>Assets/ 路径转工程内绝对路径。</summary>
        public static string AssetFullPath(string assetPath)
        {
            return Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>解析截图输出绝对路径;相对路径解析到 Unity 工程根目录下。</summary>
        public static string ResolveScreenshotOutputPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
            {
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_PATH_REQUIRED", "Screenshot requires a path.", "Pass --output <path> or a positional screenshot path.");
            }
            var normalized = rawPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(ProjectRoot(), normalized));
        }

        /// <summary>创建截图输出目录(缺失时)。</summary>
        public static void CreateScreenshotDirectory(string outputPath)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(outputPath)); }
            catch (Exception exception) { throw new UnityCliBridge.BridgeCommandException("E_IO_ERROR", "Failed to create screenshot output directory: " + exception.Message); }
        }

        /// <summary>写入截图后重新读取字节、校验格式并返回稳定 artifact proof。</summary>
        public static ScreenshotArtifactProof WriteScreenshotBytes(string outputPath, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_ARTIFACT_EMPTY", "Screenshot encoder produced no image data.", "Check the camera and render settings, then retry the screenshot.");
            try { File.WriteAllBytes(outputPath, bytes); }
            catch (Exception exception) { throw new UnityCliBridge.BridgeCommandException("E_IO_ERROR", "Failed to write screenshot output: " + exception.Message); }
            return VerifyScreenshotArtifact(outputPath, Path.GetExtension(outputPath).TrimStart('.'));
        }

        /// <summary>artifact proof 必须来自落盘后的 fresh readback，不能只检查 Exists。</summary>
        public static ScreenshotArtifactProof VerifyScreenshotArtifact(string outputPath, string format)
        {
            if (!File.Exists(outputPath))
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_ARTIFACT_MISSING", "Screenshot artifact was not written: " + outputPath, "Check the output path and available disk space, then retry the screenshot.");
            var bytes = File.ReadAllBytes(outputPath);
            var normalized = NormalizeFormat(format, outputPath);
            var valid = normalized == "png"
                ? bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47 && bytes[4] == 0x0d && bytes[5] == 0x0a && bytes[6] == 0x1a && bytes[7] == 0x0a
                : bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
            if (!valid)
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_ARTIFACT_INVALID", "Screenshot artifact is empty or does not match its requested format: " + outputPath, "Use a .png or .jpg output path and retry the capture.");
            using (var sha = SHA256.Create())
                return new ScreenshotArtifactProof { Path = outputPath, SizeBytes = bytes.LongLength, Sha256 = string.Concat(Array.ConvertAll(sha.ComputeHash(bytes), value => value.ToString("x2"))), Format = normalized };
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

        /// <summary>渲染指定 Camera 到 Texture2D(不写文件),供 orbit/surround 复用。</summary>
        public static Texture2D RenderScreenshotTexture(Camera camera, int width, int height)
        {
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D texture = null;
            try
            {
                rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
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
                if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
            }
        }

        /// <summary>计算 GameObject 的渲染 Bounds(含所有子 Renderer)。</summary>
        public static Bounds TargetRenderBounds(GameObject go)
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

        /// <summary>组合 6 张截图到 3x2 contact sheet。</summary>
        public static Texture2D ComposeScreenshotContactSheet(List<Texture2D> tiles, int tileWidth, int tileHeight)
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

        // 内联 base64 上限
        private const int InlineImageMaxBytes = 512 * 1024;

        /// <summary>在图片字节小于上限时附带 inline base64 数据。</summary>
        public static void MaybeAppendInline(ScreenshotCaptureResult result, byte[] bytes, int width, int height, string mimeType, bool inlineBase64)
        {
            if (inlineBase64 && bytes != null && bytes.Length <= InlineImageMaxBytes)
            {
                result.ImageBase64 = Convert.ToBase64String(bytes);
                result.ImageWidth = width;
                result.ImageHeight = height;
                result.ImageMimeType = mimeType;
            }
        }
    }

    /// <summary>screenshot 域截图捕获结果 DTO。</summary>
    public class ScreenshotCaptureResult
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
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; }
    }

    /// <summary>screenshot 域 artifact 条目 DTO(orbit/surround 多图输出)。</summary>
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
