#if UNITY_EDITOR
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.preview — 生成资产缩略图预览元数据(宽高/终态)。
    /// </summary>
    public class AssetPreviewTool : IUnityCliTool<AssetPreviewTool.Request, AssetPreviewTool.Result>
    {
        public sealed class Request
        {
            public string AssetPath { get; set; }
            public string Path { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string AssetPath { get; set; }
            public string Guid { get; set; }
            public string TypeName { get; set; }
            public bool HasPreview { get; set; }
            public long? Width { get; set; }
            public long? Height { get; set; }
            public bool? IsTransient { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.preview",
            Summary = "Return AssetPreview thumbnail metadata (width/height/transient) for an Assets/* asset.",
            Description = "Return terminal AssetPreview thumbnail metadata for an Assets/* asset. A cache miss waits for Editor preview generation; assets without a preview return hasPreview=false as a terminal result. Does not return image bytes; use camera.screenshot or screenshot.scene-view for rendered captures.",
            Group = "asset",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "asset-preview-generation", "editor-cache-write" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Preview an asset", Input = new Request { AssetPath = "Assets/Materials/Probe.mat" } }
            }
        };

        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AssetToolSupport.RequireAssetPath(request.AssetPath ?? request.Path);
            if (!AssetToolSupport.AssetExists(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_NOT_FOUND", "Asset not found: " + path, "Run tools run asset.search --input '{\"query\":\"<keyword>\"}' to discover the path.");
            }
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_NOT_FOUND", "Asset not found: " + path, "Run tools run asset.search --input '{\"query\":\"<keyword>\"}' to discover the path.");
            }
            var instanceId = asset.GetInstanceID();
            ctx.CommitEffect();
            AssetPreview.GetAssetPreview(asset);
            var completed = await ctx.Wait.Until(
                () => !AssetPreview.IsLoadingAssetPreview(instanceId),
                System.TimeSpan.FromMilliseconds(System.Math.Max(1, ctx.TimeoutMs)),
                ct);
            if (!completed)
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_PREVIEW_TIMEOUT", "Asset preview did not finish loading: " + path, "Wait for the Editor to finish generating previews, then retry.");
            }
            var preview = AssetPreview.GetAssetPreview(asset);
            var hasPreview = preview != null;
            var result = new Result
            {
                Command = "asset.preview",
                AssetPath = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                TypeName = asset.GetType().FullName,
                HasPreview = hasPreview,
                IsTransient = false
            };
            if (hasPreview)
            {
                result.Width = preview.width;
                result.Height = preview.height;
            }
            return result;
        }
    }
}
#endif
