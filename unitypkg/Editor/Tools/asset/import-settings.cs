#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>读取一个 asset importer 的默认设置和指定平台 override，不触发 import 或写入。</summary>
    public sealed class AssetImportSettingsTool : IUnityCliTool<AssetImportSettingsTool.Request, AssetImportSettingsSnapshot>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Path { get; set; }

            [UnityCliStringEnum("Default", "Standalone", "iOS", "Android", "WebGL", "tvOS")]
            public string Platform { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.import-settings",
            Summary = "Read importer type, Unity version, default settings, and one platform override for an Assets/ asset.",
            Description = "Reads the current AssetImporter without refreshing or reimporting. Texture, model, and audio importers return typed setting sets; other importers return the stable generic bundle fields.",
            Group = "asset",
            DestructiveHint = false,
            SideEffects = new string[0],
            SupportsDryRun = false,
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 120000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Read the default TextureImporter settings for one asset.",
                    Input = new Request { Path = "Assets/Textures/Icon.png", Platform = "Default" }
                },
                new ToolExample
                {
                    Description = "Read the Android platform override for one texture or audio asset.",
                    Input = new Request { Path = "Assets/Textures/Icon.png", Platform = "Android" }
                }
            }
        };

        public ValueTask<AssetImportSettingsSnapshot> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();
            return new ValueTask<AssetImportSettingsSnapshot>(AssetImportSettingsToolSupport.Read(request.Path, request.Platform));
        }
    }
}
#endif
