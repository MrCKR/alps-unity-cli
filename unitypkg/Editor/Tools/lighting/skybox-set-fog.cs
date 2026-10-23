#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
namespace UnityCli
{
    public class LightingSkyboxSetFogTool : IUnityCliTool<LightingSkyboxSetFogTool.Request, LightingSkyboxSetFogTool.Result>
    {
        public sealed class Request { public bool? Enabled { get; set; } public string Mode { get; set; } public string Color { get; set; } public float? Density { get; set; } public float? StartDistance { get; set; } public float? EndDistance { get; set; } }
        public sealed class Result { public bool Fog { get; set; } public string FogMode { get; set; } public string FogColor { get; set; } public double FogDensity { get; set; } public double FogStartDistance { get; set; } public double FogEndDistance { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "lighting.skybox-set-fog", Summary = "Set fog.", Description = "Set RenderSettings fog values.", Group = "lighting",SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Enable linear fog", Input = new Request { Enabled = true, Mode = "Linear", Color = "#808080", StartDistance = 0f, EndDistance = 100f } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            FogMode? mode = string.IsNullOrEmpty(r.Mode) ? (FogMode?)null : GraphicsToolSupport.ParseEnum<FogMode>(r.Mode, "mode"); var color = string.IsNullOrEmpty(r.Color) ? (Color?)null : GraphicsToolSupport.ParseColor(r.Color);
            var density = r.Density.HasValue ? GraphicsToolSupport.ParseFinite(r.Density.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "density", 0f, 10f) : (float?)null; var start = r.StartDistance.HasValue ? GraphicsToolSupport.ParseFinite(r.StartDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "startDistance", 0f, float.MaxValue) : (float?)null; var end = r.EndDistance.HasValue ? GraphicsToolSupport.ParseFinite(r.EndDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "endDistance", 0f, float.MaxValue) : (float?)null;
            if (start.HasValue && end.HasValue && end.Value < start.Value) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_RANGE_INVALID", "endDistance must not be less than startDistance.", "Pass an ordered fog distance range.");
            if (!r.Enabled.HasValue && !mode.HasValue && !color.HasValue && !density.HasValue && !start.HasValue && !end.HasValue) return new System.Threading.Tasks.ValueTask<Result>(Read(new List<GraphicsPlannedEffect>()));
            ctx.CommitEffect(); if (r.Enabled.HasValue) RenderSettings.fog = r.Enabled.Value; if (mode.HasValue) RenderSettings.fogMode = mode.Value; if (color.HasValue) RenderSettings.fogColor = color.Value; if (density.HasValue) RenderSettings.fogDensity = density.Value; if (start.HasValue) RenderSettings.fogStartDistance = start.Value; if (end.HasValue) RenderSettings.fogEndDistance = end.Value;
            if ((r.Enabled.HasValue && RenderSettings.fog != r.Enabled.Value) || (mode.HasValue && RenderSettings.fogMode != mode.Value) || (color.HasValue && !GraphicsToolSupport.SameColor(RenderSettings.fogColor, color.Value)) || (density.HasValue && !Mathf.Approximately(RenderSettings.fogDensity, density.Value)) || (start.HasValue && !Mathf.Approximately(RenderSettings.fogStartDistance, start.Value)) || (end.HasValue && !Mathf.Approximately(RenderSettings.fogEndDistance, end.Value))) throw new UnityCliBridge.BridgeCommandException("E_FOG_STATE_MISMATCH", "Fog settings readback did not match the request.", "Retry after the editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(Read(new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "lighting.skybox-set-fog", Property = "fog", Value = "updated" } }));
        }
        private static Result Read(List<GraphicsPlannedEffect> effects) { return new Result { Fog = RenderSettings.fog, FogMode = RenderSettings.fogMode.ToString(), FogColor = GraphicsToolSupport.ColorHtml(RenderSettings.fogColor), FogDensity = RenderSettings.fogDensity, FogStartDistance = RenderSettings.fogStartDistance, FogEndDistance = RenderSettings.fogEndDistance, PlannedEffects = effects }; }
    }
}
#endif
