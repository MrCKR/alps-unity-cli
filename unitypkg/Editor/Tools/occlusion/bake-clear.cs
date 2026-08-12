#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace UnityCli
{
    /// <summary>清除loaded scenes的baked Occlusion数据，并证明Umbra数据为空和scene持久化。</summary>
    public sealed class OcclusionBakeClearTool : IUnityCliTool<OcclusionBakeClearTool.Request, OcclusionBakeClearTool.Result>
    {
        public sealed class Request { }
        public sealed class Result
        {
            public bool Cleared { get; set; }
            public bool DryRun { get; set; }
            public OcclusionBakeSnapshotData Before { get; set; }
            public OcclusionBakeSnapshotData After { get; set; }
            public List<OcclusionSceneFileData> Scenes { get; set; }
            public List<OcclusionPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "occlusion.bake-clear",
            Summary = "Clear baked Occlusion Culling data and prove the current Umbra payload is empty.",
            Description = "Clears Unity StaticOcclusionCulling data for loaded saved scenes. Every target scene must be clean before the effect, so unrelated changes are never persisted. Framework dry-run returns the frozen scene and Umbra plan without clearing; an already empty payload is a verified no-op.",
            Group = "occlusion",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "occlusion-clear", "scene-modify" },
            Permissions = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Preview clearing baked Occlusion data.", Input = new Request(), Execution = new ToolExampleExecution { DryRun = true } },
                new ToolExample { Description = "Clear baked Occlusion data.", Input = new Request() }
            }
        };

        /// <summary>effect前冻结clean scene与Umbra snapshot，只跨一次barrier并以fresh zero readback完成。</summary>
        public ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default(CancellationToken))
        {
            OcclusionToolSupport.EnsureEditorReady("occlusion.bake-clear", true);
            OcclusionToolSupport.EnsureNoActiveBake("occlusion.bake-clear");
            ct.ThrowIfCancellationRequested();
            var scenes = OcclusionToolSupport.PrepareLoadedSceneFiles("occlusion.bake-clear");
            var before = OcclusionToolSupport.ReadSnapshot();
            var effects = new List<OcclusionPlannedEffect> { new OcclusionPlannedEffect { Type = "occlusion.bake-clear", Target = "open-scenes" } };
            if (ctx.DryRun || before.UmbraDataSizeBytes == 0)
                return new ValueTask<Result>(new Result { Cleared = false, DryRun = ctx.DryRun, Before = before, After = before, Scenes = scenes, PlannedEffects = effects });
            var contentProofs = OcclusionToolSupport.CaptureSceneContentProofs(scenes);

            ctx.CommitEffect();
            StaticOcclusionCulling.Clear();
            var observedScenes = OcclusionToolSupport.SaveAndProveSceneFiles(scenes, contentProofs, true);
            var after = OcclusionToolSupport.ReadSnapshot();
            if (after.UmbraDataSizeBytes != 0)
                throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion clear did not produce an empty Umbra terminal readback.", "Run occlusion.bake-status and inspect the target scenes before deciding whether to retry.");
            return new ValueTask<Result>(new Result { Cleared = true, DryRun = false, Before = before, After = after, Scenes = observedScenes, PlannedEffects = effects });
        }
    }
}
#endif
