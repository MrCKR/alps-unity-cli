#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>
    /// vfx.particle-info - 读取 ParticleSystem 摘要。
    /// </summary>
    public class VfxParticleInfoTool : IUnityCliTool<VfxParticleInfoTool.Request, VfxParticleInfoTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public VfxParticleSystemInfo ParticleSystem { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.particle-info",
            Summary = "Read ParticleSystem main, emission, shape, and module state for a target or the first scene particle system.",
            Description = "Read a ParticleSystem summary without changing the scene. When target is omitted, the first ParticleSystem found in the loaded scenes is used.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Inspect the first particle system", Input = new Request() },
                new ToolExample { Description = "Inspect a named target", Input = new Request { Target = "Effects/Dust" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var particleSystem = VfxToolSupport.ResolveParticleSystem(request.Target);
            if (particleSystem == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_PARTICLE_SYSTEM_NOT_FOUND",
                    "ParticleSystem not found: " + request.Target,
                    "Pass a GameObject name / hierarchy path with a ParticleSystem component.");
            }

            return new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(particleSystem.gameObject),
                ParticleSystem = VfxToolSupport.BuildParticleSystemInfo(particleSystem),
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
