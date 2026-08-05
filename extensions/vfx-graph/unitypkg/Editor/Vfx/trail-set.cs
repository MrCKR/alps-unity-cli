#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>配置现有 TrailRenderer。</summary>
    public sealed class VfxTrailSetTool : IUnityCliTool<VfxTrailSetTool.Request, VfxTrailSetTool.Result>
    {
        public sealed class Request : TrailRendererFields { [UnityCliRequired] public string Target { get; set; } }
        public sealed class Result { public GameObjectRef Target { get; set; } public VfxTrailRendererInfo Trail { get; set; } public List<VfxPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.trail.set", Summary = "Configure an existing TrailRenderer.",
            Description = "Apply width, gradient, material, lifetime, emission, alignment, texture, and vertex settings to one existing TrailRenderer.",
            Group = "vfx",SideEffects = new[] { "trail-renderer-edit" },
            Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Update the matrix trail", Input = new Request { Target = "Trail", StartWidth = 0.2, EndWidth = 0 } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.EnsureEditorReady("vfx.trail.set");
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "vfx.trail.set requires target.");
            var trail = gameObject.GetComponent<TrailRenderer>();
            if (trail == null) throw new UnityCliBridge.BridgeCommandException("E_TRAIL_RENDERER_NOT_FOUND", "TrailRenderer not found on target: " + GameObjectToolSupport.HierarchyPath(gameObject), "Create one with vfx.trail.create or pass a target with TrailRenderer.");
            VfxToolSupport.PrepareTrailRendererFields(request);
            VfxToolSupport.RequireMutation(request.Time.HasValue || request.StartWidth.HasValue || request.EndWidth.HasValue || request.WidthMultiplier.HasValue || request.WidthCurve?.Count > 0 || !string.IsNullOrWhiteSpace(request.StartColor) || !string.IsNullOrWhiteSpace(request.EndColor) || request.ColorGradient?.Count > 0 || !string.IsNullOrWhiteSpace(request.Material) || request.MinVertexDistance.HasValue || request.Autodestruct.HasValue || request.Emitting.HasValue || !string.IsNullOrWhiteSpace(request.Alignment) || !string.IsNullOrWhiteSpace(request.TextureMode) || request.NumCornerVertices.HasValue || request.NumCapVertices.HasValue, "vfx.trail.set");
            var gameObjectId = gameObject.GetInstanceID();
            var effects = new List<VfxPlannedEffect>();
            ctx.CommitEffect();
            trail = VfxToolSupport.ResolveObservedComponent<TrailRenderer>(gameObjectId, "vfx.trail.set");
            VfxToolSupport.ApplyTrailRendererFields(trail, request, effects, "vfx.trail.set");
            var observed = VfxToolSupport.ResolveObservedComponent<TrailRenderer>(gameObjectId, "vfx.trail.set");
            VfxToolSupport.VerifyTrailRendererFields(observed, request);
            UnityCliSceneMutation.MarkSceneObjectDirty(observed);
            VfxToolSupport.MarkSceneDirty(observed.gameObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Target = GameObjectToolSupport.BuildGameObjectRef(observed.gameObject), Trail = VfxToolSupport.BuildTrailRendererInfo(observed), PlannedEffects = effects });
        }
    }
}
#endif
