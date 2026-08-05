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
            [UnityCliRequired] public UnityCliCompletionProbe CompletionProbe { get; set; }
        }

        public sealed class Result
        {
            public SimulationInfo Simulation { get; set; }
            public PhysicsSettingsInfo Physics { get; set; }
            public UnityCliCompletionProbeReadback Completion { get; set; }
            public List<PhysicsPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.simulation",
            Summary = "Step manual Physics and Physics2D simulation and prove one declared body state.",
            Description = "simulate=true advances manual 3D and 2D physics synchronously. completionProbe is mandatory and must prove a target resource before/after state through an eligible read-only Context; API return values are not completion proof. deltaTime defaults to Time.fixedDeltaTime.",
            Group = "physics",DestructiveHint = false, SideEffects = new[] { "scene-modify" },
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Simulate one manual step for PhysicsProbe starting at y=1.",
                    Input = new Request
                    {
                        Steps = 1, Simulate = true, DeltaTime = 0.02f,
                        CompletionProbe = new UnityCliCompletionProbe
                        {
                            Context = "scene.gameobject", Input = new Dictionary<string, object> { { "target", "PhysicsProbe" } },
                            Assertions = new List<UnityCliCompletionAssertion>
                            {
                                new UnityCliCompletionAssertion { Path = "/gameObject/transform/position/y", Operator = "approx", Before = 1.0, After = 0.996076, Tolerance = 0.001 }
                            }
                        }
                    }
                }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (request == null) request = new Request();
            if (request.Steps < 1) throw new UnityCliBridge.BridgeCommandException("E_SCHEMA_INVALID", "physics.simulation steps must be at least 1.", "Pass steps >= 1.");
            var deltaTime = request.DeltaTime == 0 ? Time.fixedDeltaTime : request.DeltaTime;
            if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime <= 0)
                throw new UnityCliBridge.BridgeCommandException("E_SCHEMA_INVALID", "physics.simulation deltaTime must be finite and positive.", "Pass a positive finite deltaTime or omit it.");
            var effects = new List<PhysicsPlannedEffect>();
            UnityCliCompletionProbeReadback completion = null;
            if (request.Simulate)
            {
                if (PhysicsToolSupport.IsAutoSimulation3D() || Physics2D.simulationMode != SimulationMode2D.Script)
                    throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_MANUAL_SIMULATION_REQUIRED", "Manual simulation requires Physics.autoSimulation=false and Physics2D.simulationMode=Script.", "Configure manual simulation before invoking physics.simulation.");
                completion = ctx.RunCompletionProbe(request.CompletionProbe, () =>
                {
                    for (var i = 0; i < request.Steps; i++)
                    {
                        Physics.Simulate(deltaTime);
                        Physics2D.Simulate(deltaTime);
                    }
                });
                effects.Add(new PhysicsPlannedEffect { Type = "physics.simulation", Property = "steps", Value = request.Steps.ToString() });
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Simulation = new SimulationInfo { Simulate = request.Simulate, Steps = request.Steps, DeltaTime = deltaTime, FixedDeltaTime = Time.fixedDeltaTime, AutoSimulation = PhysicsToolSupport.IsAutoSimulation3D() },
                Physics = PhysicsToolSupport.BuildPhysicsSettings(), Completion = completion,
                PlannedEffects = effects
            });
        }
    }
}
#endif
