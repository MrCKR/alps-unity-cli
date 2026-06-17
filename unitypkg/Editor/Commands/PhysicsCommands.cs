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
            RegisterCommand(registry, "physics.raycast", "physics", false, PhysicsRaycast);
            RegisterCommand(registry, "physics.simulation", "physics", true, PhysicsSimulation);
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
