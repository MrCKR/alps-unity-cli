#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// hdrp.settings — 读取或设置活动 HDRP 管线资产的公开属性与序列化属性。
    /// 保持旧 property/value 语义，迁移为自包含工具。
    /// </summary>
    public class HdrpSettingsTool : IUnityCliTool<HdrpSettingsTool.Request, HdrpSettingsTool.Result>
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
            public List<HdrpPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "hdrp.settings",
            Summary = "Read or set active HDRP pipeline asset settings.",
            Description = "Read or set public or serialized settings on the active High Definition Render Pipeline asset, such as renderScale or shadowDistance. Requires com.unity.render-pipelines.high-definition and an active HDRenderPipelineAsset.",
            Group = "hdrp",
            Version = "1.0.0",
            DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.render-pipelines.high-definition" },
            SideEffects = new[] { "asset-write", "render-pipeline-settings" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Read HDRP settings", Input = new Request() },
                new ToolExample { Description = "Set shadowDistance", Input = new Request { Property = "shadowDistance", Value = "500" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("hdrp.settings");
            var pipeline = HdrpToolSupport.GetActivePipelineAsset();
            var plannedEffects = new List<HdrpPlannedEffect>();

            if (!string.IsNullOrEmpty(request.Property) && !string.IsNullOrEmpty(request.Value))
            {
                var publicProperty = pipeline.GetType().GetProperty(request.Property, BindingFlags.Instance | BindingFlags.Public);
                if (publicProperty != null && publicProperty.CanWrite)
                {
                    try
                    {
                        var converted = HdrpToolSupport.ConvertValue(request.Value, publicProperty.PropertyType);
                        publicProperty.SetValue(pipeline, converted, null);
                        plannedEffects.Add(new HdrpPlannedEffect { Type = "hdrp.settings", Property = request.Property, Value = request.Value });
                    }
                    catch (Exception exception)
                    {
                        throw new UnityCliBridge.BridgeCommandException(
                            "E_HDRP_SETTING_SET_FAILED",
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
                                "E_HDRP_SETTING_NOT_FOUND",
                                "HDRP setting not found: " + request.Property,
                                "Run hdrp.settings without arguments to inspect readable settings.");
                        }
                        HdrpToolSupport.SetSerializedPropertyValue(property, request.Value);
                        serializedObject.ApplyModifiedProperties();
                        plannedEffects.Add(new HdrpPlannedEffect { Type = "hdrp.settings", Property = request.Property, Value = request.Value });
                    }
                }
            }

            if (plannedEffects.Count > 0)
            {
                AssetDatabase.SaveAssets();
            }

            return new Result
            {
                Command = "hdrp.settings",
                Action = plannedEffects.Count > 0 ? "set" : "get",
                Settings = HdrpToolSupport.ReadSettings(pipeline),
                PlannedEffects = plannedEffects,
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
