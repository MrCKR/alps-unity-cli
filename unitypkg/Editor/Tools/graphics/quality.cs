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
            var level = string.IsNullOrEmpty(request.QualityLevel) ? (int?)null : GraphicsToolSupport.ResolveQualityLevel(request.QualityLevel);
            var anisotropic = string.IsNullOrEmpty(request.AnisotropicFiltering) ? (AnisotropicFiltering?)null : GraphicsToolSupport.ParseEnum<AnisotropicFiltering>(request.AnisotropicFiltering, "anisotropicFiltering");
            var shadows = string.IsNullOrEmpty(request.Shadows) ? (ShadowQuality?)null : GraphicsToolSupport.ParseEnum<ShadowQuality>(request.Shadows, "shadows");
            var shadowResolution = string.IsNullOrEmpty(request.ShadowResolution) ? (ShadowResolution?)null : GraphicsToolSupport.ParseEnum<ShadowResolution>(request.ShadowResolution, "shadowResolution");
            if (request.ShadowDistance.HasValue) GraphicsToolSupport.ParseFinite(request.ShadowDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "shadowDistance", 0f, float.MaxValue);
            if (request.LodBias.HasValue) GraphicsToolSupport.ParseFinite(request.LodBias.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "lodBias", 0.01f, 100f);
            if (request.VSyncCount.HasValue && (request.VSyncCount.Value < 0 || request.VSyncCount.Value > 4)) throw new UnityCliBridge.BridgeCommandException("E_QUALITY_RANGE_INVALID", "vSyncCount must be from 0 to 4.", "Pass a vSyncCount from 0 to 4.");
            if (request.AntiAliasing.HasValue && request.AntiAliasing.Value != 0 && request.AntiAliasing.Value != 2 && request.AntiAliasing.Value != 4 && request.AntiAliasing.Value != 8) throw new UnityCliBridge.BridgeCommandException("E_QUALITY_RANGE_INVALID", "antiAliasing must be 0, 2, 4, or 8.", "Pass a supported antiAliasing value.");
            if (request.MasterTextureLimit.HasValue && !GraphicsToolSupport.IsValidTextureLod(request.MasterTextureLimit.Value)) throw new UnityCliBridge.BridgeCommandException("E_QUALITY_RANGE_INVALID", "masterTextureLimit must be from 0 to 8.", "Pass a masterTextureLimit from 0 to 8.");
            if (request.MaximumLODLevel.HasValue && !GraphicsToolSupport.IsValidMaximumLod(request.MaximumLODLevel.Value)) throw new UnityCliBridge.BridgeCommandException("E_QUALITY_RANGE_INVALID", "maximumLODLevel must be from 0 to 100000.", "Pass a non-negative maximumLODLevel.");
            var hasWrite = level.HasValue || request.VSyncCount.HasValue || request.AntiAliasing.HasValue || request.ShadowDistance.HasValue || request.MasterTextureLimit.HasValue || request.LodBias.HasValue || request.MaximumLODLevel.HasValue || anisotropic.HasValue || shadows.HasValue || shadowResolution.HasValue || request.RealtimeReflectionProbes.HasValue || request.SoftParticles.HasValue;
            if (hasWrite) ctx.CommitEffect();
            if (level.HasValue)
            {
                QualitySettings.SetQualityLevel(level.Value, true);
                effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "qualityLevel", Value = level.Value.ToString() });
            }
            if (request.VSyncCount.HasValue) { QualitySettings.vSyncCount = request.VSyncCount.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "vSyncCount", Value = request.VSyncCount.Value.ToString() }); }
            if (request.AntiAliasing.HasValue) { QualitySettings.antiAliasing = request.AntiAliasing.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "antiAliasing", Value = request.AntiAliasing.Value.ToString() }); }
            if (request.ShadowDistance.HasValue) { QualitySettings.shadowDistance = request.ShadowDistance.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "shadowDistance", Value = request.ShadowDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (request.MasterTextureLimit.HasValue) { QualitySettings.globalTextureMipmapLimit = request.MasterTextureLimit.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "masterTextureLimit", Value = request.MasterTextureLimit.Value.ToString() }); }
            if (request.LodBias.HasValue) { QualitySettings.lodBias = request.LodBias.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "lodBias", Value = request.LodBias.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (request.MaximumLODLevel.HasValue) { QualitySettings.maximumLODLevel = request.MaximumLODLevel.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "maximumLODLevel", Value = request.MaximumLODLevel.Value.ToString() }); }
            if (anisotropic.HasValue) { QualitySettings.anisotropicFiltering = anisotropic.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "anisotropicFiltering", Value = request.AnisotropicFiltering }); }
            if (shadows.HasValue) { QualitySettings.shadows = shadows.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "shadows", Value = request.Shadows }); }
            if (shadowResolution.HasValue) { QualitySettings.shadowResolution = shadowResolution.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "shadowResolution", Value = request.ShadowResolution }); }
            if (request.RealtimeReflectionProbes.HasValue) { QualitySettings.realtimeReflectionProbes = request.RealtimeReflectionProbes.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "realtimeReflectionProbes", Value = request.RealtimeReflectionProbes.Value.ToString().ToLowerInvariant() }); }
            if (request.SoftParticles.HasValue) { QualitySettings.softParticles = request.SoftParticles.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.quality", Property = "softParticles", Value = request.SoftParticles.Value.ToString().ToLowerInvariant() }); }
            if (effects.Count > 0) AssetDatabase.SaveAssets();
            var observed = GraphicsToolSupport.BuildQualitySettings();
            if ((level.HasValue && observed.CurrentLevel != level.Value) || (request.VSyncCount.HasValue && observed.VSyncCount != request.VSyncCount.Value) || (request.AntiAliasing.HasValue && observed.AntiAliasing != request.AntiAliasing.Value) || (request.ShadowDistance.HasValue && !Mathf.Approximately((float)observed.ShadowDistance, request.ShadowDistance.Value)) || (request.MasterTextureLimit.HasValue && observed.MasterTextureLimit != request.MasterTextureLimit.Value) || (request.LodBias.HasValue && !Mathf.Approximately((float)observed.LodBias, request.LodBias.Value)) || (request.MaximumLODLevel.HasValue && observed.MaximumLODLevel != request.MaximumLODLevel.Value) || (anisotropic.HasValue && observed.AnisotropicFiltering != anisotropic.Value.ToString()) || (shadows.HasValue && observed.Shadows != shadows.Value.ToString()) || (shadowResolution.HasValue && observed.ShadowResolution != shadowResolution.Value.ToString()) || (request.RealtimeReflectionProbes.HasValue && observed.RealtimeReflectionProbes != request.RealtimeReflectionProbes.Value) || (request.SoftParticles.HasValue && observed.SoftParticles != request.SoftParticles.Value)) throw new UnityCliBridge.BridgeCommandException("E_QUALITY_STATE_MISMATCH", "Quality settings readback did not match the request.", "Retry after the editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Graphics = GraphicsToolSupport.BuildGraphicsSummary(), QualitySettings = observed, PlannedEffects = effects });
        }
    }
}
#endif
