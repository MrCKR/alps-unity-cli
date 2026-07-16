#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>
    /// graphics.quality — 读取/写入 QualitySettings(qualityLevel/vSync/antiAliasing/shadowDistance 等)。
    /// </summary>
    public class GraphicsQualityTool : IUnityCliTool<GraphicsQualityTool.Request, GraphicsQualityTool.Result>
    {
        public sealed class Request
        {
            public string QualityLevel { get; set; }
            public int? VSyncCount { get; set; }
            public int? AntiAliasing { get; set; }
            public float? ShadowDistance { get; set; }
            public int? MasterTextureLimit { get; set; }
            public float? LodBias { get; set; }
            public int? MaximumLODLevel { get; set; }
            public string AnisotropicFiltering { get; set; }
            public string Shadows { get; set; }
            public string ShadowResolution { get; set; }
            public bool? RealtimeReflectionProbes { get; set; }
            public bool? SoftParticles { get; set; }
        }

        public sealed class Result
        {
            public GraphicsSummary Graphics { get; set; }
            public QualitySettingsInfo QualitySettings { get; set; }
            public List<GraphicsPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "graphics.quality", Summary = "Read and update Unity QualitySettings (quality level, vSync, anti-aliasing, shadows, LOD).",
            Description = "Read or update QualitySettings. Pass qualityLevel (name or index) and individual settings to update; omit all to read current values.",
            Group = "graphics", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "settings-write" },
            Target = "editor", PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read quality settings", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(request.QualityLevel))
            {
                var level = GraphicsToolSupport.ResolveQualityLevel(request.QualityLevel);
                QualitySettings.SetQualityLevel(level, true);
                effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "qualityLevel", Value = level.ToString() });
            }
            if (request.VSyncCount.HasValue) { QualitySettings.vSyncCount = request.VSyncCount.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "vSyncCount", Value = request.VSyncCount.Value.ToString() }); }
            if (request.AntiAliasing.HasValue) { QualitySettings.antiAliasing = request.AntiAliasing.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "antiAliasing", Value = request.AntiAliasing.Value.ToString() }); }
            if (request.ShadowDistance.HasValue) { QualitySettings.shadowDistance = request.ShadowDistance.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "shadowDistance", Value = request.ShadowDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (request.MasterTextureLimit.HasValue) { QualitySettings.globalTextureMipmapLimit = request.MasterTextureLimit.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "masterTextureLimit", Value = request.MasterTextureLimit.Value.ToString() }); }
            if (request.LodBias.HasValue) { QualitySettings.lodBias = request.LodBias.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "lodBias", Value = request.LodBias.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (request.MaximumLODLevel.HasValue) { QualitySettings.maximumLODLevel = request.MaximumLODLevel.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "maximumLODLevel", Value = request.MaximumLODLevel.Value.ToString() }); }
            if (!string.IsNullOrEmpty(request.AnisotropicFiltering)) { QualitySettings.anisotropicFiltering = (AnisotropicFiltering)System.Enum.Parse(typeof(AnisotropicFiltering), request.AnisotropicFiltering, true); effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "anisotropicFiltering", Value = request.AnisotropicFiltering }); }
            if (!string.IsNullOrEmpty(request.Shadows)) { QualitySettings.shadows = (ShadowQuality)System.Enum.Parse(typeof(ShadowQuality), request.Shadows, true); effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "shadows", Value = request.Shadows }); }
            if (!string.IsNullOrEmpty(request.ShadowResolution)) { QualitySettings.shadowResolution = (ShadowResolution)System.Enum.Parse(typeof(ShadowResolution), request.ShadowResolution, true); effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "shadowResolution", Value = request.ShadowResolution }); }
            if (request.RealtimeReflectionProbes.HasValue) { QualitySettings.realtimeReflectionProbes = request.RealtimeReflectionProbes.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "realtimeReflectionProbes", Value = request.RealtimeReflectionProbes.Value.ToString().ToLowerInvariant() }); }
            if (request.SoftParticles.HasValue) { QualitySettings.softParticles = request.SoftParticles.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "softParticles", Value = request.SoftParticles.Value.ToString().ToLowerInvariant() }); }
            if (effects.Count > 0) AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Graphics = GraphicsToolSupport.BuildGraphicsSummary(), QualitySettings = GraphicsToolSupport.BuildQualitySettings(), PlannedEffects = effects });
        }
    }
}
#endif
