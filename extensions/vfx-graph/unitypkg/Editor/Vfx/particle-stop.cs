#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>停止目标 ParticleSystem。</summary>
    public sealed class VfxParticleStopTool : IUnityCliTool<VfxParticleStopTool.Request, VfxParticleActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } public bool? WithChildren { get; set; } public string StopBehavior { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.particle.stop", Summary = "Stop a ParticleSystem.",
            Description = "Stop the existing ParticleSystem with optional child propagation and stop behavior.",
            Group = "vfx", Version = "1.0.0", SideEffects = new[] { "particle-playback-edit" },
            Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "Stop the dust system", Input = new Request { Target = "Effects/Dust" } },
                new ToolExample { Description = "Stop and clear particles", Input = new Request { Target = "Effects/Dust", WithChildren = true, StopBehavior = "StopEmittingAndClear" } }
            }
        };

        public System.Threading.Tasks.ValueTask<VfxParticleActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            var particleSystem = VfxActionToolSupport.RequireParticle(request.Target, "vfx.particle.stop");
            var behavior = string.IsNullOrEmpty(request.StopBehavior)
                ? UnityEngine.ParticleSystemStopBehavior.StopEmitting
                : VfxToolSupport.ParseEnum<UnityEngine.ParticleSystemStopBehavior>(request.StopBehavior, "E_PARTICLE_STOP_BEHAVIOR_INVALID", "Use StopEmitting or StopEmittingAndClear.");
            particleSystem.Stop(request.WithChildren ?? true, behavior);
            return new System.Threading.Tasks.ValueTask<VfxParticleActionResult>(VfxActionToolSupport.BuildParticleResult(particleSystem, "vfx.particle.stop", context.LogPath));
        }
    }
}
#endif
