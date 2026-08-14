#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>urp.volume.add-effect：向 URP Volume 的内存 profile 添加效果。</summary>
    public sealed class UrpVolumeAddEffectTool : IUnityCliTool<UrpVolumeAddEffectTool.Request, UrpVolumeAddEffectTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public string EffectType { get; set; } }
        public sealed class Result { public List<UrpPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "urp.volume.add-effect",
            Summary = "Add a VolumeComponent effect to a scene-session URP Volume profile.",
            Description = "Adds effectType to the in-memory profile on the target URP Volume. target accepts a GameObject name or hierarchy path. The profile and scene remain unsaved. Requires com.unity.render-pipelines.universal and an active UniversalRenderPipelineAsset.",
            Group = "urp",DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, SideEffects = new[] { "scene-modify", "volume-profile-edit" }, Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Add Vignette", Input = new Request { Target = "GlobalVolume", EffectType = "Vignette" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.volume.add-effect");
            UrpToolSupport.EnsureUrpAvailable();
            UrpVolumeOperations.ValidateAddEffect(request.Target, request.EffectType, "E_URP", UrpToolSupport.FindVolumeEffectType);
            ctx.CommitEffect();
            var added = UrpVolumeOperations.AddEffect(request.Target, request.EffectType, "E_URP", UrpToolSupport.FindVolumeEffectType);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { PlannedEffects = new List<UrpPlannedEffect> { new UrpPlannedEffect { Type = "urp.volume.add-effect", Target = GameObjectToolSupport.HierarchyPath(added.GameObject), EffectType = added.ObservedEffectType } }});
        }
    }
}
#endif
