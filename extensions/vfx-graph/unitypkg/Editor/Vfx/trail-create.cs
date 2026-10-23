#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>创建并配置一个 TrailRenderer。</summary>
    public sealed class VfxTrailCreateTool : IUnityCliTool<VfxTrailCreateTool.Request, VfxTrailCreateTool.Result>
    {
        public sealed class Request : TrailRendererFields { public string Name { get; set; } }
        public sealed class Result { public GameObjectRef Target { get; set; } public VfxTrailRendererInfo Trail { get; set; } public List<VfxPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.trail-create", Summary = "Create and configure a TrailRenderer.",
            Description = "Create one scene GameObject with a TrailRenderer and apply width, gradient, material, lifetime, emission, alignment, texture, and vertex settings.",
            Group = "vfx",SideEffects = new[] { "scene-object-create", "component-add", "trail-renderer-edit" },
            Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Create a dash trail", Input = new Request { Name = "DashTrail", Time = 0.5, Emitting = true } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.EnsureEditorReady("vfx.trail-create");
            VfxToolSupport.PrepareTrailRendererFields(request);
            ctx.CommitEffect();
            var gameObject = VfxToolSupport.CreateSceneObject(string.IsNullOrWhiteSpace(request.Name) ? "Trail" : request.Name);
            var gameObjectId = gameObject.GetInstanceID();
            var trail = gameObject.AddComponent<TrailRenderer>();
            var effects = new List<VfxPlannedEffect> { new VfxPlannedEffect { Type = "vfx.trail-create", Target = GameObjectToolSupport.HierarchyPath(gameObject), Property = "component", Value = "added" } };
            VfxToolSupport.ApplyTrailRendererFields(trail, request, effects, "vfx.trail-create");
            var observed = VfxToolSupport.ResolveObservedComponent<TrailRenderer>(gameObjectId, "vfx.trail-create");
            VfxToolSupport.VerifyTrailRendererFields(observed, request);
            UnityCliSceneMutation.MarkSceneObjectDirty(observed);
            VfxToolSupport.MarkSceneDirty(observed.gameObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Target = GameObjectToolSupport.BuildGameObjectRef(observed.gameObject), Trail = VfxToolSupport.BuildTrailRendererInfo(observed), PlannedEffects = effects });
        }
    }
}
#endif
