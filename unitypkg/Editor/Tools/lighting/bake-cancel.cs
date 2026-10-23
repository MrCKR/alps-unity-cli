#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>lighting.bake-cancel — 取消正在运行的光照烘焙。</summary>
    public class LightingBakeCancelTool : IUnityCliTool<LightingBakeCancelTool.Request, LightingBakeCancelTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool Baking { get; set; } public bool Cancelled { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "lighting.bake-cancel", Summary = "Cancel a running lightmap bake.", Description = "Cancel the active Lightmapping bake if one is running. Returns cancelled=true only when a bake was in progress.", Group = "lighting",DestructiveHint = false, SideEffects = new[] { "lightmapping-bake-cancel" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Cancel bake", Input = new Request { } } } };
        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var wasRunning = Lightmapping.isRunning;
            ctx.CommitEffect();
            if (wasRunning) Lightmapping.Cancel();
            var observed = await ctx.Wait.UntilStable(() => !Lightmapping.isRunning, System.TimeSpan.FromMilliseconds(ctx.TimeoutMs), ct: ct);
            if (!observed) throw new UnityCliBridge.BridgeCommandException("E_BAKE_CANCEL_TIMEOUT", "Lightmapping did not leave the baking state before timeout.", "Wait for the current bake to finish or inspect Unity Console before retrying.");
            var baking = Lightmapping.isRunning;
            return new Result { Baking = baking, Cancelled = wasRunning, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "lighting.bake-cancel", Property = "bake", Value = "cancelled" } } };
        }
    }
}
#endif
