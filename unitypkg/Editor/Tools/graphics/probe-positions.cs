#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.probe-positions — 读取/写入探针位置(ReflectionProbe 或 LightProbeGroup)。</summary>
    public class GraphicsProbePositionsTool : IUnityCliTool<GraphicsProbePositionsTool.Request, GraphicsProbePositionsTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Type { get; set; } public string Positions { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public string ProbeType { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.probe-positions", Summary = "Read or update probe positions for a ReflectionProbe or LightProbeGroup.", Description = "Resolve a probe component by type (reflection or light), then optionally set positions.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read probe positions", Input = new Request { Target = "Probes" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var probeType = r.Type ?? "reflection";
            Component component = string.Equals(probeType, "reflection", System.StringComparison.OrdinalIgnoreCase) ? (Component)PhysicsToolSupport.ResolveReflectionProbe(r.Target) : ResolveLightProbeGroup(r.Target);
            if (component == null) throw new UnityCliBridge.BridgeCommandException("E_PROBE_NOT_FOUND", "Probe component not found: " + r.Target + " (type=" + probeType + ")", "Pass a GameObject name / hierarchy path with a ReflectionProbe or LightProbeGroup component.");
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(r.Positions))
            {
                var positions = GraphicsToolSupport.ParseVector3Array(r.Positions);
                UnityCliSceneMutation.RecordObject(component, "Set probe positions");
                if (component is ReflectionProbe rp) { rp.center = positions.Length > 0 ? positions[0] : rp.center; effects.Add(new GraphicsPlannedEffect { Type = "graphics.probe-positions", Property = "reflectionCenter", Value = positions.Length.ToString() }); }
                else if (component is LightProbeGroup lpg) { lpg.probePositions = positions; effects.Add(new GraphicsPlannedEffect { Type = "graphics.probe-positions", Property = "lightProbePositions", Value = positions.Length.ToString() }); }
                UnityCliSceneMutation.MarkSceneObjectDirty(component);
                UnityCliSceneMutation.MarkSceneDirty(component.gameObject.scene);
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Target = new GameObjectTargetRef { Id = component.gameObject.GetInstanceID(), Name = component.gameObject.name, Path = GameObjectToolSupport.HierarchyPath(component.gameObject) }, ProbeType = probeType, PlannedEffects = effects });
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
