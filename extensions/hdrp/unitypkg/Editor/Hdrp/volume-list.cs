#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>hdrp.volume.list：列出当前场景会话中的 HDRP Volume。</summary>
    public sealed class HdrpVolumeListTool : IUnityCliTool<HdrpVolumeListTool.Request, HdrpVolumeListTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public string Command { get; set; } public List<VolumeInfo> Volumes { get; set; } public string LogPath { get; set; } }
        public ToolDescriptor Describe() => new()
        {
            Name = "hdrp.volume.list", Summary = "List scene-session HDRP Volume components and their in-memory profiles.",
            Description = "Lists HDRP Volume components in loaded scenes with their global state, priority, weight, and current in-memory profile reference. Requires com.unity.render-pipelines.high-definition and an active HDRenderPipelineAsset. Does not save scenes or assets.",
            Group = "hdrp", Version = "1.0.0", DestructiveHint = false, RequiresPackages = new[] { "com.unity.render-pipelines.high-definition" }, SideEffects = new string[0], Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "List HDRP volumes", Input = new Request() } }
        };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("hdrp.volume.list"); HdrpToolSupport.EnsureHdrpAvailable();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "hdrp.volume.list", Volumes = HdrpToolSupport.ListVolumes(null), LogPath = ctx.LogPath });
        }
    }
}
#endif
