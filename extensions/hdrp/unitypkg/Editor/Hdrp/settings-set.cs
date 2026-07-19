#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace UnityCli
{
    /// <summary>设置活动 HDRP pipeline asset 的一个属性。</summary>
    public sealed class HdrpSettingsSetTool : IUnityCliTool<HdrpSettingsSetTool.Request, HdrpSettingsSetTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Property { get; set; } [UnityCliRequired] public string Value { get; set; } }
        public sealed class Result { public string Command { get; set; } public string Property { get; set; } public string ObservedValue { get; set; } [UnityCliAnyJson] public Dictionary<string, object> Settings { get; set; } public bool DryRun { get; set; } public List<HdrpPlannedEffect> PlannedEffects { get; set; } public string LogPath { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "hdrp.settings.set", Summary = "Set one active HDRP pipeline asset setting.",
            Description = "Set one public property or SerializedProperty on the active HDRenderPipelineAsset. Supports --dry-run without saving the asset.",
            Group = "hdrp", Version = "1.0.0", DestructiveHint = true, SupportsDryRun = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.high-definition" }, SideEffects = new[] { "asset-write", "render-pipeline-settings" },
            Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Rename the HDRP asset", Input = new Request { Property = "name", Value = "Matrix HDRP" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("hdrp.settings.set");
            var pipeline = HdrpToolSupport.GetActivePipelineAsset();
            var plan = HdrpToolSupport.PrepareSetting(pipeline, request.Property, request.Value);
            var effects = new List<HdrpPlannedEffect> { new HdrpPlannedEffect { Type = "hdrp.settings.set", Property = request.Property, Value = request.Value } };
            var observedValue = request.Value;
            var pipelinePath = AssetDatabase.GetAssetPath(pipeline);
            if (!ctx.DryRun && string.IsNullOrEmpty(pipelinePath)) throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_PERSISTENCE_UNPROVABLE", "HDRP pipeline setting cannot be independently persisted because the active pipeline has no asset path.", "Use an asset-backed pipeline or add a contract strategy for transient pipeline settings.");
            if (!ctx.DryRun) ctx.CommitEffect();
            if (!ctx.DryRun)
            {
                HdrpToolSupport.ApplySetting(plan);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(pipelinePath, ImportAssetOptions.ForceUpdate);
                observedValue = HdrpToolSupport.VerifySettingReadback(plan, AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(pipelinePath));
            }
            var observedPipeline = ctx.DryRun ? pipeline : HdrpToolSupport.GetActivePipelineAsset();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "hdrp.settings.set", Property = request.Property, ObservedValue = observedValue, Settings = HdrpToolSupport.ReadSettings(observedPipeline), DryRun = ctx.DryRun, PlannedEffects = effects, LogPath = ctx.LogPath });
        }
    }
}
#endif
