#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.material — 创建/修改 PhysicMaterial 资产(.physicMaterial/.asset)。
    /// 对齐旧 PhysicsMaterialCommand handler;asset-write。
    /// </summary>
    public class PhysicsMaterialTool : IUnityCliTool<PhysicsMaterialTool.Request, PhysicsMaterialTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public float? DynamicFriction { get; set; }
            public float? StaticFriction { get; set; }
            public float? Bounciness { get; set; }
        }

        public sealed class Result
        {
            public PhysicMaterialInfo Material { get; set; }
            public List<PhysicsPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.material",
            Summary = "Create or update a PhysicMaterial asset at an Assets/*.physicMaterial path.",
            Description = "Create a new PhysicMaterial asset or update an existing one. Path must be under Assets/ and end with .physicMaterial or .asset. Supports dynamicFriction, staticFriction, bounciness.",
            Group = "physics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "asset-write" },
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Create bouncy material", Input = new Request { Path = "Assets/Physics/Bouncy.physicMaterial", Bounciness = 0.8f } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var path = SceneToolSupport.NormalizeAssetPath(request.Path);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase) || (!path.EndsWith(".physicMaterial", System.StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase)))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_MATERIAL_PATH_INVALID", "Physics material path must be under Assets/ and end with .physicMaterial or .asset.", "Pass Assets/Physics/Bouncy.physicMaterial.");
            }
            var material = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(path);
            if (material == null)
            {
                SceneToolSupport.CreateAssetDirectory(path);
                material = new PhysicMaterial(Path.GetFileNameWithoutExtension(path));
                AssetDatabase.CreateAsset(material, path);
            }
            if (request.DynamicFriction.HasValue) material.dynamicFriction = request.DynamicFriction.Value;
            if (request.StaticFriction.HasValue) material.staticFriction = request.StaticFriction.Value;
            if (request.Bounciness.HasValue) material.bounciness = request.Bounciness.Value;
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            return new Result
            {
                Material = new PhysicMaterialInfo { Path = path, Name = material.name, DynamicFriction = material.dynamicFriction, StaticFriction = material.staticFriction, Bounciness = material.bounciness },
                PlannedEffects = new List<PhysicsPlannedEffect> { new PhysicsPlannedEffect { Type = "physics.material", Property = "path", Value = path } }
            };
        }
    }

    /// <summary>
    /// physics.assign-material — 将 PhysicMaterial 资产分配到 GameObject 的 Collider。
    /// 对齐旧 PhysicsAssignMaterial handler;支持 dryRun。
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
            public bool? DryRun { get; set; }
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
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
            var dryRun = request.DryRun ?? false;
            if (!dryRun)
            {
                Undo.RecordObject(collider, "Assign PhysicMaterial");
                collider.sharedMaterial = material;
                EditorUtility.SetDirty(collider);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(target.scene);
            }
            return new Result
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
            };
        }
    }
}
#endif
