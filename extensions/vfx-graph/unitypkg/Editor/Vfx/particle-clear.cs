#if UNITY_EDITOR
using UnityEngine;

namespace UnityCli
{
    /// <summary>清空目标 ParticleSystem 的活动粒子。</summary>
    public sealed class VfxParticleClearTool : IUnityCliTool<VfxParticleClearTool.Request, VfxParticleActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } public bool? WithChildren { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.particle.clear", Summary = "Clear live ParticleSystem particles.",
            Description = "Clear live particles from the existing ParticleSystem, optionally including child systems.",
            Group = "vfx", Version = "1.0.0", SideEffects = new[] { "particle-playback-edit" },
            Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "Clear the dust system", Input = new Request { Target = "Effects/Dust" } },
                new ToolExample { Description = "Clear child systems", Input = new Request { Target = "Effects/Dust", WithChildren = true } }
            }
        };

        public async System.Threading.Tasks.ValueTask<VfxParticleActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var particleSystem = VfxActionToolSupport.RequireParticle(request.Target, "vfx.particle.clear");
            var withChildren = request.WithChildren ?? true;
            var gameObjectId = particleSystem.gameObject.GetInstanceID();
            ctx.CommitEffect();

            particleSystem = VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle.clear");
            particleSystem.Clear(withChildren);
            await VfxActionToolSupport.RequireParticleState(particleSystem, withChildren, system => system.particleCount == 0, "vfx.particle.clear", ctx, ct);
            var observed = VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle.clear");
            return VfxActionToolSupport.BuildParticleResult(observed, "vfx.particle.clear", ctx.LogPath);
        }
    }
}
#endif
