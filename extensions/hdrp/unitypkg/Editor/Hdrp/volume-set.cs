#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>hdrp.volume.set：修改 HDRP Volume 内存 profile 中的效果参数。</summary>
    public sealed class HdrpVolumeSetTool : IUnityCliTool<HdrpVolumeSetTool.Request, HdrpVolumeSetTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public string EffectType { get; set; } [UnityCliRequired] public string Property { get; set; } [UnityCliRequired] public string Value { get; set; } }
        public sealed class Result { public List<HdrpPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new()
        {
            Name = "hdrp.volume.set", Summary = "Set a scalar serialized parameter on a scene-session HDRP Volume effect.",
            Description = "Sets property on effectType in the target HDRP Volume's in-memory profile, verifies its typed SerializedProperty readback, and returns the observed value. property is a SerializedProperty path such as intensity.m_Value. Only scalar serialized values are supported. The profile and scene remain unsaved. Requires com.unity.render-pipelines.high-definition and an active HDRenderPipelineAsset.",
            Group = "hdrp",DestructiveHint = false, RequiresPackages = new[] { "com.unity.render-pipelines.high-definition" }, SideEffects = new[] { "scene-modify", "volume-profile-edit" }, Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Set Bloom intensity", Input = new Request { Target = "GlobalVolume", EffectType = "Bloom", Property = "intensity.m_Value", Value = "1.5" } } }
        };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("hdrp.volume.set"); HdrpToolSupport.EnsureHdrpAvailable();
            var plan = HdrpVolumeOperations.ValidateSet(request.Target, request.EffectType, request.Property, request.Value, "E_HDRP", HdrpToolSupport.FindVolumeEffectType);
            ctx.CommitEffect();
            var set = HdrpVolumeOperations.Set(plan, "E_HDRP");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { PlannedEffects = new List<HdrpPlannedEffect> { new HdrpPlannedEffect { Type = "hdrp.volume.set", Target = GameObjectToolSupport.HierarchyPath(set.GameObject), EffectType = set.ObservedEffectType, Property = request.Property, Value = set.Value } }});
        }
    }
}
#endif
