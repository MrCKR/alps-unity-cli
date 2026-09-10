#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>lighting.light-probe-group — 管理 LightProbeGroup 组件与探针位置。</summary>
    public class GraphicsLightProbeGroupTool : IUnityCliTool<GraphicsLightProbeGroupTool.Request, GraphicsLightProbeGroupTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public bool? Create { get; set; } public string Positions { get; set; } }
        public sealed class Result { public GraphicsProbeTarget Target { get; set; } public long ProbeCount { get; set; } public List<Vector3Data> Positions { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "lighting.light-probe-group", Summary = "Read, create, or set one LightProbeGroup's probe positions.", Description = "Dedicated LightProbeGroup Tool for Edit and Play mode. Resolve one target, set create=true to add the object/component when missing, and pass positions as space-separated x,y,z vectors to replace the array. Success re-resolves the component and returns fresh target identity, probe count, and positions.", Group = "rendering",DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read light probe positions", Input = new Request { Target = "LightProbes" } }, new ToolExample { Description = "Set two light probe positions", Input = new Request { Target = "LightProbes", Positions = "0,0,0 0,2,0" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var createdObject = string.IsNullOrEmpty(r.Target);
            if (createdObject && r.Create != true)
                throw new UnityCliBridge.BridgeCommandException("E_LIGHT_PROBE_GROUP_TARGET_REQUIRED", "lighting.light-probe-group requires target unless create is true.", "Pass a target or set create=true.");
            var go = createdObject ? null : GameObjectToolSupport.ResolveUniqueTarget(r.Target, "lighting.light-probe-group requires --target or create a new one.");
            var positions = string.IsNullOrEmpty(r.Positions) ? null : GraphicsToolSupport.ParseVector3Array(r.Positions);
            var probe = go == null ? null : go.GetComponent<LightProbeGroup>();
            if (!createdObject && probe == null && r.Create != true)
                throw new UnityCliBridge.BridgeCommandException("E_LIGHT_PROBE_GROUP_NOT_FOUND", "LightProbeGroup component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Pass --create to add one or choose a target with LightProbeGroup.");
            var addComponent = probe == null;
            var mutates = createdObject || addComponent || positions != null;
            if (mutates)
            {
                ctx.CommitEffect();
                if (createdObject)
                {
                    go = new GameObject("LightProbeGroup");
                    UnityCliSceneMutation.RegisterCreatedObject(go, "Create LightProbeGroup");
                }
                if (addComponent) probe = UnityCliSceneMutation.AddComponent<LightProbeGroup>(go);
            }
            var effects = new List<GraphicsPlannedEffect>();
            if (addComponent) effects.Add(new GraphicsPlannedEffect { Type = "lighting.light-probe-group", Property = "component", Value = "added" });
            if (positions != null) { UnityCliSceneMutation.RecordObject(probe, "Set light probe positions"); probe.probePositions = positions; effects.Add(new GraphicsPlannedEffect { Type = "lighting.light-probe-group", Property = "probePositions", Value = positions.Length.ToString() }); }
            if (mutates) { UnityCliSceneMutation.MarkSceneObjectDirty(probe); UnityCliSceneMutation.MarkSceneDirty(go.scene); }
            var observedObject = GameObjectToolSupport.ResolveUniqueTarget(go.GetInstanceID().ToString(CultureInfo.InvariantCulture), "LightProbeGroup could not be re-resolved after mutation.");
            var observedProbe = observedObject.GetComponent<LightProbeGroup>();
            var observedPositions = observedProbe == null ? null : observedProbe.probePositions;
            if (observedProbe == null || (positions != null && !SamePositions(observedPositions, positions))) throw new UnityCliBridge.BridgeCommandException("E_LIGHT_PROBE_GROUP_STATE_MISMATCH", "LightProbeGroup probe positions did not retain the request.", "Retry after the editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Target = GraphicsToolSupport.BuildProbeTarget(observedObject),
                ProbeCount = observedPositions == null ? 0 : observedPositions.Length,
                Positions = GraphicsToolSupport.BuildProbePositions(observedPositions),
                PlannedEffects = effects
            });
        }
        private static bool SamePositions(Vector3[] actual, Vector3[] expected)
        {
            if (actual == null || actual.Length != expected.Length) return false;
            for (var index = 0; index < expected.Length; index++) if (actual[index] != expected[index]) return false;
            return true;
        }
    }
}
#endif
