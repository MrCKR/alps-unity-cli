#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// vfx.particle-emission - 修改 ParticleSystem 发射模块。
    /// </summary>
    public class VfxParticleEmissionTool : IUnityCliTool<VfxParticleEmissionTool.Request, VfxParticleEmissionTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Target { get; set; }
            public bool? Enabled { get; set; }
            public double? RateOverTime { get; set; }
            public double? RateOverDistance { get; set; }
            public int? BurstCount { get; set; }
            public List<ParticleBurstInput> Bursts { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public VfxParticleEmissionInfo Emission { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.particle-emission",
            Summary = "Update ParticleSystem emission enablement, rates, and optional burst definitions.",
            Description = "Update the emission module of an existing ParticleSystem. Supports enabled, rateOverTime, rateOverDistance, burstCount, and an optional bursts array with time/count/cycles/repeatInterval entries.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "particle-emission-edit" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Enable constant emission", Input = new Request { Target = "Effects/Dust", Enabled = true, RateOverTime = 12 } },
                new ToolExample { Description = "Configure one burst", Input = new Request { Target = "Effects/Dust", BurstCount = 1, Bursts = new List<ParticleBurstInput> { new ParticleBurstInput { Time = 0, Count = 24 } } } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            VfxToolSupport.EnsureEditorReady("vfx.particle-emission");
            var particleSystem = VfxToolSupport.ResolveParticleSystem(request.Target);
            if (particleSystem == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_PARTICLE_SYSTEM_NOT_FOUND",
                    "ParticleSystem not found: " + request.Target,
                    "Pass a GameObject name / hierarchy path with a ParticleSystem component.");
            }

            var emission = particleSystem.emission;
            var effects = new List<VfxPlannedEffect>();
            if (request.Enabled.HasValue)
            {
                emission.enabled = request.Enabled.Value;
                effects.Add(VfxToolSupport.Effect("vfx.particle-emission", "enabled", VfxToolSupport.Bool(emission.enabled)));
            }
            if (request.RateOverTime.HasValue)
            {
                emission.rateOverTime = (float)request.RateOverTime.Value;
                effects.Add(VfxToolSupport.Effect("vfx.particle-emission", "rateOverTime", VfxToolSupport.Number(emission.rateOverTime.constant)));
            }
            if (request.RateOverDistance.HasValue)
            {
                emission.rateOverDistance = (float)request.RateOverDistance.Value;
                effects.Add(VfxToolSupport.Effect("vfx.particle-emission", "rateOverDistance", VfxToolSupport.Number(emission.rateOverDistance.constant)));
            }
            if (request.BurstCount.HasValue)
            {
                var burstCount = request.BurstCount.Value;
                emission.SetBursts(new ParticleSystem.Burst[0]);
                for (var index = 0; index < burstCount; index++)
                {
                    var burst = request.Bursts != null && index < request.Bursts.Count ? request.Bursts[index] : null;
                    emission.SetBurst(index, new ParticleSystem.Burst(
                        (float)(burst?.Time ?? 0d),
                        (short)(burst?.Count ?? 10),
                        (short)(burst?.Cycles ?? 1),
                        (float)(burst?.RepeatInterval ?? 0.01d)));
                }
                effects.Add(VfxToolSupport.Effect("vfx.particle-emission", "burstCount", burstCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            EditorUtility.SetDirty(particleSystem);
            VfxToolSupport.MarkSceneDirty(particleSystem.gameObject);
            return new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(particleSystem.gameObject),
                Emission = new VfxParticleEmissionInfo
                {
                    Enabled = emission.enabled,
                    RateOverTime = emission.rateOverTime.constant,
                    RateOverDistance = emission.rateOverDistance.constant,
                    BurstCount = emission.burstCount
                },
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
