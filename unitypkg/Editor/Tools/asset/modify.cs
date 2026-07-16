#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.modify — 用新 content 覆盖文本类资产文件并重新导入。
    /// </summary>
    public class AssetModifyTool : IUnityCliTool<AssetModifyTool.Request, AssetModifyTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Content { get; set; }
        }

        public sealed class Result
        {
            public AssetDetail Asset { get; set; }
            public List<AssetPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.modify",
            Summary = "Overwrite a text-like asset file with new content and force-reimport it.",
            Description = "Overwrite a text-like asset file with new content and force-reimport it. The asset must already exist. Use asset.create for new files and asset.import for binary source files.",
            Group = "asset",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write", "asset-import" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Modify a text asset", Input = new Request { Path = "Assets/Generated/notes.txt", Content = "updated" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            AssetToolSupport.EnsureEditorNotBusy("asset.modify");
            var path = AssetToolSupport.RequireExistingAssetPath(request.Path);
            var content = request.Content;
            if (content == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_CONTENT_REQUIRED", "Asset modify requires content or content-file.", "Pass text content for a text-like asset file.");
            }
            AssetToolSupport.WriteTextAsset(path, content, true);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Asset = AssetToolSupport.BuildAssetDetail(path),
                PlannedEffects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.modify", Path = path } }
            });
        }
    }
}
#endif
