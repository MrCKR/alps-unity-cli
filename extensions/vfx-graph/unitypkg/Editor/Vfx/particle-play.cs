#if UNITY_EDITOR
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
            Group = "vfx", Version = "1.0.0", SideEffects = new[] { "particle-playback-edit" },
            Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "Play the dust system", Input = new Request { Target = "Effects/Dust" } },
                new ToolExample { Description = "Play with child systems", Input = new Request { Target = "Effects/Dust", WithChildren = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<VfxParticleActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            var particleSystem = VfxActionToolSupport.RequireParticle(request.Target, "vfx.particle.play");
            particleSystem.Play(request.WithChildren ?? true);
            return new System.Threading.Tasks.ValueTask<VfxParticleActionResult>(VfxActionToolSupport.BuildParticleResult(particleSystem, "vfx.particle.play", context.LogPath));
        }
    }
}
#endif
