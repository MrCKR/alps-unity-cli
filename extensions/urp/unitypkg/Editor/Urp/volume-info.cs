#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>urp.volume.info：读取一个 URP Volume 的会话状态。</summary>
    public sealed class UrpVolumeInfoTool : IUnityCliTool<UrpVolumeInfoTool.Request, UrpVolumeInfoTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }
        public sealed class Result { public List<VolumeInfo> Volumes { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "urp.volume.info",
            Summary = "Read one scene-session URP Volume component and in-memory profile reference.",
            Description = "Reads the URP Volume matching target by GameObject name or hierarchy path. Returns its current session state and profile reference. Requires com.unity.render-pipelines.universal and an active UniversalRenderPipelineAsset. Does not save scenes or assets.",
            Group = "urp",DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, SideEffects = new string[0], Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Inspect a URP volume", Input = new Request { Target = "GlobalVolume" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.volume.info");
            UrpToolSupport.EnsureUrpAvailable();
            UrpVolumeOperations.RequireTarget(request.Target, "info");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Volumes = UrpToolSupport.ListVolumes(request.Target)});
        }
    }
}
#endif
