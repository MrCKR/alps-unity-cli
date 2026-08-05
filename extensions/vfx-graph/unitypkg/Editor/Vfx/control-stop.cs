#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>停止目标 VisualEffect 的播放。</summary>
    public sealed class VfxControlStopTool : IUnityCliTool<VfxControlStopTool.Request, VfxControlActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.control.stop", Summary = "Stop a VisualEffect component.",
            Description = "Stop the VisualEffect and wait until a previously non-empty effect reports aliveParticleCount=0. Fails closed when no observable active particles exist before the call.",
            Group = "vfx",RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "component-state-edit" }, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Stop the matrix emitter", Input = new Request { Target = "Effects/Emitter" } } }
        };

        public async System.Threading.Tasks.ValueTask<VfxControlActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effect = VfxActionToolSupport.RequireEffect(request.Target, "vfx.control.stop");
            var beforeAlive = VfxActionToolSupport.ReadLong(effect, "aliveParticleCount");
            if (!beforeAlive.HasValue || beforeAlive.Value <= 0)
                throw new UnityCliBridge.BridgeCommandException("E_VFX_STATE_UNOBSERVABLE", "VisualEffect has no live particles that can prove a stop transition.", "Play an observable effect before calling vfx.control.stop.");
            var gameObjectId = effect.gameObject.GetInstanceID(); var effectType = effect.GetType();
            VfxActionToolSupport.PrepareControlMethod(effectType, "Stop");
            ctx.CommitEffect();

            effect = VfxActionToolSupport.ResolveObservedEffect(gameObjectId, effectType, "vfx.control.stop");
            VfxActionToolSupport.InvokeRequired(effect, "Stop");
            await VfxActionToolSupport.RequireAliveState(effect, count => count == 0, "stop", ctx, ct);
            effect = VfxActionToolSupport.ResolveObservedEffect(gameObjectId, effectType, "vfx.control.stop");
            return VfxActionToolSupport.BuildControlResult(effect, "vfx.control.stop", ctx.LogPath);
        }
    }
}
#endif
