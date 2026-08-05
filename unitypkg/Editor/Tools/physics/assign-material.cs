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
            Group = "physics",DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "scene-modify" },
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
            var targetId = target.GetInstanceID();
            var colliderId = collider.GetInstanceID();
            var previous = collider.sharedMaterial;
            var dryRun = ctx.DryRun;
            if (!dryRun)
            {
                ctx.CommitEffect();
                UnityCliSceneMutation.RecordObject(collider, "Assign PhysicMaterial");
                collider.sharedMaterial = material;
                UnityCliSceneMutation.MarkSceneObjectDirty(collider);
                UnityCliSceneMutation.MarkSceneDirty(target.scene);
            }
            // mutation 后同时重解析目标与组件，禁止使用写前 Collider 引用作为 proof。
            var observedTarget = dryRun ? target : EditorUtility.InstanceIDToObject(targetId) as GameObject;
            var observedCollider = dryRun ? collider : EditorUtility.InstanceIDToObject(colliderId) as Collider;
            var observedMaterial = observedCollider == null ? null : observedCollider.sharedMaterial;
            if (observedTarget == null || observedCollider == null || (!dryRun && observedMaterial != material))
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_ASSIGN_MATERIAL_STATE_MISMATCH", "Collider material did not match the requested PhysicMaterial.", "Inspect the Collider material after the Editor is idle and retry.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                DryRun = dryRun,
                Target = new GameObjectTargetRef { Id = observedTarget.GetInstanceID(), Name = observedTarget.name, Path = GameObjectToolSupport.HierarchyPath(observedTarget) },
                Collider = new ColliderAssignmentInfo
                {
                    Type = observedCollider.GetType().Name,
                    InstanceId = observedCollider.GetInstanceID(),
                    PreviousMaterial = previous == null ? null : new PhysicMaterialRef { Name = previous.name, Path = AssetDatabase.GetAssetPath(previous) },
                    Material = observedMaterial == null ? null : new PhysicMaterialRef { Name = observedMaterial.name, Path = AssetDatabase.GetAssetPath(observedMaterial) }
                },
                PlannedEffects = new List<PhysicsPlannedEffect> { new PhysicsPlannedEffect { Type = "physics.assign-material", Target = GameObjectToolSupport.HierarchyPath(observedTarget) } }
            });
        }
    }
}
#endif
