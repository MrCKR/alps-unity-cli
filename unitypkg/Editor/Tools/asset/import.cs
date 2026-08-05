#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
            Description = "Import a file into an Assets/ path. When source is set, the source file (absolute or Assets/ path) is copied to the destination; otherwise inline content is written. The destination is force-imported, compile/import/reload is absorbed, and success proves the recovered AssetDatabase resolves the destination GUID. Refuses to overwrite unless overwrite=true.",
            Group = "asset",
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

        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            AssetToolSupport.EnsureEditorNotBusy("asset.import");
            var path = AssetToolSupport.RequireAssetPath(request.Path ?? request.Destination);
            var source = request.Source;
            var overwrite = request.Overwrite ?? false;
            var effects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.import", Path = path, Source = source } };
            if (ctx.DryRun)
            {
                return new Result
                {
                    Asset = new AssetDetail { Path = path, Name = Path.GetFileNameWithoutExtension(path), Extension = Path.GetExtension(path), Exists = File.Exists(AssetToolSupport.AssetFullPath(path)) },
                    Source = source,
                    DryRun = true,
                    PlannedEffects = effects
                };
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
                var fullDestination = AssetToolSupport.AssetFullPath(path);
                if (File.Exists(fullDestination) && !overwrite)
                {
                    throw new UnityCliBridge.BridgeCommandException("E_ASSET_OUTPUT_EXISTS", "Asset already exists: " + path, "Pass --overwrite or choose another destination.");
                }
                if (string.Equals(Path.GetFullPath(fullSource), Path.GetFullPath(fullDestination), StringComparison.OrdinalIgnoreCase))
                    throw new UnityCliBridge.BridgeCommandException("E_ASSET_SOURCE_EQUALS_DESTINATION", "Asset import source and destination resolve to the same file.", "Choose a different destination path.");
                ctx.CommitEffect();
                AssetToolSupport.CreateAssetDirectory(path);
                File.Copy(fullSource, fullDestination, true);
            }
            else
            {
                if (request.Content == null)
                    throw new UnityCliBridge.BridgeCommandException("E_ASSET_IMPORT_INPUT_REQUIRED", "asset.import requires source or content.", "Pass a source file or inline content.");
                var fullDestination = AssetToolSupport.AssetFullPath(path);
                if (File.Exists(fullDestination) && !overwrite)
                    throw new UnityCliBridge.BridgeCommandException("E_ASSET_OUTPUT_EXISTS", "Asset already exists: " + path, "Pass --overwrite or choose another destination.");
                ctx.CommitEffect();
                AssetToolSupport.WriteTextAsset(path, request.Content, overwrite);
            }
            // effect 落盘后、ImportAsset 前采集目标字节哈希，用于证明 import 过程未篡改内容。
            var destFullPath = AssetToolSupport.AssetFullPath(path);
            var expectedHash = File.Exists(destFullPath) ? FileSha256(destFullPath) : null;
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var ready = await ctx.Wait.UntilEditorReady(TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct);
            if (!ready)
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_IMPORT_TIMEOUT", "The Editor did not reach a stable ready state after importing " + path + ".", "Inspect editor.state and compile diagnostics before submitting a new request.");
            var asset = AssetToolSupport.BuildAssetDetail(path);
            VerifyContentProof(path, expectedHash);
            if (!asset.Exists || string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_IMPORT_PROOF_MISSING", "The imported asset is not resolvable after import: " + path, "Inspect import/compile diagnostics before submitting a new request.");
            return new Result { Asset = asset, Source = source, PlannedEffects = effects };
        }

        /// <summary>校验 import 后目标文件字节仍与 effect 落盘时一致，避免把预存资产误当成本次 import proof。</summary>
        private static void VerifyContentProof(string path, string expectedHash)
        {
            var fullPath = AssetToolSupport.AssetFullPath(path);
            var actualHash = File.Exists(fullPath) ? FileSha256(fullPath) : null;
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_IMPORT_PROOF_MISSING", "Imported destination bytes do not match the content digest captured before import.", "Inspect the source and destination files before submitting a new request.");
        }

        private static string FileSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(stream);
                return string.Concat(Array.ConvertAll(bytes, value => value.ToString("x2")));
            }
        }
    }
}
#endif
