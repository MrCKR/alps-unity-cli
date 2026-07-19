#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace UnityCli
{
    /// <summary>设置活动 URP pipeline asset 的一个属性。</summary>
    public sealed class UrpSettingsSetTool : IUnityCliTool<UrpSettingsSetTool.Request, UrpSettingsSetTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Property { get; set; } [UnityCliRequired] public string Value { get; set; } }
        public sealed class Result { public string Command { get; set; } public string Property { get; set; } public string ObservedValue { get; set; } [UnityCliAnyJson] public Dictionary<string, object> Settings { get; set; } public bool DryRun { get; set; } public List<UrpPlannedEffect> PlannedEffects { get; set; } public string LogPath { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "urp.settings.set", Summary = "Set one active URP pipeline asset setting.",
            Description = "Set one public property or SerializedProperty on the active UniversalRenderPipelineAsset. Supports --dry-run without saving the asset.",
            Group = "urp", Version = "1.0.0", DestructiveHint = true, SupportsDryRun = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, SideEffects = new[] { "asset-write", "render-pipeline-settings" },
            Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Set URP render scale", Input = new Request { Property = "renderScale", Value = "1" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.settings.set");
            var pipeline = UrpToolSupport.GetActivePipelineAsset();
            var plan = UrpToolSupport.PrepareSetting(pipeline, request.Property, request.Value);
            var pipelinePath = AssetDatabase.GetAssetPath(pipeline);
            if (!ctx.DryRun && string.IsNullOrEmpty(pipelinePath)) throw new UnityCliBridge.BridgeCommandException("E_URP_SETTING_PERSISTENCE_UNPROVABLE", "URP pipeline setting cannot be independently persisted because the active pipeline has no asset path.", "Use an asset-backed pipeline or add a contract strategy for transient pipeline settings.");
            if (!ctx.DryRun) ctx.CommitEffect();
            var effects = new List<UrpPlannedEffect> { new UrpPlannedEffect { Type = "urp.settings.set", Property = request.Property, Value = request.Value } };
            var observedValue = request.Value;
            if (!ctx.DryRun)
            {
                UrpToolSupport.ApplySetting(plan);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(pipelinePath, ImportAssetOptions.ForceUpdate);
                var reloadedPipeline = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(pipelinePath);
                observedValue = UrpToolSupport.VerifySettingReadback(plan, reloadedPipeline);
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "urp.settings.set", Property = request.Property, ObservedValue = observedValue, Settings = UrpToolSupport.ReadSettings(ctx.DryRun ? pipeline : UrpToolSupport.GetActivePipelineAsset()), DryRun = ctx.DryRun, PlannedEffects = effects, LogPath = ctx.LogPath });
        }
    }
}
#endif
