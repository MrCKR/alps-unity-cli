#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.skybox-set-fog — 设置雾(enabled/mode/color/density/distances)。</summary>
    public class GraphicsSkyboxSetFogTool : IUnityCliTool<GraphicsSkyboxSetFogTool.Request, GraphicsSkyboxSetFogTool.Result>
    {
        public sealed class Request { public bool? Enabled { get; set; } public string Mode { get; set; } public string Color { get; set; } public float? Density { get; set; } public float? StartDistance { get; set; } public float? EndDistance { get; set; } }
        public sealed class Result { public bool Fog { get; set; } public string FogMode { get; set; } public string FogColor { get; set; } public double FogDensity { get; set; } public double FogStartDistance { get; set; } public double FogEndDistance { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-set-fog", Summary = "Set fog enabled, mode, color, density and distance range.", Description = "Set RenderSettings fog, fogMode, fogColor, fogDensity, fogStartDistance, fogEndDistance.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Enable fog", Input = new Request { Enabled = true } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (r.Enabled.HasValue) { RenderSettings.fog = r.Enabled.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-fog", Property = "fog", Value = r.Enabled.Value.ToString().ToLowerInvariant() }); }
            if (!string.IsNullOrEmpty(r.Mode) && System.Enum.TryParse<FogMode>(r.Mode, true, out var fm)) { RenderSettings.fogMode = fm; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-fog", Property = "fogMode", Value = r.Mode }); }
            if (!string.IsNullOrEmpty(r.Color)) { var c = GraphicsToolSupport.ParseColor(r.Color); RenderSettings.fogColor = c; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-fog", Property = "fogColor", Value = GraphicsToolSupport.ColorHtml(c) }); }
            if (r.Density.HasValue) { RenderSettings.fogDensity = r.Density.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-fog", Property = "fogDensity", Value = r.Density.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (r.StartDistance.HasValue) { RenderSettings.fogStartDistance = r.StartDistance.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-fog", Property = "fogStartDistance", Value = r.StartDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (r.EndDistance.HasValue) { RenderSettings.fogEndDistance = r.EndDistance.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-fog", Property = "fogEndDistance", Value = r.EndDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Fog = RenderSettings.fog, FogMode = RenderSettings.fogMode.ToString(), FogColor = GraphicsToolSupport.ColorHtml(RenderSettings.fogColor), FogDensity = RenderSettings.fogDensity, FogStartDistance = RenderSettings.fogStartDistance, FogEndDistance = RenderSettings.fogEndDistance, PlannedEffects = effects });
        }
    }
}
#endif
