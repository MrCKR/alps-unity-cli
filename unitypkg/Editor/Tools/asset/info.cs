#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>
    /// asset.info — 读取资产主对象摘要(path/guid/name/type/extension/isFolder/exists/sizeBytes)。
    /// 对齐旧 AssetInfo handler;只读。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class AssetInfoTool : IUnityCliTool<AssetInfoTool.Request, AssetInfoTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Target { get; set; }
        }

        public sealed class Result
        {
            public AssetDetail Asset { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.info",
            Summary = "Return path, guid, name, type, extension, folder flag, existence, and byte size for an Assets/* asset.",
            Description = "Return path, guid, name, type, extension, folder flag, existence, and byte size for an Assets/* asset. Read-only; use asset.get-data for deep member reads and asset.get-components for Prefab/GameObject component summaries.",
            Group = "asset",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Inspect an asset", Input = new Request { Path = "Assets/Textures/Icon.png" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var path = AssetToolSupport.RequireAssetPath(request.Path ?? request.Target);
            if (!AssetToolSupport.AssetExists(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_NOT_FOUND", "Asset not found: " + path, "Run tools run asset.search --input '{\"query\":\"<keyword>\"}' or pass an existing Assets/* path.");
            }
            return new Result { Asset = AssetToolSupport.BuildAssetDetail(path) };
        }
    }
}
#endif
