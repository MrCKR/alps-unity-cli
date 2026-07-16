#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.assign-material — 将 PhysicMaterial 资产分配到 GameObject 的 Collider。
    /// </summary>
    public class PhysicsAssignMaterialTool : IUnityCliTool<PhysicsAssignMaterialTool.Request, PhysicsAssignMaterialTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string MaterialPath { get; set; }
            public string Path { get; set; }
            public string Collider { get; set; }
            public int ColliderIndex { get; set; }
        }

        public sealed class Result
        {
            public bool DryRun { get; set; }
            public GameObjectTargetRef Target { get; set; }
            public ColliderAssignmentInfo Collider { get; set; }
            public List<PhysicsPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.assign-material",
            Summary = "Assign a PhysicMaterial asset to a Collider on a target GameObject, with dry-run support.",
            Description = "Resolve a target GameObject, load a PhysicMaterial asset, and assign it to the matching Collider. Supports collider type filter and index. dryRun=true previews without mutating.",
            Group = "physics", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "scene-modify" },
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Assign bouncy material", Input = new Request { Target = "Player", MaterialPath = "Assets/Physics/Bouncy.physicMaterial" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var target = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "physics.assign-material requires --target.");
            var path = SceneToolSupport.NormalizeAssetPath(request.MaterialPath ?? request.Path);
            if (string.IsNullOrEmpty(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_MATERIAL_PATH_REQUIRED", "physics.assign-material requires a PhysicMaterial asset path.", "Pass materialPath like Assets/Physics/Bouncy.physicMaterial.");
            }
            var material = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(path);
            if (material == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_MATERIAL_NOT_FOUND", "PhysicMaterial asset not found: " + path, "Create it with physics.material first, then rerun physics.assign-material.");
            }
            var collider = PhysicsToolSupport.ResolveCollider(target, request.Collider, request.ColliderIndex);
            var previous = collider.sharedMaterial;
            var dryRun = ctx.DryRun;
            if (!dryRun)
            {
                UnityCliSceneMutation.RecordObject(collider, "Assign PhysicMaterial");
                collider.sharedMaterial = material;
                UnityCliSceneMutation.MarkSceneObjectDirty(collider);
                UnityCliSceneMutation.MarkSceneDirty(target.scene);
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                DryRun = dryRun,
                Target = new GameObjectTargetRef { Id = target.GetInstanceID(), Name = target.name, Path = GameObjectToolSupport.HierarchyPath(target) },
                Collider = new ColliderAssignmentInfo
                {
                    Type = collider.GetType().Name,
                    InstanceId = collider.GetInstanceID(),
                    PreviousMaterial = previous == null ? null : new PhysicMaterialRef { Name = previous.name, Path = AssetDatabase.GetAssetPath(previous) },
                    Material = new PhysicMaterialRef { Name = material.name, Path = path }
                },
                PlannedEffects = new List<PhysicsPlannedEffect> { new PhysicsPlannedEffect { Type = "physics.assign-material", Target = GameObjectToolSupport.HierarchyPath(target) } }
            });
        }
    }
}
#endif
