#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.pipeline-info — 读取渲染管线摘要与 QualitySettings(只读)。</summary>
    public class GraphicsPipelineInfoTool : IUnityCliTool<GraphicsPipelineInfoTool.Request, GraphicsPipelineInfoTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public GraphicsSummary Graphics { get; set; } public QualitySettingsInfo QualitySettings { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.pipeline-info", Summary = "Read the active render pipeline asset and quality settings.", Description = "Read-only: returns render pipeline summary and current QualitySettings.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read pipeline info", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) => new System.Threading.Tasks.ValueTask<Result>(new Result { Graphics = GraphicsToolSupport.BuildGraphicsSummary(), QualitySettings = GraphicsToolSupport.BuildQualitySettings() });
    }
}
#endif
