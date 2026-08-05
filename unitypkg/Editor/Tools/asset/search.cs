#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// asset.search — 搜索 AssetDatabase:支持 Unity 原生 filter、类型追加、多目录范围与分页。
    /// </summary>
    public class AssetSearchTool : IUnityCliTool<AssetSearchTool.Request, AssetSearchTool.Result>
    {
        public sealed class Request
        {
            public string Filter { get; set; }
            public string Query { get; set; }
            public string Type { get; set; }
            public string Path { get; set; }
            public List<string> SearchInFolders { get; set; }
            public int? PageSize { get; set; }
            public int? MaxResults { get; set; }
            public int? Cursor { get; set; }
        }

        public sealed class Result
        {
            public string Filter { get; set; }
            public string Query { get; set; }
            public string Type { get; set; }
            public string Path { get; set; }
            public List<string> SearchInFolders { get; set; }
            public List<AssetSummary> Items { get; set; }
            public AssetPageInfo PageInfo { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.search",
            Summary = "Search the Unity AssetDatabase by native filter, type, and folder scope with paging.",
            Description = "Search the Unity AssetDatabase using a native filter, optional type suffix, and one or more Assets/ or Packages/ folder scopes. Results are lightweight summaries, paged via cursor/pageSize.",
            Group = "asset",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Find textures by type", Input = new Request { Filter = "t:Texture2D", PageSize = 25 } },
                new ToolExample { Description = "Search a folder scope", Input = new Request { Query = "Player", SearchInFolders = new List<string> { "Assets/Characters" } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var searchInFolders = AssetToolSupport.NormalizeAssetSearchFolders(request.Path, request.SearchInFolders);
            var filterParts = new List<string>();
            if (!string.IsNullOrEmpty(request.Filter)) filterParts.Add(request.Filter);
            else if (!string.IsNullOrEmpty(request.Query)) filterParts.Add(request.Query);
            if (!string.IsNullOrEmpty(request.Type)) filterParts.Add("t:" + request.Type);
            var resolvedFilter = string.Join(" ", filterParts.ToArray()).Trim();
            var guids = AssetDatabase.FindAssets(resolvedFilter, searchInFolders.Length == 0 ? null : searchInFolders);
            var cursor = Math.Max(0, request.Cursor ?? 0);
            var pageSize = Math.Max(1, Math.Min(request.PageSize ?? request.MaxResults ?? 100, 500));
            var items = new List<AssetSummary>();
            for (var index = cursor; index < guids.Length && items.Count < pageSize; index++)
            {
                var assetPath = AssetToolSupport.NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(guids[index]));
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                items.Add(AssetToolSupport.BuildAssetSummary(assetPath, assetType));
            }
            long? nextCursor = cursor + items.Count < guids.Length ? (long?)(cursor + items.Count) : null;
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Filter = resolvedFilter,
                Query = request.Query,
                Type = request.Type,
                Path = searchInFolders.Length == 1 ? searchInFolders[0] : null,
                SearchInFolders = new List<string>(searchInFolders),
                Items = items,
                PageInfo = new AssetPageInfo
                {
                    PageSize = pageSize,
                    Cursor = cursor,
                    NextCursor = nextCursor,
                    Total = guids.Length,
                    Truncated = nextCursor != null
                }
            });
        }
    }
}
#endif
