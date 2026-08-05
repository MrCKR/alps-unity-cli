#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    public sealed class UrpSettingsGetTool : IUnityCliTool<UrpSettingsGetTool.Request, UrpSettingsGetTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { [UnityCliAnyJson] public Dictionary<string, object> Settings { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "urp.settings.get", Summary = "Read active URP pipeline asset settings.",
            Description = "Reads supported public settings from the active UniversalRenderPipelineAsset without modifying assets or project settings. Requires com.unity.render-pipelines.universal and an active URP pipeline asset. Available in Edit and Play mode.",
            Group = "urp",DestructiveHint = false, RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read URP settings", Input = new Request() } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.settings.get");
            var pipeline = UrpToolSupport.GetActivePipelineAsset();
            return new(new Result { Settings = UrpToolSupport.ReadSettings(pipeline)});
        }
    }
}
#endif
