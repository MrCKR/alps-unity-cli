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
            return SuccessJson("{\"physics\":" + PhysicsSettingsJson() + ",\"plannedEffects\":[" + string.Join(",", effects.ToArray()) + "],\"logPath\":" + Quote(LogPath()) + "}");
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
                    return SuccessJson("{\"mode\":\"2d\",\"query\":" + Raycast2DQueryJson(origin, direction, distance, layerMask) + ",\"hitCount\":" + hits.Length.ToString(CultureInfo.InvariantCulture) + ",\"hits\":" + Raycast2DHitsJson(hits) + ",\"logPath\":" + Quote(LogPath()) + "}");
                }
                var hit = Physics2D.Raycast(origin, direction, distance, layerMask);
                return SuccessJson("{\"mode\":\"2d\",\"query\":" + Raycast2DQueryJson(origin, direction, distance, layerMask) + ",\"hit\":" + Bool(hit.collider != null) + ",\"hitCount\":" + (hit.collider != null ? "1" : "0") + ",\"hits\":[" + (hit.collider != null ? Raycast2DHitJson(hit) : string.Empty) + "],\"nextActions\":" + RaycastNextActionsJson(hit.collider != null) + ",\"logPath\":" + Quote(LogPath()) + "}");
            }

            var origin3d = new Vector3(request.FloatArg("originX", 0f), request.FloatArg("originY", 0f), request.FloatArg("originZ", 0f));
            var direction3d = NormalizeVector3(new Vector3(request.FloatArg("directionX", 0f), request.FloatArg("directionY", 0f), request.FloatArg("directionZ", 1f)));
            if (request.BoolArg("all", false))
            {
                var hits = Physics.RaycastAll(origin3d, direction3d, distance, layerMask);
                Array.Sort(hits, delegate(RaycastHit a, RaycastHit b) { return a.distance.CompareTo(b.distance); });
                return SuccessJson("{\"mode\":\"3d\",\"query\":" + Raycast3DQueryJson(origin3d, direction3d, distance, layerMask) + ",\"hitCount\":" + hits.Length.ToString(CultureInfo.InvariantCulture) + ",\"hits\":" + Raycast3DHitsJson(hits) + ",\"logPath\":" + Quote(LogPath()) + "}");
            }
            RaycastHit hit3d;
            var didHit = Physics.Raycast(origin3d, direction3d, out hit3d, distance, layerMask);
            return SuccessJson("{\"mode\":\"3d\",\"query\":" + Raycast3DQueryJson(origin3d, direction3d, distance, layerMask) + ",\"hit\":" + Bool(didHit) + ",\"hitCount\":" + (didHit ? "1" : "0") + ",\"hits\":[" + (didHit ? Raycast3DHitJson(hit3d) : string.Empty) + "],\"nextActions\":" + RaycastNextActionsJson(didHit) + ",\"logPath\":" + Quote(LogPath()) + "}");
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
            return SuccessJson("{\"simulation\":{\"simulate\":" + Bool(simulate) + ",\"steps\":" + steps.ToString(CultureInfo.InvariantCulture) + ",\"deltaTime\":" + deltaTime.ToString(CultureInfo.InvariantCulture) + ",\"fixedDeltaTime\":" + Time.fixedDeltaTime.ToString(CultureInfo.InvariantCulture) + ",\"autoSimulation\":" + Bool(Physics.autoSimulation) + "},\"physics\":" + PhysicsSettingsJson() + ",\"plannedEffects\":[" + string.Join(",", effects.ToArray()) + "],\"logPath\":" + Quote(LogPath()) + "}");
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
            return hit ? "[]" : "[" + Quote("No Physics collider matched the ray; adjust origin, direction, distance or layer mask.") + "]";
        }

        private static string Raycast3DQueryJson(Vector3 origin, Vector3 direction, float distance, int layerMask)
        {
            return "{\"origin\":" + Vector3Json(origin) + ",\"direction\":" + Vector3Json(direction) + ",\"distance\":" + Number(distance) + ",\"layerMask\":" + layerMask.ToString(CultureInfo.InvariantCulture) + "}";
        }

        private static string Raycast2DQueryJson(Vector2 origin, Vector2 direction, float distance, int layerMask)
        {
            return "{\"origin\":" + Vector2Json(origin) + ",\"direction\":" + Vector2Json(direction) + ",\"distance\":" + Number(distance) + ",\"layerMask\":" + layerMask.ToString(CultureInfo.InvariantCulture) + "}";
        }

        private static string Raycast3DHitsJson(RaycastHit[] hits)
        {
            var items = new List<string>();
            foreach (var hit in hits)
            {
                items.Add(Raycast3DHitJson(hit));
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static string Raycast3DHitJson(RaycastHit hit)
        {
            var gameObject = hit.collider != null ? hit.collider.gameObject : null;
            return "{\"distance\":" + Number(hit.distance) + ",\"point\":" + Vector3Json(hit.point) + ",\"normal\":" + Vector3Json(hit.normal) + ",\"collider\":" + ColliderJson(gameObject, hit.collider == null ? null : hit.collider.GetType().Name) + "}";
        }

        private static string Raycast2DHitsJson(RaycastHit2D[] hits)
        {
            var items = new List<string>();
            foreach (var hit in hits)
            {
                items.Add(Raycast2DHitJson(hit));
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static string Raycast2DHitJson(RaycastHit2D hit)
        {
            var gameObject = hit.collider != null ? hit.collider.gameObject : null;
            return "{\"distance\":" + Number(hit.distance) + ",\"point\":" + Vector2Json(hit.point) + ",\"normal\":" + Vector2Json(hit.normal) + ",\"collider\":" + ColliderJson(gameObject, hit.collider == null ? null : hit.collider.GetType().Name) + "}";
        }

        private static string ColliderJson(GameObject gameObject, string colliderType)
        {
            if (gameObject == null)
            {
                return "null";
            }
            return "{\"id\":" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ",\"name\":" + Quote(gameObject.name) + ",\"path\":" + Quote(HierarchyPath(gameObject)) + ",\"type\":" + Quote(colliderType) + "}";
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
