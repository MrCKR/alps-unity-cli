#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// vfx.control - 控制 VisualEffect 播放状态与随机种子。
    /// </summary>
    public class VfxControlTool : IUnityCliTool<VfxControlTool.Request, VfxControlTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Target { get; set; }
            public string Action { get; set; }
            public double? Rate { get; set; }
            public int? Seed { get; set; }
            public bool? ResetSeedOnPlay { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public string Action { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.control",
            Summary = "Play, stop, pause, reinitialize, retime, or reseed a VisualEffect component.",
            Description = "Control a VisualEffect component on a target GameObject. action supports play, stop, pause, reinit, rate, and seed. rate uses the reflected playRate property; seed updates startSeed and optionally resetSeedOnPlay.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "component-state-edit" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Play the effect", Input = new Request { Target = "Effects/Emitter", Action = "play" } },
                new ToolExample { Description = "Set playback rate", Input = new Request { Target = "Effects/Emitter", Action = "rate", Rate = 0.5 } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            VfxToolSupport.EnsureEditorReady("vfx.control");
            var visualEffectType = VfxToolSupport.RequireVisualEffectType();
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "VFX control requires target.");
            var component = gameObject.GetComponent(visualEffectType);
            if (component == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_COMPONENT_NOT_FOUND",
                    "VisualEffect component not found on target: " + GameObjectToolSupport.HierarchyPath(gameObject),
                    "Pass a target with VisualEffect.");
            }

            var action = (request.Action ?? "play").Trim().ToLowerInvariant();
            var effects = new List<VfxPlannedEffect>();
            switch (action)
            {
                case "play":
                    Invoke(visualEffectType, component, "Play");
                    effects.Add(VfxToolSupport.Effect("vfx.control", "action", "play"));
                    break;
                case "stop":
                    Invoke(visualEffectType, component, "Stop");
                    effects.Add(VfxToolSupport.Effect("vfx.control", "action", "stop"));
                    break;
                case "pause":
                    var pauseProperty = visualEffectType.GetProperty("pause", BindingFlags.Instance | BindingFlags.Public);
                    if (pauseProperty != null && pauseProperty.CanWrite)
                    {
                        var current = (bool)(pauseProperty.GetValue(component, null) ?? false);
                        pauseProperty.SetValue(component, !current, null);
                    }
                    effects.Add(VfxToolSupport.Effect("vfx.control", "action", "pause"));
                    break;
                case "reinit":
                    Invoke(visualEffectType, component, "Reinit");
                    effects.Add(VfxToolSupport.Effect("vfx.control", "action", "reinit"));
                    break;
                case "rate":
                    if (!request.Rate.HasValue)
                    {
                        throw new UnityCliBridge.BridgeCommandException("E_VFX_RATE_REQUIRED", "Rate action requires rate.", "Pass a playback rate (1.0 = normal).");
                    }
                    var playRateProperty = visualEffectType.GetProperty("playRate", BindingFlags.Instance | BindingFlags.Public);
                    if (playRateProperty != null && playRateProperty.CanWrite)
                    {
                        playRateProperty.SetValue(component, (float)request.Rate.Value, null);
                    }
                    effects.Add(VfxToolSupport.Effect("vfx.control", "playRate", VfxToolSupport.Number(request.Rate.Value)));
                    break;
                case "seed":
                    if (!request.Seed.HasValue)
                    {
                        throw new UnityCliBridge.BridgeCommandException("E_VFX_SEED_REQUIRED", "Seed action requires seed.", "Pass an integer seed.");
                    }
                    var seedProperty = visualEffectType.GetProperty("startSeed", BindingFlags.Instance | BindingFlags.Public);
                    if (seedProperty != null && seedProperty.CanWrite)
                    {
                        seedProperty.SetValue(component, request.Seed.Value, null);
                    }
                    if (request.ResetSeedOnPlay.HasValue)
                    {
                        var resetProperty = visualEffectType.GetProperty("resetSeedOnPlay", BindingFlags.Instance | BindingFlags.Public);
                        if (resetProperty != null && resetProperty.CanWrite)
                        {
                            resetProperty.SetValue(component, request.ResetSeedOnPlay.Value, null);
                        }
                    }
                    effects.Add(VfxToolSupport.Effect("vfx.control", "startSeed", request.Seed.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    break;
                default:
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_VFX_CONTROL_ACTION_UNSUPPORTED",
                        "Unsupported VFX control action: " + action,
                        "Use play, stop, pause, reinit, rate, or seed.");
            }

            EditorUtility.SetDirty(component);
            VfxToolSupport.MarkSceneDirty(gameObject);
            return new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                Action = action,
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            };
        }

        private static void Invoke(Type owner, Component component, string methodName)
        {
            var method = owner.GetMethod(methodName, Type.EmptyTypes);
            if (method != null)
            {
                method.Invoke(component, null);
            }
        }
    }
}
#endif
