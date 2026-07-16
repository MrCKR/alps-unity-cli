#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// texture.generate — 生成程序化纹理(solid/gradient/radial/noise/checkerboard/stripes/dots/grid/brick)。
    /// </summary>
    public class TextureGenerateTool : IUnityCliTool<TextureGenerateTool.Request, TextureGenerateTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public string Pattern { get; set; }
            public string Color { get; set; }
            public string Color2 { get; set; }
            public int? PatternSize { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool? DryRun { get; set; }
            public string Path { get; set; }
            public long Width { get; set; }
            public long Height { get; set; }
            public string Pattern { get; set; }
            public long SizeBytes { get; set; }
            public List<TexturePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "texture.generate",
            Summary = "Generate a procedural PNG texture (solid/gradient/radial/noise/checkerboard/stripes/dots/grid/brick); destructive, supports --dry-run.",
            Description = "Generate a procedural PNG texture at an Assets/*.png path. Patterns: solid, gradient (linear along x), radial (center outward), noise (Perlin), checkerboard, stripes, dots, grid, brick. color/color2 lerp endpoints; patternSize controls cell size for the tiled patterns. Destructive: writes a new asset file.",
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
                new ToolExample { Description = "Generate a 128x128 checkerboard", Input = new Request { Path = "Assets/Textures/Checker.png", Width = 128, Height = 128, Pattern = "checkerboard", Color = "ffffff", Color2 = "000000", PatternSize = 16 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            TextureToolSupport.EnsureEditorNotBusy("texture.generate");
            var path = request.Path;
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_PATH_REQUIRED", "texture.generate requires an Assets/*.png path.", "Pass Assets/Textures/Generated.png.");
            }
            var width = Math.Max(1, Math.Min(request.Width ?? 256, 4096));
            var height = Math.Max(1, Math.Min(request.Height ?? 256, 4096));
            var pattern = request.Pattern ?? "solid";
            var color1 = ParseColorArg(request.Color, Color.white);
            var color2 = ParseColorArg(request.Color2, Color.black);
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Command = "texture.generate",
                    DryRun = true,
                    Path = path,
                    Width = width,
                    Height = height,
                    Pattern = pattern,
                    PlannedEffects = new List<TexturePlannedEffect> { new TexturePlannedEffect { Type = "texture.generate", Path = path } }
                });
            }
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    pixels[y * width + x] = ComputePixel(pattern, x, y, width, height, color1, color2, request.PatternSize ?? 8);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            AssetToolSupport.CreateAssetDirectory(path);
            var bytes = texture.EncodeToPNG();
            File.WriteAllBytes(AssetToolSupport.AssetFullPath(path), bytes);
            AssetDatabase.ImportAsset(path);
            UnityEngine.Object.DestroyImmediate(texture);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "texture.generate",
                Path = path,
                Width = width,
                Height = height,
                Pattern = pattern,
                SizeBytes = bytes.Length,
                PlannedEffects = new List<TexturePlannedEffect> { new TexturePlannedEffect { Type = "texture.generate", Path = path } }
            });
        }

        private static Color32 ComputePixel(string pattern, int x, int y, int width, int height, Color32 color1, Color32 color2, int patternSize)
        {
            if (string.Equals(pattern, "gradient", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pattern, "radial", StringComparison.OrdinalIgnoreCase))
            {
                // gradient=linear(沿 x), radial=从中心向外
                var t = string.Equals(pattern, "radial", StringComparison.OrdinalIgnoreCase)
                    ? Vector2.Distance(new Vector2(x, y), new Vector2(width / 2f, height / 2f)) / Mathf.Max(width, height) * 2f
                    : (float)x / Mathf.Max(1, width - 1);
                return Color32.Lerp(color1, color2, Mathf.Clamp01(t));
            }
            if (string.Equals(pattern, "noise", StringComparison.OrdinalIgnoreCase))
            {
                var n = Mathf.PerlinNoise((float)x / width * 8f, (float)y / height * 8f);
                return Color32.Lerp(color1, color2, n);
            }
            var size = Mathf.Max(1, patternSize);
            if (string.Equals(pattern, "checkerboard", StringComparison.OrdinalIgnoreCase))
            {
                return ((x / size + y / size) % 2 == 0) ? color1 : color2;
            }
            if (string.Equals(pattern, "stripes", StringComparison.OrdinalIgnoreCase))
            {
                return ((y / size) % 2 == 0) ? color1 : color2;
            }
            if (string.Equals(pattern, "dots", StringComparison.OrdinalIgnoreCase))
            {
                var cx = x % size;
                var cy = y % size;
                var r = size / 2f;
                return (cx - r) * (cx - r) + (cy - r) * (cy - r) <= r * r ? color2 : color1;
            }
            if (string.Equals(pattern, "grid", StringComparison.OrdinalIgnoreCase))
            {
                return (x % size == 0 || y % size == 0) ? color2 : color1;
            }
            if (string.Equals(pattern, "brick", StringComparison.OrdinalIgnoreCase))
            {
                var row = y / size;
                var offset = (row % 2 == 0) ? 0 : size / 2;
                return (x + offset) % size == 0 || y % size == 0 ? color2 : color1;
            }
            return color1;
        }

        private static Color32 ParseColorArg(string value, Color32 fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            if (ColorUtility.TryParseHtmlString(value, out var c)) return c;
            return fallback;
        }
    }
}
#endif
