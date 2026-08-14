#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.force — 对目标 Rigidbody 施加力/扭矩/爆炸力。
    /// </summary>
    public class PhysicsForceTool : IUnityCliTool<PhysicsForceTool.Request, PhysicsForceTool.Result>
    {
        private enum ForceApplicationType { Normal, Explosion }

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
            public Vector3Data Force { get; set; }
            public Vector3Data Velocity { get; set; }
            public Vector3Data Position { get; set; }
            public List<PhysicsPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.force",
            Summary = "Apply force, torque, or explosion force to a target Rigidbody.",
            Description = "Resolve a target GameObject with a Rigidbody, then apply force (x/y/z), torque, or explosion force (forceType=explosion). mode defaults to Force.",
            Group = "physics",DestructiveHint = false, SideEffects = new[] { "scene-modify" },
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Apply upward force", Input = new Request { Target = "Player", Y = 100f } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var target = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "physics.force requires --target.");
            var rigidbody = target.GetComponent<Rigidbody>();
            if (rigidbody == null)
                throw new UnityCliBridge.BridgeCommandException("E_RIGIDBODY_NOT_FOUND", "Target has no Rigidbody: " + target.name, "Run physics.rigidbody to add/configure a Rigidbody first.");
            var force = new Vector3(request.X, request.Y, request.Z);
            var torque = new Vector3(request.TorqueX.GetValueOrDefault(), request.TorqueY, request.TorqueZ);
            var explosionPosition = new Vector3(request.ExplosionX, request.ExplosionY, request.ExplosionZ);
            ValidateFinite(force, "force");
            ValidateFinite(torque, "torque");
            ValidateFinite(explosionPosition, "explosion position");
            ValidateFinite(request.ExplosionRadius, "explosionRadius");
            ValidateFinite(request.ExplosionForce, "explosionForce");
            ValidateFinite(request.UpwardsModifier, "upwardsModifier");
            if (request.ExplosionRadius < 0f)
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_FORCE_VALUE_INVALID", "explosionRadius must be non-negative.", "Pass an explosionRadius of zero or greater.");
            ForceMode mode;
            if (!Enum.TryParse(request.Mode ?? ForceMode.Force.ToString(), true, out mode) || !Enum.IsDefined(typeof(ForceMode), mode))
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_FORCE_MODE_INVALID", "mode must be a declared ForceMode value.", "Use Force, Acceleration, Impulse, or VelocityChange.");
            ForceApplicationType forceType;
            if (!Enum.TryParse(request.ForceType ?? ForceApplicationType.Normal.ToString(), true, out forceType) || !Enum.IsDefined(typeof(ForceApplicationType), forceType))
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_FORCE_TYPE_INVALID", "forceType must be normal or explosion.", "Pass forceType=normal or forceType=explosion.");
            if (rigidbody.isKinematic)
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_FORCE_KINEMATIC", "Cannot prove force on a kinematic Rigidbody.", "Set isKinematic=false with physics.rigidbody before applying force.");
            var requestedNonZero = forceType == ForceApplicationType.Explosion
                ? request.ExplosionForce != 0f || (request.TorqueX.HasValue && torque.sqrMagnitude > 0f)
                : force.sqrMagnitude > 0f || (request.TorqueX.HasValue && torque.sqrMagnitude > 0f);
            if (!requestedNonZero)
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_FORCE_VALUE_INVALID", "physics.force requires a non-zero force, explosion force, or torque.", "Pass at least one non-zero force component.");
            var targetId = target.GetInstanceID();
            var rigidbodyId = rigidbody.GetInstanceID();
            ctx.CommitEffect();
            // AddForce 的速度积分发生在后续 physics step；先强制 sleep，再以同步 wake 状态证明 API 已接受非零请求。
            rigidbody.Sleep();
            if (!rigidbody.IsSleeping())
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_FORCE_STATE_UNPROVABLE", "Rigidbody did not enter the proof sleep state before applying force.", "Inspect the target Rigidbody and retry after the Editor is idle.");
            if (forceType == ForceApplicationType.Explosion)
                rigidbody.AddExplosionForce(request.ExplosionForce, explosionPosition, request.ExplosionRadius, request.UpwardsModifier, mode);
            else rigidbody.AddForce(force, mode);
            if (request.TorqueX.HasValue) rigidbody.AddTorque(torque, mode);
            UnityCliSceneMutation.MarkSceneDirty(target.scene);
            var observedTarget = EditorUtility.InstanceIDToObject(targetId) as GameObject;
            Rigidbody observedRigidbody = null;
            if (observedTarget != null)
                foreach (var candidate in observedTarget.GetComponents<Rigidbody>())
                    if (candidate != null && candidate.GetInstanceID() == rigidbodyId) { observedRigidbody = candidate; break; }
            if (observedRigidbody == null)
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_FORCE_STATE_UNPROVABLE", "Rigidbody could not be freshly resolved after applying force.", "Inspect the target Rigidbody and retry after the Editor is idle.");
            var observedVelocity = observedRigidbody.velocity;
            var observedPosition = observedRigidbody.position;
            if (observedRigidbody.IsSleeping())
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_FORCE_STATE_UNPROVABLE", "The non-zero force request did not wake the freshly resolved Rigidbody.", "Inspect the target Rigidbody before submitting a new force request.");
            target = observedTarget;
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Force = new Vector3Data(force), Velocity = new Vector3Data(observedVelocity), Position = new Vector3Data(observedPosition),
                PlannedEffects = new List<PhysicsPlannedEffect> { new PhysicsPlannedEffect { Type = "physics.force", Target = GameObjectToolSupport.HierarchyPath(target) } }
            });
        }

        private static void ValidateFinite(Vector3 value, string name)
        {
            ValidateFinite(value.x, name + ".x"); ValidateFinite(value.y, name + ".y"); ValidateFinite(value.z, name + ".z");
        }

        private static void ValidateFinite(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_FORCE_VALUE_INVALID", name + " must be finite.", "Pass a finite float value.");
        }
    }
}
#endif
