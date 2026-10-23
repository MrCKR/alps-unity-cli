#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>列出活动 URP renderer 的 feature。</summary>
    public sealed class UrpFeatureListTool : IUnityCliTool<UrpFeatureListTool.Request, UrpFeatureListResult>
    {
        public sealed class Request { }
        public ToolDescriptor Describe() => new() { Name = "urp.feature-list", Summary = "List URP Renderer Features.", Description = "List Renderer Features on the active UniversalRenderPipelineAsset without modifying renderer data. Returns availability, active renderer identity, and recoverable diagnostics when URP is unavailable.", Group = "urp",RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, SideEffects = new string[0], Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000, Examples = new[] { new ToolExample { Description = "List renderer features", Input = new Request() } } };
        public System.Threading.Tasks.ValueTask<UrpFeatureListResult> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default) => new System.Threading.Tasks.ValueTask<UrpFeatureListResult>(UrpFeatureToolSupport.List(context.LogPath));
    }
}
#endif
