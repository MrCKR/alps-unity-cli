#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.create — 创建文本资产或文件夹。拒绝覆盖已存在资产(除非 overwrite)。
    /// </summary>
    public class AssetCreateTool : IUnityCliTool<AssetCreateTool.Request, AssetCreateTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Type { get; set; }
            public string Content { get; set; }
            public bool? Overwrite { get; set; }
        }

        public sealed class Result
        {
            public AssetDetail Asset { get; set; }
            public List<AssetPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.create",
            Summary = "Create a text asset file or folder under Assets/, refusing to overwrite unless overwrite=true.",
            Description = "Create a text asset file or folder under Assets/. When type=folder (or the path has no extension) a folder is created; otherwise a text file is written and imported. Refuses to overwrite an existing asset unless overwrite=true.",
            Group = "asset",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Create a text file", Input = new Request { Path = "Assets/Generated/created.txt", Content = "hello" } },
                new ToolExample { Description = "Create a folder", Input = new Request { Path = "Assets/Generated/Sub", Type = "folder" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            AssetToolSupport.EnsureEditorNotBusy("asset.create");
            var path = AssetToolSupport.RequireAssetPath(request.Path);
            var type = request.Type ?? "file";
            var overwrite = request.Overwrite ?? false;
            if (!string.Equals(type, "file", StringComparison.OrdinalIgnoreCase) && !string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase) && !string.Equals(type, "directory", StringComparison.OrdinalIgnoreCase))
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_TYPE_INVALID", "asset.create type must be file or folder.", "Pass type=file or type=folder.");
            // 无扩展名或显式 folder 类型视为目录创建
            if (string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "directory", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(Path.GetExtension(path)))
            {
                if (AssetToolSupport.AssetExists(path))
                    throw new UnityCliBridge.BridgeCommandException("E_ASSET_OUTPUT_EXISTS", "Asset already exists: " + path, "Choose another Assets/* folder path.");
                ctx.CommitEffect();
                AssetToolSupport.CreateAssetFolder(path);
                if (!AssetDatabase.IsValidFolder(path)) throw new UnityCliBridge.BridgeCommandException("E_ASSET_CREATE_STATE_MISMATCH", "Created folder could not be reloaded: " + path, "Inspect the AssetDatabase and retry.");
            }
            else
            {
                if (AssetToolSupport.AssetExists(path) && !overwrite)
                    throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Output already exists: " + path, "Pass --overwrite or choose a different asset path.");
                ctx.CommitEffect();
                AssetToolSupport.WriteTextAsset(path, request.Content ?? string.Empty, overwrite);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                if (!File.Exists(AssetToolSupport.AssetFullPath(path)) || File.ReadAllText(AssetToolSupport.AssetFullPath(path)) != (request.Content ?? string.Empty))
                    throw new UnityCliBridge.BridgeCommandException("E_ASSET_CREATE_STATE_MISMATCH", "Fresh asset bytes did not match the requested content: " + path, "Check file permissions and retry.");
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Asset = AssetToolSupport.BuildAssetDetail(path),
                PlannedEffects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.create", Path = path } }
            });
        }
    }
}
#endif
