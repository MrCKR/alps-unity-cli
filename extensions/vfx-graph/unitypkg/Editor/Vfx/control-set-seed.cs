#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>设置目标 VisualEffect 的随机种子。</summary>
    public sealed class VfxControlSetSeedTool : IUnityCliTool<VfxControlSetSeedTool.Request, VfxControlActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public int? Seed { get; set; } public bool? ResetSeedOnPlay { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.control-set-seed", Summary = "Set VisualEffect random seed.",
            Description = "Set startSeed and optionally resetSeedOnPlay on the existing VisualEffect component, then return verified public-property readbacks.",
            Group = "vfx",RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "component-state-edit" }, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "Set a deterministic seed", Input = new Request { Target = "Effects/Emitter", Seed = 42 } },
                new ToolExample { Description = "Reset the seed on play", Input = new Request { Target = "Effects/Emitter", Seed = 7, ResetSeedOnPlay = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<VfxControlActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!request.Seed.HasValue || request.Seed.Value < 0) throw new UnityCliBridge.BridgeCommandException("E_VFX_SEED_INVALID", "VisualEffect seed must be non-negative.", "Pass seed as an integer greater than or equal to zero.");
            var effect = VfxActionToolSupport.RequireEffect(request.Target, "vfx.control-set-seed");
            var seedProperty = effect.GetType().GetProperty("startSeed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            var resetProperty = effect.GetType().GetProperty("resetSeedOnPlay", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (seedProperty == null || !seedProperty.CanRead || !seedProperty.CanWrite || seedProperty.PropertyType != typeof(uint) || (request.ResetSeedOnPlay.HasValue && (resetProperty == null || !resetProperty.CanRead || !resetProperty.CanWrite || resetProperty.PropertyType != typeof(bool)))) throw new UnityCliBridge.BridgeCommandException("E_VFX_PROPERTY_UNSUPPORTED", "VisualEffect seed property is unavailable.", "Check the installed Visual Effect Graph package version.");
            var gameObjectId = effect.gameObject.GetInstanceID(); var effectType = effect.GetType();
            ctx.CommitEffect();
            var observedObject = VfxToolSupport.FindGameObjectByTarget(gameObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            effect = observedObject == null ? null : observedObject.GetComponent(effectType);
            if (effect == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_OBSERVED_TARGET_MISSING", "vfx.control-set-seed target/component disappeared after commit.", "Retry after checking the target still exists.");
            VfxActionToolSupport.SetAndReadRequired(effect, "startSeed", (uint)request.Seed.Value);
            if (request.ResetSeedOnPlay.HasValue) VfxActionToolSupport.SetAndReadRequired(effect, "resetSeedOnPlay", request.ResetSeedOnPlay.Value);
            var result = VfxActionToolSupport.BuildControlResult(effect, "vfx.control-set-seed", ctx.LogPath);
            return new System.Threading.Tasks.ValueTask<VfxControlActionResult>(result);
        }
    }
}
#endif
