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
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.particle-create",
            Summary = "Create or initialize a ParticleSystem on a target GameObject and apply main module defaults.",
            Description = "Create a new GameObject when target is omitted, ensure a ParticleSystem and ParticleSystemRenderer exist, and apply any provided main module fields such as duration, startLifetime, startSpeed, size, color, looping, and simulation space.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = true,
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            VfxToolSupport.EnsureEditorReady("vfx.particle-create");
            var gameObject = string.IsNullOrWhiteSpace(request.Target)
                ? VfxToolSupport.CreateSceneObject(string.IsNullOrWhiteSpace(request.Name) ? "ParticleSystem" : request.Name)
                : GameObjectToolSupport.ResolveUniqueTarget(request.Target, "vfx.particle-create requires target.");
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
            EditorUtility.SetDirty(particleSystem);
            VfxToolSupport.MarkSceneDirty(gameObject);
            return new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                ParticleSystem = VfxToolSupport.BuildParticleSystemInfo(particleSystem),
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
