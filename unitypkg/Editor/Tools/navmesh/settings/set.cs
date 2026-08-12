#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>写入 default agent 的批准 NavMesh settings，并以 fresh SerializedObject/file hash 证明。</summary>
    public sealed class NavMeshSettingsSetTool : IUnityCliTool<NavMeshSettingsSetTool.Request, NavMeshSettingsSetTool.Result>
    {
        public sealed class Request
        {
            public double? AgentRadius { get; set; }
            public double? AgentHeight { get; set; }
            public double? AgentSlope { get; set; }
            public double? AgentClimb { get; set; }
            public double? MinRegionArea { get; set; }
            public double? VoxelSize { get; set; }
            public bool? ManualVoxelSize { get; set; }
        }

        public sealed class Result
        {
            public bool Changed { get; set; }
            public bool DryRun { get; set; }
            public List<string> AppliedFields { get; set; }
            public NavMeshSettingsData Before { get; set; }
            public NavMeshSettingsData After { get; set; }
            public List<NavMeshPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "navmesh.settings.set",
            Summary = "Set declared default-agent NavMesh fields and prove exact ProjectSettings persistence.",
            Description = "Accepts any non-empty subset of agentRadius, agentHeight, agentSlope, agentClimb, minRegionArea, voxelSize, and manualVoxelSize. Values are fully validated before one effect barrier; voxelSize enables manual mode. Dry-run and exact no-op requests do not write ProjectSettings/NavMeshAreas.asset.",
            Group = "navmesh",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "project-settings-write" },
            Permissions = new[] { "project-settings-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Set agent radius and height.", Input = new Request { AgentRadius = 0.5d, AgentHeight = 2d } },
                new ToolExample { Description = "Preview manual voxel size.", Input = new Request { VoxelSize = 0.1d }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        /// <summary>冻结字段、当前文件 hash 与目标值；no-op/dry-run 不跨 effect barrier。</summary>
        public ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default(CancellationToken))
        {
            NavMeshToolSupport.EnsureEditorReady("navmesh.settings.set", true);
            NavMeshToolSupport.EnsureNoActiveBake("navmesh.settings.set");
            ct.ThrowIfCancellationRequested();
            request = request ?? new Request();
            var plan = NavMeshToolSupport.PrepareSettings(request.AgentRadius, request.AgentHeight, request.AgentSlope, request.AgentClimb, request.MinRegionArea, request.VoxelSize, request.ManualVoxelSize);
            var effects = new List<NavMeshPlannedEffect> { new NavMeshPlannedEffect { Type = "navmesh.settings.set", Target = plan.Before.SettingsPath } };
            if (ctx.DryRun || !plan.Changed)
                return new ValueTask<Result>(new Result { Changed = false, DryRun = ctx.DryRun, AppliedFields = plan.Fields, Before = plan.Before, After = plan.Desired, PlannedEffects = effects });

            ct.ThrowIfCancellationRequested();
            ctx.CommitEffect();
            NavMeshToolSupport.ApplySettings(plan);
            var after = NavMeshToolSupport.ProveSettings(plan);
            return new ValueTask<Result>(new Result { Changed = true, DryRun = false, AppliedFields = plan.Fields, Before = plan.Before, After = after, PlannedEffects = effects });
        }
    }
}
#endif
