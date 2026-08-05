#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
namespace UnityCli
{
    public class GraphicsSkyboxSetAmbientTool : IUnityCliTool<GraphicsSkyboxSetAmbientTool.Request, GraphicsSkyboxSetAmbientTool.Result>
    {
        public sealed class Request { public string Mode { get; set; } public float? Intensity { get; set; } public string Color { get; set; } public string AmbientEquatorColor { get; set; } public string AmbientGroundColor { get; set; } public string AmbientSkyColor { get; set; } }
        public sealed class Result { public string AmbientMode { get; set; } public double AmbientIntensity { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-set-ambient", Summary = "Set ambient lighting.", Description = "Set RenderSettings ambient values.", Group = "graphics",SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set flat ambient lighting", Input = new Request { Mode = "Flat", Intensity = 1f, Color = "#808080" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            AmbientMode? mode = string.IsNullOrEmpty(r.Mode) ? (AmbientMode?)null : GraphicsToolSupport.ParseEnum<AmbientMode>(r.Mode, "mode");
            var intensity = r.Intensity.HasValue ? GraphicsToolSupport.ParseFinite(r.Intensity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "intensity", 0f, 8f) : (float?)null;
            Color? color = string.IsNullOrEmpty(r.Color) ? (Color?)null : GraphicsToolSupport.ParseColor(r.Color); Color? equator = string.IsNullOrEmpty(r.AmbientEquatorColor) ? (Color?)null : GraphicsToolSupport.ParseColor(r.AmbientEquatorColor); Color? ground = string.IsNullOrEmpty(r.AmbientGroundColor) ? (Color?)null : GraphicsToolSupport.ParseColor(r.AmbientGroundColor); Color? sky = string.IsNullOrEmpty(r.AmbientSkyColor) ? (Color?)null : GraphicsToolSupport.ParseColor(r.AmbientSkyColor);
            if (!mode.HasValue && !intensity.HasValue && !color.HasValue && !equator.HasValue && !ground.HasValue && !sky.HasValue) return new System.Threading.Tasks.ValueTask<Result>(new Result { AmbientMode = RenderSettings.ambientMode.ToString(), AmbientIntensity = RenderSettings.ambientIntensity, PlannedEffects = new List<GraphicsPlannedEffect>() });
            ctx.CommitEffect(); if (mode.HasValue) RenderSettings.ambientMode = mode.Value; if (intensity.HasValue) RenderSettings.ambientIntensity = intensity.Value; if (color.HasValue) RenderSettings.ambientLight = color.Value; if (equator.HasValue) RenderSettings.ambientEquatorColor = equator.Value; if (ground.HasValue) RenderSettings.ambientGroundColor = ground.Value; if (sky.HasValue) RenderSettings.ambientSkyColor = sky.Value;
            if ((mode.HasValue && RenderSettings.ambientMode != mode.Value) || (intensity.HasValue && !Mathf.Approximately(RenderSettings.ambientIntensity, intensity.Value)) || (color.HasValue && !GraphicsToolSupport.SameColor(RenderSettings.ambientLight, color.Value)) || (equator.HasValue && !GraphicsToolSupport.SameColor(RenderSettings.ambientEquatorColor, equator.Value)) || (ground.HasValue && !GraphicsToolSupport.SameColor(RenderSettings.ambientGroundColor, ground.Value)) || (sky.HasValue && !GraphicsToolSupport.SameColor(RenderSettings.ambientSkyColor, sky.Value))) throw new UnityCliBridge.BridgeCommandException("E_AMBIENT_STATE_MISMATCH", "Ambient settings readback did not match the request.", "Retry after the editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { AmbientMode = RenderSettings.ambientMode.ToString(), AmbientIntensity = RenderSettings.ambientIntensity, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.skybox-set-ambient", Property = "ambient", Value = "updated" } } });
        }
    }
}
#endif
