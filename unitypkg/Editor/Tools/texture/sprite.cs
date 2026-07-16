#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// texture.sprite — 把纹理导入为 Sprite,可设置 spriteMode/pixelsPerUnit/pivot/切片。
    /// </summary>
    public class TextureSpriteTool : IUnityCliTool<TextureSpriteTool.Request, TextureSpriteTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Target { get; set; }
            public string SpriteMode { get; set; }
            public string Mode { get; set; }
            public int? PixelsPerUnit { get; set; }
            public double? PivotX { get; set; }
            public double? PivotY { get; set; }
            public List<TextureSliceInput> Slices { get; set; }
        }

        public sealed class Result
        {
            public TextureInfo Texture { get; set; }
            public TextureImportSettings ImportSettings { get; set; }
            public long SliceCount { get; set; }
            public List<TextureSliceOutput> Slices { get; set; }
            public List<TexturePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "texture.sprite",
            Summary = "Reimport an Assets/ texture as a Sprite, with optional sprite mode, pivot, pixels-per-unit and sheet slices.",
            Description = "Reimport an Assets/ texture as a Sprite (TextureImporterType.Sprite). Set spriteMode to single or multiple, adjust pixelsPerUnit and pivot, and pass slices[] to define a multiple-sprite sheet (name/x/y/width/height plus optional pivotX/pivotY).",
            Group = "texture",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "asset-import" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Import as single sprite", Input = new Request { Path = "Assets/Textures/Hero.png", SpriteMode = "single", PixelsPerUnit = 32 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            TextureToolSupport.EnsureEditorNotBusy("texture.sprite");
            var path = TextureToolSupport.ResolveTexturePath(request.Path ?? request.Target);
            TextureToolSupport.LoadTextureAsset(path);
            var importer = TextureToolSupport.RequireTextureImporter(path);
            importer.textureType = TextureImporterType.Sprite;
            var spriteMode = request.SpriteMode ?? request.Mode;
            if (!string.IsNullOrEmpty(spriteMode))
            {
                importer.spriteImportMode = string.Equals(spriteMode, "multiple", System.StringComparison.OrdinalIgnoreCase) ? SpriteImportMode.Multiple : SpriteImportMode.Single;
            }
            var pixelsPerUnit = request.PixelsPerUnit ?? -1;
            if (pixelsPerUnit > 0)
            {
                importer.spritePixelsPerUnit = pixelsPerUnit;
            }
            importer.spritePivot = new Vector2(
                (float)(request.PivotX ?? importer.spritePivot.x),
                (float)(request.PivotY ?? importer.spritePivot.y));
            var slices = TextureToolSupport.BuildSpriteSlices(request.Slices);
            var sliceOutputs = new List<TextureSliceOutput>();
            if (slices.Length > 0)
            {
                importer.spriteImportMode = SpriteImportMode.Multiple;
#pragma warning disable 618
                importer.spritesheet = slices;
#pragma warning restore 618
            }
            try
            {
                importer.SaveAndReimport();
                AssetDatabase.Refresh();
            }
            catch (System.Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_IMPORT_FAILED", "Failed to reimport texture as Sprite: " + path + " (" + exception.Message + ")", "Check TextureImporter settings and Unity Console diagnostics.");
            }
            foreach (var slice in slices)
            {
                sliceOutputs.Add(new TextureSliceOutput { Name = slice.name, X = slice.rect.x, Y = slice.rect.y, Width = slice.rect.width, Height = slice.rect.height });
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Texture = TextureToolSupport.BuildTextureInfo(path, TextureToolSupport.LoadTextureAsset(path)),
                ImportSettings = TextureToolSupport.BuildTextureImportSettings(TextureToolSupport.RequireTextureImporter(path)),
                SliceCount = slices.Length,
                Slices = sliceOutputs,
                PlannedEffects = new List<TexturePlannedEffect> { new TexturePlannedEffect { Type = "texture.sprite", Path = path, Refresh = true } }
            });
        }
    }
}
#endif
