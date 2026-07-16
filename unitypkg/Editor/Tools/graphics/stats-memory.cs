#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>graphics.stats-memory — 读取渲染内存统计。</summary>
    public class GraphicsStatsMemoryTool : IUnityCliTool<GraphicsStatsMemoryTool.Request, GraphicsStatsMemoryTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public long UsedTextureMemory { get; set; } public long RenderTextureBytes { get; set; } public long UsedVertexBuffer { get; set; } public long UsedVertexBufferVRAM { get; set; } public long RenderTextureCount { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.stats-memory", Summary = "Read render memory statistics (texture memory, render texture bytes, vertex buffer).", Description = "Read-only: returns GPU memory usage from render stats.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read memory stats", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var s = GraphicsToolSupport.BuildRenderStats();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { UsedTextureMemory = s.UsedTextureMemory, RenderTextureBytes = s.RenderTextureBytes, UsedVertexBuffer = s.UsedVertexBuffer, UsedVertexBufferVRAM = s.UsedVertexBufferVRAM, RenderTextureCount = s.RenderTextureCount });
        }
    }
}
#endif
