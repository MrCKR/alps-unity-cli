#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// screenshot 域 IUnityCliTool 共享的基础设施 helper:渲染纹理、contact sheet、内联 base64。
    /// 仅供 screenshot 域自包含工具复用;复用 CameraToolSupport 的路径/格式/捕获 helper。
    /// 设计依据:.plan/工具统一形态.md(自包含工具共享只读 helper)。
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

        /// <summary>写入截图字节到文件。</summary>
        public static void WriteScreenshotBytes(string outputPath, byte[] bytes)
        {
            try { File.WriteAllBytes(outputPath, bytes); }
            catch (Exception exception) { throw new UnityCliBridge.BridgeCommandException("E_IO_ERROR", "Failed to write screenshot output: " + exception.Message); }
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
    public sealed class ScreenshotCaptureResult
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

    /// <summary>screenshot 域 artifact 条目 DTO(orbit/surround 多图输出)。</summary>
    public sealed class ScreenshotArtifactEntry
    {
        public string Source { get; set; }
        public string Path { get; set; }
        public string Camera { get; set; }
        public long Width { get; set; }
        public long Height { get; set; }
        public string Format { get; set; }
    }
}
#endif
