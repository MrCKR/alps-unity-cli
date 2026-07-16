#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>hdrp.volume.add-effect：向 HDRP Volume 的内存 profile 添加效果。</summary>
    public sealed class HdrpVolumeAddEffectTool : IUnityCliTool<HdrpVolumeAddEffectTool.Request, HdrpVolumeAddEffectTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public string EffectType { get; set; } }
        public sealed class Result { public string Command { get; set; } public GameObjectRef Target { get; set; } public string EffectType { get; set; } public List<HdrpPlannedEffect> PlannedEffects { get; set; } public string LogPath { get; set; } }
        public ToolDescriptor Describe() => new()
        {
            Name = "hdrp.volume.add-effect", Summary = "Add a VolumeComponent effect to a scene-session HDRP Volume profile.",
            Description = "Adds effectType to the in-memory profile on the target HDRP Volume. target accepts a GameObject name or hierarchy path. The profile and scene remain unsaved. Requires com.unity.render-pipelines.high-definition and an active HDRenderPipelineAsset.",
            Group = "hdrp", Version = "1.0.0", DestructiveHint = false, RequiresPackages = new[] { "com.unity.render-pipelines.high-definition" }, SideEffects = new[] { "scene-modify", "volume-profile-edit" }, Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Add VisualEnvironment", Input = new Request { Target = "GlobalVolume", EffectType = "VisualEnvironment" } } }
        };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("hdrp.volume.add-effect"); HdrpToolSupport.EnsureHdrpAvailable();
            var target = HdrpVolumeOperations.AddEffect(request.Target, request.EffectType, "E_HDRP", HdrpToolSupport.FindVolumeEffectType);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "hdrp.volume.add-effect", Target = GameObjectToolSupport.BuildGameObjectRef(target), EffectType = request.EffectType, PlannedEffects = new List<HdrpPlannedEffect> { new HdrpPlannedEffect { Type = "hdrp.volume.add-effect", Target = GameObjectToolSupport.HierarchyPath(target), EffectType = request.EffectType } }, LogPath = ctx.LogPath });
        }
    }
}
#endif
