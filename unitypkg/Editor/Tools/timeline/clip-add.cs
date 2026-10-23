#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>向唯一 Timeline track 添加一个 clip，并以 fresh timing/playable/source binding 证明持久化结果。</summary>
    public sealed class TimelineClipAddTool : IUnityCliTool<TimelineClipAddTool.Request, TimelineClipAddTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Timeline { get; set; }
            public string Track { get; set; }
            public string TrackLocalId { get; set; }
            [UnityCliRequired]
            public double Start { get; set; }
            [UnityCliRequired]
            public double Duration { get; set; }
            public string Asset { get; set; }
        }

        public sealed class Result
        {
            public bool Added { get; set; }
            public bool DryRun { get; set; }
            public TimelineAssetInfo Timeline { get; set; }
            public TimelineClipMutationInfo Clip { get; set; }
            public List<TimelineMutationPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "timeline.clip-add",
            Summary = "Add one Timeline clip and prove its track, timing, playable asset, source binding, and serialized artifact.",
            Description = "Requires com.unity.timeline. timeline and asset accept an Assets path or GUID. Select exactly one target with track (unique exact name) or trackLocalId (stable decimal string from timeline.info). start must be finite and >= 0; duration must be finite and > 0 within the installed Timeline range. Animation tracks require an AnimationClip asset and Audio tracks require an AudioClip; other approved built-in tracks must expose CreateDefaultClip. The complete target/source/API/timing plan is validated before dry-run or one asset-write/import effect.",
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
                new ToolExample { Description = "Add an Animation clip by stable track localId.", Input = new Request { Timeline = "Assets/Timelines/Intro.playable", TrackLocalId = "-1234567890123456789", Start = 0d, Duration = 2.5d, Asset = "Assets/Animations/Intro.anim" } },
                new ToolExample { Description = "Preview a default Activation clip by unique track name.", Input = new Request { Timeline = "guid:0123456789abcdef0123456789abcdef", Track = "Activation", Start = 1d, Duration = 3d }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        /// <summary>effect 前冻结 target/source/overload/timing/clip count；actual 路径只跨一次 barrier。</summary>
        public ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default(CancellationToken))
        {
            AssetToolSupport.EnsureEditorNotBusy("timeline.clip-add");
            ct.ThrowIfCancellationRequested();
            request = request ?? new Request();
            var plan = TimelineMutationToolSupport.PrepareClipAdd(request.Timeline, request.Track, request.TrackLocalId,
                request.Start, request.Duration, request.Asset);
            var effects = new List<TimelineMutationPlannedEffect>
            {
                new TimelineMutationPlannedEffect { Type = "timeline.clip-add", Path = plan.Timeline.Path, Target = plan.Track.Identity.LocalId, SideEffects = "asset-write,asset-import" }
            };
            if (ctx.DryRun)
            {
                return new ValueTask<Result>(new Result
                {
                    Added = false,
                    DryRun = true,
                    Timeline = TimelineMutationToolSupport.BuildTimelineInfo(plan.Timeline),
                    Clip = TimelineMutationToolSupport.PreviewClip(plan),
                    PlannedEffects = effects
                });
            }

            ct.ThrowIfCancellationRequested();
            ctx.CommitEffect();
            var clip = TimelineMutationToolSupport.AddClip(plan);
            return new ValueTask<Result>(new Result
            {
                Added = true,
                DryRun = false,
                Timeline = TimelineMutationToolSupport.BuildTimelineInfo(plan.Timeline),
                Clip = clip,
                PlannedEffects = effects
            });
        }
    }
}
#endif
