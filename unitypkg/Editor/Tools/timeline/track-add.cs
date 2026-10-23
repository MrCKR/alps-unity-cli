#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>向 TimelineAsset 添加一个批准的 built-in track，并以 fresh localId/parent 结构证明持久化结果。</summary>
    public sealed class TimelineTrackAddTool : IUnityCliTool<TimelineTrackAddTool.Request, TimelineTrackAddTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Timeline { get; set; }
            [UnityCliRequired]
            public string TrackType { get; set; }
            public string Name { get; set; }
            public string ParentTrack { get; set; }
            public string ParentLocalId { get; set; }
        }

        public sealed class Result
        {
            public bool Added { get; set; }
            public bool DryRun { get; set; }
            public TimelineAssetInfo Timeline { get; set; }
            public TimelineTrackMutationInfo Track { get; set; }
            public List<TimelineMutationPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "timeline.track-add",
            Summary = "Add one approved built-in Timeline track and prove its stable identity, exact type, name, and parent.",
            Description = "Requires com.unity.timeline. timeline accepts an Assets/*.playable path or GUID. trackType is Animation, Audio, Activation, Control, Playable, Signal, or Marker. parentTrack selects one exact unique name; parentLocalId selects the stable decimal string returned by timeline.info, and the selectors are mutually exclusive. The complete type/parent/name plan is validated before dry-run or one asset-write/import effect.",
            Group = "timeline",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "asset-write", "asset-import" },
            Permissions = new[] { "project-asset-write" },
            RequiresPackages = new[] { TimelineToolSupport.PackageId },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 60000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Add a root Animation track.", Input = new Request { Timeline = "Assets/Timelines/Intro.playable", TrackType = "Animation", Name = "Character Animation" } },
                new ToolExample { Description = "Preview an Audio track under a parent localId.", Input = new Request { Timeline = "guid:0123456789abcdef0123456789abcdef", TrackType = "Audio", Name = "Music", ParentLocalId = "-1234567890123456789" }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        /// <summary>effect 前冻结全部 identity/API/parent/name 约束；actual 路径只跨一次 barrier。</summary>
        public ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default(CancellationToken))
        {
            AssetToolSupport.EnsureEditorNotBusy("timeline.track-add");
            ct.ThrowIfCancellationRequested();
            request = request ?? new Request();
            var plan = TimelineMutationToolSupport.PrepareTrackAdd(request.Timeline, request.TrackType, request.Name,
                request.ParentTrack, request.ParentLocalId);
            var effects = new List<TimelineMutationPlannedEffect>
            {
                new TimelineMutationPlannedEffect { Type = "timeline.track-add", Path = plan.Timeline.Path, Target = plan.Name, SideEffects = "asset-write,asset-import" }
            };
            if (ctx.DryRun)
            {
                return new ValueTask<Result>(new Result
                {
                    Added = false,
                    DryRun = true,
                    Timeline = TimelineMutationToolSupport.BuildTimelineInfo(plan.Timeline),
                    Track = TimelineMutationToolSupport.PreviewTrack(plan),
                    PlannedEffects = effects
                });
            }

            ct.ThrowIfCancellationRequested();
            ctx.CommitEffect();
            var track = TimelineMutationToolSupport.AddTrack(plan);
            return new ValueTask<Result>(new Result
            {
                Added = true,
                DryRun = false,
                Timeline = TimelineMutationToolSupport.BuildTimelineInfo(plan.Timeline),
                Track = track,
                PlannedEffects = effects
            });
        }
    }
}
#endif
