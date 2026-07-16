#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// texture.create — 从源文件或内联生成的纯色纹理创建纹理资产,并可应用导入设置。
    /// </summary>
    public class TextureCreateTool : IUnityCliTool<TextureCreateTool.Request, TextureCreateTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Source { get; set; }
            public bool? Overwrite { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public string Color { get; set; }
            public string TextureType { get; set; }
            public bool? Readable { get; set; }
            public bool? Mipmap { get; set; }
            public bool? SRGB { get; set; }
            public bool? AlphaIsTransparency { get; set; }
            public string WrapMode { get; set; }
            public string FilterMode { get; set; }
            public string Compression { get; set; }
            public int? MaxTextureSize { get; set; }
        }

        public sealed class Result
        {
            public TextureInfo Texture { get; set; }
            public TextureImportSettings ImportSettings { get; set; }
            public List<TexturePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "texture.create",
            Summary = "Create a texture asset from a source file or a generated solid-color image, then apply import settings.",
            Description = "Create a texture asset at an Assets/ path. With source, copies an existing file (absolute or Assets/ path); otherwise generates a solid-color PNG/JPG from width/height/color. Refuses to overwrite unless overwrite=true. Import settings (textureType/readable/mipmap/sRGB/wrapMode/filterMode/compression/maxTextureSize) are applied after import.",
            Group = "texture",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write", "asset-import" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Generate a 64x64 red texture", Input = new Request { Path = "Assets/Textures/Red.png", Width = 64, Height = 64, Color = "ff0000" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            TextureToolSupport.EnsureEditorNotBusy("texture.create");
            var path = TextureToolSupport.RequireTexturePath(request.Path);
            var overwrite = request.Overwrite ?? false;
            if (AssetToolSupport.AssetExists(path) && !overwrite)
            {
                throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_OUTPUT_EXISTS", "Texture output already exists: " + path, "Pass --overwrite or choose a different texture asset path.");
            }

            AssetToolSupport.CreateAssetDirectory(path);
            if (!string.IsNullOrEmpty(request.Source))
            {
                CopyTextureSource(request.Source, path, overwrite);
            }
            else
            {
                WriteGeneratedTexture(path, request);
            }

            try
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                TextureToolSupport.ApplyTextureSettings(TextureToolSupport.RequireTextureImporter(path), BuildSettings(request), true);
                AssetDatabase.SaveAssets();
            }
            catch (UnityCliBridge.BridgeCommandException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_IMPORT_FAILED", "Failed to import texture: " + path + " (" + exception.Message + ")", "Check texture file contents, importer settings and Unity Console diagnostics.");
            }
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_IMPORT_FAILED", "Texture import did not produce a Texture2D asset: " + path, "Check texture file contents and Unity Console diagnostics.");
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Texture = TextureToolSupport.BuildTextureInfo(path, texture),
                ImportSettings = TextureToolSupport.BuildTextureImportSettings(TextureToolSupport.RequireTextureImporter(path)),
                PlannedEffects = new List<TexturePlannedEffect> { new TexturePlannedEffect { Type = "texture.create", Path = path, Refresh = true } }
            });
        }

        private static void CopyTextureSource(string source, string destinationPath, bool overwrite)
        {
            var sourcePath = AssetToolSupport.NormalizeAssetPath(source);
            var fullSource = sourcePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? AssetToolSupport.AssetFullPath(sourcePath)
                : Path.GetFullPath(source.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
            var fullDestination = AssetToolSupport.AssetFullPath(destinationPath);
            if (!File.Exists(fullSource))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_SOURCE_NOT_FOUND", "Texture source file not found: " + source, "Pass --source with an existing file path or Assets/ texture path.");
            }
            if (File.Exists(fullDestination) && overwrite)
            {
                File.Delete(fullDestination);
            }
            File.Copy(fullSource, fullDestination, overwrite);
            AssetDatabase.Refresh();
        }

        private static void WriteGeneratedTexture(string path, Request request)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
            {
                throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_FORMAT_UNSUPPORTED", "Generated textures can only be written as .png or .jpg: " + path, "Pass --source for other texture formats.");
            }
            var width = Math.Max(1, request.Width ?? 1);
            var height = Math.Max(1, request.Height ?? 1);
            var color = TextureToolSupport.ParseTextureColor(request.Color);
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color[width * height];
                for (var index = 0; index < pixels.Length; index++) pixels[index] = color;
                texture.SetPixels(pixels);
                texture.Apply();
                var bytes = extension == ".png" ? texture.EncodeToPNG() : texture.EncodeToJPG(95);
                File.WriteAllBytes(AssetToolSupport.AssetFullPath(path), bytes);
                AssetDatabase.Refresh();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        internal static TextureSettings BuildSettings(Request request)
        {
            return new TextureSettings
            {
                TextureType = request.TextureType,
                Readable = request.Readable,
                Mipmap = request.Mipmap,
                SRGB = request.SRGB,
                AlphaIsTransparency = request.AlphaIsTransparency,
                WrapMode = request.WrapMode,
                FilterMode = request.FilterMode,
                Compression = request.Compression,
                MaxTextureSize = request.MaxTextureSize
            };
        }
    }
}
#endif
