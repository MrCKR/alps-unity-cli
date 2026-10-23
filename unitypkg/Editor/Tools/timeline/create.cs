#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>创建或显式替换一个 TimelineAsset，并证明 path、GUID、frame rate 与 file artifact。</summary>
    public sealed class TimelineCreateTool : IUnityCliTool<TimelineCreateTool.Request, TimelineCreateTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Path { get; set; }
            public double? FrameRate { get; set; }
            public bool Overwrite { get; set; }
        }

        public sealed class Result
        {
            public bool Created { get; set; }
            public bool Overwritten { get; set; }
            public bool DryRun { get; set; }
            public TimelineAssetInfo Timeline { get; set; }
            public List<TimelinePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "timeline.create",
            Summary = "Create a TimelineAsset with a validated .playable path and frame rate, then prove its GUID and serialized artifact.",
            Description = "Requires com.unity.timeline. frameRate defaults to 60 and must be finite in [0.000001,1000]; it is written through editorSettings.frameRate (Timeline 1.5 and newer) or the legacy editorSettings.fps member on earlier versions, and the Tool fails with E_TIMELINE_FRAME_RATE_UNSUPPORTED when neither member is writable. The created asset keeps that member's precision. Existing Timeline assets require overwrite=true and retain their GUID; another asset type or orphan metadata at the path is always rejected. Dry-run validates the package API and complete create plan without writing assets.",
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
                new ToolExample { Description = "Create a 30 fps Timeline asset.", Input = new Request { Path = "Assets/Timelines/Intro.playable", FrameRate = 30d } },
                new ToolExample { Description = "Preview replacing an existing Timeline asset.", Input = new Request { Path = "Assets/Timelines/Intro.playable", FrameRate = 60d, Overwrite = true }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        /// <summary>先冻结反射 API/path/existing identity，再按 dry-run 或一次 effect barrier 创建并 fresh 证明。</summary>
        public ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default(CancellationToken))
        {
            AssetToolSupport.EnsureEditorNotBusy("timeline.create");
            ct.ThrowIfCancellationRequested();
            request = request ?? new Request();
            var plan = TimelineToolSupport.PrepareCreate(request.Path, request.FrameRate, request.Overwrite);
            var effects = new List<TimelinePlannedEffect>
            {
                new TimelinePlannedEffect { Type = "timeline.create", Path = plan.Path, SideEffects = "asset-write,asset-import" }
            };
            if (ctx.DryRun)
            {
                return new ValueTask<Result>(new Result
                {
                    Created = false,
                    Overwritten = plan.Existing,
                    DryRun = true,
                    Timeline = TimelineToolSupport.Preview(plan),
                    PlannedEffects = effects
                });
            }

            ct.ThrowIfCancellationRequested();
            ctx.CommitEffect();
            TimelineToolSupport.Create(plan);
            var observed = TimelineToolSupport.ProveCreate(plan);
            return new ValueTask<Result>(new Result
            {
                Created = true,
                Overwritten = plan.Existing,
                DryRun = false,
                Timeline = observed,
                PlannedEffects = effects
            });
        }
    }
}
#endif
