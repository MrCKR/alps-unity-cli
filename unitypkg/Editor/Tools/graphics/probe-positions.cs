#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.probe-positions — 读取/写入探针位置(ReflectionProbe 或 LightProbeGroup)。</summary>
    public class GraphicsProbePositionsTool : IUnityCliTool<GraphicsProbePositionsTool.Request, GraphicsProbePositionsTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Type { get; set; } public string Positions { get; set; } }
        public sealed class Result { public string ProbeType { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.probe-positions", Summary = "Read or update probe positions for a ReflectionProbe or LightProbeGroup.", Description = "Resolve a probe component by type (reflection or light), then optionally set positions.", Group = "graphics",DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read probe positions", Input = new Request { Target = "Probes" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var probeType = r.Type ?? "reflection";
            var reflection = string.Equals(probeType, "reflection", System.StringComparison.OrdinalIgnoreCase);
            var light = string.Equals(probeType, "light", System.StringComparison.OrdinalIgnoreCase);
            if (!reflection && !light) throw new UnityCliBridge.BridgeCommandException("E_PROBE_TYPE_INVALID", "Unsupported probe type: " + probeType, "Pass reflection or light.");
            Component component = reflection ? (Component)PhysicsToolSupport.ResolveReflectionProbe(r.Target) : ResolveLightProbeGroup(r.Target);
            if (component == null) throw new UnityCliBridge.BridgeCommandException("E_PROBE_NOT_FOUND", "Probe component not found: " + r.Target + " (type=" + probeType + ")", "Pass a GameObject name / hierarchy path with a ReflectionProbe or LightProbeGroup component.");
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(r.Positions))
            {
                var positions = GraphicsToolSupport.ParseVector3Array(r.Positions);
                if (reflection && positions.Length != 1) throw new UnityCliBridge.BridgeCommandException("E_PROBE_POSITIONS_INVALID", "ReflectionProbe requires exactly one position.", "Pass one x,y,z position.");
                ctx.CommitEffect();
                UnityCliSceneMutation.RecordObject(component, "Set probe positions");
                if (component is ReflectionProbe rp) { rp.center = positions.Length > 0 ? positions[0] : rp.center; effects.Add(new GraphicsPlannedEffect { Type = "graphics.probe-positions", Property = "reflectionCenter", Value = positions.Length.ToString() }); }
                else if (component is LightProbeGroup lpg) { lpg.probePositions = positions; effects.Add(new GraphicsPlannedEffect { Type = "graphics.probe-positions", Property = "lightProbePositions", Value = positions.Length.ToString() }); }
                UnityCliSceneMutation.MarkSceneObjectDirty(component);
                UnityCliSceneMutation.MarkSceneDirty(component.gameObject.scene);
                var observedObject = GameObjectToolSupport.ResolveUniqueTarget(component.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture), "Probe target could not be re-resolved after mutation.");
                var observedComponent = string.Equals(probeType, "reflection", System.StringComparison.OrdinalIgnoreCase) ? (Component)observedObject.GetComponent<ReflectionProbe>() : observedObject.GetComponent<LightProbeGroup>();
                if (observedComponent == null || (observedComponent is ReflectionProbe observedReflection && (positions.Length == 0 || observedReflection.center != positions[0])) || (observedComponent is LightProbeGroup observedLight && !SamePositions(observedLight.probePositions, positions))) throw new UnityCliBridge.BridgeCommandException("E_PROBE_STATE_MISMATCH", "Probe positions did not retain the request.", "Retry after the editor is idle.");
                component = observedComponent;
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { ProbeType = probeType, PlannedEffects = effects });
        }
        private static bool SamePositions(Vector3[] actual, Vector3[] expected)
        {
            if (actual == null || actual.Length != expected.Length) return false;
            for (var index = 0; index < expected.Length; index++) if (actual[index] != expected[index]) return false;
            return true;
        }

        private static LightProbeGroup ResolveLightProbeGroup(string target)
        {
            var groups = UnityEngine.Object.FindObjectsOfType<LightProbeGroup>();
            if (string.IsNullOrEmpty(target)) return groups.Length > 0 ? groups[0] : null;
            foreach (var g in groups) if (GameObjectToolSupport.HierarchyPath(g.gameObject) == target || g.gameObject.name == target || g.GetInstanceID().ToString() == target) return g;
            return null;
        }
    }
}
#endif
