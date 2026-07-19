#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UnityCli
{
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

        public sealed class Result { public PhysicsSettingsInfo Physics { get; set; } public List<PhysicsPlannedEffect> PlannedEffects { get; set; } }
        private sealed class SettingPlan
        {
            public readonly string Property; public readonly string Value; public readonly Action Apply; public readonly Func<bool> ReadbackMatches;
            public SettingPlan(string property, string value, Action apply, Func<bool> readbackMatches) { Property = property; Value = value; Apply = apply; ReadbackMatches = readbackMatches; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.settings", Summary = "Read and update Physics and Physics2D global settings (gravity, contact offset, solver iterations, queries, auto simulation).",
            Description = "Read and update Physics and Physics2D global settings. Pass individual fields to update; omit all to read current values. Supports a generic property/value pair for gravityY, defaultContactOffset, queriesHitTriggers.",
            Group = "physics", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read physics settings", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var plans = new List<SettingPlan>();
            AddTypedPlans(request, plans);
            AddGenericPlan(request, plans);
            var immutablePlans = plans.AsReadOnly();
            if (immutablePlans.Count > 0)
            {
                ctx.CommitEffect();
                foreach (var plan in immutablePlans) plan.Apply();
                foreach (var plan in immutablePlans)
                    if (!plan.ReadbackMatches())
                        throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_SETTINGS_STATE_MISMATCH", "Physics setting did not match after readback: " + plan.Property, "Inspect Project Settings > Physics and retry after the Editor is idle.");
            }
            var effects = new List<PhysicsPlannedEffect>();
            foreach (var plan in immutablePlans) effects.Add(new PhysicsPlannedEffect { Type = "physics.settings", Property = plan.Property, Value = plan.Value });
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Physics = PhysicsToolSupport.BuildPhysicsSettings(), PlannedEffects = effects });
        }

        // 先将所有 typed 输入校验为不可变计划，确保任何写入前已完成完整校验。
        private static void AddTypedPlans(Request request, List<SettingPlan> plans)
        {
            if (request.Gravity.HasValue) { var value = request.Gravity.Value; ValidateFinite(value, "gravity"); plans.Add(new SettingPlan("gravity", JsonVec(value), () => Physics.gravity = value, () => Physics.gravity == value)); }
            if (request.Gravity2d.HasValue) { var value = request.Gravity2d.Value; ValidateFinite(value, "gravity2d"); plans.Add(new SettingPlan("gravity2d", JsonVec(value), () => Physics2D.gravity = value, () => Physics2D.gravity == value)); }
            if (request.DefaultContactOffset.HasValue) { var value = request.DefaultContactOffset.Value; ValidateFinite(value, "defaultContactOffset"); ValidatePositive(value, "defaultContactOffset"); plans.Add(new SettingPlan("defaultContactOffset", Number(value), () => Physics.defaultContactOffset = value, () => Mathf.Approximately(Physics.defaultContactOffset, value))); }
            if (request.BounceThreshold.HasValue) { var value = request.BounceThreshold.Value; ValidateFinite(value, "bounceThreshold"); ValidateNonNegative(value, "bounceThreshold"); plans.Add(new SettingPlan("bounceThreshold", Number(value), () => Physics.bounceThreshold = value, () => Mathf.Approximately(Physics.bounceThreshold, value))); }
            if (request.DefaultSolverIterations.HasValue) { var value = request.DefaultSolverIterations.Value; ValidatePositive(value, "defaultSolverIterations"); plans.Add(new SettingPlan("defaultSolverIterations", Number(value), () => Physics.defaultSolverIterations = value, () => Physics.defaultSolverIterations == value)); }
            if (request.DefaultSolverVelocityIterations.HasValue) { var value = request.DefaultSolverVelocityIterations.Value; ValidatePositive(value, "defaultSolverVelocityIterations"); plans.Add(new SettingPlan("defaultSolverVelocityIterations", Number(value), () => Physics.defaultSolverVelocityIterations = value, () => Physics.defaultSolverVelocityIterations == value)); }
            if (request.QueriesHitTriggers.HasValue) { var value = request.QueriesHitTriggers.Value; plans.Add(new SettingPlan("queriesHitTriggers", value.ToString().ToLowerInvariant(), () => Physics.queriesHitTriggers = value, () => Physics.queriesHitTriggers == value)); }
            if (request.QueriesHitBackfaces.HasValue) { var value = request.QueriesHitBackfaces.Value; plans.Add(new SettingPlan("queriesHitBackfaces", value.ToString().ToLowerInvariant(), () => Physics.queriesHitBackfaces = value, () => Physics.queriesHitBackfaces == value)); }
            if (request.QueriesStartInColliders.HasValue) { var value = request.QueriesStartInColliders.Value; plans.Add(new SettingPlan("queriesStartInColliders", value.ToString().ToLowerInvariant(), () => Physics2D.queriesStartInColliders = value, () => Physics2D.queriesStartInColliders == value)); }
            if (request.AutoSimulation.HasValue) { var value = request.AutoSimulation.Value; plans.Add(new SettingPlan("autoSimulation", value.ToString().ToLowerInvariant(), () => Physics.autoSimulation = value, () => Physics.autoSimulation == value)); }
        }

        // property/value 只接受当前公开的设置，并在写入前转换为对应的强类型值。
        private static void AddGenericPlan(Request request, List<SettingPlan> plans)
        {
            if (string.IsNullOrEmpty(request.Property) && string.IsNullOrEmpty(request.Value)) return;
            if (string.IsNullOrEmpty(request.Property) || string.IsNullOrEmpty(request.Value)) throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_SETTING_VALUE_INVALID", "property and value must be supplied together.", "Pass both property and value, or use a typed setting field.");
            if (request.Property == "gravityY") { var value = ParseFiniteFloat(request.Value, request.Property); plans.Add(new SettingPlan(request.Property, Number(value), () => Physics.gravity = new Vector3(Physics.gravity.x, value, Physics.gravity.z), () => Mathf.Approximately(Physics.gravity.y, value))); return; }
            if (request.Property == "defaultContactOffset") { var value = ParseFiniteFloat(request.Value, request.Property); ValidatePositive(value, request.Property); plans.Add(new SettingPlan(request.Property, Number(value), () => Physics.defaultContactOffset = value, () => Mathf.Approximately(Physics.defaultContactOffset, value))); return; }
            if (request.Property == "queriesHitTriggers") { bool value; if (!bool.TryParse(request.Value, out value)) throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_SETTING_VALUE_INVALID", "queriesHitTriggers value must be true or false.", "Pass true or false."); plans.Add(new SettingPlan(request.Property, value.ToString().ToLowerInvariant(), () => Physics.queriesHitTriggers = value, () => Physics.queriesHitTriggers == value)); return; }
            throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_SETTING_UNSUPPORTED", "Unsupported physics setting: " + request.Property, "Use gravityY, defaultContactOffset, queriesHitTriggers, or a typed setting field.");
        }

        private static float ParseFiniteFloat(string text, string name) { float value; if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_SETTING_VALUE_INVALID", name + " must be a float.", "Pass a finite numeric value."); ValidateFinite(value, name); return value; }
        private static void ValidateFinite(float value, string name) { if (float.IsNaN(value) || float.IsInfinity(value)) throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_SETTING_VALUE_INVALID", name + " must be finite.", "Pass a finite numeric value."); }
        private static void ValidateFinite(Vector3 value, string name) { ValidateFinite(value.x, name + ".x"); ValidateFinite(value.y, name + ".y"); ValidateFinite(value.z, name + ".z"); }
        private static void ValidateFinite(Vector2 value, string name) { ValidateFinite(value.x, name + ".x"); ValidateFinite(value.y, name + ".y"); }
        private static void ValidatePositive(int value, string name) { if (value <= 0) throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_SETTING_VALUE_INVALID", name + " must be greater than zero.", "Pass a value greater than zero."); }
        private static void ValidatePositive(float value, string name) { if (value <= 0f) throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_SETTING_VALUE_INVALID", name + " must be greater than zero.", "Pass a value greater than zero."); }
        private static void ValidateNonNegative(float value, string name) { if (value < 0f) throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_SETTING_VALUE_INVALID", name + " must be non-negative.", "Pass zero or a positive value."); }
        private static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string JsonVec(Vector3 value) => value.x + "," + value.y + "," + value.z;
        private static string JsonVec(Vector2 value) => value.x + "," + value.y;
    }
}
#endif
