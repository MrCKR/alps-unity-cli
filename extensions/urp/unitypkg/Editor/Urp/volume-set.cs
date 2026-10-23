#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>urp.volume-set：修改 URP Volume 内存 profile 中的效果参数。</summary>
    public sealed class UrpVolumeSetTool : IUnityCliTool<UrpVolumeSetTool.Request, UrpVolumeSetTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public string EffectType { get; set; } [UnityCliRequired] public string Property { get; set; } [UnityCliRequired] public string Value { get; set; } }
        public sealed class Result { public List<UrpPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "urp.volume-set",
            Summary = "Set a scalar serialized parameter on a scene-session URP Volume effect.",
            Description = "Sets property on effectType in the target URP Volume's in-memory profile, verifies its typed SerializedProperty readback, and returns the observed value. property is a SerializedProperty path such as intensity.m_Value. Only scalar serialized values are supported. The profile and scene remain unsaved. Requires com.unity.render-pipelines.universal and an active UniversalRenderPipelineAsset.",
            Group = "urp",DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, SideEffects = new[] { "scene-modify", "volume-profile-edit" }, Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Set Bloom intensity", Input = new Request { Target = "GlobalVolume", EffectType = "Bloom", Property = "intensity.m_Value", Value = "1.5" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.volume-set");
            UrpToolSupport.EnsureUrpAvailable();
            var plan = UrpVolumeOperations.ValidateSet(request.Target, request.EffectType, request.Property, request.Value, "E_URP", UrpToolSupport.FindVolumeEffectType);
            ctx.CommitEffect();
            var set = UrpVolumeOperations.Set(plan, "E_URP");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { PlannedEffects = new List<UrpPlannedEffect> { new UrpPlannedEffect { Type = "urp.volume-set", Target = GameObjectToolSupport.HierarchyPath(set.GameObject), EffectType = set.ObservedEffectType, Property = request.Property, Value = set.Value } }});
        }
    }
}
#endif
