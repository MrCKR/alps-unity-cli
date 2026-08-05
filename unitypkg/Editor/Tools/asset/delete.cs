#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.delete — 删除 Assets/ 资产(文件或文件夹)。destructive,支持 dryRun。
    /// </summary>
    public class AssetDeleteTool : IUnityCliTool<AssetDeleteTool.Request, AssetDeleteTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
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
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "asset-delete" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Preview a deletion", Input = new Request { Path = "Assets/Generated/old.txt" }, Execution = new ToolExampleExecution { DryRun = true } },
                new ToolExample { Description = "Delete an asset", Input = new Request { Path = "Assets/Generated/old.txt" } }
            }
        };

        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            AssetToolSupport.EnsureEditorNotBusy("asset.delete");
            var path = AssetToolSupport.RequireExistingAssetPath(request.Path);
            if (ctx.DryRun)
            {
                return new Result
                {
                    Asset = AssetToolSupport.BuildAssetDetail(path),
                    DryRun = true,
                    PlannedEffects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.delete", Path = path, SideEffects = "none" } }
                };
            }
            ctx.CommitEffect();
            if (!AssetDatabase.DeleteAsset(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_DELETE_FAILED", "Failed to delete asset: " + path, "Check Unity Console and file permissions.");
            }
            var fullPath = AssetToolSupport.AssetFullPath(path);
            var deleted = await ctx.Wait.Until(
                () => !File.Exists(fullPath)
                    && !Directory.Exists(fullPath)
                    && !AssetDatabase.IsValidFolder(path)
                    && AssetDatabase.LoadMainAssetAtPath(path) == null,
                TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)),
                ct);
            var observedExists = File.Exists(fullPath)
                || Directory.Exists(fullPath)
                || AssetDatabase.IsValidFolder(path)
                || AssetDatabase.LoadMainAssetAtPath(path) != null;
            if (!deleted || observedExists)
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_DELETE_PROOF_MISSING", "Asset deletion was not observed in the file system and AssetDatabase: " + path, "Inspect Unity Console and file permissions before retrying.");
            }
            return new Result
            {
                Asset = new AssetDetail { Path = path, Exists = observedExists },
                PlannedEffects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.delete", Path = path } }
            };
        }
    }
}
#endif
