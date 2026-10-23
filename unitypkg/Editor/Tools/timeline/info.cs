#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>以 bounded fresh snapshot 读取 Timeline tracks、clips、timing 与 asset/output bindings。</summary>
    public sealed class TimelineInfoTool : IUnityCliTool<TimelineInfoTool.Request, TimelineInfoResult>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Timeline { get; set; }
            public int? Cursor { get; set; }
            public int? PageSize { get; set; }
            public int? MaxDepth { get; set; }
            public int? MaxClipsPerTrack { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "timeline.info",
            Summary = "Read a bounded TimelineAsset structure with stable track identities, clips, timing, and asset/output bindings.",
            Description = "Requires com.unity.timeline. timeline accepts an Assets/*.playable path, a 32-character GUID, or guid:<GUID>. frameRate is the persisted editor frame rate read from editorSettings.frameRate (Timeline 1.5 and newer) or the legacy editorSettings.fps member. Tracks are flattened in root pre-order and paged by cursor/pageSize (default 0/50, max 200); maxDepth defaults to 8 (max 32), and maxClipsPerTrack defaults to 100 (max 500). timeline.trackCount and pageInfo.total count tracks within maxDepth; depthTruncated reports omitted descendants. Track parent identity and clip playable/source assets use stable GUID/localId string pairs. Output bindings describe Timeline track output types only; PlayableDirector scene-object bindings are outside a TimelineAsset and are not read.",
            Group = "timeline",
            DestructiveHint = false,
            SupportsDryRun = false,
            SideEffects = new string[0],
            Permissions = new[] { "project-asset-read" },
            RequiresPackages = new[] { TimelineToolSupport.PackageId },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 120000,
            Examples = new[]
            {
                new ToolExample { Description = "Read the first Timeline structure page.", Input = new Request { Timeline = "Assets/Timelines/Intro.playable", PageSize = 50, MaxDepth = 8, MaxClipsPerTrack = 100 } },
                new ToolExample { Description = "Read the next bounded page by GUID.", Input = new Request { Timeline = "guid:0123456789abcdef0123456789abcdef", Cursor = 50, PageSize = 50 } }
            }
        };

        /// <summary>解析并 fresh 读取现有 asset；不调用 refresh/import/SaveAssets，也不跨 effect barrier。</summary>
        public ValueTask<TimelineInfoResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default(CancellationToken))
        {
            AssetToolSupport.EnsureEditorNotBusy("timeline.info");
            ct.ThrowIfCancellationRequested();
            request = request ?? new Request();
            var plan = TimelineReadToolSupport.Prepare(request.Timeline, request.Cursor, request.PageSize, request.MaxDepth, request.MaxClipsPerTrack);
            var result = TimelineReadToolSupport.Read(plan, ct);
            return new ValueTask<TimelineInfoResult>(result);
        }
    }
}
#endif
