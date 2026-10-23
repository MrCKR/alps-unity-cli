#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>向活动 URP renderer 添加一个 feature。</summary>
    public sealed class UrpFeatureAddTool : IUnityCliTool<UrpFeatureAddTool.Request, UrpFeatureResult>
    {
        public sealed class Request { [UnityCliRequired] public string FeatureType { get; set; } public int? RendererIndex { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "urp.feature-add", Summary = "Add one URP Renderer Feature.", Description = "Add one ScriptableRendererFeature-derived object to a renderer on the active URP asset. Supports --dry-run.", Group = "urp",DestructiveHint = true, SupportsDryRun = true, RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, SideEffects = new[] { "asset-write", "renderer-feature-edit" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add ambient occlusion", Input = new Request { FeatureType = "ScreenSpaceAmbientOcclusion", RendererIndex = 0 } } } };
        public System.Threading.Tasks.ValueTask<UrpFeatureResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) { var plan = UrpFeatureToolSupport.ValidateAdd(request.FeatureType, request.RendererIndex ?? 0); if (!ctx.DryRun) ctx.CommitEffect(); return new System.Threading.Tasks.ValueTask<UrpFeatureResult>(UrpFeatureToolSupport.Add(plan, ctx.DryRun, ctx.LogPath)); }
    }
}
#endif
