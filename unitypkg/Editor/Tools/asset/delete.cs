#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.delete — 删除 Assets/ 资产(文件或文件夹)。destructive,支持 dryRun。
    /// 对齐旧 AssetDelete handler;session-affecting(asset-delete)。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class AssetDeleteTool : IUnityCliTool<AssetDeleteTool.Request, AssetDeleteTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public bool? DryRun { get; set; }
        }

        public sealed class Result
        {
            public AssetDetail Asset { get; set; }
            public bool? DryRun { get; set; }
            public List<AssetPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.delete",
            Summary = "Delete an Assets/ asset file or folder; destructive, supports --dry-run preview.",
            Description = "Delete an Assets/ asset file or folder through AssetDatabase. Destructive: removes a persisted asset. Supports --dry-run to preview the planned deletion without removing anything.",
            Group = "asset",
            Version = "1.0.0",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "asset-delete" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Preview a deletion", Input = new Request { Path = "Assets/Generated/old.txt", DryRun = true } },
                new ToolExample { Description = "Delete an asset", Input = new Request { Path = "Assets/Generated/old.txt" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            AssetToolSupport.EnsureEditorNotBusy("asset.delete");
            var path = AssetToolSupport.RequireExistingAssetPath(request.Path);
            if (request.DryRun == true)
            {
                return new Result
                {
                    Asset = AssetToolSupport.BuildAssetDetail(path),
                    DryRun = true,
                    PlannedEffects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.delete", Path = path, SideEffects = "none" } }
                };
            }
            if (!AssetDatabase.DeleteAsset(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_DELETE_FAILED", "Failed to delete asset: " + path, "Check Unity Console and file permissions.");
            }
            AssetDatabase.Refresh();
            return new Result
            {
                Asset = new AssetDetail { Path = path, Exists = false },
                PlannedEffects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.delete", Path = path } }
            };
        }
    }
}
#endif
