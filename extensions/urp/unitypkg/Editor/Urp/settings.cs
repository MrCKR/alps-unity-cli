#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// urp.settings — 读取或设置活动 URP 管线资产的公开属性与序列化属性。
    /// 保持旧 property/value 语义，输出强类型 Result。
    /// </summary>
    public class UrpSettingsTool : IUnityCliTool<UrpSettingsTool.Request, UrpSettingsTool.Result>
    {
        public sealed class Request
        {
            public string Property { get; set; }
            public string Value { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string Action { get; set; }
            public Dictionary<string, object> Settings { get; set; }
            public List<UrpPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "urp.settings",
            Summary = "Read or set active URP pipeline asset settings.",
            Description = "Read or set public or serialized settings on the active Universal Render Pipeline asset, such as renderScale, supportsHDR, msaaSampleCount, or shadowDistance. Requires com.unity.render-pipelines.universal and an active UniversalRenderPipelineAsset.",
            Group = "urp",
            Version = "1.0.0",
            DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" },
            SideEffects = new[] { "asset-write", "render-pipeline-settings" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Read URP settings", Input = new Request() },
                new ToolExample { Description = "Set renderScale", Input = new Request { Property = "renderScale", Value = "1.5" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.settings");
            var pipeline = UrpToolSupport.GetActivePipelineAsset();
            var plannedEffects = new List<UrpPlannedEffect>();

            if (!string.IsNullOrEmpty(request.Property) && !string.IsNullOrEmpty(request.Value))
            {
                var publicProperty = pipeline.GetType().GetProperty(request.Property, BindingFlags.Instance | BindingFlags.Public);
                if (publicProperty != null && publicProperty.CanWrite)
                {
                    try
                    {
                        var converted = UrpToolSupport.ConvertValue(request.Value, publicProperty.PropertyType);
                        publicProperty.SetValue(pipeline, converted, null);
                        plannedEffects.Add(new UrpPlannedEffect { Type = "urp.settings", Property = request.Property, Value = request.Value });
                    }
                    catch (Exception exception)
                    {
                        throw new UnityCliBridge.BridgeCommandException(
                            "E_URP_SETTING_SET_FAILED",
                            "Failed to set " + request.Property + ": " + exception.Message,
                            "Check the property name and value type.");
                    }
                }
                else
                {
                    using (var serializedObject = new SerializedObject(pipeline))
                    {
                        var property = serializedObject.FindProperty(request.Property)
                            ?? serializedObject.FindProperty("m_" + char.ToUpper(request.Property[0]) + request.Property.Substring(1));
                        if (property == null)
                        {
                            throw new UnityCliBridge.BridgeCommandException(
                                "E_URP_SETTING_NOT_FOUND",
                                "URP setting not found: " + request.Property,
                                "Run urp.settings without arguments to inspect readable settings.");
                        }
                        UrpToolSupport.SetSerializedPropertyValue(property, request.Value);
                        serializedObject.ApplyModifiedProperties();
                        plannedEffects.Add(new UrpPlannedEffect { Type = "urp.settings", Property = request.Property, Value = request.Value });
                    }
                }
            }

            if (plannedEffects.Count > 0)
            {
                AssetDatabase.SaveAssets();
            }

            return new Result
            {
                Command = "urp.settings",
                Action = plannedEffects.Count > 0 ? "set" : "get",
                Settings = UrpToolSupport.ReadSettings(pipeline),
                PlannedEffects = plannedEffects,
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
