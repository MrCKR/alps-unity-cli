#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// vfx.line-positions - 更新 LineRenderer 顶点集合或单个顶点。
    /// </summary>
    public class VfxLinePositionsTool : IUnityCliTool<VfxLinePositionsTool.Request, VfxLinePositionsTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Target { get; set; }
            public List<Vector3Input> Positions { get; set; }
            public int? Index { get; set; }
            public Vector3Input Position { get; set; }
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
            Name = "vfx.line-positions",
            Summary = "Replace all LineRenderer positions or patch a single indexed point.",
            Description = "Update a LineRenderer's position set. Provide positions to replace the full array, or provide index plus position to patch one point in place.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "line-renderer-edit" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Replace all positions", Input = new Request { Target = "AimLine", Positions = new List<Vector3Input> { new Vector3Input { X = 0, Y = 0, Z = 0 }, new Vector3Input { X = 4, Y = 0, Z = 0 } } } },
                new ToolExample { Description = "Patch one point", Input = new Request { Target = "AimLine", Index = 1, Position = new Vector3Input { X = 2, Y = 1, Z = 0 } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.EnsureEditorReady("vfx.line-positions");
            var line = VfxToolSupport.ResolveLineRenderer(request.Target);
            if (line == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_LINE_RENDERER_NOT_FOUND",
                    "LineRenderer not found: " + request.Target,
                    "Pass a GameObject name / hierarchy path with a LineRenderer component.");
            }

            var effects = new List<VfxPlannedEffect>();
            if (request.Positions != null && request.Positions.Count > 0)
            {
                var positions = VfxToolSupport.ToUnityVector3Array(request.Positions);
                line.positionCount = positions.Length;
                line.SetPositions(positions);
                effects.Add(new VfxPlannedEffect { Type = "vfx.line-positions", Target = GameObjectToolSupport.HierarchyPath(line.gameObject), Property = "positions", Value = positions.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            if (request.Index.HasValue && request.Position != null)
            {
                if (request.Index.Value < 0 || request.Index.Value >= line.positionCount)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_INDEX_OUT_OF_RANGE",
                        "Line position index out of range: " + request.Index.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "Use an index between 0 and " + (line.positionCount - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
                }
                line.SetPosition(request.Index.Value, VfxToolSupport.ToUnityVector3(request.Position));
                effects.Add(new VfxPlannedEffect { Type = "vfx.line-positions", Target = GameObjectToolSupport.HierarchyPath(line.gameObject), Property = "index", Value = request.Index.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }

            UnityCliSceneMutation.MarkSceneObjectDirty(line);
            VfxToolSupport.MarkSceneDirty(line.gameObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(line.gameObject),
                Line = VfxToolSupport.BuildLineRendererInfo(line),
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            });
        }
    }
}
#endif
