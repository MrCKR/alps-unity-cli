#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.duplicate — 复制资产到新 Assets/ 路径。拒绝覆盖已存在目标。
    /// </summary>
    public class AssetDuplicateTool : IUnityCliTool<AssetDuplicateTool.Request, AssetDuplicateTool.Result>
    {
        public sealed class Request
        {
            public string Source { get; set; }
            public string Path { get; set; }
            public string Destination { get; set; }
        }

        public sealed class Result
        {
            public AssetDetail Asset { get; set; }
            public string Source { get; set; }
            public List<AssetPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.duplicate",
            Summary = "Copy an existing Assets/ asset to a new Assets/ path, refusing to overwrite the destination.",
            Description = "Copy an existing Assets/ asset to a new Assets/ path through AssetDatabase.CopyAsset. Refuses to overwrite an existing destination; choose a different path or delete it first.",
            Group = "asset",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Duplicate an asset", Input = new Request { Source = "Assets/Materials/A.mat", Destination = "Assets/Materials/A_copy.mat" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            AssetToolSupport.EnsureEditorNotBusy("asset.duplicate");
            var source = AssetToolSupport.RequireExistingAssetPath(request.Source ?? request.Path);
            var destination = AssetToolSupport.RequireAssetPath(request.Destination);
            AssetToolSupport.CreateAssetDirectory(destination);
            if (AssetToolSupport.AssetExists(destination))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_OUTPUT_EXISTS", "Destination asset already exists: " + destination, "Choose another destination path.");
            }
            if (!AssetDatabase.CopyAsset(source, destination))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_DUPLICATE_FAILED", "Failed to duplicate asset: " + source, "Check destination path and Unity Console diagnostics.");
            }
            AssetDatabase.Refresh();
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Asset = AssetToolSupport.BuildAssetDetail(destination),
                Source = source,
                PlannedEffects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.duplicate", Path = destination, Source = source } }
            });
        }
    }
}
#endif
