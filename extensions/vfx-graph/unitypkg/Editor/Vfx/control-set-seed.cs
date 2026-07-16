#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>设置目标 VisualEffect 的随机种子。</summary>
    public sealed class VfxControlSetSeedTool : IUnityCliTool<VfxControlSetSeedTool.Request, VfxControlActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public int? Seed { get; set; } public bool? ResetSeedOnPlay { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.control.set-seed", Summary = "Set VisualEffect random seed.",
            Description = "Set startSeed and optionally resetSeedOnPlay on the existing VisualEffect component.",
            Group = "vfx", Version = "1.0.0", RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "component-state-edit" }, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "Set a deterministic seed", Input = new Request { Target = "Effects/Emitter", Seed = 42 } },
                new ToolExample { Description = "Reset the seed on play", Input = new Request { Target = "Effects/Emitter", Seed = 7, ResetSeedOnPlay = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<VfxControlActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            if (request.Seed.Value < 0) throw new UnityCliBridge.BridgeCommandException("E_VFX_SEED_INVALID", "VisualEffect seed must be non-negative.", "Pass seed as an integer greater than or equal to zero.");
            var effect = VfxActionToolSupport.RequireEffect(request.Target, "vfx.control.set-seed");
            VfxActionToolSupport.SetRequired(effect, "startSeed", (uint)request.Seed.Value);
            if (request.ResetSeedOnPlay.HasValue) VfxActionToolSupport.SetRequired(effect, "resetSeedOnPlay", request.ResetSeedOnPlay.Value);
            var result = VfxActionToolSupport.BuildControlResult(effect, "vfx.control.set-seed", context.LogPath);
            result.Seed = request.Seed.Value;
            result.ResetSeedOnPlay = request.ResetSeedOnPlay;
            return new System.Threading.Tasks.ValueTask<VfxControlActionResult>(result);
        }
    }
}
#endif
