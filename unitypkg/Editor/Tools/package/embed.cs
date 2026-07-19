#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCli
{
    /// <summary>package.embed — 将 Packages/ 包嵌入为 Assets/ 本地副本。</summary>
    public class PackageEmbedTool : IUnityCliTool<PackageEmbedTool.Request, PackageEmbedTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Package { get; set; } }
        public sealed class Result { public string PackageId { get; set; } public string EmbeddedPath { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "package.embed", Summary = "Embed an installed registry package and return its verified local package readback.", Description = "Use PackageClient.Embed to copy an installed registry package into Packages/<id>, absorb package import/reload, and succeed only when PackageInfo reports source=Embedded and a real resolved directory. The embedded local copy replaces registry resolution until it is removed.", Group = "package", Version = "1.0.0", DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "package-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 120000, MaxTimeoutMs = 300000, Examples = new[] { new ToolExample { Description = "Embed the installed ProBuilder package", Input = new Request { Package = "com.unity.probuilder" } } } };
        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(r.Package)) throw new UnityCliBridge.BridgeCommandException("E_PACKAGE_ID_REQUIRED", "package.embed requires --package <id>.", "Pass a package id like com.unity.x.");
            // dry-run 在调用 Package Manager 前返回，避免创建 Packages/<id> 本地副本或触发解析。
            if (ctx.DryRun)
            {
                return new Result
                {
                    PackageId = r.Package,
                    EmbeddedPath = "Packages/" + r.Package,
                    PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "package.embed", Path = r.Package } }
                };
            }
            var continuation = ctx.ProofOnly ? ctx.RequireProofContinuation("package-embed-v1") : ctx.ProofContinuation;
            object continuationPackage;
            object beforeEmbedded;
            if (continuation == null
                || !continuation.TryGetValue("packageId", out continuationPackage) || !string.Equals(continuationPackage as string, r.Package, StringComparison.Ordinal)
                || !continuation.TryGetValue("beforeEmbedded", out beforeEmbedded) || !(beforeEmbedded is bool) || (bool)beforeEmbedded)
                throw new UnityCliBridge.BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "Package embed continuation is missing or does not match the request.", "Inspect the request journal before submitting a new request.");
            if (ctx.ProofOnly)
            {
                var recoveredReady = await ctx.Wait.UntilEditorReady(TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct);
                if (!recoveredReady) throw new UnityCliBridge.BridgeCommandException("E_PACKAGE_EMBED_PROOF_MISSING", "The Editor did not recover to a stable state for embedded package proof.", "Inspect Package Manager diagnostics before submitting a new request.");
                return BuildEmbeddedResult(r.Package);
            }
            var client = typeof(UnityEditor.PackageManager.Client);
            var embedMethod = client.GetMethod("Embed", BindingFlags.Static | BindingFlags.Public);
            if (embedMethod == null) throw new UnityCliBridge.BridgeCommandException("E_PACKAGE_EMBED_UNSUPPORTED", "PackageClient.Embed is not available in this Unity version.", "Use Unity Editor Package Manager UI.");
            ctx.CommitEffect();
            var request = embedMethod.Invoke(null, new object[] { r.Package });
            // 等待嵌入完成(同步轮询)
            var requestType = request.GetType();
            var isCompletedProp = requestType.GetProperty("IsCompleted");
            var statusProp = requestType.GetProperty("Status");
            var resultProp = requestType.GetProperty("Result");
            var completed = await ctx.Wait.Until(() => (bool)isCompletedProp.GetValue(request, null), TimeSpan.FromSeconds(120), ct);
            ct.ThrowIfCancellationRequested();
            if (!completed)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_EXECUTION_TIMEOUT",
                    "Package Manager did not finish embedding " + r.Package + " within 120 seconds.",
                    new Dictionary<string, object> { { "packageId", r.Package }, { "timeoutMs", 120000 } },
                    new[] { "Check Package Manager and Unity Console state, then retry after the Editor is responsive." },
                    transient: true,
                    retryAfterMs: 5000);
            }

            var status = statusProp != null ? statusProp.GetValue(request, null) : null;
            if (status != null && string.Equals(status.ToString(), "Failure", StringComparison.OrdinalIgnoreCase))
            {
                var error = requestType.GetProperty("Error")?.GetValue(request, null);
                var errorMessage = error?.GetType().GetProperty("message")?.GetValue(error, null) as string;
                throw new UnityCliBridge.BridgeCommandException(
                    "E_PACKAGE_EMBED_FAILED",
                    string.IsNullOrEmpty(errorMessage) ? "Package Manager failed to embed " + r.Package + "." : errorMessage,
                    new Dictionary<string, object> { { "packageId", r.Package }, { "status", status.ToString() } },
                    new[] { "Inspect the package id and Package Manager diagnostics, then retry." });
            }

            var result = resultProp != null ? resultProp.GetValue(request, null) : null;
            if (result == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PACKAGE_EMBED_FAILED", "Package Manager completed without an embed result for " + r.Package + ".", "Inspect Unity Console and Package Manager diagnostics before retrying.");
            }
            var ready = await ctx.Wait.UntilEditorReady(TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct);
            if (!ready) throw new UnityCliBridge.BridgeCommandException("E_PACKAGE_EMBED_PROOF_MISSING", "The Editor did not become stable after embedding " + r.Package + ".", "Inspect Package Manager diagnostics before submitting a new request.");
            return BuildEmbeddedResult(r.Package);
        }

        /// <summary>只从 PackageManager registry 与磁盘构造 terminal proof，不触发 resolve/embed。</summary>
        private static Result BuildEmbeddedResult(string packageId)
        {
            var assetPath = "Packages/" + packageId;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            var expected = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Directory.GetParent(Application.dataPath).FullName, "Packages", packageId));
            var actual = info == null || string.IsNullOrEmpty(info.resolvedPath) ? null : System.IO.Path.GetFullPath(info.resolvedPath);
            if (info == null
                || info.source != UnityEditor.PackageManager.PackageSource.Embedded
                || !string.Equals(info.name, packageId, StringComparison.Ordinal)
                || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                || !System.IO.Directory.Exists(actual))
                throw new UnityCliBridge.BridgeCommandException("E_PACKAGE_EMBED_PROOF_MISSING", "Package Manager does not report a verified embedded package at " + assetPath + ".", "Inspect Packages/ and Package Manager diagnostics before submitting a new request.");
            return new Result { PackageId = packageId, EmbeddedPath = actual, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "package.embed", Path = packageId } } };
        }
    }
}
#endif
