#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// texture.modify — 修改 PNG/JPG 纹理的像素区域并重新导入。支持 dryRun。destructive(asset-write)。
    /// </summary>
    public class TextureModifyTool : IUnityCliTool<TextureModifyTool.Request, TextureModifyTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Target { get; set; }
            public string Color { get; set; }
            public string FillColor { get; set; }
            public int? X { get; set; }
            public int? Y { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool? DryRun { get; set; }
            public TextureInfo Texture { get; set; }
            public TextureImportSettings ImportSettings { get; set; }
            public long ChangedPixels { get; set; }
            public TextureRegion Region { get; set; }
            public List<TexturePlannedEffect> PlannedEffects { get; set; }
        }

        public sealed class TextureRegion
        {
            public long X { get; set; }
            public long Y { get; set; }
            public long Width { get; set; }
            public long Height { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "texture.modify",
            Summary = "Fill a pixel region of a PNG/JPG texture with a color and reimport; destructive, supports --dry-run.",
            Description = "Fill a rectangular pixel region of an existing PNG/JPG texture with a solid color, then force-reimport it. Only PNG/JPG assets are supported. Pass x/y/width/height for the region (defaults fill from 0,0 to the texture size). Destructive: overwrites the texture file.",
            Group = "texture",
            Version = "1.0.0",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "asset-write", "asset-import" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Fill a 16x16 region red", Input = new Request { Path = "Assets/Textures/Icon.png", X = 0, Y = 0, Width = 16, Height = 16, Color = "ff0000" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            TextureToolSupport.EnsureEditorNotBusy("texture.modify");
            var path = TextureToolSupport.ResolveTexturePath(request.Path ?? request.Target);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".png")
            {
                throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_FORMAT_UNSUPPORTED", "texture.modify supports PNG only because post-write pixel proof must be exact: " + path, "Convert the texture to PNG before modifying it.");
            }
            var color = TextureToolSupport.ParseTextureColor(request.Color ?? request.FillColor);
            var texture = TextureToolSupport.LoadTextureAsset(path);
            var x = Math.Max(0, request.X ?? 0);
            var y = Math.Max(0, request.Y ?? 0);
            var width = request.Width ?? (texture.width - x);
            var height = request.Height ?? (texture.height - y);
            if (width <= 0 || height <= 0 || x >= texture.width || y >= texture.height)
            {
                throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_REGION_INVALID", "Texture modify region is outside texture bounds.", "Pass x/y/width/height within the existing texture dimensions.");
            }
            width = Math.Min(width, texture.width - x);
            height = Math.Min(height, texture.height - y);
            var sourceBytes = File.ReadAllBytes(AssetToolSupport.AssetFullPath(path));
            var decodedPlan = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!ImageConversion.LoadImage(decodedPlan, sourceBytes))
                    throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_DECODE_FAILED", "Failed to decode texture bytes: " + path, "Reimport the asset or verify the image file is valid.");
                if (decodedPlan.width != texture.width || decodedPlan.height != texture.height)
                    throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_DECODE_FAILED", "Texture bytes did not match imported texture dimensions: " + path, "Reimport the asset and retry.");
            }
            finally { UnityEngine.Object.DestroyImmediate(decodedPlan); }
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Command = "texture.modify",
                    DryRun = true,
                    Region = new TextureRegion { X = x, Y = y, Width = width, Height = height },
                    PlannedEffects = new List<TexturePlannedEffect> { new TexturePlannedEffect { Type = "texture.modify", Path = path } }
                });
            }
            ctx.CommitEffect();
            var working = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!ImageConversion.LoadImage(working, sourceBytes))
                    throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_DECODE_FAILED", "Texture bytes changed after planning: " + path, "Retry after the Editor is idle.");
                for (var row = y; row < y + height; row++)
                {
                    for (var col = x; col < x + width; col++)
                    {
                        working.SetPixel(col, row, color);
                    }
                }
                working.Apply();
                var bytes = extension == ".png" ? working.EncodeToPNG() : working.EncodeToJPG(95);
                File.WriteAllBytes(AssetToolSupport.AssetFullPath(path), bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();
                VerifyPixels(path, x, y, width, height, color);
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Command = "texture.modify",
                    Texture = TextureToolSupport.BuildTextureInfo(path, TextureToolSupport.LoadTextureAsset(path)),
                    ImportSettings = TextureToolSupport.BuildTextureImportSettings(TextureToolSupport.RequireTextureImporter(path)),
                    ChangedPixels = (long)(width * height),
                    Region = new TextureRegion { X = x, Y = y, Width = width, Height = height },
                    PlannedEffects = new List<TexturePlannedEffect> { new TexturePlannedEffect { Type = "texture.modify", Path = path, Refresh = true } }
                });
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(working);
            }
        }

        // File bytes are decoded again after import; importer state is not accepted as pixel proof.
        private static void VerifyPixels(string path, int x, int y, int width, int height, Color expected)
        {
            var observed = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!ImageConversion.LoadImage(observed, File.ReadAllBytes(AssetToolSupport.AssetFullPath(path))))
                    throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_MODIFY_STATE_MISMATCH", "Texture bytes could not be decoded after write: " + path, "Check the image encoder and retry.");
                for (var row = y; row < y + height; row++) for (var col = x; col < x + width; col++)
                    if (observed.GetPixel(col, row) != expected)
                        throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_MODIFY_STATE_MISMATCH", "A requested texture pixel did not match after write.", "Check texture compression and retry.");
            }
            finally { UnityEngine.Object.DestroyImmediate(observed); }
        }
    }
}
#endif
