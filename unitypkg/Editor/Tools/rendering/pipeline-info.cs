#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>rendering.pipeline-info — 读取渲染管线摘要与 QualitySettings(只读)。</summary>
    public class RenderingPipelineInfoTool : IUnityCliTool<RenderingPipelineInfoTool.Request, RenderingPipelineInfoTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public GraphicsSummary Graphics { get; set; } public QualitySettingsInfo QualitySettings { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "rendering.pipeline-info", Summary = "Read active/default render pipeline identity and current quality settings.", Description = "Read-only in Edit and Play mode. Returns the active/default render pipeline identity plus current QualitySettings; use rendering.pipeline-settings for default pipeline writes and rendering.quality for quality writes.", Group = "rendering",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read pipeline info", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) => new System.Threading.Tasks.ValueTask<Result>(new Result { Graphics = GraphicsToolSupport.BuildGraphicsSummary(), QualitySettings = GraphicsToolSupport.BuildQualitySettings() });
    }
}
#endif
