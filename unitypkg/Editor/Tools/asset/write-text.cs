#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.write-text — 用新 content 覆盖文本类资产文件并重新导入。
    /// </summary>
    public class AssetWriteTextTool : IUnityCliTool<AssetWriteTextTool.Request, AssetWriteTextTool.Result>
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
            Name = "asset.write-text",
            Summary = "Overwrite a text-like asset file with new content and force-reimport it.",
            Description = "Overwrite a text-like asset file with new content and force-reimport it. The asset must already exist. Use asset.create for new files and asset.import for binary source files.",
            Group = "asset",
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
            AssetToolSupport.EnsureEditorNotBusy("asset.write-text");
            var path = AssetToolSupport.RequireExistingAssetPath(request.Path);
            var content = request.Content;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var textExtensions = new HashSet<string> { ".txt", ".json", ".xml", ".csv", ".md", ".yaml", ".yml", ".uss", ".uxml", ".shader", ".cginc", ".compute", ".asmdef", ".asmref" };
            if (!textExtensions.Contains(extension) || Directory.Exists(AssetToolSupport.AssetFullPath(path)))
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_MODIFY_TYPE_UNSUPPORTED", "asset.write-text only supports existing ordinary text files with a safe extension: " + path, "Use an approved text extension or the specialized asset tool.");
            if (content == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_CONTENT_REQUIRED", "Asset modify requires content or content-file.", "Pass text content for a text-like asset file.");
            }
            ctx.CommitEffect();
            AssetToolSupport.WriteTextAsset(path, content, true);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            if (!File.Exists(AssetToolSupport.AssetFullPath(path)) || File.ReadAllText(AssetToolSupport.AssetFullPath(path)) != content)
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_MODIFY_STATE_MISMATCH", "Fresh asset bytes did not match the requested content: " + path, "Check file permissions and retry.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Asset = AssetToolSupport.BuildAssetDetail(path),
                PlannedEffects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.write-text", Path = path } }
            });
        }
    }
}
#endif
