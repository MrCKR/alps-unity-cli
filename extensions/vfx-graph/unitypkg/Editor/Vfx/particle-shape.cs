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
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.particle-shape",
            Summary = "Update ParticleSystem shape type, dimensions, and random direction settings.",
            Description = "Update the shape module of an existing ParticleSystem. Supports enabled, shapeType, radius, angle, arc, length, box axes, and randomDirection.",
            Group = "vfx",
            DestructiveHint = false,
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

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
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

            VfxToolSupport.RequireFinite(request.Radius, "radius", 0); VfxToolSupport.RequireFinite(request.Angle, "angle"); VfxToolSupport.RequireFinite(request.Arc, "arc", 0, 360); VfxToolSupport.RequireFinite(request.Length, "length", 0); VfxToolSupport.RequireFinite(request.BoxX, "boxX", 0); VfxToolSupport.RequireFinite(request.BoxY, "boxY", 0); VfxToolSupport.RequireFinite(request.BoxZ, "boxZ", 0);
            if (!string.IsNullOrWhiteSpace(request.ShapeType)) VfxToolSupport.ParseEnum<ParticleSystemShapeType>(request.ShapeType, "E_PARTICLE_SHAPE_TYPE_INVALID", "Use a valid ParticleSystemShapeType value.");
            VfxToolSupport.RequireMutation(request.Enabled.HasValue || !string.IsNullOrWhiteSpace(request.ShapeType) || request.Radius.HasValue || request.Angle.HasValue || request.Arc.HasValue || request.Length.HasValue || request.BoxX.HasValue || request.BoxY.HasValue || request.BoxZ.HasValue || request.RandomDirection.HasValue, "vfx.particle-shape");
            var gameObjectId = particleSystem.gameObject.GetInstanceID();
            var effects = new List<VfxPlannedEffect>();
            ctx.CommitEffect();
            particleSystem = VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle-shape");
            var shape = particleSystem.shape;
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
                shape.scale = new Vector3((float)request.BoxX.Value, shape.scale.y, shape.scale.z);
            }
            if (request.BoxY.HasValue)
            {
                shape.scale = new Vector3(shape.scale.x, (float)request.BoxY.Value, shape.scale.z);
            }
            if (request.BoxZ.HasValue)
            {
                shape.scale = new Vector3(shape.scale.x, shape.scale.y, (float)request.BoxZ.Value);
            }
            if (request.BoxX.HasValue || request.BoxY.HasValue || request.BoxZ.HasValue)
            {
                effects.Add(VfxToolSupport.Effect("vfx.particle-shape", "box", VfxToolSupport.Number(shape.scale.x) + "," + VfxToolSupport.Number(shape.scale.y) + "," + VfxToolSupport.Number(shape.scale.z)));
            }
            if (request.RandomDirection.HasValue)
            {
                shape.randomDirectionAmount = request.RandomDirection.Value ? 1f : 0f;
                effects.Add(VfxToolSupport.Effect("vfx.particle-shape", "randomDirection", VfxToolSupport.Bool(request.RandomDirection.Value)));
            }

            var observed = VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle-shape");
            VfxToolSupport.VerifyParticleShapeFields(observed, request.Enabled, request.ShapeType, request.Radius, request.Angle, request.Arc, request.Length, request.BoxX, request.BoxY, request.BoxZ, request.RandomDirection);
            UnityCliSceneMutation.MarkSceneObjectDirty(observed);
            VfxToolSupport.MarkSceneDirty(observed.gameObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(observed.gameObject),
                Shape = new VfxParticleShapeInfo
                {
                    Enabled = observed.shape.enabled,
                    ShapeType = observed.shape.shapeType.ToString(),
                    Radius = observed.shape.radius,
                    Angle = observed.shape.angle,
                    Arc = observed.shape.arc,
                    Length = observed.shape.length,
                    Box = new Vector3Data(observed.shape.scale),
                    RandomDirection = observed.shape.randomDirectionAmount > 0f
                },
                PlannedEffects = effects
            });
        }
    }
}
#endif
