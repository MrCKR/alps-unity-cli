#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>设置一个 URP renderer feature 的启用状态。</summary>
    public sealed class UrpFeatureToggleTool : IUnityCliTool<UrpFeatureToggleTool.Request, UrpFeatureResult>
    {
        public sealed class Request { [UnityCliRequired] public int? Index { get; set; } [UnityCliRequired] public bool? Enabled { get; set; } public int? RendererIndex { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "urp.feature.toggle", Summary = "Enable or disable one URP Renderer Feature.", Description = "Set the active state of one Renderer Feature by index and verify its re-read active state. Supports --dry-run without writing renderer data.", Group = "urp", Version = "1.0.0", DestructiveHint = true, SupportsDryRun = true, RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, SideEffects = new[] { "asset-write", "renderer-feature-edit" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Enable feature zero", Input = new Request { Index = 0, Enabled = true, RendererIndex = 0 } } } };
        public System.Threading.Tasks.ValueTask<UrpFeatureResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) { var plan = UrpFeatureToolSupport.ValidateToggle(request.Index.Value, request.RendererIndex ?? 0); if (!ctx.DryRun) ctx.CommitEffect(); return new System.Threading.Tasks.ValueTask<UrpFeatureResult>(UrpFeatureToolSupport.Toggle(plan, request.Enabled.Value, ctx.DryRun, ctx.LogPath)); }
    }
}
#endif
