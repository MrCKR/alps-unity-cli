#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.move — 移动资产到新 Assets/ 路径。
    /// </summary>
    public class AssetMoveTool : IUnityCliTool<AssetMoveTool.Request, AssetMoveTool.Result>
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
            Name = "asset.move",
            Summary = "Move an existing Assets/ asset to a new Assets/ path.",
            Description = "Move an existing Assets/ asset to a new Assets/ path through AssetDatabase.MoveAsset. Use asset.rename to change only the filename within the same directory.",
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
                new ToolExample { Description = "Move an asset", Input = new Request { Source = "Assets/Old/Icon.png", Destination = "Assets/New/Icon.png" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            AssetToolSupport.EnsureEditorNotBusy("asset.move");
            var source = AssetToolSupport.RequireExistingAssetPath(request.Source ?? request.Path);
            var destination = AssetToolSupport.RequireAssetPath(request.Destination);
            AssetToolSupport.CreateAssetDirectory(destination);
            var error = AssetDatabase.MoveAsset(source, destination);
            if (!string.IsNullOrEmpty(error))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_MOVE_FAILED", "Failed to move asset: " + error, "Check source, destination and Unity Console diagnostics.");
            }
            AssetDatabase.Refresh();
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Asset = AssetToolSupport.BuildAssetDetail(destination),
                Source = source,
                PlannedEffects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.move", Path = destination, Source = source } }
            });
        }
    }
}
#endif
