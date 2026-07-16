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
            var gameObject = string.IsNullOrWhiteSpace(request.Target)
                ? VfxToolSupport.CreateSceneObject(string.IsNullOrWhiteSpace(request.Name) ? "Line" : request.Name)
                : GameObjectToolSupport.ResolveUniqueTarget(request.Target, "vfx.line-create requires target.");
            var line = gameObject.GetComponent<LineRenderer>();
            var effects = new List<VfxPlannedEffect>();
            if (line == null)
            {
                line = gameObject.AddComponent<LineRenderer>();
                effects.Add(new VfxPlannedEffect { Type = "vfx.line-create", Target = GameObjectToolSupport.HierarchyPath(gameObject), Property = "component", Value = "added" });
            }
            VfxToolSupport.ApplyLineRendererFields(line, request, "vfx.line-create", effects);
            UnityCliSceneMutation.MarkSceneObjectDirty(line);
            VfxToolSupport.MarkSceneDirty(gameObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                Line = VfxToolSupport.BuildLineRendererInfo(line),
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            });
        }
    }
}
#endif
