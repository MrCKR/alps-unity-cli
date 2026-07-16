#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.pipeline-settings — 设置 GraphicsSettings.defaultRenderPipeline。</summary>
    public class GraphicsPipelineSettingsTool : IUnityCliTool<GraphicsPipelineSettingsTool.Request, GraphicsPipelineSettingsTool.Result>
    {
        public sealed class Request { public string RenderPipeline { get; set; } }
        public sealed class Result { public GraphicsSummary Graphics { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.pipeline-settings", Summary = "Set the default render pipeline asset for the project.", Description = "Set GraphicsSettings.defaultRenderPipeline. Pass 'built-in' or 'null' to reset to built-in.", Group = "graphics", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set URP pipeline", Input = new Request { RenderPipeline = "Assets/Settings/URP.asset" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(r.RenderPipeline))
            {
                if (string.Equals(r.RenderPipeline, "null", System.StringComparison.OrdinalIgnoreCase) || string.Equals(r.RenderPipeline, "built-in", System.StringComparison.OrdinalIgnoreCase))
                    GraphicsSettings.defaultRenderPipeline = null;
                else
                {
                    var pipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(SceneToolSupport.NormalizeAssetPath(r.RenderPipeline));
                    if (pipeline == null) throw new UnityCliBridge.BridgeCommandException("E_RENDER_PIPELINE_NOT_FOUND", "RenderPipelineAsset not found: " + r.RenderPipeline, "Pass an Assets/*.asset RenderPipelineAsset path or use --render-pipeline built-in.");
                    GraphicsSettings.defaultRenderPipeline = pipeline;
                }
                effects.Add(new GraphicsPlannedEffect { Type = "graphics.pipeline-settings", Property = "renderPipeline", Value = r.RenderPipeline });
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Graphics = GraphicsToolSupport.BuildGraphicsSummary(), PlannedEffects = effects });
        }
    }
}
#endif
