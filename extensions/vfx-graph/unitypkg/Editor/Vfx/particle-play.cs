#if UNITY_EDITOR
using UnityEngine;

namespace UnityCli
{
    /// <summary>播放目标 ParticleSystem。</summary>
    public sealed class VfxParticlePlayTool : IUnityCliTool<VfxParticlePlayTool.Request, VfxParticleActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } public bool? WithChildren { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.particle.play", Summary = "Play a ParticleSystem.",
            Description = "Play the existing ParticleSystem, optionally including child systems.",
            Group = "vfx",SideEffects = new[] { "particle-playback-edit" },
            Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "Play the dust system", Input = new Request { Target = "Effects/Dust" } },
                new ToolExample { Description = "Play with child systems", Input = new Request { Target = "Effects/Dust", WithChildren = true } }
            }
        };

        public async System.Threading.Tasks.ValueTask<VfxParticleActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var particleSystem = VfxActionToolSupport.RequireParticle(request.Target, "vfx.particle.play");
            var withChildren = request.WithChildren ?? true;
            var gameObjectId = particleSystem.gameObject.GetInstanceID();
            ctx.CommitEffect();

            particleSystem = VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle.play");
            particleSystem.Play(withChildren);
            await VfxActionToolSupport.RequireParticleState(particleSystem, withChildren, system => system.isPlaying && !system.isPaused, "vfx.particle.play", ctx, ct);
            return VfxActionToolSupport.BuildParticleResult(VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle.play"), "vfx.particle.play", ctx.LogPath);
        }
    }
}
#endif
