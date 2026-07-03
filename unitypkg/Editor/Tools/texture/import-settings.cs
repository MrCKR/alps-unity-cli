#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// texture.import-settings — 应用 TextureImporter 设置(textureType/readable/mipmap/wrapMode 等)。
    /// 对齐旧 TextureImportSettings handler;session-affecting(asset-import)。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class TextureImportSettingsTool : IUnityCliTool<TextureImportSettingsTool.Request, TextureImportSettingsTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Target { get; set; }
            public string TextureType { get; set; }
            public string Type { get; set; }
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
            public bool Changed { get; set; }
            public List<TexturePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "texture.import-settings",
            Summary = "Apply TextureImporter settings (textureType, readable, mipmap, sRGB, wrap/filter mode, compression, max size) to an Assets/ texture.",
            Description = "Apply TextureImporter settings to an Assets/ texture. Only fields you provide are changed; unspecified fields keep their current value. Documented enum values: textureType (Default/Sprite/Normal/...), wrapMode (Repeat/Clamp/Mirror/...), filterMode (Point/Bilinear/Trilinear), compression (None/Compressed/CompressedHQ/...).",
            Group = "texture",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "asset-import" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Set sprite type and clamp wrap", Input = new Request { Path = "Assets/Textures/Icon.png", TextureType = "Sprite", WrapMode = "Clamp" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            TextureToolSupport.EnsureEditorNotBusy("texture.import-settings");
            var path = TextureToolSupport.ResolveTexturePath(request.Path ?? request.Target);
            TextureToolSupport.LoadTextureAsset(path);
            bool changed;
            try
            {
                changed = TextureToolSupport.ApplyTextureSettings(TextureToolSupport.RequireTextureImporter(path), BuildSettings(request), true);
            }
            catch (UnityCliBridge.BridgeCommandException)
            {
                throw;
            }
            catch (System.Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_IMPORT_FAILED", "Failed to apply texture import settings: " + path + " (" + exception.Message + ")", "Check TextureImporter settings and Unity Console diagnostics.");
            }
            var texture = TextureToolSupport.LoadTextureAsset(path);
            var effects = new List<TexturePlannedEffect>();
            if (changed) effects.Add(new TexturePlannedEffect { Type = "texture.import-settings", Path = path });
            return new Result
            {
                Texture = TextureToolSupport.BuildTextureInfo(path, texture),
                ImportSettings = TextureToolSupport.BuildTextureImportSettings(TextureToolSupport.RequireTextureImporter(path)),
                Changed = changed,
                PlannedEffects = effects
            };
        }

        private static TextureSettings BuildSettings(Request request)
        {
            return new TextureSettings
            {
                TextureType = request.TextureType ?? request.Type,
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
