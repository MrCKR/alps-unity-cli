#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// texture.delete — 删除纹理资产。destructive,支持 dryRun。
    /// 对齐旧 TextureDelete handler;session-affecting(asset-delete)。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class TextureDeleteTool : IUnityCliTool<TextureDeleteTool.Request, TextureDeleteTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Target { get; set; }
            public bool? DryRun { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool? DryRun { get; set; }
            public TextureInfo Texture { get; set; }
            public List<TexturePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "texture.delete",
            Summary = "Delete an Assets/ texture asset; destructive, supports --dry-run preview.",
            Description = "Delete an Assets/ texture asset through AssetDatabase. Destructive: removes a persisted texture. Supports --dry-run to preview the planned deletion without removing anything.",
            Group = "texture",
            Version = "1.0.0",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "asset-delete" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Preview deleting a texture", Input = new Request { Path = "Assets/Textures/Old.png", DryRun = true } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            TextureToolSupport.EnsureEditorNotBusy("texture.delete");
            var path = TextureToolSupport.ResolveTexturePath(request.Path ?? request.Target);
            if (request.DryRun == true)
            {
                return new Result
                {
                    Command = "texture.delete",
                    DryRun = true,
                    Texture = TextureToolSupport.BuildTextureInfo(path, TextureToolSupport.LoadTextureAsset(path)),
                    PlannedEffects = new List<TexturePlannedEffect> { new TexturePlannedEffect { Type = "texture.delete", Path = path } }
                };
            }
            if (!AssetDatabase.DeleteAsset(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_DELETE_FAILED", "Failed to delete texture asset: " + path, "Check Unity Console and file permissions.");
            }
            AssetDatabase.Refresh();
            return new Result
            {
                Command = "texture.delete",
                Texture = new TextureInfo { Path = path },
                PlannedEffects = new List<TexturePlannedEffect> { new TexturePlannedEffect { Type = "texture.delete", Path = path } }
            };
        }
    }
}
#endif
