#if UNITY_EDITOR
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.get-data — 资产深读:底层复用 ObjectSerializer,支持 paths/viewQuery 互斥的 token-safe 读取。
    /// </summary>
    public class AssetGetDataTool : IUnityCliTool<AssetGetDataTool.Request, AssetGetDataTool.Result>
    {
        public sealed class Request : IUnityCliObjectSerializationInput
        {
            public string AssetPath { get; set; }
            public string Path { get; set; }
            [UnityCliRange(0L, 16L)] public int? MaxDepth { get; set; }
            [UnityCliRange(1L, 1000L)] public int? MaxArrayItems { get; set; }
            public bool? IncludeFields { get; set; }
            public bool? IncludeProperties { get; set; }
            public bool? DeepSerialization { get; set; }
            public System.Collections.Generic.List<string> Paths { get; set; }
            [UnityCliAnyJson] public object ViewQuery { get; set; }
            public string NamePattern { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public string Guid { get; set; }
            public string TypeName { get; set; }
            [UnityCliAnyJson] public object Data { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.get-data",
            Summary = "Deep-read an Assets/* asset's members via ObjectSerializer; use paths or viewQuery for token-safe fields.",
            Description = "Deep-read one loaded Assets/* object without importing. maxDepth is 0..16, maxArrayItems is 1..1000, and paths accepts at most 128 ordered exact reads. Every path and recursive branch shares the request scan/node/getter/reflection/monotonic-time/UTF-8 budget; defaults are 10000/4096/2048/4096/250 ms/1 MiB and maxima are 100000/20000/10000/20000/2000 ms/8 MiB. Truncated nodes return nextPath. Read-only in Edit and Play Mode.",
            Group = "asset",
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

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
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
            var options = GameObjectToolSupport.BuildSerializerOptions(request);
            var data = UnityCliBridge.ObjectSerializer.Serialize(asset, options, ctx.UseReadBudget(request.Budget));
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Guid = AssetDatabase.AssetPathToGUID(path),
                TypeName = asset.GetType().FullName,
                // ObjectSerializer 输出为动态 JSON 树);解析为嵌套结构供框架序列化(IDictionary 支持)
                Data = data
            });
        }
    }
}
#endif
