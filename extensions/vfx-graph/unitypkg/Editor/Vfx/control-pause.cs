#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>切换目标 VisualEffect 的暂停状态。</summary>
    public sealed class VfxControlPauseTool : IUnityCliTool<VfxControlPauseTool.Request, VfxControlActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.control.pause", Summary = "Toggle VisualEffect pause state.",
            Description = "Toggle the public pause property on the existing VisualEffect component.",
            Group = "vfx",RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "component-state-edit" }, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Toggle emitter pause", Input = new Request { Target = "Effects/Emitter" } } }
        };

        public async System.Threading.Tasks.ValueTask<VfxControlActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effect = VfxActionToolSupport.RequireEffect(request.Target, "vfx.control.pause");
            var paused = VfxActionToolSupport.ReadBool(effect, "pause");
            if (!paused.HasValue) throw new UnityCliBridge.BridgeCommandException("E_VFX_PROPERTY_UNSUPPORTED", "VisualEffect pause state is unavailable.", "Check the installed Visual Effect Graph package version.");
            var expected = !paused.Value;
            var gameObjectId = effect.gameObject.GetInstanceID(); var effectType = effect.GetType();
            VfxActionToolSupport.PrepareWritableBoolProperty(effectType, "pause");
            ctx.CommitEffect();

            effect = VfxActionToolSupport.ResolveObservedEffect(gameObjectId, effectType, "vfx.control.pause");
            VfxActionToolSupport.SetRequired(effect, "pause", expected);
            await VfxActionToolSupport.RequirePauseState(effect, expected, "vfx.control.pause", ctx, ct);
            effect = VfxActionToolSupport.ResolveObservedEffect(gameObjectId, effectType, "vfx.control.pause");
            return VfxActionToolSupport.BuildControlResult(effect, "vfx.control.pause", ctx.LogPath);
        }
    }
}
#endif
