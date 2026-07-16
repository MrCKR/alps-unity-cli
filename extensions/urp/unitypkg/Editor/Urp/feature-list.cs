#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>列出活动 URP renderer 的 feature。</summary>
    public sealed class UrpFeatureListTool : IUnityCliTool<UrpFeatureListTool.Request, UrpFeatureResult>
    {
        public sealed class Request { }
        public ToolDescriptor Describe() => new() { Name = "urp.feature.list", Summary = "List URP Renderer Features.", Description = "List Renderer Features on the active UniversalRenderPipelineAsset without modifying renderer data.", Group = "urp", Version = "1.0.0", RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, SideEffects = new string[0], Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000, Examples = new[] { new ToolExample { Description = "List renderer features", Input = new Request() } } };
        public System.Threading.Tasks.ValueTask<UrpFeatureResult> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default) => new System.Threading.Tasks.ValueTask<UrpFeatureResult>(UrpFeatureToolSupport.List(context.LogPath));
    }
}
#endif
