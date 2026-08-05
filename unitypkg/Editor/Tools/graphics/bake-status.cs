#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>graphics.bake-status — 读取光照烘焙状态。</summary>
    public class GraphicsBakeStatusTool : IUnityCliTool<GraphicsBakeStatusTool.Request, GraphicsBakeStatusTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool Baking { get; set; } public double Progress { get; set; } public BakeModeInfo Mode { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.bake-status", Summary = "Read lightmap baking progress and mode.", Description = "Read-only: returns baking state, progress (0-1), and lighting mode.", Group = "graphics",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Check bake status", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
#if UNITY_2023_2_OR_NEWER
            var progress = Lightmapping.bakeProgress;
            var mode = Lightmapping.lightingMode;
            var modeStr = mode == LightingMode.Baked ? "baked" : mode == LightingMode.Mixed ? "mixed" : mode == LightingMode.Realtime ? "realtime" : mode.ToString();
#else
            var progress = Lightmapping.buildProgress;
            var modeStr = "unknown";
            var ls = Lightmapping.GetLightingSettingsForScene(SceneManager.GetActiveScene());
            if (ls != null) modeStr = ls.mixedBakeMode.ToString();
#endif
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Baking = Lightmapping.isRunning, Progress = progress, Mode = new BakeModeInfo { LightingMode = modeStr, GiWorkflowMode = (long)Lightmapping.giWorkflowMode } });
        }
    }
}
#endif
