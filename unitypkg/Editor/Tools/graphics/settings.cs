#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>
    /// graphics.settings — 读取/写入 QualitySettings + GraphicsSettings.defaultRenderPipeline。
    /// </summary>
    public class GraphicsSettingsTool : IUnityCliTool<GraphicsSettingsTool.Request, GraphicsSettingsTool.Result>
    {
        public sealed class Request
        {
            public int? AntiAliasing { get; set; }
            public float? ShadowDistance { get; set; }
            public int? VSyncCount { get; set; }
            public int? MasterTextureLimit { get; set; }
            public float? LodBias { get; set; }
            public int? MaximumLODLevel { get; set; }
            public string RenderPipeline { get; set; }
            public string Property { get; set; }
            public string Value { get; set; }
        }

        public sealed class Result
        {
            public GraphicsSummary Graphics { get; set; }
            public QualitySettingsInfo QualitySettings { get; set; }
            public List<GraphicsPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "graphics.settings", Summary = "Read and update graphics settings including render pipeline asset and quality fields.",
            Description = "Read or update QualitySettings fields and GraphicsSettings.defaultRenderPipeline. renderPipeline='built-in' or 'null' resets to built-in.",
            Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" },
            Target = "editor", PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read graphics settings", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (request.AntiAliasing.HasValue) { QualitySettings.antiAliasing = request.AntiAliasing.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = "antiAliasing", Value = request.AntiAliasing.Value.ToString() }); }
            if (request.ShadowDistance.HasValue) { QualitySettings.shadowDistance = request.ShadowDistance.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = "shadowDistance", Value = request.ShadowDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (request.VSyncCount.HasValue) { QualitySettings.vSyncCount = request.VSyncCount.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = "vSyncCount", Value = request.VSyncCount.Value.ToString() }); }
            if (request.MasterTextureLimit.HasValue) { QualitySettings.globalTextureMipmapLimit = request.MasterTextureLimit.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = "masterTextureLimit", Value = request.MasterTextureLimit.Value.ToString() }); }
            if (request.LodBias.HasValue) { QualitySettings.lodBias = request.LodBias.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = "lodBias", Value = request.LodBias.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (request.MaximumLODLevel.HasValue) { QualitySettings.maximumLODLevel = request.MaximumLODLevel.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = "maximumLODLevel", Value = request.MaximumLODLevel.Value.ToString() }); }
            if (!string.IsNullOrEmpty(request.RenderPipeline))
            {
                if (string.Equals(request.RenderPipeline, "null", System.StringComparison.OrdinalIgnoreCase) || string.Equals(request.RenderPipeline, "built-in", System.StringComparison.OrdinalIgnoreCase))
                {
                    GraphicsSettings.defaultRenderPipeline = null;
                }
                else
                {
                    var pipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(SceneToolSupport.NormalizeAssetPath(request.RenderPipeline));
                    if (pipeline == null)
                        throw new UnityCliBridge.BridgeCommandException("E_RENDER_PIPELINE_NOT_FOUND", "RenderPipelineAsset not found: " + request.RenderPipeline, "Pass an Assets/*.asset RenderPipelineAsset path or use --render-pipeline built-in.");
                    GraphicsSettings.defaultRenderPipeline = pipeline;
                }
                effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = "renderPipeline", Value = request.RenderPipeline });
            }
            if (!string.IsNullOrEmpty(request.Property) && !string.IsNullOrEmpty(request.Value))
            {
                ApplyGenericGraphicsSetting(request.Property, request.Value, effects);
            }
            if (effects.Count > 0) AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Graphics = GraphicsToolSupport.BuildGraphicsSummary(), QualitySettings = GraphicsToolSupport.BuildQualitySettings(), PlannedEffects = effects });
        }

        private static void ApplyGenericGraphicsSetting(string property, string value, List<GraphicsPlannedEffect> effects)
        {
            if (property == "vSyncCount") QualitySettings.vSyncCount = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            else if (property == "antiAliasing") QualitySettings.antiAliasing = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            else if (property == "shadowDistance") QualitySettings.shadowDistance = GameObjectToolSupport.ParseFloat(value, property);
            else throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_SETTING_UNSUPPORTED", "Unsupported graphics setting: " + property, "Use named flags such as --anti-aliasing, --shadow-distance or --render-pipeline.");
            effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = property, Value = value });
        }
    }
}
#endif
