#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.import — 从源文件或内联 content 导入资产到 Assets/ 路径。
    /// </summary>
    public class AssetImportTool : IUnityCliTool<AssetImportTool.Request, AssetImportTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Destination { get; set; }
            public string Source { get; set; }
            public string Content { get; set; }
            public bool? Overwrite { get; set; }
        }

        public sealed class Result
        {
            public AssetDetail Asset { get; set; }
            public string Source { get; set; }
            public bool DryRun { get; set; }
            public List<AssetPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.import",
            Summary = "Import a file into an Assets/ path from a source file or inline content, then force-import it.",
            Description = "Import a file into an Assets/ path. When source is set, the source file (absolute or Assets/ path) is copied to the destination; otherwise inline content is written. The destination is then force-imported. Refuses to overwrite unless overwrite=true.",
            Group = "asset",
            Version = "1.0.0",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "asset-write", "asset-import" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Import inline text", Input = new Request { Path = "Assets/Generated/imported.txt", Content = "matrix import", Overwrite = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            AssetToolSupport.EnsureEditorNotBusy("asset.import");
            var path = AssetToolSupport.RequireAssetPath(request.Path ?? request.Destination);
            var source = request.Source;
            var overwrite = request.Overwrite ?? false;
            var effects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.import", Path = path, Source = source } };
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Asset = new AssetDetail { Path = path, Name = Path.GetFileNameWithoutExtension(path), Extension = Path.GetExtension(path), Exists = File.Exists(AssetToolSupport.AssetFullPath(path)) },
                    Source = source,
                    DryRun = true,
                    PlannedEffects = effects
                });
            }
            if (!string.IsNullOrEmpty(source))
            {
                var normalizedSource = AssetToolSupport.NormalizeAssetPath(source);
                var fullSource = normalizedSource.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? AssetToolSupport.AssetFullPath(normalizedSource)
                    : Path.GetFullPath(source.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullSource))
                {
                    throw new UnityCliBridge.BridgeCommandException("E_ASSET_SOURCE_NOT_FOUND", "Source file not found: " + source, "Pass an existing file path or Assets/* source asset.");
                }
                AssetToolSupport.CreateAssetDirectory(path);
                var fullDestination = AssetToolSupport.AssetFullPath(path);
                if (File.Exists(fullDestination) && !overwrite)
                {
                    throw new UnityCliBridge.BridgeCommandException("E_ASSET_OUTPUT_EXISTS", "Asset already exists: " + path, "Pass --overwrite or choose another destination.");
                }
                File.Copy(fullSource, fullDestination, true);
            }
            else
            {
                AssetToolSupport.WriteTextAsset(path, request.Content ?? string.Empty, overwrite);
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Asset = AssetToolSupport.BuildAssetDetail(path),
                Source = source,
                PlannedEffects = effects
            });
        }
    }
}
#endif
