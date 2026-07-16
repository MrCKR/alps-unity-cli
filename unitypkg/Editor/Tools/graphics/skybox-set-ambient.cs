#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.skybox-set-ambient — 设置环境光(mode/intensity/color)。</summary>
    public class GraphicsSkyboxSetAmbientTool : IUnityCliTool<GraphicsSkyboxSetAmbientTool.Request, GraphicsSkyboxSetAmbientTool.Result>
    {
        public sealed class Request { public string Mode { get; set; } public float? Intensity { get; set; } public string Color { get; set; } public string AmbientEquatorColor { get; set; } public string AmbientGroundColor { get; set; } public string AmbientSkyColor { get; set; } }
        public sealed class Result { public string AmbientMode { get; set; } public double AmbientIntensity { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-set-ambient", Summary = "Set ambient lighting mode, intensity and colors.", Description = "Set RenderSettings ambientMode, ambientIntensity, ambientLight and equator/ground/sky colors.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set ambient intensity", Input = new Request { Intensity = 1.2f } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(r.Mode) && System.Enum.TryParse<UnityEngine.Rendering.AmbientMode>(r.Mode, true, out var mode)) { RenderSettings.ambientMode = mode; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-ambient", Property = "ambientMode", Value = r.Mode }); }
            if (r.Intensity.HasValue) { RenderSettings.ambientIntensity = r.Intensity.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-ambient", Property = "ambientIntensity", Value = r.Intensity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (!string.IsNullOrEmpty(r.Color)) { var c = GraphicsToolSupport.ParseColor(r.Color); RenderSettings.ambientLight = c; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-ambient", Property = "ambientLight", Value = GraphicsToolSupport.ColorHtml(c) }); }
            if (!string.IsNullOrEmpty(r.AmbientEquatorColor)) RenderSettings.ambientEquatorColor = GraphicsToolSupport.ParseColor(r.AmbientEquatorColor);
            if (!string.IsNullOrEmpty(r.AmbientGroundColor)) RenderSettings.ambientGroundColor = GraphicsToolSupport.ParseColor(r.AmbientGroundColor);
            if (!string.IsNullOrEmpty(r.AmbientSkyColor)) RenderSettings.ambientSkyColor = GraphicsToolSupport.ParseColor(r.AmbientSkyColor);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { AmbientMode = RenderSettings.ambientMode.ToString(), AmbientIntensity = RenderSettings.ambientIntensity, PlannedEffects = effects });
        }
    }
}
#endif
