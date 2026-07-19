#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>hdrp.volume.create：创建仅存在于当前会话的 HDRP Volume。</summary>
    public sealed class HdrpVolumeCreateTool : IUnityCliTool<HdrpVolumeCreateTool.Request, HdrpVolumeCreateTool.Result>
    {
        public sealed class Request { public string Name { get; set; } public bool? IsGlobal { get; set; } public double? Priority { get; set; } public double? Weight { get; set; } }
        public sealed class Result { public string Command { get; set; } public GameObjectRef Target { get; set; } public bool IsGlobal { get; set; } public List<HdrpPlannedEffect> PlannedEffects { get; set; } public string LogPath { get; set; } }
        public ToolDescriptor Describe() => new()
        {
            Name = "hdrp.volume.create", Summary = "Create a scene-session HDRP Volume with a new in-memory profile.",
            Description = "Creates an HDRP Volume GameObject and assigns a new in-memory VolumeProfile. name defaults to HDRPVolume; isGlobal defaults to true. The scene and profile are not saved as assets. Requires com.unity.render-pipelines.high-definition and an active HDRenderPipelineAsset.",
            Group = "hdrp", Version = "1.0.0", DestructiveHint = false, RequiresPackages = new[] { "com.unity.render-pipelines.high-definition" }, SideEffects = new[] { "scene-modify", "volume-profile-edit" }, Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Create a global HDRP volume", Input = new Request { Name = "GlobalVolume", IsGlobal = true } }, new ToolExample { Description = "Create a weighted HDRP volume", Input = new Request { Name = "OptionalHDRPVolume", IsGlobal = true, Priority = 1, Weight = 0.5 } } }
        };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("hdrp.volume.create"); HdrpToolSupport.EnsureHdrpAvailable();
            var plan = HdrpVolumeOperations.ValidateCreate(request.Priority, request.Weight, "E_HDRP");
            ctx.CommitEffect();
            var created = HdrpVolumeOperations.Create(request.Name, request.IsGlobal, plan, "HDRP", "E_HDRP");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "hdrp.volume.create", Target = GameObjectToolSupport.BuildGameObjectRef(created.GameObject), IsGlobal = created.IsGlobal, PlannedEffects = new List<HdrpPlannedEffect> { new HdrpPlannedEffect { Type = "hdrp.volume.create", Name = created.GameObject.name } }, LogPath = ctx.LogPath });
        }
    }
}
#endif
