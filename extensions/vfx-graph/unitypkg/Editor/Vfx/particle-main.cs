#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// vfx.particle-main - 修改 ParticleSystem 主模块。
    /// </summary>
    public class VfxParticleMainTool : IUnityCliTool<VfxParticleMainTool.Request, VfxParticleMainTool.Result>
    {
        public sealed class Request : ParticleMainFields
        {
            [UnityCliRequired]
            public string Target { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public VfxParticleSystemInfo ParticleSystem { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.particle-main",
            Summary = "Update ParticleSystem main module fields such as lifetime, speed, size, looping, and simulation space.",
            Description = "Update the main module of an existing ParticleSystem. Supports duration, start lifetime, start speed, start size, start color, rotation, gravity, maxParticles, loop, playOnAwake, simulationSpace, and scalingMode.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "particle-main-edit" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Set looping dust", Input = new Request { Target = "Effects/Dust", Loop = true, Duration = 3 } },
                new ToolExample { Description = "Switch to world simulation", Input = new Request { Target = "Effects/Dust", SimulationSpace = "World" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.EnsureEditorReady("vfx.particle-main");
            var particleSystem = VfxToolSupport.ResolveParticleSystem(request.Target);
            if (particleSystem == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_PARTICLE_SYSTEM_NOT_FOUND",
                    "ParticleSystem not found: " + request.Target,
                    "Pass a GameObject name / hierarchy path with a ParticleSystem component.");
            }

            VfxToolSupport.PrepareParticleMainFields(request);
            VfxToolSupport.RequireMutation(request.Duration.HasValue || request.StartLifetime.HasValue || request.StartSpeed.HasValue || request.StartSize.HasValue || !string.IsNullOrWhiteSpace(request.StartColor) || request.StartRotation.HasValue || request.GravityModifier.HasValue || request.MaxParticles.HasValue || request.Loop.HasValue || request.PlayOnAwake.HasValue || !string.IsNullOrWhiteSpace(request.SimulationSpace) || !string.IsNullOrWhiteSpace(request.ScalingMode), "vfx.particle-main");
            var gameObjectId = particleSystem.gameObject.GetInstanceID();
            var effects = new List<VfxPlannedEffect>();
            ctx.CommitEffect();
            var writable = VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle-main");
            VfxToolSupport.ApplyParticleMainFields(writable, request, "vfx.particle-main", effects);
            var observed = VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle-main");
            VfxToolSupport.VerifyParticleMainFields(observed, request);
            UnityCliSceneMutation.MarkSceneObjectDirty(observed);
            VfxToolSupport.MarkSceneDirty(observed.gameObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(observed.gameObject),
                ParticleSystem = VfxToolSupport.BuildParticleSystemInfo(observed),
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            });
        }
    }
}
#endif
