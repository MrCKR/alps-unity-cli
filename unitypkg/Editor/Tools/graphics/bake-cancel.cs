#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>graphics.bake-cancel — 取消正在运行的光照烘焙。</summary>
    public class GraphicsBakeCancelTool : IUnityCliTool<GraphicsBakeCancelTool.Request, GraphicsBakeCancelTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool Baking { get; set; } public bool Cancelled { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.bake-cancel", Summary = "Cancel a running lightmap bake.", Description = "Cancel Lightmapping if running. Returns cancelled=true if a bake was in progress.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Cancel bake", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var wasRunning = Lightmapping.isRunning;
            if (wasRunning) Lightmapping.Cancel();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Baking = Lightmapping.isRunning, Cancelled = wasRunning, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.bake-cancel", Property = "bake", Value = "cancelled" } } });
        }
    }
}
#endif
