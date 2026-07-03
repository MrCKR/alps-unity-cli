#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>
    /// texture.info — 读取纹理资产摘要与导入设置。
    /// 对齐旧 TextureInfo handler;只读。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class TextureInfoTool : IUnityCliTool<TextureInfoTool.Request, TextureInfoTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Target { get; set; }
        }

        public sealed class Result
        {
            public TextureInfo Texture { get; set; }
            public TextureImportSettings ImportSettings { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "texture.info",
            Summary = "Return path, guid, dimensions, format, byte size and TextureImporter settings for an Assets/ texture.",
            Description = "Return path, guid, name, width, height, format, byte size and the full TextureImporter settings for an Assets/ texture. Read-only; use texture.import-settings to change importer fields and texture.modify to edit pixels.",
            Group = "texture",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Inspect a texture", Input = new Request { Path = "Assets/Textures/Icon.png" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var path = TextureToolSupport.ResolveTexturePath(request.Path ?? request.Target);
            var texture = TextureToolSupport.LoadTextureAsset(path);
            return new Result
            {
                Texture = TextureToolSupport.BuildTextureInfo(path, texture),
                ImportSettings = TextureToolSupport.BuildTextureImportSettings(TextureToolSupport.RequireTextureImporter(path))
            };
        }
    }
}
#endif
