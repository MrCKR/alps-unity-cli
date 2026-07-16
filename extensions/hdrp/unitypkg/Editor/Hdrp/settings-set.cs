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
        public sealed class Result { public string Command { get; set; } [UnityCliAnyJson] public Dictionary<string, object> Settings { get; set; } public bool DryRun { get; set; } public List<HdrpPlannedEffect> PlannedEffects { get; set; } public string LogPath { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "hdrp.settings.set", Summary = "Set one active HDRP pipeline asset setting.",
            Description = "Set one public property or SerializedProperty on the active HDRenderPipelineAsset. Supports --dry-run without saving the asset.",
            Group = "hdrp", Version = "1.0.0", DestructiveHint = true, SupportsDryRun = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.high-definition" }, SideEffects = new[] { "asset-write", "render-pipeline-settings" },
            Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Rename the HDRP asset", Input = new Request { Property = "name", Value = "Matrix HDRP" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("hdrp.settings.set");
            var pipeline = HdrpToolSupport.GetActivePipelineAsset();
            var effects = new List<HdrpPlannedEffect> { new HdrpPlannedEffect { Type = "hdrp.settings.set", Property = request.Property, Value = request.Value } };
            var publicProperty = pipeline.GetType().GetProperty(request.Property, BindingFlags.Instance | BindingFlags.Public);
            if (publicProperty != null && publicProperty.CanWrite)
            {
                object converted;
                try { converted = HdrpToolSupport.ConvertValue(request.Value, publicProperty.PropertyType); }
                catch (Exception exception) { throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_SET_FAILED", "Failed to convert " + request.Property + ": " + exception.Message, "Check the property name and value type."); }
                if (!context.DryRun) publicProperty.SetValue(pipeline, converted, null);
            }
            else
            {
                using (var serialized = new SerializedObject(pipeline))
                {
                    var property = serialized.FindProperty(request.Property) ?? serialized.FindProperty("m_" + char.ToUpper(request.Property[0]) + request.Property.Substring(1));
                    if (property == null) throw new UnityCliBridge.BridgeCommandException("E_HDRP_SETTING_NOT_FOUND", "HDRP setting not found: " + request.Property, "Run hdrp.settings.get to inspect readable settings.");
                    if (!context.DryRun) { HdrpToolSupport.SetSerializedPropertyValue(property, request.Value); serialized.ApplyModifiedProperties(); }
                }
            }
            if (!context.DryRun) AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "hdrp.settings.set", Settings = HdrpToolSupport.ReadSettings(pipeline), DryRun = context.DryRun, PlannedEffects = effects, LogPath = context.LogPath });
        }
    }
}
#endif
