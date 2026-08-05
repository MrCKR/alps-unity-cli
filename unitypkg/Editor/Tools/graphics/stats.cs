#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>graphics.stats — 读取渲染统计(drawCalls/batches/triangles/vertices 等)。</summary>
    public class GraphicsStatsTool : IUnityCliTool<GraphicsStatsTool.Request, GraphicsStatsTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public RenderStatsInfo Stats { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.stats", Summary = "Read current render statistics (draw calls, batches, triangles, vertices, memory).", Description = "Read-only: returns a unified render stats snapshot from RenderStats (Unity 6) or UnityStats (2022.3).", Group = "graphics",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read render stats", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) => new System.Threading.Tasks.ValueTask<Result>(new Result { Stats = GraphicsToolSupport.BuildRenderStats() });
    }
}
#endif
