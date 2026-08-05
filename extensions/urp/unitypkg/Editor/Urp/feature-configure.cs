#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>设置一个 URP renderer feature 的序列化属性。</summary>
    public sealed class UrpFeatureConfigureTool : IUnityCliTool<UrpFeatureConfigureTool.Request, UrpFeatureResult>
    {
        public sealed class Request { [UnityCliRequired] public int? Index { get; set; } [UnityCliRequired] public string Property { get; set; } [UnityCliRequired] public string Value { get; set; } public int? RendererIndex { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "urp.feature.configure", Summary = "Configure one URP Renderer Feature property.", Description = "Set one SerializedProperty on a Renderer Feature selected by index and verify its typed serialized readback. Supports --dry-run without writing renderer data.", Group = "urp",DestructiveHint = true, SupportsDryRun = true, RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, SideEffects = new[] { "asset-write", "renderer-feature-edit" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Rename feature zero", Input = new Request { Index = 0, Property = "name", Value = "Matrix Feature", RendererIndex = 0 } } } };
        public System.Threading.Tasks.ValueTask<UrpFeatureResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) { UrpFeatureToolSupport.ValidateConfigure(request.Index.Value, request.RendererIndex ?? 0, request.Property, request.Value); if (!ctx.DryRun) ctx.CommitEffect(); return new System.Threading.Tasks.ValueTask<UrpFeatureResult>(UrpFeatureToolSupport.Configure(request.Index.Value, request.RendererIndex ?? 0, request.Property, request.Value, ctx.DryRun, ctx.LogPath)); }
    }
}
#endif
