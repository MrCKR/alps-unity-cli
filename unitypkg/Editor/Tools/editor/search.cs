#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>运行Unity Search provider查询并返回有界页。</summary>
    public sealed class EditorSearchTool : IUnityCliTool<EditorSearchRequest, EditorSearchResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "editor.search",
            Summary = "Run a bounded Unity Search query with explicit provider metadata and paging.",
            Description = "Uses UnityEditor.Search directly across active Unity Search providers. providers accepts at most 16 exact active provider IDs; omission uses the active provider set. offset accepts 0..10000 and limit defaults to 50 with a maximum of 200. At most offset+limit+1 items are scanned and only the requested page is retained. This is provider-level Unity Search, not an alias for asset.search or go.find.",
            Group = "editor",
            DestructiveHint = false,
            SupportsDryRun = false,
            SideEffects = System.Array.Empty<string>(),
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 120000,
            Examples = new[]
            {
                new ToolExample { Description = "Find Material items through Unity Search.", Input = new EditorSearchRequest { Query = "t:Material", Limit = 20 } },
                new ToolExample { Description = "Query one exact provider page.", Input = new EditorSearchRequest { Query = "Main Camera", Providers = new List<string> { "scene" }, Offset = 0, Limit = 50 } }
            }
        };

        /// <summary>同步Search context在方法内创建和释放，输出只保留bounded page。</summary>
        public System.Threading.Tasks.ValueTask<EditorSearchResult> ExecuteAsync(EditorSearchRequest request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return new System.Threading.Tasks.ValueTask<EditorSearchResult>(EditorSearchToolSupport.Search(request));
        }
    }

    public sealed class EditorSearchRequest
    {
        [UnityCliRequired]
        public string Query { get; set; }
        public List<string> Providers { get; set; }
        public long? Offset { get; set; }
        public long? Limit { get; set; }
    }

    public sealed class EditorSearchResult
    {
        public string Query { get; set; }
        public long Offset { get; set; }
        public long Limit { get; set; }
        public long ReturnedCount { get; set; }
        public bool HasMore { get; set; }
        public long? NextOffset { get; set; }
        public List<EditorSearchProvider> Providers { get; set; }
        public List<EditorSearchItem> Results { get; set; }
    }
}
#endif
