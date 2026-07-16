#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>urp.volume.list：列出当前场景会话中的 URP Volume。</summary>
    public sealed class UrpVolumeListTool : IUnityCliTool<UrpVolumeListTool.Request, UrpVolumeListTool.Result>
    {
        public sealed class Request { }
        public sealed class Result
        {
            public string Command { get; set; }
            public List<VolumeInfo> Volumes { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "urp.volume.list",
            Summary = "List scene-session URP Volume components and their in-memory profiles.",
            Description = "Lists URP Volume components in loaded scenes with their global state, priority, weight, and current in-memory profile reference. Requires com.unity.render-pipelines.universal and an active UniversalRenderPipelineAsset. Does not save scenes or assets.",
            Group = "urp",
            Version = "1.0.0",
            DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" },
            SideEffects = new string[0],
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "List URP volumes", Input = new Request() } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.volume.list");
            UrpToolSupport.EnsureUrpAvailable();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "urp.volume.list", Volumes = UrpToolSupport.ListVolumes(null), LogPath = ctx.LogPath });
        }
    }
}
#endif
