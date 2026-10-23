#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>启动目标 VisualEffect 的播放。</summary>
    public sealed class VfxControlPlayTool : IUnityCliTool<VfxControlPlayTool.Request, VfxControlActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.control-play", Summary = "Play a VisualEffect component.",
            Description = "Play the VisualEffect and prove a running transition through pause=false plus a changed positive aliveParticleCount. Fails closed when the graph exposes no observable particle transition.",
            Group = "vfx",RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "component-state-edit" }, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Play the matrix emitter", Input = new Request { Target = "Effects/Emitter" } } }
        };

        public async System.Threading.Tasks.ValueTask<VfxControlActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effect = VfxActionToolSupport.RequireEffect(request.Target, "vfx.control-play");
            var beforeAlive = VfxActionToolSupport.ReadLong(effect, "aliveParticleCount");
            if (!beforeAlive.HasValue)
                throw new UnityCliBridge.BridgeCommandException("E_VFX_STATE_UNOBSERVABLE", "VisualEffect aliveParticleCount is unavailable for play proof.", "Use a Visual Effect Graph version exposing aliveParticleCount.");
            var gameObjectId = effect.gameObject.GetInstanceID(); var effectType = effect.GetType();
            VfxActionToolSupport.PrepareControlMethod(effectType, "Play");
            VfxActionToolSupport.PrepareWritableBoolProperty(effectType, "pause");
            ctx.CommitEffect();

            effect = VfxActionToolSupport.ResolveObservedEffect(gameObjectId, effectType, "vfx.control-play");
            VfxActionToolSupport.InvokeRequired(effect, "Play");
            VfxActionToolSupport.SetRequired(effect, "pause", false);
            await VfxActionToolSupport.RequirePauseState(effect, false, "play", ctx, ct);
            await VfxActionToolSupport.RequireAliveState(effect, count => count > 0 && count != beforeAlive.Value, "play", ctx, ct);
            effect = VfxActionToolSupport.ResolveObservedEffect(gameObjectId, effectType, "vfx.control-play");
            return VfxActionToolSupport.BuildControlResult(effect, "vfx.control-play", ctx.LogPath);
        }
    }
}
#endif
