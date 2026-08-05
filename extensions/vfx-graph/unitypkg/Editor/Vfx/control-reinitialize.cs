#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>重新初始化目标 VisualEffect。</summary>
    public sealed class VfxControlReinitializeTool : IUnityCliTool<VfxControlReinitializeTool.Request, VfxControlActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.control.reinitialize", Summary = "Reinitialize a VisualEffect component.",
            Description = "Invoke Reinit and prove the reset by observing a transition from a positive aliveParticleCount to zero. Fails closed when no observable active particles exist before the call.",
            Group = "vfx",RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "component-state-edit" }, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Reinitialize the matrix emitter", Input = new Request { Target = "Effects/Emitter" } } }
        };

        public async System.Threading.Tasks.ValueTask<VfxControlActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effect = VfxActionToolSupport.RequireEffect(request.Target, "vfx.control.reinitialize");
            var beforeAlive = VfxActionToolSupport.ReadLong(effect, "aliveParticleCount");
            if (!beforeAlive.HasValue || beforeAlive.Value <= 0)
                throw new UnityCliBridge.BridgeCommandException("E_VFX_STATE_UNOBSERVABLE", "VisualEffect has no live particles that can prove a reinitialize transition.", "Play an observable effect before calling vfx.control.reinitialize.");
            var gameObjectId = effect.gameObject.GetInstanceID(); var effectType = effect.GetType();
            VfxActionToolSupport.PrepareControlMethod(effectType, "Reinit");
            ctx.CommitEffect();

            effect = VfxActionToolSupport.ResolveObservedEffect(gameObjectId, effectType, "vfx.control.reinitialize");
            VfxActionToolSupport.InvokeRequired(effect, "Reinit");
            await VfxActionToolSupport.RequireAliveState(effect, count => count == 0, "reinitialize", ctx, ct);
            effect = VfxActionToolSupport.ResolveObservedEffect(gameObjectId, effectType, "vfx.control.reinitialize");
            return VfxActionToolSupport.BuildControlResult(effect, "vfx.control.reinitialize", ctx.LogPath);
        }
    }
}
#endif
