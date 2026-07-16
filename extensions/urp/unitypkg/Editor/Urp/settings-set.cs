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
        public sealed class Result { public string Command { get; set; } [UnityCliAnyJson] public Dictionary<string, object> Settings { get; set; } public bool DryRun { get; set; } public List<UrpPlannedEffect> PlannedEffects { get; set; } public string LogPath { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "urp.settings.set", Summary = "Set one active URP pipeline asset setting.",
            Description = "Set one public property or SerializedProperty on the active UniversalRenderPipelineAsset. Supports --dry-run without saving the asset.",
            Group = "urp", Version = "1.0.0", DestructiveHint = true, SupportsDryRun = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, SideEffects = new[] { "asset-write", "render-pipeline-settings" },
            Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Set URP render scale", Input = new Request { Property = "renderScale", Value = "1" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.settings.set");
            var pipeline = UrpToolSupport.GetActivePipelineAsset();
            var effects = new List<UrpPlannedEffect> { new UrpPlannedEffect { Type = "urp.settings.set", Property = request.Property, Value = request.Value } };
            var publicProperty = pipeline.GetType().GetProperty(request.Property, BindingFlags.Instance | BindingFlags.Public);
            if (publicProperty != null && publicProperty.CanWrite)
            {
                object converted;
                try { converted = UrpToolSupport.ConvertValue(request.Value, publicProperty.PropertyType); }
                catch (Exception exception) { throw new UnityCliBridge.BridgeCommandException("E_URP_SETTING_SET_FAILED", "Failed to convert " + request.Property + ": " + exception.Message, "Check the property name and value type."); }
                if (!context.DryRun) publicProperty.SetValue(pipeline, converted, null);
            }
            else
            {
                using (var serialized = new SerializedObject(pipeline))
                {
                    var property = serialized.FindProperty(request.Property) ?? serialized.FindProperty("m_" + char.ToUpper(request.Property[0]) + request.Property.Substring(1));
                    if (property == null) throw new UnityCliBridge.BridgeCommandException("E_URP_SETTING_NOT_FOUND", "URP setting not found: " + request.Property, "Run urp.settings.get to inspect readable settings.");
                    if (!context.DryRun) { UrpToolSupport.SetSerializedPropertyValue(property, request.Value); serialized.ApplyModifiedProperties(); }
                }
            }
            if (!context.DryRun) AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "urp.settings.set", Settings = UrpToolSupport.ReadSettings(pipeline), DryRun = context.DryRun, PlannedEffects = effects, LogPath = context.LogPath });
        }
    }
}
#endif
