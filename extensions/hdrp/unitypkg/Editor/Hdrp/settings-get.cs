#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>读取活动 HDRP pipeline asset 设置。</summary>
    public sealed class HdrpSettingsGetTool : IUnityCliTool<HdrpSettingsGetTool.Request, HdrpSettingsGetTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { [UnityCliAnyJson] public Dictionary<string, object> Settings { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "hdrp.settings-get", Summary = "Read active HDRP pipeline asset settings.",
            Description = "Read supported public settings from the active HDRenderPipelineAsset without modifying it.",
            Group = "hdrp",RequiresPackages = new[] { "com.unity.render-pipelines.high-definition" },
            SideEffects = new string[0], Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read HDRP settings", Input = new Request() } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("hdrp.settings-get");
            var pipeline = HdrpToolSupport.GetActivePipelineAsset();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Settings = HdrpToolSupport.ReadSettings(pipeline)});
        }
    }
}
#endif
