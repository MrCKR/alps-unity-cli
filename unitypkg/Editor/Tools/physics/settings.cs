#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.settings — 读取/写入 Physics 与 Physics2D 全局设置(gravity/contactOffset/solverIterations/queries/autoSimulation)。
    /// </summary>
    public class PhysicsSettingsTool : IUnityCliTool<PhysicsSettingsTool.Request, PhysicsSettingsTool.Result>
    {
        public sealed class Request
        {
            public Vector3? Gravity { get; set; }
            public Vector2? Gravity2d { get; set; }
            public float? DefaultContactOffset { get; set; }
            public float? BounceThreshold { get; set; }
            public int? DefaultSolverIterations { get; set; }
            public int? DefaultSolverVelocityIterations { get; set; }
            public bool? QueriesHitTriggers { get; set; }
            public bool? QueriesHitBackfaces { get; set; }
            public bool? QueriesStartInColliders { get; set; }
            public bool? AutoSimulation { get; set; }
            public string Property { get; set; }
            public string Value { get; set; }
        }

        public sealed class Result
        {
            public PhysicsSettingsInfo Physics { get; set; }
            public List<PhysicsPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.settings",
            Summary = "Read and update Physics and Physics2D global settings (gravity, contact offset, solver iterations, queries, auto simulation).",
            Description = "Read and update Physics and Physics2D global settings. Pass individual fields to update; omit all to read current values. Supports a generic property/value pair for gravityY, defaultContactOffset, queriesHitTriggers.",
            Group = "physics", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "settings-write" },
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read physics settings", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effects = new List<PhysicsPlannedEffect>();
            if (request.Gravity.HasValue)
            {
                Physics.gravity = request.Gravity.Value;
                effects.Add(new PhysicsPlannedEffect { Type = "physics.settings", Property = "gravity", Value = JsonVec(request.Gravity.Value) });
            }
            if (request.Gravity2d.HasValue)
            {
                Physics2D.gravity = request.Gravity2d.Value;
                effects.Add(new PhysicsPlannedEffect { Type = "physics.settings", Property = "gravity2d", Value = JsonVec(request.Gravity2d.Value) });
            }
            if (request.DefaultContactOffset.HasValue)
            {
                Physics.defaultContactOffset = request.DefaultContactOffset.Value;
                effects.Add(new PhysicsPlannedEffect { Type = "physics.settings", Property = "defaultContactOffset", Value = request.DefaultContactOffset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            if (request.BounceThreshold.HasValue)
            {
                Physics.bounceThreshold = request.BounceThreshold.Value;
                effects.Add(new PhysicsPlannedEffect { Type = "physics.settings", Property = "bounceThreshold", Value = request.BounceThreshold.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            if (request.DefaultSolverIterations.HasValue)
            {
                Physics.defaultSolverIterations = request.DefaultSolverIterations.Value;
                effects.Add(new PhysicsPlannedEffect { Type = "physics.settings", Property = "defaultSolverIterations", Value = request.DefaultSolverIterations.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            if (request.DefaultSolverVelocityIterations.HasValue)
            {
                Physics.defaultSolverVelocityIterations = request.DefaultSolverVelocityIterations.Value;
                effects.Add(new PhysicsPlannedEffect { Type = "physics.settings", Property = "defaultSolverVelocityIterations", Value = request.DefaultSolverVelocityIterations.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            if (request.QueriesHitTriggers.HasValue)
            {
                Physics.queriesHitTriggers = request.QueriesHitTriggers.Value;
                effects.Add(new PhysicsPlannedEffect { Type = "physics.settings", Property = "queriesHitTriggers", Value = request.QueriesHitTriggers.Value.ToString().ToLowerInvariant() });
            }
            if (request.QueriesHitBackfaces.HasValue)
            {
                Physics.queriesHitBackfaces = request.QueriesHitBackfaces.Value;
                effects.Add(new PhysicsPlannedEffect { Type = "physics.settings", Property = "queriesHitBackfaces", Value = request.QueriesHitBackfaces.Value.ToString().ToLowerInvariant() });
            }
            if (request.QueriesStartInColliders.HasValue)
            {
                Physics2D.queriesStartInColliders = request.QueriesStartInColliders.Value;
                effects.Add(new PhysicsPlannedEffect { Type = "physics.settings", Property = "queriesStartInColliders", Value = request.QueriesStartInColliders.Value.ToString().ToLowerInvariant() });
            }
            if (request.AutoSimulation.HasValue)
            {
                Physics.autoSimulation = request.AutoSimulation.Value;
                effects.Add(new PhysicsPlannedEffect { Type = "physics.settings", Property = "autoSimulation", Value = request.AutoSimulation.Value.ToString().ToLowerInvariant() });
            }
            if (!string.IsNullOrEmpty(request.Property) && !string.IsNullOrEmpty(request.Value))
            {
                ApplyGenericPhysicsSetting(request.Property, request.Value, effects);
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Physics = PhysicsToolSupport.BuildPhysicsSettings(), PlannedEffects = effects });
        }

        // 通用 property/value 物理设置写入
        private static void ApplyGenericPhysicsSetting(string property, string value, List<PhysicsPlannedEffect> effects)
        {
            if (property == "gravityY")
            {
                Physics.gravity = new Vector3(Physics.gravity.x, GameObjectToolSupport.ParseFloat(value, property), Physics.gravity.z);
            }
            else if (property == "defaultContactOffset")
            {
                Physics.defaultContactOffset = GameObjectToolSupport.ParseFloat(value, property);
            }
            else if (property == "queriesHitTriggers")
            {
                Physics.queriesHitTriggers = bool.Parse(value);
            }
            else
            {
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_SETTING_UNSUPPORTED", "Unsupported physics setting: " + property, "Use named flags such as --gravity-y, --default-contact-offset or --queries-hit-triggers.");
            }
            effects.Add(new PhysicsPlannedEffect { Type = "physics.settings", Property = property, Value = value });
        }

        private static string JsonVec(Vector3 v) => $"{v.x},{v.y},{v.z}";
        private static string JsonVec(Vector2 v) => $"{v.x},{v.y}";
    }
}
#endif
