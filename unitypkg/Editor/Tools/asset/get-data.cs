#if UNITY_EDITOR
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.get-data — 资产深读:底层复用 ObjectSerializer,支持 paths/viewQuery 互斥的 token-safe 读取。
    /// 对齐旧 AssetGetData handler;只读。通过 ctx.InputJson 构建 BridgeRequest 复用 ObjectSerializer。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class AssetGetDataTool : IUnityCliTool<AssetGetDataTool.Request, AssetGetDataTool.Result>
    {
        public sealed class Request
        {
            public string AssetPath { get; set; }
            public string Path { get; set; }
            public int? MaxDepth { get; set; }
            public int? MaxArrayItems { get; set; }
            public bool? IncludeFields { get; set; }
            public bool? IncludeProperties { get; set; }
            public bool? DeepSerialization { get; set; }
            public string NamePattern { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string AssetPath { get; set; }
            public string Guid { get; set; }
            public string TypeName { get; set; }
            public object Data { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.get-data",
            Summary = "Deep-read an Assets/* asset's members via ObjectSerializer; use paths or viewQuery for token-safe fields.",
            Description = "Deep-read an Assets/* asset's members. For token safety, pass exact paths or one viewQuery; they are mutually exclusive and skip full fields/props expansion. Keep maxDepth low before expanding large assets. Arrays/lists/dicts are bounded by maxArrayItems.",
            Group = "asset",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Read exact fields only", Input = new Request { AssetPath = "Assets/Materials/Probe.mat", NamePattern = "name" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var path = AssetToolSupport.RequireAssetPath(request.AssetPath ?? request.Path);
            if (!AssetToolSupport.AssetExists(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_NOT_FOUND", "Asset not found: " + path, "Run tools run asset.search --input '{\"query\":\"<keyword>\"}' to discover the path.");
            }
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_LOAD_FAILED", "Asset main object could not be loaded: " + path, "Run asset.info to confirm the asset type, or use a more specific tool for folders/importers.");
            }
            // 通过原始输入 JSON 构建 BridgeRequest,复用 ObjectSerializer.Options 构造(paths/viewQuery 互斥校验)
            var bridgeRequest = UnityCliBridge.BridgeRequest.Parse(ctx.InputJson);
            var options = GameObjectToolSupport.BuildSerializerOptions(bridgeRequest);
            var memberJson = UnityCliBridge.ObjectSerializer.Serialize(asset, options);
            return new Result
            {
                Command = "asset.get-data",
                AssetPath = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                TypeName = asset.GetType().FullName,
                // ObjectSerializer 输出为动态 JSON 树;解析为嵌套结构供框架序列化(IDictionary 支持)
                Data = GameObjectToolSupport.ParseJsonToObject(memberJson)
            };
        }
    }
}
#endif
