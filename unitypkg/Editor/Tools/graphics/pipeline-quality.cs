#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.pipeline-quality — 设置 QualityLevel。</summary>
    public class GraphicsPipelineQualityTool : IUnityCliTool<GraphicsPipelineQualityTool.Request, GraphicsPipelineQualityTool.Result>
    {
        public sealed class Request { public string QualityLevel { get; set; } }
        public sealed class Result { public QualitySettingsInfo QualitySettings { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.pipeline-quality", Summary = "Set the active quality level by name or index.", Description = "Set QualitySettings quality level. Accepts a numeric index or quality level name.", Group = "graphics", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set quality to Ultra", Input = new Request { QualityLevel = "Ultra" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(r.QualityLevel)) { var level = GraphicsToolSupport.ResolveQualityLevel(r.QualityLevel); QualitySettings.SetQualityLevel(level, true); AssetDatabase.SaveAssets(); effects.Add(new GraphicsPlannedEffect { Type = "graphics.pipeline-quality", Property = "qualityLevel", Value = level.ToString() }); }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { QualitySettings = GraphicsToolSupport.BuildQualitySettings(), PlannedEffects = effects });
        }
    }
}
#endif
