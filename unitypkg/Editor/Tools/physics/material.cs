#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.material — 创建/修改 PhysicMaterial 资产(.physicMaterial/.asset)。
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
            Group = "physics", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "asset-write" },
            Target = "editor", PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Create bouncy material", Input = new Request { Path = "Assets/Physics/Bouncy.physicMaterial", Bounciness = 0.8f } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
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
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Material = new PhysicMaterialInfo { Path = path, Name = material.name, DynamicFriction = material.dynamicFriction, StaticFriction = material.staticFriction, Bounciness = material.bounciness },
                PlannedEffects = new List<PhysicsPlannedEffect> { new PhysicsPlannedEffect { Type = "physics.material", Property = "path", Value = path } }
            });
        }
    }
}
#endif
