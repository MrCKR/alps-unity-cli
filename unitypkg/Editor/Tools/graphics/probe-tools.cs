#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.light-probe-group — 管理 LightProbeGroup 组件与探针位置。</summary>
    public class GraphicsLightProbeGroupTool : IUnityCliTool<GraphicsLightProbeGroupTool.Request, GraphicsLightProbeGroupTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public bool? Create { get; set; } public string Positions { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public long ProbeCount { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.light-probe-group", Summary = "Add, configure, or read a LightProbeGroup component on a target.", Description = "Resolve a target GameObject, optionally create a LightProbeGroup, and set probe positions from a space-separated vector array.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read light probe group", Input = new Request { Target = "LightProbes" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var go = string.IsNullOrEmpty(r.Target) ? new GameObject("LightProbeGroup") : GameObjectToolSupport.ResolveUniqueTarget(r.Target, "graphics.light-probe-group requires --target or create a new one.");
            var probe = go.GetComponent<LightProbeGroup>();
            var effects = new List<GraphicsPlannedEffect>();
            if (probe == null && r.Create == true) { probe = go.AddComponent<LightProbeGroup>(); effects.Add(new GraphicsPlannedEffect { Type = "graphics.light-probe-group", Property = "component", Value = "added" }); }
            if (probe == null) throw new UnityCliBridge.BridgeCommandException("E_LIGHT_PROBE_GROUP_NOT_FOUND", "LightProbeGroup component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Pass --create to add one or choose a target with LightProbeGroup.");
            if (!string.IsNullOrEmpty(r.Positions)) { var positions = GraphicsToolSupport.ParseVector3Array(r.Positions); probe.probePositions = positions; EditorUtility.SetDirty(probe); effects.Add(new GraphicsPlannedEffect { Type = "graphics.light-probe-group", Property = "probePositions", Value = positions.Length.ToString() }); }
            AssetDatabase.SaveAssets();
            return new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, ProbeCount = probe.probePositions == null ? 0 : probe.probePositions.Length, PlannedEffects = effects };
        }
    }

    /// <summary>graphics.reflection-probe — 管理 ReflectionProbe 组件。</summary>
    public class GraphicsReflectionProbeTool : IUnityCliTool<GraphicsReflectionProbeTool.Request, GraphicsReflectionProbeTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public bool? Create { get; set; } public string Mode { get; set; } public int? Resolution { get; set; } public int? Bounces { get; set; } public bool? Hdr { get; set; } public string BoxSize { get; set; } public string BoxOffset { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public ReflectionProbeInfo Probe { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.reflection-probe", Summary = "Add, configure, or read a ReflectionProbe component on a target.", Description = "Resolve a target GameObject, optionally create a ReflectionProbe, and configure mode/resolution/bounces/hdr/boxSize/boxOffset.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read reflection probe", Input = new Request { Target = "ReflectionProbe" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var go = string.IsNullOrEmpty(r.Target) ? new GameObject("ReflectionProbe") : GameObjectToolSupport.ResolveUniqueTarget(r.Target, "graphics.reflection-probe requires --target or create a new one.");
            var probe = go.GetComponent<ReflectionProbe>();
            var effects = new List<GraphicsPlannedEffect>();
            if (probe == null && r.Create == true) { probe = go.AddComponent<ReflectionProbe>(); effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "component", Value = "added" }); }
            if (probe == null) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_NOT_FOUND", "ReflectionProbe component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Pass --create to add one or choose a target with ReflectionProbe.");
            if (!string.IsNullOrEmpty(r.Mode) && System.Enum.TryParse<UnityEngine.Rendering.ReflectionProbeMode>(r.Mode, true, out var mode)) { probe.mode = mode; effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "mode", Value = r.Mode }); }
            if (r.Resolution.HasValue) { probe.resolution = r.Resolution.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "resolution", Value = r.Resolution.Value.ToString() }); }
            if (r.Bounces.HasValue) { SetBounces(probe, r.Bounces.Value); effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "bounces", Value = r.Bounces.Value.ToString() }); }
            if (r.Hdr.HasValue) { probe.hdr = r.Hdr.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "hdr", Value = r.Hdr.Value.ToString().ToLowerInvariant() }); }
            if (!string.IsNullOrEmpty(r.BoxSize)) { probe.size = GameObjectToolSupport.ParseVector3(r.BoxSize); effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "boxSize", Value = r.BoxSize }); }
            if (!string.IsNullOrEmpty(r.BoxOffset)) { probe.center = GameObjectToolSupport.ParseVector3(r.BoxOffset); effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "boxOffset", Value = r.BoxOffset }); }
            EditorUtility.SetDirty(probe); AssetDatabase.SaveAssets();
            return new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Probe = GraphicsBakeReflectionProbeTool.BuildReflectionProbeInfo(probe), PlannedEffects = effects };
        }
        // 版本适配:通过 #if 在方法内部分支
        private static void SetBounces(ReflectionProbe p, int v)
        {
#if UNITY_2023_2_OR_NEWER
            p.bounces = v;
#else
            RenderSettings.reflectionBounces = v;
#endif
        }
    }

    /// <summary>graphics.probe-positions — 读取/写入探针位置(ReflectionProbe 或 LightProbeGroup)。</summary>
    public class GraphicsProbePositionsTool : IUnityCliTool<GraphicsProbePositionsTool.Request, GraphicsProbePositionsTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Type { get; set; } public string Positions { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public string ProbeType { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.probe-positions", Summary = "Read or update probe positions for a ReflectionProbe or LightProbeGroup.", Description = "Resolve a probe component by type (reflection or light), then optionally set positions.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read probe positions", Input = new Request { Target = "Probes" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var probeType = r.Type ?? "reflection";
            Component component = string.Equals(probeType, "reflection", System.StringComparison.OrdinalIgnoreCase) ? (Component)PhysicsToolSupport.ResolveReflectionProbe(r.Target) : ResolveLightProbeGroup(r.Target);
            if (component == null) throw new UnityCliBridge.BridgeCommandException("E_PROBE_NOT_FOUND", "Probe component not found: " + r.Target + " (type=" + probeType + ")", "Pass a GameObject name / hierarchy path with a ReflectionProbe or LightProbeGroup component.");
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(r.Positions))
            {
                var positions = GraphicsToolSupport.ParseVector3Array(r.Positions);
                if (component is ReflectionProbe rp) { rp.center = positions.Length > 0 ? positions[0] : rp.center; effects.Add(new GraphicsPlannedEffect { Type = "graphics.probe-positions", Property = "reflectionCenter", Value = positions.Length.ToString() }); }
                else if (component is LightProbeGroup lpg) { lpg.probePositions = positions; effects.Add(new GraphicsPlannedEffect { Type = "graphics.probe-positions", Property = "lightProbePositions", Value = positions.Length.ToString() }); }
                EditorUtility.SetDirty(component); AssetDatabase.SaveAssets();
            }
            return new Result { Target = new GameObjectTargetRef { Id = component.gameObject.GetInstanceID(), Name = component.gameObject.name, Path = GameObjectToolSupport.HierarchyPath(component.gameObject) }, ProbeType = probeType, PlannedEffects = effects };
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
