#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
            Summary = "Reimport one Assets/ asset with content and AssetDatabase readback proof; supports --dry-run preview.",
            Description = "Reimport one Assets/ asset and return only after its continued file digest and AssetDatabase GUID are stable. Whole-database refresh has no attributable business completion identity and is rejected. forceUpdate and importRecursive map to ImportAssetOptions. Supports --dry-run to preview the planned import.",
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
                new ToolExample { Description = "Preview reimporting one asset", Input = new Request { Path = "Assets/Textures/Icon.png", ForceUpdate = true }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ctx.ProofOnly) AssetToolSupport.EnsureEditorNotBusy("asset.refresh");
            var path = request.Path ?? request.AssetPath ?? request.Target;
            if (string.IsNullOrEmpty(path))
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_REFRESH_PROOF_MISSING", "asset.refresh requires one asset path because whole-database refresh has no attributable completion proof.", "Pass an Assets/* path to reimport one observable asset.");
            var options = ImportAssetOptions.Default;
            if (request.ForceUpdate ?? false) options |= ImportAssetOptions.ForceUpdate;
            if (request.ImportRecursive ?? false) options |= ImportAssetOptions.ImportRecursive;
            if (ctx.DryRun)
            {
                return new Result
                {
                    Command = "asset.refresh",
                    DryRun = true,
                    Path = path,
                    Options = options.ToString(),
                    PlannedEffects = new List<AssetPlannedEffect>
                    {
                        new AssetPlannedEffect { Type = "asset.import", Path = path, SideEffects = options.ToString() }
                    }
                };
            }
            var continuation = ctx.ProofOnly ? ctx.RequireProofContinuation("asset-refresh-v1") : ctx.ProofContinuation;
            object expectedPath;
            object beforeGeneration;
            var normalizedPath = string.IsNullOrEmpty(path) ? string.Empty : AssetToolSupport.RequireAssetPath(path);
            if (continuation == null
                || !continuation.TryGetValue("path", out expectedPath) || !string.Equals(expectedPath as string, normalizedPath, StringComparison.Ordinal)
                || !continuation.TryGetValue("beforeListenerGeneration", out beforeGeneration))
                throw new UnityCliBridge.BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "Asset refresh continuation does not match the requested path.", "Inspect the request journal before submitting a new request.");
            if (ctx.ProofOnly && ctx.ListenerGeneration <= Convert.ToInt64(beforeGeneration))
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_REFRESH_PROOF_MISSING", "Asset refresh recovery did not observe a replacement listener generation.", "Inspect editor.state before submitting a new request.");
            if (!ctx.ProofOnly)
            {
                ctx.CommitEffect();
                AssetDatabase.ImportAsset(AssetToolSupport.RequireAssetPath(path), options);
            }
            path = AssetToolSupport.RequireAssetPath(path);
            object expectedHash;
            var fullPath = AssetToolSupport.AssetFullPath(path);
            if (!continuation.TryGetValue("contentSha256", out expectedHash))
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_REFRESH_PROOF_MISSING", "Asset refresh continuation is missing the content digest: " + path, "Inspect the asset file before submitting a new request.");
            var stable = await ctx.Wait.UntilStable(() =>
                File.Exists(fullPath)
                && string.Equals(FileSha256(fullPath), expectedHash as string, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)),
                TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct: ct);
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (!stable || !File.Exists(fullPath) || !string.Equals(FileSha256(fullPath), expectedHash as string, StringComparison.Ordinal) || string.IsNullOrEmpty(guid))
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_REFRESH_PROOF_MISSING", "The reimported asset did not retain its continued content digest and AssetDatabase identity: " + path, "Inspect the asset import diagnostics before submitting a new request.");
            return new Result
            {
                Command = "asset.refresh",
                Path = path,
                Options = options.ToString(),
                Refreshed = true,
                PlannedEffects = new List<AssetPlannedEffect>
                {
                    new AssetPlannedEffect { Type = "asset.import", Path = path }
                }
            };
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
