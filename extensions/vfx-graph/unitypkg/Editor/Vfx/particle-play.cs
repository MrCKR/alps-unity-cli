#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// vfx.particle-play - 控制 ParticleSystem 播放生命周期。
    /// </summary>
    public class VfxParticlePlayTool : IUnityCliTool<VfxParticlePlayTool.Request, VfxParticlePlayTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Target { get; set; }
            public string Action { get; set; }
            public bool? WithChildren { get; set; }
            public string StopBehavior { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public bool IsPlaying { get; set; }
            public bool IsEmitting { get; set; }
            public bool IsPaused { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.particle-play",
            Summary = "Play, stop, pause, or clear a ParticleSystem with optional child propagation and stop behavior.",
            Description = "Control ParticleSystem playback for an existing target. action supports play, stop, pause, and clear. stopBehavior is only used when action=stop.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "particle-playback-edit" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Play with children", Input = new Request { Target = "Effects/Dust", Action = "play", WithChildren = true } },
                new ToolExample { Description = "Stop and clear", Input = new Request { Target = "Effects/Dust", Action = "stop", StopBehavior = "StopEmittingAndClear" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            VfxToolSupport.EnsureEditorReady("vfx.particle-play");
            var particleSystem = VfxToolSupport.ResolveParticleSystem(request.Target);
            if (particleSystem == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_PARTICLE_SYSTEM_NOT_FOUND",
                    "ParticleSystem not found: " + request.Target,
                    "Pass a GameObject name / hierarchy path with a ParticleSystem component.");
            }

            var action = (request.Action ?? "play").Trim().ToLowerInvariant();
            var withChildren = request.WithChildren ?? true;
            var effects = new List<VfxPlannedEffect>();
            switch (action)
            {
                case "play":
                    particleSystem.Play(withChildren);
                    effects.Add(VfxToolSupport.Effect("vfx.particle-play", "action", "play"));
                    break;
                case "stop":
                    var stopBehavior = string.IsNullOrWhiteSpace(request.StopBehavior)
                        ? ParticleSystemStopBehavior.StopEmitting
                        : VfxToolSupport.ParseEnum<ParticleSystemStopBehavior>(request.StopBehavior, "E_PARTICLE_STOP_BEHAVIOR_INVALID", "Use a valid ParticleSystemStopBehavior value.");
                    particleSystem.Stop(withChildren, stopBehavior);
                    effects.Add(VfxToolSupport.Effect("vfx.particle-play", "action", "stop"));
                    break;
                case "pause":
                    particleSystem.Pause(withChildren);
                    effects.Add(VfxToolSupport.Effect("vfx.particle-play", "action", "pause"));
                    break;
                case "clear":
                    particleSystem.Clear(withChildren);
                    effects.Add(VfxToolSupport.Effect("vfx.particle-play", "action", "clear"));
                    break;
                default:
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_INVALID_ACTION",
                        "Invalid particle action: " + action,
                        "Use play, stop, pause, or clear.");
            }

            return new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(particleSystem.gameObject),
                IsPlaying = particleSystem.isPlaying,
                IsEmitting = particleSystem.isEmitting,
                IsPaused = particleSystem.isPaused,
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
