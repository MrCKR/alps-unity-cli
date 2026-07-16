#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.skybox-set-reflection — 设置反射(mode/bounces/intensity/resolution)。</summary>
    public class GraphicsSkyboxSetReflectionTool : IUnityCliTool<GraphicsSkyboxSetReflectionTool.Request, GraphicsSkyboxSetReflectionTool.Result>
    {
        public sealed class Request { public string Mode { get; set; } public int? Bounces { get; set; } public float? Intensity { get; set; } public string Resolution { get; set; } }
        public sealed class Result { public string DefaultReflectionMode { get; set; } public long ReflectionBounces { get; set; } public double ReflectionIntensity { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-set-reflection", Summary = "Set reflection mode, bounces, intensity and resolution.", Description = "Set RenderSettings defaultReflectionMode, reflectionBounces, reflectionIntensity, defaultReflectionResolution.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set reflection bounces", Input = new Request { Bounces = 3 } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(r.Mode) && System.Enum.TryParse<UnityEngine.Rendering.DefaultReflectionMode>(r.Mode, true, out var drm)) { RenderSettings.defaultReflectionMode = drm; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-reflection", Property = "defaultReflectionMode", Value = r.Mode }); }
            if (r.Bounces.HasValue) { RenderSettings.reflectionBounces = r.Bounces.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-reflection", Property = "reflectionBounces", Value = r.Bounces.Value.ToString() }); }
            if (r.Intensity.HasValue) { RenderSettings.reflectionIntensity = r.Intensity.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-reflection", Property = "reflectionIntensity", Value = r.Intensity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (!string.IsNullOrEmpty(r.Resolution))
            {
#if UNITY_2023_2_OR_NEWER
                if (System.Enum.TryParse<DefaultReflectionResolution>(r.Resolution, true, out var drr)) { RenderSettings.defaultReflectionResolution = drr; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-reflection", Property = "defaultReflectionResolution", Value = r.Resolution }); }
#else
                if (int.TryParse(r.Resolution, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ri)) { RenderSettings.defaultReflectionResolution = ri; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-reflection", Property = "defaultReflectionResolution", Value = r.Resolution }); }
#endif
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { DefaultReflectionMode = RenderSettings.defaultReflectionMode.ToString(), ReflectionBounces = RenderSettings.reflectionBounces, ReflectionIntensity = RenderSettings.reflectionIntensity, PlannedEffects = effects });
        }
    }
}
#endif
