#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// vfx.particle-module - 修改多个 ParticleSystem 子模块。
    /// </summary>
    public class VfxParticleModuleTool : IUnityCliTool<VfxParticleModuleTool.Request, VfxParticleModuleTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Target { get; set; }
            [UnityCliRequired]
            public string Module { get; set; }
            public bool? Enabled { get; set; }
            public string Color { get; set; }
            public double? Size { get; set; }
            public double? Rotation { get; set; }
            public double? X { get; set; }
            public double? Y { get; set; }
            public double? Z { get; set; }
            public string Space { get; set; }
            public double? SpeedModifier { get; set; }
            public double? Strength { get; set; }
            public double? Frequency { get; set; }
            public double? ScrollSpeed { get; set; }
            public int? OctaveCount { get; set; }
            public string Quality { get; set; }
            public string CollisionType { get; set; }
            public string CollisionMode { get; set; }
            public double? Dampen { get; set; }
            public double? Bounce { get; set; }
            public double? LifetimeLoss { get; set; }
            public double? MinKillSpeed { get; set; }
            public double? MaxKillSpeed { get; set; }
            public double? RadiusScale { get; set; }
            public double? Ratio { get; set; }
            public double? Lifetime { get; set; }
            public double? Width { get; set; }
            public string SubEmitterAction { get; set; }
            public string SubEmitterTarget { get; set; }
            public string SubEmitterType { get; set; }
            public string SubEmitterProperties { get; set; }
            public int? Index { get; set; }
            public string Mode { get; set; }
            public double? Curve { get; set; }
            public double? Speed { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public string Module { get; set; }
            public VfxParticleSystemInfo ParticleSystem { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.particle-module",
            Summary = "Configure color, size, velocity, force, noise, collision, trails, sub-emitters, and other ParticleSystem modules.",
            Description = "Configure a named ParticleSystem module on an existing target. module supports color, size, rotation, velocity, force, noise, collision, subEmitter, textureAnimation, lights, trails, inheritVelocity, and limitVelocity. The accepted input fields depend on the selected module.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "particle-module-edit" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Enable trails module", Input = new Request { Target = "Effects/Dust", Module = "trails", Enabled = true, Ratio = 0.5, Lifetime = 0.75 } },
                new ToolExample { Description = "Configure velocity over lifetime", Input = new Request { Target = "Effects/Dust", Module = "velocity", Enabled = true, X = 0, Y = 2, Z = 0, Space = "World" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            VfxToolSupport.EnsureEditorReady("vfx.particle-module");
            var particleSystem = VfxToolSupport.ResolveParticleSystem(request.Target);
            if (particleSystem == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_PARTICLE_SYSTEM_NOT_FOUND",
                    "ParticleSystem not found: " + request.Target,
                    "Pass a GameObject name / hierarchy path with a ParticleSystem component.");
            }

            var effects = new List<VfxPlannedEffect>();
            if (!VfxToolSupport.ApplyParticleModuleFields(particleSystem, request, effects))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_UNKNOWN_MODULE",
                    "Unknown or unsupported particle module: " + request.Module,
                    "Use color, size, rotation, velocity, force, noise, collision, subEmitter, textureAnimation, lights, trails, inheritVelocity, or limitVelocity.");
            }

            EditorUtility.SetDirty(particleSystem);
            VfxToolSupport.MarkSceneDirty(particleSystem.gameObject);
            return new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(particleSystem.gameObject),
                Module = request.Module,
                ParticleSystem = VfxToolSupport.BuildParticleSystemInfo(particleSystem),
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
