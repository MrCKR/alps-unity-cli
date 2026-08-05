#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics 域 IUnityCliTool 共享的基础设施 helper:物理设置构造、刚体信息、
    /// 射线查询、碰撞解析、模式规范化。仅供 physics 域自包含工具复用;
    /// 不实现 IUnityCliTool,不参与工具发现。
    /// </summary>
    public static class PhysicsToolSupport
    {
        /// <summary>拦截编译/导入/构建/PlayMode 过渡期的写入请求。</summary>
        public static void EnsureEditorNotBusy(string command)
        {
            SceneToolSupport.EnsureEditorNotBusy(command);
        }

        /// <summary>规范化 2d/3d 模式字符串;空或非 2d 返回 3d。</summary>
        public static string NormalizeMode(string mode)
        {
            return string.Equals(mode, "2d", StringComparison.OrdinalIgnoreCase) ? "2d" : "3d";
        }

        /// <summary>规范化 Vector2;零向量回退 right。</summary>
        public static Vector2 NormalizeVector2(Vector2 value)
        {
            return value.sqrMagnitude <= 0f ? Vector2.right : value.normalized;
        }

        /// <summary>规范化 Vector3;零向量回退 forward。</summary>
        public static Vector3 NormalizeVector3(Vector3 value)
        {
            return value.sqrMagnitude <= 0f ? Vector3.forward : value.normalized;
        }

        /// <summary>解析 ForceMode 枚举;空返回 Force。</summary>
        public static ForceMode ParseForceMode(string value)
        {
            if (string.IsNullOrEmpty(value)) return ForceMode.Force;
            return (ForceMode)Enum.Parse(typeof(ForceMode), value, true);
        }

        /// <summary>解析 CapsuleDirection2D;horizontal/0 返回 Horizontal,否则 Vertical。</summary>
        public static CapsuleDirection2D ParseCapsuleDirection2D(string value, int? numeric)
        {
            if (numeric.HasValue && numeric.Value == 0) return CapsuleDirection2D.Horizontal;
            if (string.Equals(value, "horizontal", StringComparison.OrdinalIgnoreCase) || value == "0") return CapsuleDirection2D.Horizontal;
            return CapsuleDirection2D.Vertical;
        }

        /// <summary>构造 PhysicsSettings DTO(3d+2d 合并视图)。</summary>
        public static PhysicsSettingsInfo BuildPhysicsSettings()
        {
            return new PhysicsSettingsInfo
            {
                Mode = "3d+2d",
                Gravity = new Vector3Data(Physics.gravity),
                Gravity2d = new Vector2Data(Physics2D.gravity),
                DefaultContactOffset = Physics.defaultContactOffset,
                BounceThreshold = Physics.bounceThreshold,
                DefaultSolverIterations = Physics.defaultSolverIterations,
                DefaultSolverVelocityIterations = Physics.defaultSolverVelocityIterations,
                QueriesHitTriggers = Physics.queriesHitTriggers,
                QueriesHitBackfaces = Physics.queriesHitBackfaces,
                QueriesStartInColliders = Physics2D.queriesStartInColliders,
                AutoSimulation = IsAutoSimulation3D(),
                SimulationMode2D = Physics2D.simulationMode.ToString()
            };
        }

        /// <summary>按 Unity 2022.3 SimulationMode 读取旧 autoSimulation 的等价语义。</summary>
        public static bool IsAutoSimulation3D()
        {
            return Physics.simulationMode != SimulationMode.Script;
        }

        /// <summary>设置 3D 物理自动/脚本模拟模式；自动模式固定恢复为 FixedUpdate。</summary>
        public static void SetAutoSimulation3D(bool enabled)
        {
            Physics.simulationMode = enabled ? SimulationMode.FixedUpdate : SimulationMode.Script;
        }

        /// <summary>构造 Rigidbody 摘要 DTO。</summary>
        public static RigidbodyInfo BuildRigidbodyInfo(Rigidbody rb)
        {
            return new RigidbodyInfo
            {
                Mass = rb.mass,
                Drag = rb.drag,
                AngularDrag = rb.angularDrag,
                UseGravity = rb.useGravity,
                IsKinematic = rb.isKinematic,
                Velocity = new Vector3Data(rb.velocity),
                AngularVelocity = new Vector3Data(rb.angularVelocity),
                CenterOfMass = new Vector3Data(rb.centerOfMass),
                CollisionDetectionMode = rb.collisionDetectionMode.ToString(),
                Interpolation = rb.interpolation.ToString(),
                Constraints = rb.constraints.ToString(),
                Sleeping = rb.IsSleeping()
            };
        }

        /// <summary>构造 Rigidbody2D 摘要 DTO。</summary>
        public static Rigidbody2DInfo BuildRigidbody2DInfo(Rigidbody2D rb)
        {
            return new Rigidbody2DInfo
            {
                Mass = rb.mass,
                Drag = rb.drag,
                AngularDrag = rb.angularDrag,
                GravityScale = rb.gravityScale,
                BodyType = rb.bodyType.ToString(),
                Velocity = new Vector2Data(rb.velocity),
                AngularVelocity = rb.angularVelocity,
                CollisionDetectionMode = rb.collisionDetectionMode.ToString(),
                Interpolation = rb.interpolation.ToString(),
                Constraints = rb.constraints.ToString(),
                Sleeping = rb.IsSleeping()
            };
        }

        /// <summary>构造 Collider 引用 DTO。</summary>
        public static ColliderRef BuildColliderRef(GameObject gameObject, string colliderType)
        {
            if (gameObject == null) return null;
            return new ColliderRef
            {
                Id = gameObject.GetInstanceID(),
                Name = gameObject.name,
                Path = GameObjectToolSupport.HierarchyPath(gameObject),
                Type = colliderType
            };
        }

        /// <summary>构造 3D 射线命中 DTO。</summary>
        public static RaycastHitInfo BuildRaycast3DHit(RaycastHit hit)
        {
            var gameObject = hit.collider != null ? hit.collider.gameObject : null;
            return new RaycastHitInfo
            {
                Distance = hit.distance,
                Point = new Vector3Data(hit.point),
                Normal = new Vector3Data(hit.normal),
                Collider = BuildColliderRef(gameObject, hit.collider == null ? null : hit.collider.GetType().Name)
            };
        }

        /// <summary>构造 2D 射线命中 DTO。</summary>
        public static RaycastHitInfo BuildRaycast2DHit(RaycastHit2D hit)
        {
            var gameObject = hit.collider != null ? hit.collider.gameObject : null;
            return new RaycastHitInfo
            {
                Distance = hit.distance,
                Point = new Vector3Data(new Vector3(hit.point.x, hit.point.y, 0)),
                Normal = new Vector3Data(new Vector3(hit.normal.x, hit.normal.y, 0)),
                Collider = BuildColliderRef(gameObject, hit.collider == null ? null : hit.collider.GetType().Name)
            };
        }

        /// <summary>构造射线查询参数 DTO(3D)。</summary>
        public static RaycastQuery BuildRaycast3DQuery(Vector3 origin, Vector3 direction, float distance, int layerMask)
        {
            return new RaycastQuery
            {
                Origin = new Vector3Data(origin),
                Direction = new Vector3Data(direction),
                Distance = distance,
                LayerMask = layerMask
            };
        }

        /// <summary>构造射线查询参数 DTO(2D)。</summary>
        public static RaycastQuery BuildRaycast2DQuery(Vector2 origin, Vector2 direction, float distance, int layerMask)
        {
            return new RaycastQuery
            {
                Origin = new Vector3Data(new Vector3(origin.x, origin.y, 0)),
                Direction = new Vector3Data(new Vector3(direction.x, direction.y, 0)),
                Distance = distance,
                LayerMask = layerMask
            };
        }

        /// <summary>解析 Layer 名称或数字为 layer index。</summary>
        public static int ParseLayer(string value)
        {
            return GameObjectToolSupport.ParseLayer(value);
        }

        /// <summary>在 GameObject 上按类型名+可选索引解析 Collider。</summary>
        public static Collider ResolveCollider(GameObject target, string colliderType, int colliderIndex)
        {
            var colliders = target.GetComponents<Collider>();
            if (!string.IsNullOrEmpty(colliderType))
            {
                var filtered = new List<Collider>();
                foreach (var c in colliders)
                {
                    if (string.Equals(c.GetType().Name, colliderType, StringComparison.OrdinalIgnoreCase) || string.Equals(c.GetType().FullName, colliderType, StringComparison.OrdinalIgnoreCase))
                        filtered.Add(c);
                }
                colliders = filtered.ToArray();
            }
            if (colliders.Length == 0)
            {
                throw new UnityCliBridge.BridgeCommandException("E_COLLIDER_NOT_FOUND", "No 3D Collider found on target: " + target.name, "Add a Collider component before assigning a PhysicMaterial.");
            }
            if (colliderIndex < 0 || colliderIndex >= colliders.Length)
            {
                throw new UnityCliBridge.BridgeCommandException("E_COLLIDER_INDEX_INVALID", "colliderIndex is outside the matching Collider component range.", "Use a colliderIndex from 0 to " + (colliders.Length - 1).ToString(CultureInfo.InvariantCulture) + ".");
            }
            return colliders[colliderIndex];
        }

        /// <summary>解析 ReflectionProbe(供 graphics 域复用)。</summary>
        public static ReflectionProbe ResolveReflectionProbe(string target)
        {
            var probes = UnityEngine.Object.FindObjectsOfType<ReflectionProbe>();
            if (string.IsNullOrEmpty(target)) return probes.Length > 0 ? probes[0] : null;
            foreach (var probe in probes)
            {
                if (GameObjectToolSupport.HierarchyPath(probe.gameObject) == target || probe.gameObject.name == target || probe.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target)
                    return probe;
            }
            return null;
        }
    }

    // ── physics 域 DTO ──

    public sealed class PhysicsSettingsInfo
    {
        public string Mode { get; set; }
        public Vector3Data Gravity { get; set; }
        public Vector2Data Gravity2d { get; set; }
        public double DefaultContactOffset { get; set; }
        public double BounceThreshold { get; set; }
        public long DefaultSolverIterations { get; set; }
        public long DefaultSolverVelocityIterations { get; set; }
        public bool QueriesHitTriggers { get; set; }
        public bool QueriesHitBackfaces { get; set; }
        public bool QueriesStartInColliders { get; set; }
        public bool AutoSimulation { get; set; }
        public string SimulationMode2D { get; set; }
    }

    public sealed class RigidbodyInfo
    {
        public double Mass { get; set; }
        public double Drag { get; set; }
        public double AngularDrag { get; set; }
        public bool UseGravity { get; set; }
        public bool IsKinematic { get; set; }
        public Vector3Data Velocity { get; set; }
        public Vector3Data AngularVelocity { get; set; }
        public Vector3Data CenterOfMass { get; set; }
        public string CollisionDetectionMode { get; set; }
        public string Interpolation { get; set; }
        public string Constraints { get; set; }
        public bool Sleeping { get; set; }
    }

    public sealed class Rigidbody2DInfo
    {
        public double Mass { get; set; }
        public double Drag { get; set; }
        public double AngularDrag { get; set; }
        public double GravityScale { get; set; }
        public string BodyType { get; set; }
        public Vector2Data Velocity { get; set; }
        public double AngularVelocity { get; set; }
        public string CollisionDetectionMode { get; set; }
        public string Interpolation { get; set; }
        public string Constraints { get; set; }
        public bool Sleeping { get; set; }
    }

    public sealed class Vector2Data
    {
        public Vector2Data() { }
        public Vector2Data(Vector2 v) { X = v.x; Y = v.y; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    public sealed class ColliderRef
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Type { get; set; }
    }

    public sealed class RaycastHitInfo
    {
        public double Distance { get; set; }
        public Vector3Data Point { get; set; }
        public Vector3Data Normal { get; set; }
        public ColliderRef Collider { get; set; }
    }

    public sealed class RaycastQuery
    {
        public Vector3Data Origin { get; set; }
        public Vector3Data Direction { get; set; }
        public double Distance { get; set; }
        public long LayerMask { get; set; }
    }

    public sealed class CollisionLayerEntry
    {
        public long Index { get; set; }
        public string Name { get; set; }
    }

    public sealed class CollisionMatrixRow
    {
        public long Layer { get; set; }
        public string Name { get; set; }
        public List<CollisionMatrixPair> Pairs { get; set; }
    }

    public sealed class CollisionMatrixPair
    {
        public long Layer { get; set; }
        public string Name { get; set; }
        public bool Ignored { get; set; }
    }

    public sealed class CollisionPairInfo
    {
        public long LayerA { get; set; }
        public long LayerB { get; set; }
        public bool Ignored { get; set; }
    }

    public sealed class PhysicMaterialInfo
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public double DynamicFriction { get; set; }
        public double StaticFriction { get; set; }
        public double Bounciness { get; set; }
    }

    public sealed class PhysicMaterialRef
    {
        public string Name { get; set; }
        public string Path { get; set; }
    }

    public sealed class ColliderAssignmentInfo
    {
        public string Type { get; set; }
        public long InstanceId { get; set; }
        public PhysicMaterialRef PreviousMaterial { get; set; }
        public PhysicMaterialRef Material { get; set; }
    }

    public sealed class PhysicsPlannedEffect
    {
        public string Type { get; set; }
        public string Property { get; set; }
        public string Value { get; set; }
        public string Target { get; set; }
    }

    public sealed class GameObjectTargetRef
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
    }

    public sealed class SimulationInfo
    {
        public bool Simulate { get; set; }
        public long Steps { get; set; }
        public double DeltaTime { get; set; }
        public double FixedDeltaTime { get; set; }
        public bool AutoSimulation { get; set; }
    }
}
#endif
