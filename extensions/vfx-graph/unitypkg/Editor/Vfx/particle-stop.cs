#if UNITY_EDITOR
using UnityEngine;

namespace UnityCli
{
    /// <summary>停止目标 ParticleSystem。</summary>
    public sealed class VfxParticleStopTool : IUnityCliTool<VfxParticleStopTool.Request, VfxParticleActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } public bool? WithChildren { get; set; } public string StopBehavior { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.particle-stop", Summary = "Stop a ParticleSystem.",
            Description = "Stop the existing ParticleSystem with optional child propagation and stop behavior.",
            Group = "vfx",SideEffects = new[] { "particle-playback-edit" },
            Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "Stop the dust system", Input = new Request { Target = "Effects/Dust" } },
                new ToolExample { Description = "Stop and clear particles", Input = new Request { Target = "Effects/Dust", WithChildren = true, StopBehavior = "StopEmittingAndClear" } }
            }
        };

        public async System.Threading.Tasks.ValueTask<VfxParticleActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var particleSystem = VfxActionToolSupport.RequireParticle(request.Target, "vfx.particle-stop");
            var withChildren = request.WithChildren ?? true;
            var behavior = string.IsNullOrEmpty(request.StopBehavior)
                ? UnityEngine.ParticleSystemStopBehavior.StopEmitting
                : VfxToolSupport.ParseEnum<UnityEngine.ParticleSystemStopBehavior>(request.StopBehavior, "E_PARTICLE_STOP_BEHAVIOR_INVALID", "Use StopEmitting or StopEmittingAndClear.");
            var gameObjectId = particleSystem.gameObject.GetInstanceID();
            ctx.CommitEffect();

            particleSystem = VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle-stop");
            particleSystem.Stop(withChildren, behavior);
            await VfxActionToolSupport.RequireParticleState(particleSystem, withChildren, system => !system.isPlaying && !system.isEmitting && !system.isPaused, "vfx.particle-stop", ctx, ct);
            return VfxActionToolSupport.BuildParticleResult(VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle-stop"), "vfx.particle-stop", ctx.LogPath);
        }
    }
}
#endif
