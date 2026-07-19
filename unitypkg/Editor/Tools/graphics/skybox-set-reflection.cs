#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
namespace UnityCli
{
    public class GraphicsSkyboxSetReflectionTool : IUnityCliTool<GraphicsSkyboxSetReflectionTool.Request, GraphicsSkyboxSetReflectionTool.Result>
    {
        public sealed class Request { public string Mode { get; set; } public int? Bounces { get; set; } public float? Intensity { get; set; } public string Resolution { get; set; } }
        public sealed class Result { public string DefaultReflectionMode { get; set; } public long ReflectionBounces { get; set; } public double ReflectionIntensity { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-set-reflection", Summary = "Set reflection settings.", Description = "Set RenderSettings reflection values.", Group = "graphics", Version = "1.0.0", SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Configure skybox reflections", Input = new Request { Mode = "Skybox", Bounces = 1, Intensity = 1f, Resolution = "128" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            DefaultReflectionMode? mode = string.IsNullOrEmpty(r.Mode) ? (DefaultReflectionMode?)null : GraphicsToolSupport.ParseEnum<DefaultReflectionMode>(r.Mode, "mode"); if (r.Bounces.HasValue && (r.Bounces.Value < 0 || r.Bounces.Value > 100)) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_RANGE_INVALID", "Invalid reflection bounces.", "Pass bounces from 0 to 100."); var intensity = r.Intensity.HasValue ? GraphicsToolSupport.ParseFinite(r.Intensity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "intensity", 0f, 8f) : (float?)null;
            int? resolution = null;
            if (!string.IsNullOrEmpty(r.Resolution))
            {
                int parsed;
                if (!int.TryParse(r.Resolution, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out parsed) || parsed < 16 || parsed > 8192 || (parsed & (parsed - 1)) != 0)
                    throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_RANGE_INVALID", "Invalid reflection resolution.", "Pass a power-of-two resolution from 16 to 8192.");
                resolution = parsed;
            }
            if (!mode.HasValue && !r.Bounces.HasValue && !intensity.HasValue && !resolution.HasValue) return new System.Threading.Tasks.ValueTask<Result>(Read(new List<GraphicsPlannedEffect>()));
            ctx.CommitEffect(); if (mode.HasValue) RenderSettings.defaultReflectionMode = mode.Value; if (r.Bounces.HasValue) RenderSettings.reflectionBounces = r.Bounces.Value; if (intensity.HasValue) RenderSettings.reflectionIntensity = intensity.Value; if (resolution.HasValue) RenderSettings.defaultReflectionResolution = resolution.Value;
            if ((mode.HasValue && RenderSettings.defaultReflectionMode != mode.Value) || (r.Bounces.HasValue && RenderSettings.reflectionBounces != r.Bounces.Value) || (intensity.HasValue && !Mathf.Approximately(RenderSettings.reflectionIntensity, intensity.Value)) || (resolution.HasValue && RenderSettings.defaultReflectionResolution != resolution.Value)) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_STATE_MISMATCH", "Reflection settings readback did not match the request.", "Retry after the editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(Read(new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.skybox-set-reflection", Property = "reflection", Value = "updated" } }));
        }
        private static Result Read(List<GraphicsPlannedEffect> effects) { return new Result { DefaultReflectionMode = RenderSettings.defaultReflectionMode.ToString(), ReflectionBounces = RenderSettings.reflectionBounces, ReflectionIntensity = RenderSettings.reflectionIntensity, PlannedEffects = effects }; }
    }
}
#endif
