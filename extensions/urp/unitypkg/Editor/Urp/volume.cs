#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// urp.volume — 枚举、创建或配置 URP Volume 组件及其 profile effect。
    /// 保持旧 action 入口，迁移为强类型 Request/Result。
    /// </summary>
    public class UrpVolumeTool : IUnityCliTool<UrpVolumeTool.Request, UrpVolumeTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Name { get; set; }
            public string Action { get; set; }
            public string EffectType { get; set; }
            public bool? IsGlobal { get; set; }
            public double? Priority { get; set; }
            public double? Weight { get; set; }
            public string Property { get; set; }
            public string Value { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string Action { get; set; }
            public List<VolumeInfo> Volumes { get; set; }
            public GameObjectRef Target { get; set; }
            public bool? IsGlobal { get; set; }
            public string EffectType { get; set; }
            public string Property { get; set; }
            public string Value { get; set; }
            public string Note { get; set; }
            public List<UrpPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "urp.volume",
            Summary = "List, create, inspect, or modify URP Volume components and profile effects.",
            Description = "List, create, inspect, or modify URP Volume components and their profile effects. Requires com.unity.render-pipelines.universal and an active UniversalRenderPipelineAsset in Graphics Settings. action supports list, create, add-effect, set, and info.",
            Group = "urp",
            Version = "1.0.0",
            DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" },
            SideEffects = new[] { "scene-modify", "volume-profile-edit" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List URP volumes", Input = new Request { Action = "list" } },
                new ToolExample { Description = "Create a global URP volume", Input = new Request { Action = "create", Name = "GlobalVolume", IsGlobal = true } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.volume");
            UrpToolSupport.EnsureUrpAvailable();
            var action = (request.Action ?? "list").Trim().ToLowerInvariant();

            if (action == "list")
            {
                return new Result
                {
                    Command = "urp.volume",
                    Action = "list",
                    Volumes = UrpToolSupport.ListVolumes(null),
                    LogPath = ctx.LogPath
                };
            }

            if (action == "info")
            {
                return new Result
                {
                    Command = "urp.volume",
                    Action = "info",
                    Volumes = UrpToolSupport.ListVolumes(request.Target),
                    LogPath = ctx.LogPath
                };
            }

            if (action == "create")
            {
                return CreateVolume(request, ctx.LogPath);
            }

            if (action == "add-effect")
            {
                return AddEffect(request, ctx.LogPath);
            }

            if (action == "set")
            {
                return SetParameter(request, ctx.LogPath);
            }

            throw new UnityCliBridge.BridgeCommandException(
                "E_URP_VOLUME_ACTION_UNSUPPORTED",
                "Unsupported URP volume action: " + action,
                "Use list, create, add-effect, set, or info.");
        }

        private static Result CreateVolume(Request request, string logPath)
        {
            var volumeType = UrpToolSupport.VolumeType;
            if (volumeType == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_VOLUME_SYSTEM_NOT_FOUND",
                    "Volume system types not found. Requires Unity.RenderPipelines.Core.Runtime.",
                    "Ensure URP is fully imported before running this tool.");
            }

            var volumeName = request.Name ?? request.Target ?? "URPVolume";
            var isGlobal = request.IsGlobal ?? true;
            var gameObject = new GameObject(volumeName);
            var volumeComponent = gameObject.AddComponent(volumeType);

            var isGlobalField = volumeType.GetField("isGlobal", BindingFlags.Instance | BindingFlags.Public);
            if (isGlobalField != null) isGlobalField.SetValue(volumeComponent, isGlobal);

            if (request.Priority.HasValue)
            {
                var priorityField = volumeType.GetField("priority", BindingFlags.Instance | BindingFlags.Public);
                if (priorityField != null) priorityField.SetValue(volumeComponent, (float)request.Priority.Value);
            }

            if (request.Weight.HasValue)
            {
                var weightField = volumeType.GetField("weight", BindingFlags.Instance | BindingFlags.Public);
                if (weightField != null) weightField.SetValue(volumeComponent, (float)request.Weight.Value);
            }

            var volumeProfileType = UrpToolSupport.VolumeProfileType;
            if (volumeProfileType != null)
            {
                var profile = ScriptableObject.CreateInstance(volumeProfileType);
                profile.name = volumeName + "Profile";
                var profileProp = volumeType.GetProperty("profile", BindingFlags.Instance | BindingFlags.Public);
                if (profileProp != null && profileProp.CanWrite)
                {
                    profileProp.SetValue(volumeComponent, profile, null);
                }
                else
                {
                    var sharedProp = volumeType.GetProperty("sharedProfile", BindingFlags.Instance | BindingFlags.Public);
                    if (sharedProp != null && sharedProp.CanWrite)
                    {
                        sharedProp.SetValue(volumeComponent, profile, null);
                    }
                }
            }

            Undo.RegisterCreatedObjectUndo(gameObject, "UnityCli URP Volume Create");
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return new Result
            {
                Command = "urp.volume",
                Action = "create",
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                IsGlobal = isGlobal,
                PlannedEffects = new List<UrpPlannedEffect>
                {
                    new UrpPlannedEffect { Type = "urp.volume.create", Name = volumeName }
                },
                LogPath = logPath
            };
        }

        private static Result AddEffect(Request request, string logPath)
        {
            if (string.IsNullOrEmpty(request.Target))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_VOLUME_TARGET_REQUIRED",
                    "add-effect requires a target.",
                    "Pass the target Volume GameObject name, hierarchy path, or instance id.");
            }
            if (string.IsNullOrEmpty(request.EffectType))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_EFFECT_TYPE_REQUIRED",
                    "add-effect requires effectType.",
                    "Pass a VolumeComponent-derived type name.");
            }

            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "add-effect requires a target.");
            var volumeType = UrpToolSupport.VolumeType;
            var volumeComponent = volumeType == null ? null : gameObject.GetComponent(volumeType);
            if (volumeComponent == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_VOLUME_NOT_FOUND",
                    "Volume component not found on target: " + request.Target,
                    "Pass a target GameObject that has a Volume component.");
            }

            var effectType = UrpToolSupport.FindVolumeEffectType(request.EffectType);
            if (effectType == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_EFFECT_TYPE_NOT_FOUND",
                    "Volume effect type not found: " + request.EffectType,
                    "Ensure the effect type is correct and URP is fully imported.");
            }

            var profile = UrpToolSupport.GetVolumeProfile(volumeComponent);
            var volumeProfileType = UrpToolSupport.VolumeProfileType;
            if (profile != null && volumeProfileType != null)
            {
                var addMethod = volumeProfileType.GetMethod("Add", new[] { typeof(Type), typeof(bool) });
                if (addMethod != null)
                {
                    addMethod.Invoke(profile, new object[] { effectType, true });
                }
            }

            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return new Result
            {
                Command = "urp.volume",
                Action = "add-effect",
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                EffectType = request.EffectType,
                PlannedEffects = new List<UrpPlannedEffect>
                {
                    new UrpPlannedEffect { Type = "urp.volume.add-effect", EffectType = request.EffectType, Target = GameObjectToolSupport.HierarchyPath(gameObject) }
                },
                LogPath = logPath
            };
        }

        private static Result SetParameter(Request request, string logPath)
        {
            if (string.IsNullOrEmpty(request.Target))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_VOLUME_TARGET_REQUIRED",
                    "set requires a target.",
                    "Pass the target Volume GameObject name, hierarchy path, or instance id.");
            }
            if (string.IsNullOrEmpty(request.Property) || string.IsNullOrEmpty(request.Value))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_URP_VOLUME_PROPERTY_REQUIRED",
                    "set requires property and value.",
                    "Pass the parameter path in property and the new value as a string.");
            }

            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "set requires a target.");
            return new Result
            {
                Command = "urp.volume",
                Action = "set",
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                Property = request.Property,
                Value = request.Value,
                Note = "Volume parameter setting uses SerializedObject path; complex nested paths may require direct Unity editing.",
                PlannedEffects = new List<UrpPlannedEffect>
                {
                    new UrpPlannedEffect { Type = "urp.volume.set", Property = request.Property, Value = request.Value, Target = GameObjectToolSupport.HierarchyPath(gameObject) }
                },
                LogPath = logPath
            };
        }
    }
}
#endif
