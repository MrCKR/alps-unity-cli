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
            DestructiveHint = false,
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

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
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

            VfxToolSupport.RequireFinite(request.RateOverTime, "rateOverTime", 0);
            VfxToolSupport.RequireFinite(request.RateOverDistance, "rateOverDistance", 0);
            if (request.BurstCount.HasValue && request.BurstCount.Value < 0) throw new UnityCliBridge.BridgeCommandException("E_VFX_INPUT_RANGE_INVALID", "burstCount must be non-negative.", "Pass burstCount greater than or equal to zero.");
            if (request.Bursts != null && (!request.BurstCount.HasValue || request.Bursts.Count > request.BurstCount.Value)) throw new UnityCliBridge.BridgeCommandException("E_VFX_INPUT_RANGE_INVALID", "bursts requires burstCount large enough to contain every burst.", "Set burstCount to the number of supplied bursts.");
            VfxToolSupport.PrepareParticleBursts(request.Bursts);
            VfxToolSupport.RequireMutation(request.Enabled.HasValue || request.RateOverTime.HasValue || request.RateOverDistance.HasValue || request.BurstCount.HasValue, "vfx.particle-emission");
            var gameObjectId = particleSystem.gameObject.GetInstanceID();
            var effects = new List<VfxPlannedEffect>();
            ctx.CommitEffect();
            particleSystem = VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle-emission");
            var emission = particleSystem.emission;
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
                var bursts = new ParticleSystem.Burst[burstCount];
                for (var index = 0; index < burstCount; index++)
                {
                    var burst = request.Bursts != null && index < request.Bursts.Count ? request.Bursts[index] : null;
                    bursts[index] = new ParticleSystem.Burst(
                        (float)(burst?.Time ?? 0d),
                        (short)(burst?.Count ?? 10),
                        (short)(burst?.Cycles ?? 1),
                        (float)(burst?.RepeatInterval ?? 0.01d));
                }
                emission.SetBursts(bursts);
            }

            var observed = VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle-emission");
            VfxToolSupport.VerifyParticleEmissionFields(observed, request.Enabled, request.RateOverTime, request.RateOverDistance, request.BurstCount, request.Bursts);
            var observedEmission = observed.emission;
            if (request.Enabled.HasValue) effects.Add(VfxToolSupport.Effect("vfx.particle-emission", "enabled", VfxToolSupport.Bool(observedEmission.enabled)));
            if (request.RateOverTime.HasValue) effects.Add(VfxToolSupport.Effect("vfx.particle-emission", "rateOverTime", VfxToolSupport.Number(observedEmission.rateOverTime.constant)));
            if (request.RateOverDistance.HasValue) effects.Add(VfxToolSupport.Effect("vfx.particle-emission", "rateOverDistance", VfxToolSupport.Number(observedEmission.rateOverDistance.constant)));
            if (request.BurstCount.HasValue) effects.Add(VfxToolSupport.Effect("vfx.particle-emission", "burstCount", observedEmission.burstCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            UnityCliSceneMutation.MarkSceneObjectDirty(observed);
            VfxToolSupport.MarkSceneDirty(observed.gameObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(observed.gameObject),
                Emission = new VfxParticleEmissionInfo
                {
                    Enabled = observed.emission.enabled,
                    RateOverTime = observed.emission.rateOverTime.constant,
                    RateOverDistance = observed.emission.rateOverDistance.constant,
                    BurstCount = observed.emission.burstCount
                },
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            });
        }
    }
}
#endif
