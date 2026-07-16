#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>启动目标 VisualEffect 的播放。</summary>
    public sealed class VfxControlPlayTool : IUnityCliTool<VfxControlPlayTool.Request, VfxControlActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.control.play", Summary = "Play a VisualEffect component.",
            Description = "Start the existing VisualEffect component on one target GameObject.",
            Group = "vfx", Version = "1.0.0", RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "component-state-edit" }, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Play the matrix emitter", Input = new Request { Target = "Effects/Emitter" } } }
        };

        public System.Threading.Tasks.ValueTask<VfxControlActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            var effect = VfxActionToolSupport.RequireEffect(request.Target, "vfx.control.play");
            VfxActionToolSupport.InvokeRequired(effect, "Play");
            return new System.Threading.Tasks.ValueTask<VfxControlActionResult>(VfxActionToolSupport.BuildControlResult(effect, "vfx.control.play", context.LogPath));
        }
    }
}
#endif
