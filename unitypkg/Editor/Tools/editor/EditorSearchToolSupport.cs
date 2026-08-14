#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Search;

namespace UnityCli
{
    /// <summary>Unity Search provider选择、有界同步枚举和portable item投影owner。</summary>
    internal static class EditorSearchToolSupport
    {
        internal const int MaxProviders = 16;
        internal const int MaxOffset = 10000;
        internal const int MaxLimit = 200;
        internal const int MaxQueryLength = 2048;
        private const int MaxTextLength = 4096;

        internal static EditorSearchResult Search(EditorSearchRequest request)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.search");
            if (request == null || string.IsNullOrWhiteSpace(request.Query))
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_SEARCH_QUERY_REQUIRED", "editor.search requires a non-empty Unity Search query.", "Pass a query such as t:Material or h:Main Camera.");
            if (request.Query.Length > MaxQueryLength)
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_SEARCH_QUERY_TOO_LONG", "Unity Search query exceeds 2048 characters.", "Reduce the query before retrying.");
            var offset = request.Offset ?? 0L;
            var limit = request.Limit ?? 50L;
            if (offset < 0 || offset > MaxOffset)
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_SEARCH_PAGE_INVALID", "offset must be between 0 and 10000.", "Use a bounded offset.");
            if (limit < 1 || limit > MaxLimit)
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_SEARCH_PAGE_INVALID", "limit must be between 1 and 200.", "Use a bounded limit.");

            var active = SearchService.GetActiveProviders().Where(provider => provider != null && provider.active).ToList();
            var byId = active.GroupBy(provider => provider.id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var requested = request.Providers == null || request.Providers.Count == 0
                ? active.Select(provider => provider.id).Distinct(StringComparer.Ordinal).ToList()
                : new List<string>(request.Providers);
            if (requested.Count == 0)
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_SEARCH_PROVIDER_NOT_FOUND", "Unity Search has no active providers.", "Open Unity Search and inspect available providers.");
            if (requested.Count > MaxProviders || requested.Any(string.IsNullOrWhiteSpace) || requested.Distinct(StringComparer.Ordinal).Count() != requested.Count)
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_SEARCH_PROVIDER_INVALID", "providers must contain 1..16 unique non-empty exact provider IDs.", "Use provider IDs returned by editor.search provider metadata.");
            var missing = requested.Where(id => !byId.ContainsKey(id)).ToList();
            if (missing.Count > 0)
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_SEARCH_PROVIDER_NOT_FOUND", "Unity Search provider is not active: " + string.Join(", ", missing) + ".", "Remove unavailable provider IDs or enable them in Unity Search.");

            var results = new List<EditorSearchItem>((int)limit);
            var matched = 0L;
            var hasMore = false;
            SearchContext context = null;
            ISearchList list = null;
            try
            {
                context = SearchService.CreateContext(requested, request.Query, SearchFlags.Synchronous);
                list = SearchService.Request(context, SearchFlags.Synchronous);
                foreach (var item in list)
                {
                    if (item == null) continue;
                    if (matched < offset) { matched++; continue; }
                    if (results.Count >= limit) { hasMore = true; break; }
                    results.Add(Item(item));
                    matched++;
                }
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_SEARCH_FAILED", "Unity Search failed: " + exception.GetType().Name + ".", "Validate the query and provider IDs before retrying.");
            }
            finally
            {
                var disposable = list as IDisposable;
                if (disposable != null) disposable.Dispose();
                else context?.Dispose();
            }

            var providers = requested.Select(id => Provider(byId[id])).ToList();
            return new EditorSearchResult
            {
                ReturnedCount = results.Count,
                HasMore = hasMore,
                NextOffset = hasMore ? offset + results.Count : (long?)null,
                Results = results,
                Providers = providers
            };
        }

        private static EditorSearchProvider Provider(SearchProvider provider)
        {
            return new EditorSearchProvider
            {
                Id = Bound(provider.id),
                Name = Bound(provider.name),
                FilterId = Bound(provider.filterId),
                Priority = provider.priority
            };
        }

        private static EditorSearchItem Item(SearchItem item)
        {
            string label = null;
            string description = null;
            string path = null;
            try { label = item.GetLabel(item.context, true); } catch { }
            try { description = item.GetDescription(item.context, true); } catch { }
            try
            {
                var value = item.ToObject();
                if (value != null) path = AssetDatabase.GetAssetPath(value);
            }
            catch { }
            return new EditorSearchItem
            {
                Id = Bound(item.id),
                Label = Bound(string.IsNullOrEmpty(label) ? item.label ?? item.id : label),
                Description = Bound(description),
                ProviderId = Bound(item.provider == null ? null : item.provider.id),
                Path = Bound(string.IsNullOrEmpty(path) ? null : path),
                Score = item.score
            };
        }

        private static string Bound(string value)
        {
            return value == null || value.Length <= MaxTextLength ? value : value.Substring(0, MaxTextLength);
        }
    }

    public sealed class EditorSearchProvider
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string FilterId { get; set; }
        public long Priority { get; set; }
    }

    public sealed class EditorSearchItem
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }
        public string ProviderId { get; set; }
        public string Path { get; set; }
        public long Score { get; set; }
    }
}
#endif
