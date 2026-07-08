#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// vfx.particle-shape - 修改 ParticleSystem 形状模块。
    /// </summary>
    public class VfxParticleShapeTool : IUnityCliTool<VfxParticleShapeTool.Request, VfxParticleShapeTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Target { get; set; }
            public bool? Enabled { get; set; }
            public string ShapeType { get; set; }
            public double? Radius { get; set; }
            public double? Angle { get; set; }
            public double? Arc { get; set; }
            public double? Length { get; set; }
            public double? BoxX { get; set; }
            public double? BoxY { get; set; }
            public double? BoxZ { get; set; }
            public bool? RandomDirection { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public VfxParticleShapeInfo Shape { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.particle-shape",
            Summary = "Update ParticleSystem shape type, dimensions, and random direction settings.",
            Description = "Update the shape module of an existing ParticleSystem. Supports enabled, shapeType, radius, angle, arc, length, box axes, and randomDirection.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "particle-shape-edit" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Switch to cone", Input = new Request { Target = "Effects/Dust", ShapeType = "Cone", Radius = 0.5, Angle = 15 } },
                new ToolExample { Description = "Edit box extents", Input = new Request { Target = "Effects/Dust", BoxX = 1, BoxY = 2, BoxZ = 3 } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            VfxToolSupport.EnsureEditorReady("vfx.particle-shape");
            var particleSystem = VfxToolSupport.ResolveParticleSystem(request.Target);
            if (particleSystem == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_PARTICLE_SYSTEM_NOT_FOUND",
                    "ParticleSystem not found: " + request.Target,
                    "Pass a GameObject name / hierarchy path with a ParticleSystem component.");
            }

            var shape = particleSystem.shape;
            var effects = new List<VfxPlannedEffect>();
            if (request.Enabled.HasValue)
            {
                shape.enabled = request.Enabled.Value;
                effects.Add(VfxToolSupport.Effect("vfx.particle-shape", "enabled", VfxToolSupport.Bool(shape.enabled)));
            }
            if (!string.IsNullOrWhiteSpace(request.ShapeType))
            {
                shape.shapeType = VfxToolSupport.ParseEnum<ParticleSystemShapeType>(request.ShapeType, "E_PARTICLE_SHAPE_TYPE_INVALID", "Use a valid ParticleSystemShapeType value.");
                effects.Add(VfxToolSupport.Effect("vfx.particle-shape", "shapeType", shape.shapeType.ToString()));
            }
            if (request.Radius.HasValue)
            {
                shape.radius = (float)request.Radius.Value;
                effects.Add(VfxToolSupport.Effect("vfx.particle-shape", "radius", VfxToolSupport.Number(shape.radius)));
            }
            if (request.Angle.HasValue)
            {
                shape.angle = (float)request.Angle.Value;
                effects.Add(VfxToolSupport.Effect("vfx.particle-shape", "angle", VfxToolSupport.Number(shape.angle)));
            }
            if (request.Arc.HasValue)
            {
                shape.arc = (float)request.Arc.Value;
                effects.Add(VfxToolSupport.Effect("vfx.particle-shape", "arc", VfxToolSupport.Number(shape.arc)));
            }
            if (request.Length.HasValue)
            {
                shape.length = (float)request.Length.Value;
                effects.Add(VfxToolSupport.Effect("vfx.particle-shape", "length", VfxToolSupport.Number(shape.length)));
            }
            if (request.BoxX.HasValue)
            {
                shape.box = new Vector3((float)request.BoxX.Value, shape.box.y, shape.box.z);
            }
            if (request.BoxY.HasValue)
            {
                shape.box = new Vector3(shape.box.x, (float)request.BoxY.Value, shape.box.z);
            }
            if (request.BoxZ.HasValue)
            {
                shape.box = new Vector3(shape.box.x, shape.box.y, (float)request.BoxZ.Value);
            }
            if (request.BoxX.HasValue || request.BoxY.HasValue || request.BoxZ.HasValue)
            {
                effects.Add(VfxToolSupport.Effect("vfx.particle-shape", "box", VfxToolSupport.Number(shape.box.x) + "," + VfxToolSupport.Number(shape.box.y) + "," + VfxToolSupport.Number(shape.box.z)));
            }
            if (request.RandomDirection.HasValue)
            {
                shape.randomDirection = request.RandomDirection.Value;
                effects.Add(VfxToolSupport.Effect("vfx.particle-shape", "randomDirection", VfxToolSupport.Bool(shape.randomDirection)));
            }

            EditorUtility.SetDirty(particleSystem);
            VfxToolSupport.MarkSceneDirty(particleSystem.gameObject);
            return new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(particleSystem.gameObject),
                Shape = new VfxParticleShapeInfo
                {
                    Enabled = shape.enabled,
                    ShapeType = shape.shapeType.ToString(),
                    Radius = shape.radius,
                    Angle = shape.angle,
                    Arc = shape.arc,
                    Length = shape.length,
                    Box = new Vector3Data(shape.box),
                    RandomDirection = shape.randomDirection
                },
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
