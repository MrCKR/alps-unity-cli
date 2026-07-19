#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.simulation — 步进 Physics/Physics2D 模拟。
    /// </summary>
    public class PhysicsSimulationTool : IUnityCliTool<PhysicsSimulationTool.Request, PhysicsSimulationTool.Result>
    {
        public sealed class Request
        {
            public int Steps { get; set; } = 1;
            public float DeltaTime { get; set; }
            public bool Simulate { get; set; }
        }

        public sealed class Result
        {
            public SimulationInfo Simulation { get; set; }
            public PhysicsSettingsInfo Physics { get; set; }
            public List<PhysicsPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.simulation",
            Summary = "Step Physics and Physics2D simulation by N steps with a given deltaTime.",
            Description = "Advance the physics simulation. simulate=true triggers the steps; otherwise reads current simulation state. deltaTime defaults to Time.fixedDeltaTime.",
            Group = "physics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" },
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Simulate 10 steps", Input = new Request { Steps = 10, Simulate = true } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var steps = System.Math.Max(1, request.Steps);
            var deltaTime = request.DeltaTime > 0 ? request.DeltaTime : Time.fixedDeltaTime;
            var effects = new List<PhysicsPlannedEffect>();
            if (request.Simulate)
            {
                ctx.CommitEffect();
                for (var i = 0; i < steps; i++)
                {
                    Physics.Simulate(deltaTime);
                    Physics2D.Simulate(deltaTime);
                }
                effects.Add(new PhysicsPlannedEffect { Type = "physics.simulation", Property = "steps", Value = steps.ToString() });
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_SIMULATION_PROOF_UNAVAILABLE", "Physics simulation advanced shared world state, but this request cannot attribute every affected body to a typed business readback.", "Use a target-specific physics operation with an observed Rigidbody state, or inspect a controlled fixture after simulation.");
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Simulation = new SimulationInfo { Simulate = request.Simulate, Steps = steps, DeltaTime = deltaTime, FixedDeltaTime = Time.fixedDeltaTime, AutoSimulation = Physics.autoSimulation },
                Physics = PhysicsToolSupport.BuildPhysicsSettings(),
                PlannedEffects = effects
            });
        }
    }
}
#endif
