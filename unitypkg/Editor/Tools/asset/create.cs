#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.create — 创建文本资产或文件夹。拒绝覆盖已存在资产(除非 overwrite)。
    /// 对齐旧 AssetCreate handler;session-affecting(asset-write)。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
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
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Create a text file", Input = new Request { Path = "Assets/Generated/notes.txt", Content = "hello" } },
                new ToolExample { Description = "Create a folder", Input = new Request { Path = "Assets/Generated/Sub", Type = "folder" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            AssetToolSupport.EnsureEditorNotBusy("asset.create");
            var path = AssetToolSupport.RequireAssetPath(request.Path);
            var type = request.Type ?? "file";
            var overwrite = request.Overwrite ?? false;
            // 无扩展名或显式 folder 类型视为目录创建
            if (string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "directory", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(Path.GetExtension(path)))
            {
                AssetToolSupport.CreateAssetFolder(path);
            }
            else
            {
                AssetToolSupport.WriteTextAsset(path, request.Content ?? string.Empty, overwrite);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            return new Result
            {
                Asset = AssetToolSupport.BuildAssetDetail(path),
                PlannedEffects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.create", Path = path } }
            };
        }
    }
}
#endif
