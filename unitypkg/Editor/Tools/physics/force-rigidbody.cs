#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.force — 对目标 Rigidbody 施加力/扭矩/爆炸力。
    /// 对齐旧 PhysicsForceCommand handler。
    /// </summary>
    public class PhysicsForceTool : IUnityCliTool<PhysicsForceTool.Request, PhysicsForceTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
            public string Mode { get; set; }
            public string ForceType { get; set; }
            public float? TorqueX { get; set; }
            public float TorqueY { get; set; }
            public float TorqueZ { get; set; }
            public float ExplosionX { get; set; }
            public float ExplosionY { get; set; }
            public float ExplosionZ { get; set; }
            public float ExplosionRadius { get; set; } = 10f;
            public float ExplosionForce { get; set; } = 500f;
            public float UpwardsModifier { get; set; }
        }

        public sealed class Result
        {
            public GameObjectTargetRef Target { get; set; }
            public Vector3Data Force { get; set; }
            public string Mode { get; set; }
            public List<PhysicsPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.force",
            Summary = "Apply force, torque, or explosion force to a target Rigidbody.",
            Description = "Resolve a target GameObject with a Rigidbody, then apply force (x/y/z), torque, or explosion force (forceType=explosion). mode defaults to Force.",
            Group = "physics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" },
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Apply upward force", Input = new Request { Target = "Player", Y = 100f } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var target = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "physics.force requires --target.");
            var rigidbody = target.GetComponent<Rigidbody>();
            if (rigidbody == null)
                throw new UnityCliBridge.BridgeCommandException("E_RIGIDBODY_NOT_FOUND", "Target has no Rigidbody: " + target.name, "Run physics.rigidbody to add/configure a Rigidbody first.");
            var force = new Vector3(request.X, request.Y, request.Z);
            var mode = PhysicsToolSupport.ParseForceMode(request.Mode);
            var forceType = request.ForceType ?? "normal";
            if (forceType.ToLowerInvariant() == "explosion")
            {
                var expPos = new Vector3(request.ExplosionX, request.ExplosionY, request.ExplosionZ);
                rigidbody.AddExplosionForce(request.ExplosionForce, expPos, request.ExplosionRadius, request.UpwardsModifier, mode);
            }
            else
            {
                rigidbody.AddForce(force, mode);
            }
            if (request.TorqueX.HasValue)
            {
                var torqueVec = new Vector3(request.TorqueX.Value, request.TorqueY, request.TorqueZ);
                rigidbody.AddTorque(torqueVec, mode);
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(target.scene);
            return new Result
            {
                Target = new GameObjectTargetRef { Id = target.GetInstanceID(), Name = target.name, Path = GameObjectToolSupport.HierarchyPath(target) },
                Force = new Vector3Data(force), Mode = mode.ToString(),
                PlannedEffects = new List<PhysicsPlannedEffect> { new PhysicsPlannedEffect { Type = "physics.force", Target = GameObjectToolSupport.HierarchyPath(target) } }
            };
        }
    }

    /// <summary>
    /// physics.rigidbody — 添加/配置/校验目标 GameObject 的 Rigidbody。
    /// 对齐旧 PhysicsRigidbodyCommand handler。
    /// </summary>
    public class PhysicsRigidbodyTool : IUnityCliTool<PhysicsRigidbodyTool.Request, PhysicsRigidbodyTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public float? Mass { get; set; }
            public float? Drag { get; set; }
            public float? AngularDrag { get; set; }
            public bool? UseGravity { get; set; }
            public bool? IsKinematic { get; set; }
            public bool Validate { get; set; }
        }

        public sealed class Result
        {
            public bool Validate { get; set; }
            public GameObjectTargetRef Target { get; set; }
            public bool HasRigidbody { get; set; }
            public RigidbodyInfo Rigidbody { get; set; }
            public Rigidbody2DInfo Rigidbody2D { get; set; }
            public List<string> NextActions { get; set; }
            public List<PhysicsPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.rigidbody",
            Summary = "Add, configure, or validate a Rigidbody on a target GameObject.",
            Description = "Add a Rigidbody (if missing) and configure mass/drag/angularDrag/useGravity/isKinematic. validate=true reads existing Rigidbody/Rigidbody2D without mutating.",
            Group = "physics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" },
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Validate rigidbody", Input = new Request { Target = "Player", Validate = true } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var target = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "physics.rigidbody requires --target.");
            var rigidbody = target.GetComponent<Rigidbody>();
            if (request.Validate)
            {
                var rigidbody2D = target.GetComponent<Rigidbody2D>();
                return new Result
                {
                    Validate = true,
                    Target = new GameObjectTargetRef { Id = target.GetInstanceID(), Name = target.name, Path = GameObjectToolSupport.HierarchyPath(target) },
                    HasRigidbody = rigidbody != null,
                    Rigidbody = rigidbody == null ? null : PhysicsToolSupport.BuildRigidbodyInfo(rigidbody),
                    Rigidbody2D = rigidbody2D == null ? null : PhysicsToolSupport.BuildRigidbody2DInfo(rigidbody2D),
                    NextActions = rigidbody != null ? new List<string>() : new List<string> { "No Rigidbody is attached; run physics.rigidbody without validate=true to add and configure one." }
                };
            }
            if (rigidbody == null) rigidbody = Undo.AddComponent<Rigidbody>(target);
            if (request.Mass.HasValue) rigidbody.mass = request.Mass.Value;
            if (request.Drag.HasValue) rigidbody.drag = request.Drag.Value;
            if (request.AngularDrag.HasValue) rigidbody.angularDrag = request.AngularDrag.Value;
            if (request.UseGravity.HasValue) rigidbody.useGravity = request.UseGravity.Value;
            if (request.IsKinematic.HasValue) rigidbody.isKinematic = request.IsKinematic.Value;
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(target.scene);
            return new Result
            {
                Target = new GameObjectTargetRef { Id = target.GetInstanceID(), Name = target.name, Path = GameObjectToolSupport.HierarchyPath(target) },
                Rigidbody = PhysicsToolSupport.BuildRigidbodyInfo(rigidbody),
                PlannedEffects = new List<PhysicsPlannedEffect> { new PhysicsPlannedEffect { Type = "physics.rigidbody", Target = GameObjectToolSupport.HierarchyPath(target) } }
            };
        }
    }
}
#endif
