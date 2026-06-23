#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterPhysicsCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "physics.settings", "physics", true, PhysicsSettingsCommand);
            RegisterCommand(registry, "physics.collision-matrix", "physics", true, PhysicsCollisionMatrix);
            RegisterCommand(registry, "physics.material", "physics", true, PhysicsMaterialCommand);
            RegisterCommand(registry, "physics.joint", "physics", true, PhysicsJointCommand);
            RegisterCommand(registry, "physics.force", "physics", true, PhysicsForceCommand);
            RegisterCommand(registry, "physics.overlap", "physics", false, PhysicsOverlap);
            RegisterCommand(registry, "physics.rigidbody", "physics", true, PhysicsRigidbodyCommand);
            RegisterCommand(registry, "physics.raycast", "physics", false, PhysicsRaycast);
            RegisterCommand(registry, "physics.simulation", "physics", true, PhysicsSimulation);
            RegisterCommand(registry, "physics.raycast-all", "physics", false, PhysicsRaycastAll);
            RegisterCommand(registry, "physics.linecast", "physics", false, PhysicsLinecast);
            RegisterCommand(registry, "physics.shapecast", "physics", false, PhysicsShapecast);
            RegisterCommand(registry, "physics.validate", "physics", false, PhysicsValidate);
        }

        private static string PhysicsSettingsCommand(BridgeRequest request)
        {
            var effects = new List<string>();
            ApplyPhysicsVector3(request, "gravity", effects);
            ApplyPhysicsVector2(request, "gravity2d", effects);
            ApplyPhysicsFloat(request, "defaultContactOffset", "defaultContactOffset", delegate(float value) { Physics.defaultContactOffset = value; }, effects);
            ApplyPhysicsFloat(request, "bounceThreshold", "bounceThreshold", delegate(float value) { Physics.bounceThreshold = value; }, effects);
            ApplyPhysicsInt(request, "defaultSolverIterations", "defaultSolverIterations", delegate(int value) { Physics.defaultSolverIterations = value; }, effects);
            ApplyPhysicsInt(request, "defaultSolverVelocityIterations", "defaultSolverVelocityIterations", delegate(int value) { Physics.defaultSolverVelocityIterations = value; }, effects);
            ApplyPhysicsBool(request, "queriesHitTriggers", "queriesHitTriggers", delegate(bool value) { Physics.queriesHitTriggers = value; }, effects);
            ApplyPhysicsBool(request, "queriesHitBackfaces", "queriesHitBackfaces", delegate(bool value) { Physics.queriesHitBackfaces = value; }, effects);
            ApplyPhysicsBool(request, "queriesStartInColliders", "queriesStartInColliders", delegate(bool value) { Physics2D.queriesStartInColliders = value; }, effects);
            ApplyPhysicsBool(request, "autoSimulation", "autoSimulation", delegate(bool value) { Physics.autoSimulation = value; }, effects);
            if (request.HasArg("property"))
            {
                ApplyGenericPhysicsSetting(request, effects);
            }
            if (effects.Count > 0)
            {
                AssetDatabase.SaveAssets();
                WriteLog("physics_settings_updated effects=" + effects.Count.ToString(CultureInfo.InvariantCulture));
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("physics", PhysicsSettingsJson())
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string PhysicsCollisionMatrix(BridgeRequest request)
        {
            var layerA = ParseLayer(request.StringArg("layerA"));
            var layerB = ParseLayer(request.StringArg("layerB"));
            var ignore = request.OptionalBoolArg("ignore");
            if (ignore.HasValue)
            {
                Physics.IgnoreLayerCollision(layerA, layerB, ignore.Value);
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropObject("collision", w => w.Prop("layerA", (long)layerA).Prop("layerB", (long)layerB).Prop("ignored", Physics.GetIgnoreLayerCollision(layerA, layerB)))
                .PropArray("plannedEffects", arr => { if (ignore.HasValue) arr.RawValue(EffectJson("physics.collision-matrix", "ignore", Bool(ignore.Value))); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string PhysicsMaterialCommand(BridgeRequest request)
        {
            var path = NormalizeAssetPath(request.StringArg("path"));
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".physicMaterial", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_PHYSICS_MATERIAL_PATH_INVALID", "Physics material path must be under Assets/ and end with .physicMaterial or .asset.", "Pass Assets/Physics/Bouncy.physicMaterial.");
            }
            var material = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(path);
            if (material == null)
            {
                CreateAssetDirectory(path);
                material = new PhysicMaterial(Path.GetFileNameWithoutExtension(path));
                AssetDatabase.CreateAsset(material, path);
            }
            if (request.HasArg("dynamicFriction")) material.dynamicFriction = request.OptionalFloatArg("dynamicFriction").Value;
            if (request.HasArg("staticFriction")) material.staticFriction = request.OptionalFloatArg("staticFriction").Value;
            if (request.HasArg("bounciness")) material.bounciness = request.OptionalFloatArg("bounciness").Value;
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("material", PhysicsMaterialJson(path, material))
                .PropArray("plannedEffects", arr => arr.RawValue(EffectJson("physics.material", "path", Quote(path))))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string PhysicsJointCommand(BridgeRequest request)
        {
            var target = ResolveUniqueTarget(request.StringArg("target"), "physics.joint requires --target.");
            var typeName = request.StringArg("type") ?? "FixedJoint";
            var type = ResolveComponentType(typeName.EndsWith("Joint", StringComparison.OrdinalIgnoreCase) ? typeName : typeName + "Joint");
            if (!typeof(Joint).IsAssignableFrom(type)) throw new BridgeCommandException("E_PHYSICS_JOINT_TYPE_INVALID", "Joint type must derive from UnityEngine.Joint: " + typeName, "Use FixedJoint, HingeJoint, SpringJoint, or CharacterJoint.");
            var joint = target.GetComponent(type) as Joint ?? Undo.AddComponent(target, type) as Joint;
            var connected = request.StringArg("connectedBody");
            if (!string.IsNullOrEmpty(connected))
            {
                var connectedGo = ResolveUniqueTarget(connected, "Connected body target not found.");
                joint.connectedBody = connectedGo.GetComponent<Rigidbody>();
            }
            EditorSceneManager.MarkSceneDirty(target.scene);
            return SuccessJson(ComponentMutationJson("physics.joint", target, joint, null));
        }

        private static string PhysicsForceCommand(BridgeRequest request)
        {
            var target = ResolveUniqueTarget(request.StringArg("target"), "physics.force requires --target.");
            var rigidbody = target.GetComponent<Rigidbody>();
            if (rigidbody == null) throw new BridgeCommandException("E_RIGIDBODY_NOT_FOUND", "Target has no Rigidbody: " + target.name, "Run physics.rigidbody to add/configure a Rigidbody first.");
            var force = new Vector3(request.FloatArg("x", 0f), request.FloatArg("y", 0f), request.FloatArg("z", 0f));
            var mode = ParseForceMode(request.StringArg("mode"));
            var forceType = request.StringArg("forceType") ?? request.StringArg("force_type") ?? "normal";
            var torque = request.OptionalFloatArg("torqueX");
            // explosion: 3D only, 在爆炸中心施加衰减力
            if (string.Equals(forceType, "explosion", StringComparison.OrdinalIgnoreCase))
            {
                var expPos = new Vector3(request.FloatArg("explosionX", 0f), request.FloatArg("explosionY", 0f), request.FloatArg("explosionZ", 0f));
                var expRadius = request.FloatArg("explosionRadius", 10f);
                var expForce = request.FloatArg("explosionForce", 500f);
                var upMod = request.FloatArg("upwardsModifier", 0f);
                rigidbody.AddExplosionForce(expForce, expPos, expRadius, upMod, mode);
            }
            else
            {
                rigidbody.AddForce(force, mode);
            }
            // torque: 3D 用 [x,y,z], 2D 用 [z]
            if (torque.HasValue)
            {
                var torqueVec = new Vector3(torque.Value, request.FloatArg("torqueY", 0f), request.FloatArg("torqueZ", 0f));
                rigidbody.AddTorque(torqueVec, mode);
            }
            EditorSceneManager.MarkSceneDirty(target.scene);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(target))
                .PropRaw("force", Vector3Json(force))
                .Prop("mode", mode.ToString())
                .PropArray("plannedEffects", arr => arr.RawValue(EffectJson("physics.force", "target", HierarchyPath(target))))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string PhysicsOverlap(BridgeRequest request)
        {
            var origin = new Vector3(request.FloatArg("originX", 0f), request.FloatArg("originY", 0f), request.FloatArg("originZ", 0f));
            var layerMask = request.IntArg("layerMask", Physics.DefaultRaycastLayers);
            Collider[] colliders;
            if (string.Equals(request.StringArg("shape"), "box", StringComparison.OrdinalIgnoreCase))
            {
                var halfExtents = new Vector3(request.FloatArg("sizeX", 1f), request.FloatArg("sizeY", 1f), request.FloatArg("sizeZ", 1f)) * 0.5f;
                colliders = Physics.OverlapBox(origin, halfExtents, Quaternion.identity, layerMask);
            }
            else
            {
                colliders = Physics.OverlapSphere(origin, Math.Max(0.0001f, request.FloatArg("radius", 1f)), layerMask);
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("origin", Vector3Json(origin))
                .Prop("hitCount", (long)colliders.Length)
                .PropArray("hits", arr => { foreach (var collider in colliders) arr.RawValue(ColliderJson(collider.gameObject, collider.GetType().Name)); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string PhysicsRigidbodyCommand(BridgeRequest request)
        {
            var target = ResolveUniqueTarget(request.StringArg("target"), "physics.rigidbody requires --target.");
            var rigidbody = target.GetComponent<Rigidbody>();
            if (rigidbody == null)
            {
                rigidbody = Undo.AddComponent<Rigidbody>(target);
            }
            if (request.HasArg("mass")) rigidbody.mass = request.OptionalFloatArg("mass").Value;
            if (request.HasArg("drag")) rigidbody.drag = request.OptionalFloatArg("drag").Value;
            if (request.HasArg("angularDrag")) rigidbody.angularDrag = request.OptionalFloatArg("angularDrag").Value;
            if (request.HasArg("useGravity")) rigidbody.useGravity = request.OptionalBoolArg("useGravity").Value;
            if (request.HasArg("isKinematic")) rigidbody.isKinematic = request.OptionalBoolArg("isKinematic").Value;
            EditorSceneManager.MarkSceneDirty(target.scene);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(target))
                .PropRaw("rigidbody", RigidbodyJson(rigidbody))
                .PropArray("plannedEffects", arr => arr.RawValue(EffectJson("physics.rigidbody", "target", HierarchyPath(target))))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        /// <summary>raycast-all：返回所有命中。对齐 CoplayDev raycast_all。</summary>
        private static string PhysicsRaycastAll(BridgeRequest request)
        {
            var mode = NormalizeMode(request.StringArg("mode"));
            var distance = Math.Max(0f, request.FloatArg("distance", 100f));
            var layerMask = request.IntArg("layerMask", Physics.DefaultRaycastLayers);
            var origin = new Vector3(request.FloatArg("originX", 0f), request.FloatArg("originY", 0f), request.FloatArg("originZ", 0f));
            var direction = NormalizeVector3(new Vector3(request.FloatArg("directionX", 0f), request.FloatArg("directionY", 0f), request.FloatArg("directionZ", 1f)));
            if (mode == "2d")
            {
                var hits = Physics2D.RaycastAll(new Vector2(origin.x, origin.y), new Vector2(direction.x, direction.y), distance, layerMask);
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "physics.raycast-all").Prop("mode", "2d")
                    .Prop("hitCount", (long)hits.Length).PropRaw("hits", Raycast2DHitsJson(hits)).Prop("logPath", LogPath()).EndObject().ToString());
            }
            var hits3d = Physics.RaycastAll(origin, direction, distance, layerMask);
            return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "physics.raycast-all").Prop("mode", "3d")
                .Prop("hitCount", (long)hits3d.Length).PropRaw("hits", Raycast3DHitsJson(hits3d)).Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>linecast：检测两点间是否有碰撞。对齐 CoplayDev linecast。</summary>
        private static string PhysicsLinecast(BridgeRequest request)
        {
            var mode = NormalizeMode(request.StringArg("mode"));
            var layerMask = request.IntArg("layerMask", Physics.DefaultRaycastLayers);
            var start = new Vector3(request.FloatArg("startX", 0f), request.FloatArg("startY", 0f), request.FloatArg("startZ", 0f));
            var end = new Vector3(request.FloatArg("endX", 0f), request.FloatArg("endY", 0f), request.FloatArg("endZ", 0f));
            if (mode == "2d")
            {
                var hit = Physics2D.Linecast(new Vector2(start.x, start.y), new Vector2(end.x, end.y), layerMask);
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "physics.linecast").Prop("mode", "2d")
                    .Prop("hit", hit.collider != null).Prop("logPath", LogPath()).EndObject().ToString());
            }
            RaycastHit hit3d;
            var hit3dFound = Physics.Linecast(start, end, out hit3d, layerMask);
            return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "physics.linecast").Prop("mode", "3d")
                .Prop("hit", hit3dFound).PropRaw("hitInfo", hit3dFound ? Raycast3DHitJson(hit3d) : "null").Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>shapecast：球体/盒子投射。对齐 CoplayDev shapecast。</summary>
        private static string PhysicsShapecast(BridgeRequest request)
        {
            var shape = request.StringArg("shape") ?? "sphere";
            var distance = Math.Max(0f, request.FloatArg("distance", 100f));
            var layerMask = request.IntArg("layerMask", Physics.DefaultRaycastLayers);
            var origin = new Vector3(request.FloatArg("originX", 0f), request.FloatArg("originY", 0f), request.FloatArg("originZ", 0f));
            var direction = NormalizeVector3(new Vector3(request.FloatArg("directionX", 0f), request.FloatArg("directionY", 0f), request.FloatArg("directionZ", 1f)));
            var radius = request.FloatArg("radius", 0.5f);
            var halfExtents = new Vector3(request.FloatArg("halfExtentX", 0.5f), request.FloatArg("halfExtentY", 0.5f), request.FloatArg("halfExtentZ", 0.5f));
            RaycastHit[] hits;
            if (shape == "box")
            {
                hits = Physics.BoxCastAll(origin, halfExtents, direction, Quaternion.identity, distance, layerMask);
            }
            else if (shape == "capsule")
            {
                // capsule: point1/point2 定义胶囊两端, 或用 height + capsuleDirection
                var height = request.FloatArg("height", 1f);
                var capDir = request.IntArg("capsuleDirection", 1); // 0=X,1=Y,2=Z
                Vector3 point1, point2;
                var p1Provided = request.OptionalFloatArg("point1X");
                if (p1Provided.HasValue)
                {
                    point1 = new Vector3(request.FloatArg("point1X", 0f), request.FloatArg("point1Y", 0f), request.FloatArg("point1Z", 0f));
                    point2 = new Vector3(request.FloatArg("point2X", 0f), request.FloatArg("point2Y", 0f), request.FloatArg("point2Z", 0f));
                }
                else
                {
                    var halfH = height / 2f - radius;
                    if (halfH < 0) halfH = 0;
                    var offset = capDir == 0 ? new Vector3(halfH, 0, 0) : capDir == 2 ? new Vector3(0, 0, halfH) : new Vector3(0, halfH, 0);
                    point1 = origin + offset;
                    point2 = origin - offset;
                }
                hits = Physics.CapsuleCastAll(point1, point2, radius, direction, distance, layerMask);
            }
            else
            {
                hits = Physics.SphereCastAll(origin, radius, direction, distance, layerMask);
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "physics.shapecast").Prop("shape", shape)
                .Prop("hitCount", (long)hits.Length).PropRaw("hits", Raycast3DHitsJson(hits)).Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>physics.validate：校验 Rigidbody/Collider 配置。对齐 CoplayDev physics validate。</summary>
        private static string PhysicsValidate(BridgeRequest request)
        {
            var target = request.StringArg("target") ?? request.StringArg("path");
            var go = string.IsNullOrEmpty(target) ? null : ObjectRefResolver.FindGameObjectByPath(target);
            var issues = new List<string>();
            if (go == null)
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "physics.validate requires a target GameObject.", "Pass --target <path>.");
            }
            var rb = go.GetComponent<Rigidbody>();
            var rb2d = go.GetComponent<Rigidbody2D>();
            var colliders = go.GetComponents<Collider>();
            var colliders2d = go.GetComponents<Collider2D>();
            if (rb != null && colliders.Length == 0 && colliders2d.Length == 0)
            {
                issues.Add("Rigidbody without any Collider: physics body will not collide.");
            }
            if (rb != null && rb2d != null)
            {
                issues.Add("Both Rigidbody and Rigidbody2D present: ambiguous physics domain.");
            }
            if (rb != null && rb.isKinematic && colliders.Length > 0)
            {
                var anyNonTrigger = colliders.Any(c => !c.isTrigger);
                if (!anyNonTrigger) issues.Add("Kinematic Rigidbody with only trigger colliders: no solid collision.");
            }
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "physics.validate").Prop("target", go.name);
            w.Prop("valid", issues.Count == 0);
            w.PropArray("issues", arr => { foreach (var i in issues) arr.Value(i); });
            w.Prop("hasRigidbody", rb != null);
            w.Prop("hasRigidbody2D", rb2d != null);
            w.Prop("colliderCount", (long)(colliders.Length + colliders2d.Length));
            w.Prop("logPath", LogPath());
            return w.EndObject().ToString();
        }

        private static string PhysicsRaycast(BridgeRequest request)
        {
            var mode = NormalizeMode(request.StringArg("mode"));
            var distance = Math.Max(0f, request.FloatArg("distance", 100f));
            var layerMask = request.IntArg("layerMask", Physics.DefaultRaycastLayers);
            if (mode == "2d")
            {
                var origin = new Vector2(request.FloatArg("originX", 0f), request.FloatArg("originY", 0f));
                var direction = NormalizeVector2(new Vector2(request.FloatArg("directionX", 1f), request.FloatArg("directionY", 0f)));
                if (request.BoolArg("all", false))
                {
                    var hits = Physics2D.RaycastAll(origin, direction, distance, layerMask);
                    return SuccessJson(new UnityCliJsonWriter().BeginObject()
                        .Prop("mode", "2d")
                        .PropRaw("query", Raycast2DQueryJson(origin, direction, distance, layerMask))
                        .Prop("hitCount", (long)hits.Length)
                        .PropRaw("hits", Raycast2DHitsJson(hits))
                        .Prop("logPath", LogPath())
                        .EndObject().ToString());
                }
                var hit = Physics2D.Raycast(origin, direction, distance, layerMask);
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("mode", "2d")
                    .PropRaw("query", Raycast2DQueryJson(origin, direction, distance, layerMask))
                    .Prop("hit", hit.collider != null)
                    .Prop("hitCount", hit.collider != null ? 1L : 0L)
                    .PropArray("hits", arr => { if (hit.collider != null) arr.RawValue(Raycast2DHitJson(hit)); })
                    .PropRaw("nextActions", RaycastNextActionsJson(hit.collider != null))
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }

            var origin3d = new Vector3(request.FloatArg("originX", 0f), request.FloatArg("originY", 0f), request.FloatArg("originZ", 0f));
            var direction3d = NormalizeVector3(new Vector3(request.FloatArg("directionX", 0f), request.FloatArg("directionY", 0f), request.FloatArg("directionZ", 1f)));
            if (request.BoolArg("all", false))
            {
                var hits = Physics.RaycastAll(origin3d, direction3d, distance, layerMask);
                Array.Sort(hits, delegate(RaycastHit a, RaycastHit b) { return a.distance.CompareTo(b.distance); });
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("mode", "3d")
                    .PropRaw("query", Raycast3DQueryJson(origin3d, direction3d, distance, layerMask))
                    .Prop("hitCount", (long)hits.Length)
                    .PropRaw("hits", Raycast3DHitsJson(hits))
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }
            RaycastHit hit3d;
            var didHit = Physics.Raycast(origin3d, direction3d, out hit3d, distance, layerMask);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("mode", "3d")
                .PropRaw("query", Raycast3DQueryJson(origin3d, direction3d, distance, layerMask))
                .Prop("hit", didHit)
                .Prop("hitCount", didHit ? 1L : 0L)
                .PropArray("hits", arr => { if (didHit) arr.RawValue(Raycast3DHitJson(hit3d)); })
                .PropRaw("nextActions", RaycastNextActionsJson(didHit))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string PhysicsSimulation(BridgeRequest request)
        {
            var steps = Math.Max(1, request.IntArg("steps", 1));
            var deltaTime = Math.Max(0.0001f, request.FloatArg("deltaTime", Time.fixedDeltaTime));
            var simulate = request.BoolArg("simulate", false);
            var effects = new List<string>();
            if (simulate)
            {
                for (var i = 0; i < steps; i++)
                {
                    Physics.Simulate(deltaTime);
                    Physics2D.Simulate(deltaTime);
                }
                effects.Add(EffectJson("physics.simulation", "steps", steps.ToString(CultureInfo.InvariantCulture)));
                WriteLog("physics_simulated steps=" + steps.ToString(CultureInfo.InvariantCulture) + " deltaTime=" + deltaTime.ToString(CultureInfo.InvariantCulture));
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropObject("simulation", w => w
                    .Prop("simulate", simulate)
                    .Prop("steps", (long)steps)
                    .Prop("deltaTime", deltaTime)
                    .Prop("fixedDeltaTime", Time.fixedDeltaTime)
                    .Prop("autoSimulation", Physics.autoSimulation))
                .PropRaw("physics", PhysicsSettingsJson())
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static void ApplyPhysicsInt(BridgeRequest request, string arg, string property, Action<int> setter, List<string> effects)
        {
            var value = request.OptionalIntArg(arg);
            if (!value.HasValue) return;
            setter(value.Value);
            effects.Add(EffectJson("physics.settings", property, value.Value.ToString(CultureInfo.InvariantCulture)));
        }

        private static void ApplyPhysicsFloat(BridgeRequest request, string arg, string property, Action<float> setter, List<string> effects)
        {
            var value = request.OptionalFloatArg(arg);
            if (!value.HasValue) return;
            setter(value.Value);
            effects.Add(EffectJson("physics.settings", property, Number(value.Value)));
        }

        private static void ApplyPhysicsBool(BridgeRequest request, string arg, string property, Action<bool> setter, List<string> effects)
        {
            var value = request.OptionalBoolArg(arg);
            if (!value.HasValue) return;
            setter(value.Value);
            effects.Add(EffectJson("physics.settings", property, Bool(value.Value)));
        }

        private static void ApplyPhysicsVector3(BridgeRequest request, string property, List<string> effects)
        {
            var x = request.OptionalFloatArg(property + "X");
            var y = request.OptionalFloatArg(property + "Y");
            var z = request.OptionalFloatArg(property + "Z");
            if (!x.HasValue && !y.HasValue && !z.HasValue) return;
            Physics.gravity = new Vector3(x ?? Physics.gravity.x, y ?? Physics.gravity.y, z ?? Physics.gravity.z);
            effects.Add(EffectJson("physics.settings", property, Vector3Json(Physics.gravity)));
        }

        private static void ApplyPhysicsVector2(BridgeRequest request, string property, List<string> effects)
        {
            var x = request.OptionalFloatArg(property + "X");
            var y = request.OptionalFloatArg(property + "Y");
            if (!x.HasValue && !y.HasValue) return;
            Physics2D.gravity = new Vector2(x ?? Physics2D.gravity.x, y ?? Physics2D.gravity.y);
            effects.Add(EffectJson("physics.settings", property, Vector2Json(Physics2D.gravity)));
        }

        private static string NormalizeMode(string mode)
        {
            return string.Equals(mode, "2d", StringComparison.OrdinalIgnoreCase) ? "2d" : "3d";
        }

        private static Vector2 NormalizeVector2(Vector2 value)
        {
            return value.sqrMagnitude <= 0f ? Vector2.right : value.normalized;
        }

        private static Vector3 NormalizeVector3(Vector3 value)
        {
            return value.sqrMagnitude <= 0f ? Vector3.forward : value.normalized;
        }

        private static string RaycastNextActionsJson(bool hit)
        {
            if (hit) return "[]";
            return new UnityCliJsonWriter().BeginArray()
                .Value("No Physics collider matched the ray; adjust origin, direction, distance or layer mask.")
                .EndArray().ToString();
        }

        private static string Raycast3DQueryJson(Vector3 origin, Vector3 direction, float distance, int layerMask)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("origin", Vector3Json(origin))
                .PropRaw("direction", Vector3Json(direction))
                .Prop("distance", distance)
                .Prop("layerMask", (long)layerMask)
                .EndObject().ToString();
        }

        private static string Raycast2DQueryJson(Vector2 origin, Vector2 direction, float distance, int layerMask)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("origin", Vector2Json(origin))
                .PropRaw("direction", Vector2Json(direction))
                .Prop("distance", distance)
                .Prop("layerMask", (long)layerMask)
                .EndObject().ToString();
        }

        private static string Raycast3DHitsJson(RaycastHit[] hits)
        {
            var w = new UnityCliJsonWriter().BeginArray();
            foreach (var hit in hits)
            {
                w.RawValue(Raycast3DHitJson(hit));
            }
            return w.EndArray().ToString();
        }

        private static string Raycast3DHitJson(RaycastHit hit)
        {
            var gameObject = hit.collider != null ? hit.collider.gameObject : null;
            return new UnityCliJsonWriter().BeginObject()
                .Prop("distance", hit.distance)
                .PropRaw("point", Vector3Json(hit.point))
                .PropRaw("normal", Vector3Json(hit.normal))
                .PropRaw("collider", ColliderJson(gameObject, hit.collider == null ? null : hit.collider.GetType().Name))
                .EndObject().ToString();
        }

        private static string Raycast2DHitsJson(RaycastHit2D[] hits)
        {
            var w = new UnityCliJsonWriter().BeginArray();
            foreach (var hit in hits)
            {
                w.RawValue(Raycast2DHitJson(hit));
            }
            return w.EndArray().ToString();
        }

        private static string Raycast2DHitJson(RaycastHit2D hit)
        {
            var gameObject = hit.collider != null ? hit.collider.gameObject : null;
            return new UnityCliJsonWriter().BeginObject()
                .Prop("distance", hit.distance)
                .PropRaw("point", Vector2Json(hit.point))
                .PropRaw("normal", Vector2Json(hit.normal))
                .PropRaw("collider", ColliderJson(gameObject, hit.collider == null ? null : hit.collider.GetType().Name))
                .EndObject().ToString();
        }

        private static string PhysicsMaterialJson(string path, PhysicMaterial material)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("path", path)
                .Prop("name", material.name)
                .Prop("dynamicFriction", material.dynamicFriction)
                .Prop("staticFriction", material.staticFriction)
                .Prop("bounciness", material.bounciness)
                .EndObject().ToString();
        }

        private static string RigidbodyJson(Rigidbody rigidbody)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("mass", rigidbody.mass)
                .Prop("drag", rigidbody.drag)
                .Prop("angularDrag", rigidbody.angularDrag)
                .Prop("useGravity", rigidbody.useGravity)
                .Prop("isKinematic", rigidbody.isKinematic)
                .EndObject().ToString();
        }

        private static ForceMode ParseForceMode(string value)
        {
            if (string.IsNullOrEmpty(value)) return ForceMode.Force;
            return (ForceMode)Enum.Parse(typeof(ForceMode), value, true);
        }

        private static string ColliderJson(GameObject gameObject, string colliderType)
        {
            if (gameObject == null)
            {
                return "null";
            }
            return new UnityCliJsonWriter().BeginObject()
                .Prop("id", (long)gameObject.GetInstanceID())
                .Prop("name", gameObject.name)
                .Prop("path", HierarchyPath(gameObject))
                .Prop("type", colliderType)
                .EndObject().ToString();
        }

        private static int ParseInt(string value, string property)
        {
            int parsed;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                throw new BridgeCommandException("E_SETTING_VALUE_INVALID", "Expected integer for " + property + ": " + value, "Pass a valid numeric setting value.");
            }
            return parsed;
        }

        private static float ParseFloat(string value, string property)
        {
            float parsed;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                throw new BridgeCommandException("E_SETTING_VALUE_INVALID", "Expected number for " + property + ": " + value, "Pass a valid numeric setting value.");
            }
            return parsed;
        }

        private static bool ParseBool(string value, string property)
        {
            bool parsed;
            if (!bool.TryParse(value, out parsed))
            {
                throw new BridgeCommandException("E_SETTING_VALUE_INVALID", "Expected boolean for " + property + ": " + value, "Pass true or false.");
            }
            return parsed;
        }
    }
}
#endif
