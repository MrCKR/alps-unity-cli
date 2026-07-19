#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
namespace UnityCli
{
    public class GraphicsPipelineQualityTool : IUnityCliTool<GraphicsPipelineQualityTool.Request, GraphicsPipelineQualityTool.Result>
    {
        public sealed class Request { public string QualityLevel { get; set; } }
        public sealed class Result { public QualitySettingsInfo QualitySettings { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.pipeline-quality", Summary = "Set the active quality level by name or index.", Description = "Set QualitySettings quality level.", Group = "graphics", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Select a quality level by name", Input = new Request { QualityLevel = "Medium" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(r.QualityLevel)) return new System.Threading.Tasks.ValueTask<Result>(new Result { QualitySettings = GraphicsToolSupport.BuildQualitySettings(), PlannedEffects = new List<GraphicsPlannedEffect>() });
            var level = GraphicsToolSupport.ResolveQualityLevel(r.QualityLevel); // immutable validated plan
            ctx.CommitEffect();
            QualitySettings.SetQualityLevel(level, true);
            AssetDatabase.SaveAssets();
            if (QualitySettings.GetQualityLevel() != level) throw new UnityCliBridge.BridgeCommandException("E_PIPELINE_QUALITY_STATE_MISMATCH", "Quality level readback did not match the request.", "Retry after the editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { QualitySettings = GraphicsToolSupport.BuildQualitySettings(), PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.pipeline-quality", Property = "qualityLevel", Value = level.ToString() } } });
        }
    }
}
#endif
