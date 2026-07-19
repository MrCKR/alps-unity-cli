#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// vfx.line-create - 创建或初始化 LineRenderer。
    /// </summary>
    public class VfxLineCreateTool : IUnityCliTool<VfxLineCreateTool.Request, VfxLineCreateTool.Result>
    {
        public sealed class Request : LineRendererFields
        {
            public string Target { get; set; }
            public string Name { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public VfxLineRendererInfo Line { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.line-create",
            Summary = "Create or initialize a LineRenderer and configure widths, colors, material, positions, and render settings.",
            Description = "Create a new GameObject when target is omitted, ensure a LineRenderer exists, and configure widths, widthCurve, colors, colorGradient, material, positions, looping, world-space mode, alignment, texture mode, and corner/cap vertex counts.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "scene-object-create", "component-add", "line-renderer-edit" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Create a line with two points", Input = new Request { Name = "AimLine", Positions = new List<Vector3Input> { new Vector3Input { X = 0, Y = 0, Z = 0 }, new Vector3Input { X = 1, Y = 2, Z = 3 } } } },
                new ToolExample { Description = "Configure widths and material", Input = new Request { Target = "AimLine", StartWidth = 0.2, EndWidth = 0.05, Material = "Assets/Materials/Line.mat" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.EnsureEditorReady("vfx.line-create");
            VfxToolSupport.PrepareLineRendererFields(request);
            var preparedTarget = string.IsNullOrWhiteSpace(request.Target) ? null : GameObjectToolSupport.ResolveUniqueTarget(request.Target, "vfx.line-create requires target.");
            ctx.CommitEffect();
            var gameObject = preparedTarget == null
                ? VfxToolSupport.CreateSceneObject(string.IsNullOrWhiteSpace(request.Name) ? "Line" : request.Name)
                : VfxToolSupport.FindGameObjectByTarget(preparedTarget.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (gameObject == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_OBSERVED_TARGET_MISSING", "vfx.line-create target disappeared after commit.", "Retry after checking the target still exists.");
            var gameObjectId = gameObject.GetInstanceID();
            var line = gameObject.GetComponent<LineRenderer>();
            var effects = new List<VfxPlannedEffect>();
            if (line == null)
            {
                line = gameObject.AddComponent<LineRenderer>();
                effects.Add(new VfxPlannedEffect { Type = "vfx.line-create", Target = GameObjectToolSupport.HierarchyPath(gameObject), Property = "component", Value = "added" });
            }
            VfxToolSupport.ApplyLineRendererFields(line, request, "vfx.line-create", effects);
            var observed = VfxToolSupport.ResolveObservedComponent<LineRenderer>(gameObjectId, "vfx.line-create");
            VfxToolSupport.VerifyLineRendererFields(observed, request);
            UnityCliSceneMutation.MarkSceneObjectDirty(observed);
            VfxToolSupport.MarkSceneDirty(observed.gameObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(observed.gameObject),
                Line = VfxToolSupport.BuildLineRendererInfo(observed),
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            });
        }
    }
}
#endif
