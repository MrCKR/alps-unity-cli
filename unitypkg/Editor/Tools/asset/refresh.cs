#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.refresh — 触发 AssetDatabase 刷新或单资产导入。支持 dryRun 预览。
    /// </summary>
    public class AssetRefreshTool : IUnityCliTool<AssetRefreshTool.Request, AssetRefreshTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string AssetPath { get; set; }
            public string Target { get; set; }
            public bool? ForceUpdate { get; set; }
            public bool? ImportRecursive { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string Path { get; set; }
            public string Options { get; set; }
            public bool? DryRun { get; set; }
            public bool Refreshed { get; set; }
            public List<AssetPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.refresh",
            Summary = "Refresh the AssetDatabase or reimport a single Assets/ asset; supports --dry-run preview.",
            Description = "Refresh the AssetDatabase. With no path, refreshes the whole database; with a path, reimports that single asset. forceUpdate and importRecursive map to ImportAssetOptions. Supports --dry-run to preview the planned import.",
            Group = "asset",
            Version = "1.0.0",
            DestructiveHint = false,
            SupportsDryRun = true,
            SupportsJob = true,
            SideEffects = new[] { "asset-import" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Refresh the whole database", Input = new Request { } },
                new ToolExample { Description = "Preview reimporting one asset", Input = new Request { Path = "Assets/Textures/Icon.png", ForceUpdate = true }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            AssetToolSupport.EnsureEditorNotBusy("asset.refresh");
            var path = request.Path ?? request.AssetPath ?? request.Target;
            var options = ImportAssetOptions.Default;
            if (request.ForceUpdate ?? false) options |= ImportAssetOptions.ForceUpdate;
            if (request.ImportRecursive ?? false) options |= ImportAssetOptions.ImportRecursive;
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Command = "asset.refresh",
                    DryRun = true,
                    Path = path,
                    Options = options.ToString(),
                    PlannedEffects = new List<AssetPlannedEffect>
                    {
                        new AssetPlannedEffect { Type = string.IsNullOrEmpty(path) ? "asset.refresh" : "asset.import", Path = path, SideEffects = options.ToString() }
                    }
                });
            }
            if (string.IsNullOrEmpty(path))
            {
                AssetDatabase.Refresh(options);
            }
            else
            {
                AssetDatabase.ImportAsset(AssetToolSupport.RequireAssetPath(path), options);
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "asset.refresh",
                Path = path,
                Options = options.ToString(),
                Refreshed = true,
                PlannedEffects = new List<AssetPlannedEffect>
                {
                    new AssetPlannedEffect { Type = string.IsNullOrEmpty(path) ? "asset.refresh" : "asset.import", Path = path }
                }
            });
        }
    }
}
#endif
