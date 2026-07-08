#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// vfx.trail - 创建或修改 TrailRenderer。
    /// </summary>
    public class VfxTrailTool : IUnityCliTool<VfxTrailTool.Request, VfxTrailTool.Result>
    {
        public sealed class Request : TrailRendererFields
        {
            public string Target { get; set; }
            public string Name { get; set; }
            public bool? Create { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public VfxTrailRendererInfo Trail { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.trail",
            Summary = "Create or configure a TrailRenderer, including widths, gradient, material, lifetime, and emission state.",
            Description = "Create a new GameObject when target is omitted, or operate on an existing target. When create=true, the tool adds a TrailRenderer if one is missing; then it applies width, gradient, material, lifetime, emission, alignment, texture mode, and vertex settings.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "scene-object-create", "component-add", "trail-renderer-edit" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Create a dash trail", Input = new Request { Name = "DashTrail", Create = true, Time = 0.5, Emitting = true } },
                new ToolExample { Description = "Update an existing trail", Input = new Request { Target = "Player/Trail", StartWidth = 0.2, EndWidth = 0 } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            VfxToolSupport.EnsureEditorReady("vfx.trail");
            var gameObject = string.IsNullOrWhiteSpace(request.Target)
                ? VfxToolSupport.CreateSceneObject(string.IsNullOrWhiteSpace(request.Name) ? "Trail" : request.Name)
                : GameObjectToolSupport.ResolveUniqueTarget(request.Target, "vfx.trail requires target.");
            var trail = gameObject.GetComponent<TrailRenderer>();
            var shouldCreate = request.Create == true || string.IsNullOrWhiteSpace(request.Target);
            var effects = new List<VfxPlannedEffect>();
            if (trail == null && shouldCreate)
            {
                trail = gameObject.AddComponent<TrailRenderer>();
                effects.Add(new VfxPlannedEffect { Type = "vfx.trail", Target = GameObjectToolSupport.HierarchyPath(gameObject), Property = "component", Value = "added" });
            }
            if (trail == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_TRAIL_RENDERER_NOT_FOUND",
                    "TrailRenderer not found on target: " + GameObjectToolSupport.HierarchyPath(gameObject),
                    "Pass create=true to add one or choose a target with TrailRenderer.");
            }
            VfxToolSupport.ApplyTrailRendererFields(trail, request, effects);
            EditorUtility.SetDirty(trail);
            VfxToolSupport.MarkSceneDirty(gameObject);
            return new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                Trail = VfxToolSupport.BuildTrailRendererInfo(trail),
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
