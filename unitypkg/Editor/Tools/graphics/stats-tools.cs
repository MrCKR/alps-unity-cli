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
        public ToolDescriptor Describe() => new() { Name = "graphics.stats", Summary = "Read current render statistics (draw calls, batches, triangles, vertices, memory).", Description = "Read-only: returns a unified render stats snapshot from RenderStats (Unity 6) or UnityStats (2022.3).", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read render stats", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx) => new Result { Stats = GraphicsToolSupport.BuildRenderStats() };
    }

    /// <summary>graphics.stats-counters — 读取渲染统计计数器列表。</summary>
    public class GraphicsStatsCountersTool : IUnityCliTool<GraphicsStatsCountersTool.Request, GraphicsStatsCountersTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public List<GraphicsPlannedEffect> Counters { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.stats-counters", Summary = "Read render stats as a flat counter list (drawCalls, batches, triangles, vertices, setPass, renderTextures).", Description = "Read-only: returns render stats as key-value counter entries.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read counters", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var s = GraphicsToolSupport.BuildRenderStats();
            return new Result { Counters = new List<GraphicsPlannedEffect> {
                new GraphicsPlannedEffect { Property = "drawCalls", Value = s.DrawCalls.ToString() },
                new GraphicsPlannedEffect { Property = "batches", Value = s.Batches.ToString() },
                new GraphicsPlannedEffect { Property = "triangles", Value = s.Triangles.ToString() },
                new GraphicsPlannedEffect { Property = "vertices", Value = s.Vertices.ToString() },
                new GraphicsPlannedEffect { Property = "setPassCalls", Value = s.SetPassCalls.ToString() },
                new GraphicsPlannedEffect { Property = "renderTextureCount", Value = s.RenderTextureCount.ToString() },
            } };
        }
    }

    /// <summary>graphics.stats-memory — 读取渲染内存统计。</summary>
    public class GraphicsStatsMemoryTool : IUnityCliTool<GraphicsStatsMemoryTool.Request, GraphicsStatsMemoryTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public long UsedTextureMemory { get; set; } public long RenderTextureBytes { get; set; } public long UsedVertexBuffer { get; set; } public long UsedVertexBufferVRAM { get; set; } public long RenderTextureCount { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.stats-memory", Summary = "Read render memory statistics (texture memory, render texture bytes, vertex buffer).", Description = "Read-only: returns GPU memory usage from render stats.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read memory stats", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var s = GraphicsToolSupport.BuildRenderStats();
            return new Result { UsedTextureMemory = s.UsedTextureMemory, RenderTextureBytes = s.RenderTextureBytes, UsedVertexBuffer = s.UsedVertexBuffer, UsedVertexBufferVRAM = s.UsedVertexBufferVRAM, RenderTextureCount = s.RenderTextureCount };
        }
    }

    /// <summary>graphics.scene-debug — 设置 SceneView 着色/绘制模式。</summary>
    public class GraphicsSceneDebugTool : IUnityCliTool<GraphicsSceneDebugTool.Request, GraphicsSceneDebugTool.Result>
    {
        public sealed class Request { public string ShadingMode { get; set; } public string DrawMode { get; set; } }
        public sealed class Result { public bool SceneViewAvailable { get; set; } public string CurrentDrawMode { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.scene-debug", Summary = "Set the SceneView shading or draw mode for debugging.", Description = "Set the last active SceneView's camera mode. shadingMode (Unity 6 ShadingMode) or drawMode (DrawCameraMode enum).", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set wireframe mode", Input = new Request { DrawMode = "Wireframe" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(r.ShadingMode) && SceneView.lastActiveSceneView != null)
            {
#if UNITY_2023_2_OR_NEWER
                if (System.Enum.TryParse<SceneView.ShadingMode>(r.ShadingMode, true, out var sm)) { SceneView.lastActiveSceneView.SetSceneViewMode(sm, true); effects.Add(new GraphicsPlannedEffect { Type = "graphics.scene-debug", Property = "shadingMode", Value = r.ShadingMode }); }
#else
                if (System.Enum.TryParse<DrawCameraMode>(r.ShadingMode, true, out var dm)) { SceneView.lastActiveSceneView.cameraMode = SceneView.GetBuiltinCameraMode(dm); effects.Add(new GraphicsPlannedEffect { Type = "graphics.scene-debug", Property = "shadingMode", Value = r.ShadingMode }); }
#endif
            }
            if (!string.IsNullOrEmpty(r.DrawMode) && SceneView.lastActiveSceneView != null)
            {
                if (System.Enum.TryParse<DrawCameraMode>(r.DrawMode, true, out var parsed)) { SceneView.lastActiveSceneView.cameraMode = SceneView.GetBuiltinCameraMode(parsed); effects.Add(new GraphicsPlannedEffect { Type = "graphics.scene-debug", Property = "drawMode", Value = r.DrawMode }); }
            }
            return new Result { SceneViewAvailable = SceneView.lastActiveSceneView != null, CurrentDrawMode = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.cameraMode.name : null, PlannedEffects = effects };
        }
    }
}
#endif
