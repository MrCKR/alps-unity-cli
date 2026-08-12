#if UNITY_EDITOR
#pragma warning disable 0618
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor.AI;

namespace UnityCli
{
    /// <summary>清除当前 open scenes 的 legacy baked NavMesh，并证明 world topology 为空。</summary>
    public sealed class NavMeshBakeClearTool : IUnityCliTool<NavMeshBakeClearTool.Request, NavMeshBakeClearTool.Result>
    {
        public sealed class Request { }
        public sealed class Result
        {
            public bool Cleared { get; set; }
            public bool DryRun { get; set; }
            public NavMeshTopologyData Before { get; set; }
            public NavMeshTopologyData After { get; set; }
            public List<NavMeshSceneFileData> Scenes { get; set; }
            public List<NavMeshPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "navmesh.bake-clear",
            Summary = "Clear the legacy baked NavMesh and prove the current NavMesh world is empty.",
            Description = "Clears only Unity's legacy baked NavMesh for loaded saved scenes, then saves and hashes those exact scenes. Every target scene must be clean before the effect, so unrelated changes are never persisted. Active NavMeshSurface data is rejected because global triangulation cannot prove a legacy-only clear; surface assets are never deleted.",
            Group = "navmesh",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "navmesh-clear", "scene-modify" },
            Permissions = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Preview clearing the legacy NavMesh.", Input = new Request(), Execution = new ToolExampleExecution { DryRun = true } },
                new ToolExample { Description = "Clear the legacy NavMesh.", Input = new Request() }
            }
        };

        /// <summary>读取 before topology 后只跨一次 barrier；返回前重新计算 triangulation。</summary>
        public ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default(CancellationToken))
        {
            NavMeshToolSupport.EnsureEditorReady("navmesh.bake-clear", true);
            NavMeshToolSupport.EnsureNoActiveBake("navmesh.bake-clear");
            if (UnityCliBridge.UnityCliNavMeshBakeCoordinator.HasActiveSurfaceData())
                throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_SURFACE_DATA_ACTIVE", "navmesh.bake-clear cannot prove a legacy-only clear while active NavMeshSurface data is registered.", "Disable or clear NavMeshSurface data, then retry the legacy clear.");
            ct.ThrowIfCancellationRequested();
            var scenes = NavMeshToolSupport.PrepareLoadedSceneFiles("navmesh.bake-clear");
            var before = NavMeshToolSupport.ReadTopology();
            var effects = new List<NavMeshPlannedEffect> { new NavMeshPlannedEffect { Type = "navmesh.bake-clear", Target = "open-scenes" } };
            if (ctx.DryRun || NavMeshToolSupport.TopologyEmpty(before))
                return new ValueTask<Result>(new Result { Cleared = false, DryRun = ctx.DryRun, Before = before, After = before, Scenes = scenes, PlannedEffects = effects });

            ctx.CommitEffect();
            NavMeshBuilder.ClearAllNavMeshes();
            var observedScenes = NavMeshToolSupport.SaveAndProveSceneFiles(scenes, true);
            var after = NavMeshToolSupport.ReadTopology();
            if (!NavMeshToolSupport.TopologyEmpty(after))
                throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "Legacy NavMesh clear did not produce an empty terminal readback.", "Run navmesh.bake-status and inspect the open scenes before deciding whether to retry.");
            return new ValueTask<Result>(new Result { Cleared = true, DryRun = false, Before = before, After = after, Scenes = observedScenes, PlannedEffects = effects });
        }
    }
}
#pragma warning restore 0618
#endif
