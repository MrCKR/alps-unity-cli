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
            var hasWrite = request.AntiAliasing.HasValue || request.ShadowDistance.HasValue || request.VSyncCount.HasValue || request.MasterTextureLimit.HasValue || request.LodBias.HasValue || request.MaximumLODLevel.HasValue || !string.IsNullOrEmpty(request.RenderPipeline) || (!string.IsNullOrEmpty(request.Property) && !string.IsNullOrEmpty(request.Value));
            if (request.ShadowDistance.HasValue) GraphicsToolSupport.ParseFinite(request.ShadowDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "shadowDistance", 0f, float.MaxValue);
            if (request.LodBias.HasValue) GraphicsToolSupport.ParseFinite(request.LodBias.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "lodBias", 0.01f, 100f);
            if (request.VSyncCount.HasValue && (request.VSyncCount.Value < 0 || request.VSyncCount.Value > 4)) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_SETTING_INPUT_INVALID", "vSyncCount must be from 0 to 4.", "Pass a vSyncCount from 0 to 4.");
            if (request.AntiAliasing.HasValue && request.AntiAliasing.Value != 0 && request.AntiAliasing.Value != 2 && request.AntiAliasing.Value != 4 && request.AntiAliasing.Value != 8) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_SETTING_INPUT_INVALID", "antiAliasing must be 0, 2, 4, or 8.", "Pass a supported antiAliasing value.");
            if (request.MasterTextureLimit.HasValue && !GraphicsToolSupport.IsValidTextureLod(request.MasterTextureLimit.Value)) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_SETTING_INPUT_INVALID", "masterTextureLimit must be from 0 to 8.", "Pass a masterTextureLimit from 0 to 8.");
            if (request.MaximumLODLevel.HasValue && !GraphicsToolSupport.IsValidMaximumLod(request.MaximumLODLevel.Value)) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_SETTING_INPUT_INVALID", "maximumLODLevel must be from 0 to 100000.", "Pass a non-negative maximumLODLevel.");
            RenderPipelineAsset requestedPipeline = null; var resetPipeline = false; string requestedPipelinePath = null; string requestedPipelineGuid = null;
            if (!string.IsNullOrEmpty(request.RenderPipeline)) { resetPipeline = string.Equals(request.RenderPipeline, "null", System.StringComparison.OrdinalIgnoreCase) || string.Equals(request.RenderPipeline, "built-in", System.StringComparison.OrdinalIgnoreCase); if (!resetPipeline) { requestedPipelinePath = GraphicsToolSupport.NormalizeAssetPathRequired(request.RenderPipeline, ".asset"); requestedPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(requestedPipelinePath); if (requestedPipeline == null) throw new UnityCliBridge.BridgeCommandException("E_RENDER_PIPELINE_NOT_FOUND", "RenderPipelineAsset not found: " + request.RenderPipeline, "Pass an existing Assets/*.asset RenderPipelineAsset path."); requestedPipelineGuid = AssetDatabase.AssetPathToGUID(requestedPipelinePath); if (string.IsNullOrEmpty(requestedPipelineGuid)) throw new UnityCliBridge.BridgeCommandException("E_RENDER_PIPELINE_NOT_FOUND", "RenderPipelineAsset has no stable GUID: " + request.RenderPipeline, "Pass an imported Assets/*.asset RenderPipelineAsset path."); } }
            if (!string.IsNullOrEmpty(request.Property) || !string.IsNullOrEmpty(request.Value)) { if (string.IsNullOrEmpty(request.Property) || string.IsNullOrEmpty(request.Value)) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_SETTING_INPUT_INVALID", "property and value must be provided together.", "Use a supported setting member and value."); if (request.Property != "vSyncCount" && request.Property != "antiAliasing" && request.Property != "shadowDistance" && request.Property != "masterTextureLimit" && request.Property != "lodBias" && request.Property != "maximumLODLevel") throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_SETTING_UNSUPPORTED", "Unsupported graphics setting: " + request.Property, "Use named graphics setting flags."); if (request.Property == "shadowDistance") GraphicsToolSupport.ParseFinite(request.Value, request.Property, 0f, float.MaxValue); else if (request.Property == "lodBias") GraphicsToolSupport.ParseFinite(request.Value, request.Property, 0.01f, 100f); else { int parsed; if (!int.TryParse(request.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out parsed) || (request.Property == "vSyncCount" && (parsed < 0 || parsed > 4)) || (request.Property == "antiAliasing" && parsed != 0 && parsed != 2 && parsed != 4 && parsed != 8) || (request.Property == "masterTextureLimit" && !GraphicsToolSupport.IsValidTextureLod(parsed)) || (request.Property == "maximumLODLevel" && !GraphicsToolSupport.IsValidMaximumLod(parsed))) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_SETTING_INPUT_INVALID", "Invalid value: " + request.Value, "Pass a value within the supported range."); } }
            if (hasWrite) ctx.CommitEffect();
            if (request.AntiAliasing.HasValue) { QualitySettings.antiAliasing = request.AntiAliasing.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = "antiAliasing", Value = request.AntiAliasing.Value.ToString() }); }
            if (request.ShadowDistance.HasValue) { QualitySettings.shadowDistance = request.ShadowDistance.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = "shadowDistance", Value = request.ShadowDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (request.VSyncCount.HasValue) { QualitySettings.vSyncCount = request.VSyncCount.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = "vSyncCount", Value = request.VSyncCount.Value.ToString() }); }
            if (request.MasterTextureLimit.HasValue) { QualitySettings.globalTextureMipmapLimit = request.MasterTextureLimit.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = "masterTextureLimit", Value = request.MasterTextureLimit.Value.ToString() }); }
            if (request.LodBias.HasValue) { QualitySettings.lodBias = request.LodBias.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = "lodBias", Value = request.LodBias.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (request.MaximumLODLevel.HasValue) { QualitySettings.maximumLODLevel = request.MaximumLODLevel.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = "maximumLODLevel", Value = request.MaximumLODLevel.Value.ToString() }); }
            if (!string.IsNullOrEmpty(request.RenderPipeline))
            {
                GraphicsSettings.defaultRenderPipeline = resetPipeline ? null : requestedPipeline;
                effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = "renderPipeline", Value = request.RenderPipeline });
            }
            if (!string.IsNullOrEmpty(request.Property) && !string.IsNullOrEmpty(request.Value))
            {
                ApplyGenericGraphicsSetting(request.Property, request.Value, effects);
            }
            if (effects.Count > 0) AssetDatabase.SaveAssets();
            RenderPipelineAsset observedPipeline = null;
            if (!string.IsNullOrEmpty(request.RenderPipeline) && !resetPipeline) observedPipeline = GraphicsToolSupport.ReloadAsset<RenderPipelineAsset>(requestedPipelinePath, requestedPipelineGuid);
            var observed = GraphicsToolSupport.BuildQualitySettings();
            if ((request.AntiAliasing.HasValue && observed.AntiAliasing != request.AntiAliasing.Value) || (request.VSyncCount.HasValue && observed.VSyncCount != request.VSyncCount.Value) || (request.ShadowDistance.HasValue && !Mathf.Approximately((float)observed.ShadowDistance, request.ShadowDistance.Value)) || (request.MasterTextureLimit.HasValue && observed.MasterTextureLimit != request.MasterTextureLimit.Value) || (request.LodBias.HasValue && !Mathf.Approximately((float)observed.LodBias, request.LodBias.Value)) || (request.MaximumLODLevel.HasValue && observed.MaximumLODLevel != request.MaximumLODLevel.Value) || (!string.IsNullOrEmpty(request.Property) && !GenericSettingMatches(observed, request.Property, request.Value)) || (!string.IsNullOrEmpty(request.RenderPipeline) && GraphicsSettings.defaultRenderPipeline != observedPipeline)) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_SETTINGS_STATE_MISMATCH", "Graphics settings readback did not match the request.", "Retry after the editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Graphics = GraphicsToolSupport.BuildGraphicsSummary(), QualitySettings = observed, PlannedEffects = effects });
        }

        private static bool GenericSettingMatches(QualitySettingsInfo observed, string property, string value)
        {
            if (property == "vSyncCount") return observed.VSyncCount == int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            if (property == "antiAliasing") return observed.AntiAliasing == int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            if (property == "shadowDistance") return Mathf.Approximately((float)observed.ShadowDistance, GameObjectToolSupport.ParseFloat(value, property));
            if (property == "masterTextureLimit") return observed.MasterTextureLimit == int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            if (property == "lodBias") return Mathf.Approximately((float)observed.LodBias, GameObjectToolSupport.ParseFloat(value, property));
            return property == "maximumLODLevel" && observed.MaximumLODLevel == int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void ApplyGenericGraphicsSetting(string property, string value, List<GraphicsPlannedEffect> effects)
        {
            if (property == "vSyncCount") QualitySettings.vSyncCount = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            else if (property == "antiAliasing") QualitySettings.antiAliasing = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            else if (property == "shadowDistance") QualitySettings.shadowDistance = GameObjectToolSupport.ParseFloat(value, property);
            else if (property == "masterTextureLimit") QualitySettings.globalTextureMipmapLimit = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            else if (property == "lodBias") QualitySettings.lodBias = GameObjectToolSupport.ParseFloat(value, property);
            else if (property == "maximumLODLevel") QualitySettings.maximumLODLevel = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            else throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_SETTING_UNSUPPORTED", "Unsupported graphics setting: " + property, "Use named flags such as --anti-aliasing, --shadow-distance or --render-pipeline.");
            effects.Add(new GraphicsPlannedEffect { Type = "graphics.settings", Property = property, Value = value });
        }
    }
}
#endif
