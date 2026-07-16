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
            Description = "Invoke Reinit on the existing VisualEffect component to reset its simulation state.",
            Group = "vfx", Version = "1.0.0", RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "component-state-edit" }, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Reinitialize the matrix emitter", Input = new Request { Target = "Effects/Emitter" } } }
        };

        public System.Threading.Tasks.ValueTask<VfxControlActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            var effect = VfxActionToolSupport.RequireEffect(request.Target, "vfx.control.reinitialize");
            VfxActionToolSupport.InvokeRequired(effect, "Reinit");
            return new System.Threading.Tasks.ValueTask<VfxControlActionResult>(VfxActionToolSupport.BuildControlResult(effect, "vfx.control.reinitialize", context.LogPath));
        }
    }
}
#endif
