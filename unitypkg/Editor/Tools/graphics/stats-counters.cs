#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>graphics.stats-counters — 读取渲染统计计数器列表。</summary>
    public class GraphicsStatsCountersTool : IUnityCliTool<GraphicsStatsCountersTool.Request, GraphicsStatsCountersTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public List<GraphicsPlannedEffect> Counters { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.stats-counters", Summary = "Read render stats as a flat counter list (drawCalls, batches, triangles, vertices, setPass, renderTextures).", Description = "Read-only: returns render stats as key-value counter entries.", Group = "graphics",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read counters", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var s = GraphicsToolSupport.BuildRenderStats();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Counters = new List<GraphicsPlannedEffect> {
                new GraphicsPlannedEffect { Property = "drawCalls", Value = s.DrawCalls.ToString() },
                new GraphicsPlannedEffect { Property = "batches", Value = s.Batches.ToString() },
                new GraphicsPlannedEffect { Property = "triangles", Value = s.Triangles.ToString() },
                new GraphicsPlannedEffect { Property = "vertices", Value = s.Vertices.ToString() },
                new GraphicsPlannedEffect { Property = "setPassCalls", Value = s.SetPassCalls.ToString() },
                new GraphicsPlannedEffect { Property = "renderTextureCount", Value = s.RenderTextureCount.ToString() },
            } });
        }
    }
}
#endif
