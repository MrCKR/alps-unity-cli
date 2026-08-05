#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>设置目标 VisualEffect 的播放倍率。</summary>
    public sealed class VfxControlSetRateTool : IUnityCliTool<VfxControlSetRateTool.Request, VfxControlActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public double? Rate { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.control.set-rate", Summary = "Set VisualEffect playback rate.",
            Description = "Set the public playRate property on the existing VisualEffect component and return its verified readback.",
            Group = "vfx",RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "component-state-edit" }, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Set half-speed playback", Input = new Request { Target = "Effects/Emitter", Rate = 0.5 } } }
        };

        public System.Threading.Tasks.ValueTask<VfxControlActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.RequireFinite(request.Rate, "rate", 0);
            var effect = VfxActionToolSupport.RequireEffect(request.Target, "vfx.control.set-rate");
            var property = effect.GetType().GetProperty("playRate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (property == null || !property.CanRead || !property.CanWrite || property.PropertyType != typeof(float)) throw new UnityCliBridge.BridgeCommandException("E_VFX_PROPERTY_UNSUPPORTED", "VisualEffect playRate property is unavailable.", "Check the installed Visual Effect Graph package version.");
            var gameObjectId = effect.gameObject.GetInstanceID(); var effectType = effect.GetType();
            ctx.CommitEffect();
            var observedObject = VfxToolSupport.FindGameObjectByTarget(gameObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            effect = observedObject == null ? null : observedObject.GetComponent(effectType);
            if (effect == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_OBSERVED_TARGET_MISSING", "vfx.control.set-rate target/component disappeared after commit.", "Retry after checking the target still exists.");
            var rate = VfxActionToolSupport.SetAndReadRequired(effect, "playRate", (float)request.Rate.Value);
            var result = VfxActionToolSupport.BuildControlResult(effect, "vfx.control.set-rate", ctx.LogPath);
            result.Rate = rate;
            return new System.Threading.Tasks.ValueTask<VfxControlActionResult>(result);
        }
    }
}
#endif
