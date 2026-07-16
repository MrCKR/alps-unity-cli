#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.rename — 重命名资产(不改目录)。禁止路径分隔符。
    /// </summary>
    public class AssetRenameTool : IUnityCliTool<AssetRenameTool.Request, AssetRenameTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Name { get; set; }
        }

        public sealed class Result
        {
            public AssetDetail Asset { get; set; }
            public string Source { get; set; }
            public List<AssetPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.rename",
            Summary = "Rename an existing Assets/ asset file in place, without path separators.",
            Description = "Rename an existing Assets/ asset file in place. The name must not include path separators; use asset.move to change directories.",
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
                new ToolExample { Description = "Rename an asset", Input = new Request { Path = "Assets/Icons/old.png", Name = "new" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            AssetToolSupport.EnsureEditorNotBusy("asset.rename");
            var path = AssetToolSupport.RequireExistingAssetPath(request.Path);
            var name = request.Name;
            if (string.IsNullOrEmpty(name))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_NAME_REQUIRED", "Asset rename requires a new name.", "Pass a filename without path separators.");
            }
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_NAME_INVALID", "Asset rename name must not include path separators: " + name, "Use asset move for changing directories.");
            }
            var guid = AssetDatabase.AssetPathToGUID(path);
            var error = AssetDatabase.RenameAsset(path, name);
            if (!string.IsNullOrEmpty(error))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_RENAME_FAILED", "Failed to rename asset: " + error, "Check name validity and Unity Console diagnostics.");
            }
            AssetDatabase.Refresh();
            var renamedPath = AssetToolSupport.NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(guid));
            if (string.IsNullOrEmpty(renamedPath))
            {
                renamedPath = AssetToolSupport.NormalizeAssetPath(Path.Combine(Path.GetDirectoryName(path) ?? "Assets", name));
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Asset = AssetToolSupport.BuildAssetDetail(renamedPath),
                Source = path,
                PlannedEffects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.rename", Path = renamedPath, Source = path } }
            });
        }
    }
}
#endif
