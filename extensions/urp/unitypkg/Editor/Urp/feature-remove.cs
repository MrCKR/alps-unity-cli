#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>从活动 URP renderer 删除一个 feature。</summary>
    public sealed class UrpFeatureRemoveTool : IUnityCliTool<UrpFeatureRemoveTool.Request, UrpFeatureResult>
    {
        public sealed class Request { [UnityCliRequired] public int? Index { get; set; } public int? RendererIndex { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "urp.feature.remove", Summary = "Remove one URP Renderer Feature.", Description = "Remove one Renderer Feature sub-asset by index and verify it is absent from the re-read feature collection. Supports --dry-run without modifying renderer data.", Group = "urp", Version = "1.0.0", DestructiveHint = true, SupportsDryRun = true, RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, SideEffects = new[] { "asset-write", "renderer-feature-edit" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Remove feature zero", Input = new Request { Index = 0, RendererIndex = 0 } } } };
        public System.Threading.Tasks.ValueTask<UrpFeatureResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) { UrpFeatureToolSupport.ValidateFeature(request.Index.Value, request.RendererIndex ?? 0, "urp.feature.remove"); if (!ctx.DryRun) ctx.CommitEffect(); return new System.Threading.Tasks.ValueTask<UrpFeatureResult>(UrpFeatureToolSupport.Remove(request.Index.Value, request.RendererIndex ?? 0, ctx.DryRun, ctx.LogPath)); }
    }
}
#endif
