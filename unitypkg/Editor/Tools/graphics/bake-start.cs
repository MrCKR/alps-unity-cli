#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>graphics.bake-start — 异步启动光照烘焙。</summary>
    public class GraphicsBakeStartTool : IUnityCliTool<GraphicsBakeStartTool.Request, GraphicsBakeStartTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool Baking { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.bake-start", Summary = "Start asynchronous lightmap baking.", Description = "Start Lightmapping.BakeAsync. Returns error if a bake is already running.", Group = "graphics", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "bake" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Start baking", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (Lightmapping.isRunning) throw new UnityCliBridge.BridgeCommandException("E_BAKE_ALREADY_RUNNING", "Lightmapping bake is already running.", "Run tools run graphics.bake-status to inspect progress or graphics.bake-cancel to stop it.");
            Lightmapping.BakeAsync();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Baking = Lightmapping.isRunning, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.bake-start", Property = "bake", Value = "async" } } });
        }
    }
}
#endif
