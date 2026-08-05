#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// vfx.particle-create - 创建或初始化 ParticleSystem。
    /// </summary>
    public class VfxParticleCreateTool : IUnityCliTool<VfxParticleCreateTool.Request, VfxParticleCreateTool.Result>
    {
        public sealed class Request : ParticleMainFields
        {
            public string Target { get; set; }
            public string Name { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public VfxParticleSystemInfo ParticleSystem { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.particle-create",
            Summary = "Create or initialize a ParticleSystem on a target GameObject and apply main module defaults.",
            Description = "Create a new GameObject when target is omitted, ensure a ParticleSystem and ParticleSystemRenderer exist, and apply any provided main module fields such as duration, startLifetime, startSpeed, size, color, looping, and simulation space.",
            Group = "vfx",
            DestructiveHint = false,
            SideEffects = new[] { "scene-object-create", "component-add", "particle-main-edit" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Create a new particle system", Input = new Request { Name = "Dust", Duration = 2, StartLifetime = 1, StartSpeed = 3 } },
                new ToolExample { Description = "Ensure particle system on target", Input = new Request { Target = "Effects/Dust", Loop = true, MaxParticles = 512 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.EnsureEditorReady("vfx.particle-create");
            VfxToolSupport.PrepareParticleMainFields(request);
            var preparedTarget = string.IsNullOrWhiteSpace(request.Target) ? null : GameObjectToolSupport.ResolveUniqueTarget(request.Target, "vfx.particle-create requires target.");
            ctx.CommitEffect();
            var gameObject = preparedTarget == null
                ? VfxToolSupport.CreateSceneObject(string.IsNullOrWhiteSpace(request.Name) ? "ParticleSystem" : request.Name)
                : VfxToolSupport.FindGameObjectByTarget(preparedTarget.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (gameObject == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_OBSERVED_TARGET_MISSING", "vfx.particle-create target disappeared after commit.", "Retry after checking the target still exists.");
            var gameObjectId = gameObject.GetInstanceID();
            var particleSystem = gameObject.GetComponent<ParticleSystem>();
            var effects = new List<VfxPlannedEffect>();
            if (particleSystem == null)
            {
                particleSystem = gameObject.AddComponent<ParticleSystem>();
                if (gameObject.GetComponent<ParticleSystemRenderer>() == null)
                {
                    gameObject.AddComponent<ParticleSystemRenderer>();
                }
                effects.Add(new VfxPlannedEffect { Type = "vfx.particle-create", Target = GameObjectToolSupport.HierarchyPath(gameObject), Property = "component", Value = "added" });
            }

            VfxToolSupport.ApplyParticleMainFields(particleSystem, request, "vfx.particle-create", effects);
            var observed = VfxToolSupport.ResolveObservedComponent<ParticleSystem>(gameObjectId, "vfx.particle-create");
            VfxToolSupport.VerifyParticleMainFields(observed, request);
            UnityCliSceneMutation.MarkSceneObjectDirty(observed);
            VfxToolSupport.MarkSceneDirty(observed.gameObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(observed.gameObject),
                ParticleSystem = VfxToolSupport.BuildParticleSystemInfo(observed),
                PlannedEffects = effects
            });
        }
    }
}
#endif
