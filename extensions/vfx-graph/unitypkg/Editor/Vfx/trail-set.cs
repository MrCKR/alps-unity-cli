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
        public sealed class Result { public GameObjectRef Target { get; set; } public VfxTrailRendererInfo Trail { get; set; } public List<VfxPlannedEffect> PlannedEffects { get; set; } public string LogPath { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.trail.set", Summary = "Configure an existing TrailRenderer.",
            Description = "Apply width, gradient, material, lifetime, emission, alignment, texture, and vertex settings to one existing TrailRenderer.",
            Group = "vfx", Version = "1.0.0", SideEffects = new[] { "trail-renderer-edit" },
            Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Update the matrix trail", Input = new Request { Target = "Trail", StartWidth = 0.2, EndWidth = 0 } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.EnsureEditorReady("vfx.trail.set");
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "vfx.trail.set requires target.");
            var trail = gameObject.GetComponent<TrailRenderer>();
            if (trail == null) throw new UnityCliBridge.BridgeCommandException("E_TRAIL_RENDERER_NOT_FOUND", "TrailRenderer not found on target: " + GameObjectToolSupport.HierarchyPath(gameObject), "Create one with vfx.trail.create or pass a target with TrailRenderer.");
            var effects = new List<VfxPlannedEffect>();
            VfxToolSupport.ApplyTrailRendererFields(trail, request, effects, "vfx.trail.set");
            UnityCliSceneMutation.MarkSceneObjectDirty(trail);
            VfxToolSupport.MarkSceneDirty(gameObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Target = GameObjectToolSupport.BuildGameObjectRef(gameObject), Trail = VfxToolSupport.BuildTrailRendererInfo(trail), PlannedEffects = effects, LogPath = context.LogPath });
        }
    }
}
#endif
