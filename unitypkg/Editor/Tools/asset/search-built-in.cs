#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.search-built-in — 查找内置资源(Resources/ 与 Packages/ 路径),可按类型过滤。
    /// </summary>
    public class AssetSearchBuiltInTool : IUnityCliTool<AssetSearchBuiltInTool.Request, AssetSearchBuiltInTool.Result>
    {
        public sealed class Request
        {
            public string Keyword { get; set; }
            public string Search { get; set; }
            public string Type { get; set; }
            public int? PageSize { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public long Count { get; set; }
            public List<BuiltInAsset> Assets { get; set; }
        }

        public sealed class BuiltInAsset
        {
            public string Path { get; set; }
            public string Guid { get; set; }
            public string Name { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.search-built-in",
            Summary = "Find built-in Unity assets under Resources/ and Packages/ by keyword and optional type.",
            Description = "Find built-in Unity assets (Resources/ and Packages/ paths) by keyword and optional main-asset type name. Read-only; use this to discover built-in shaders, textures, or defaults before creating materials or assigning assets.",
            Group = "asset",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Find built-in textures", Input = new Request { Keyword = "Default", Type = "Texture2D", PageSize = 25 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var keyword = request.Keyword ?? request.Search;
            var typeFilter = request.Type;
            var guids = AssetDatabase.FindAssets(keyword ?? string.Empty);
            var results = new List<BuiltInAsset>();
            var pageSize = Math.Max(1, Math.Min(request.PageSize ?? 100, 500));
            foreach (var guid in guids)
            {
                if (results.Count >= pageSize) break;
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                // 仅保留 Resources/ 与内置包路径
                if (!assetPath.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase)
                    && !assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                    && !assetPath.Contains("/Resources/"))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(typeFilter))
                {
                    var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                    if (mainAsset == null || !mainAsset.GetType().Name.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                }
                results.Add(new BuiltInAsset
                {
                    Path = AssetToolSupport.NormalizeAssetPath(assetPath),
                    Guid = guid,
                    Name = System.IO.Path.GetFileNameWithoutExtension(assetPath)
                });
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "asset.search-built-in",
                Count = results.Count,
                Assets = results
            });
        }
    }
}
#endif
