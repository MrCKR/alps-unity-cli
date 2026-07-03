#if UNITY_EDITOR
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.preview — 生成资产缩略图预览元数据(宽高/是否异步加载中)。
    /// 对齐旧 AssetPreview handler;只读。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
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
            Description = "Return AssetPreview thumbnail metadata for an Assets/* asset. Read-only; preview may be transient (still loading) — re-run when isTransient is true. Does not return image bytes; use camera.screenshot or scene.screenshot for rendered captures.",
            Group = "asset",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Preview an asset", Input = new Request { AssetPath = "Assets/Materials/Probe.mat" } }
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
            var preview = AssetPreview.GetAssetPreview(asset);
            var hasPreview = preview != null;
            var result = new Result
            {
                Command = "asset.preview",
                AssetPath = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                TypeName = asset.GetType().FullName,
                HasPreview = hasPreview
            };
            if (hasPreview)
            {
                result.Width = preview.width;
                result.Height = preview.height;
                result.IsTransient = AssetPreview.IsLoadingAssetPreview(asset.GetInstanceID());
            }
            return result;
        }
    }
}
#endif
