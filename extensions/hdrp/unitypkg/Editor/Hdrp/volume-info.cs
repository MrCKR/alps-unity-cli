#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>hdrp.volume.info：读取一个 HDRP Volume 的会话状态。</summary>
    public sealed class HdrpVolumeInfoTool : IUnityCliTool<HdrpVolumeInfoTool.Request, HdrpVolumeInfoTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }
        public sealed class Result { public string Command { get; set; } public List<VolumeInfo> Volumes { get; set; } public string LogPath { get; set; } }
        public ToolDescriptor Describe() => new()
        {
            Name = "hdrp.volume.info", Summary = "Read one scene-session HDRP Volume component and in-memory profile reference.",
            Description = "Reads the HDRP Volume matching target by GameObject name or hierarchy path. Returns its current session state and profile reference. Requires com.unity.render-pipelines.high-definition and an active HDRenderPipelineAsset. Does not save scenes or assets.",
            Group = "hdrp", Version = "1.0.0", DestructiveHint = false, RequiresPackages = new[] { "com.unity.render-pipelines.high-definition" }, SideEffects = new string[0], Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Inspect an HDRP volume", Input = new Request { Target = "GlobalVolume" } } }
        };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("hdrp.volume.info"); HdrpToolSupport.EnsureHdrpAvailable(); HdrpVolumeOperations.RequireTarget(request.Target, "info");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "hdrp.volume.info", Volumes = HdrpToolSupport.ListVolumes(request.Target), LogPath = ctx.LogPath });
        }
    }
}
#endif
