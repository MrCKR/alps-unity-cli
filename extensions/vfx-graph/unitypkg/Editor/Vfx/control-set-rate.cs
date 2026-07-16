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
            Description = "Set the public playRate property on the existing VisualEffect component.",
            Group = "vfx", Version = "1.0.0", RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "component-state-edit" }, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Set half-speed playback", Input = new Request { Target = "Effects/Emitter", Rate = 0.5 } } }
        };

        public System.Threading.Tasks.ValueTask<VfxControlActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            var effect = VfxActionToolSupport.RequireEffect(request.Target, "vfx.control.set-rate");
            VfxActionToolSupport.SetRequired(effect, "playRate", (float)request.Rate.Value);
            var result = VfxActionToolSupport.BuildControlResult(effect, "vfx.control.set-rate", context.LogPath);
            result.Rate = request.Rate.Value;
            return new System.Threading.Tasks.ValueTask<VfxControlActionResult>(result);
        }
    }
}
#endif
