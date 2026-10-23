#if UNITY_EDITOR
using UnityEngine;

namespace UnityCli
{
    /// <summary>暂停目标 ParticleSystem。</summary>
    public sealed class VfxParticlePauseTool : IUnityCliTool<VfxParticlePauseTool.Request, VfxParticleActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } public bool? WithChildren { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.particle-pause", Summary = "Pause a ParticleSystem.",
            Description = "Pause the existing ParticleSystem, optionally including child systems.",
            Group = "vfx",SideEffects = new[] { "particle-playback-edit" },
            Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "Pause the dust system", Input = new Request { Target = "Effects/Dust" } },
                new ToolExample { Description = "Pause child systems", Input = new Request { Target = "Effects/Dust", WithChildren = true } }
            }
        };

        public async System.Threading.Tasks.ValueTask<VfxParticleActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var particleSystem = VfxActionToolSupport.RequireParticle(request.Target, "vfx.particle-pause");
            var withChildren = request.WithChildren ?? true;
            var gameObjectId = particleSystem.gameObject.GetInstanceID();
            ctx.CommitEffect();

            particleSystem = VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle-pause");
            particleSystem.Pause(withChildren);
            await VfxActionToolSupport.RequireParticleState(particleSystem, withChildren, system => system.isPaused, "vfx.particle-pause", ctx, ct);
            return VfxActionToolSupport.BuildParticleResult(VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle-pause"), "vfx.particle-pause", ctx.LogPath);
        }
    }
}
#endif
