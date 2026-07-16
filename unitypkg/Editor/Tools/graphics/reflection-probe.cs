#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.reflection-probe — 管理 ReflectionProbe 组件。</summary>
    public class GraphicsReflectionProbeTool : IUnityCliTool<GraphicsReflectionProbeTool.Request, GraphicsReflectionProbeTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public bool? Create { get; set; } public string Mode { get; set; } public int? Resolution { get; set; } public int? Bounces { get; set; } public bool? Hdr { get; set; } public string BoxSize { get; set; } public string BoxOffset { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public ReflectionProbeInfo Probe { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.reflection-probe", Summary = "Add, configure, or read a ReflectionProbe component on a target.", Description = "Resolve a target GameObject, optionally create a ReflectionProbe, and configure mode/resolution/bounces/hdr/boxSize/boxOffset.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read reflection probe", Input = new Request { Target = "ReflectionProbe" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var createdObject = string.IsNullOrEmpty(r.Target);
            if (createdObject && r.Create != true)
                throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_TARGET_REQUIRED", "graphics.reflection-probe requires target unless create is true.", "Pass a target or set create=true.");
            var go = createdObject ? new GameObject("ReflectionProbe") : GameObjectToolSupport.ResolveUniqueTarget(r.Target, "graphics.reflection-probe requires --target or create a new one.");
            if (createdObject) UnityCliSceneMutation.RegisterCreatedObject(go, "Create ReflectionProbe");
            var probe = go.GetComponent<ReflectionProbe>();
            var effects = new List<GraphicsPlannedEffect>();
            if (probe == null && r.Create == true) { probe = UnityCliSceneMutation.AddComponent<ReflectionProbe>(go); effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "component", Value = "added" }); }
            if (probe == null) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_NOT_FOUND", "ReflectionProbe component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Pass --create to add one or choose a target with ReflectionProbe.");
            UnityCliSceneMutation.RecordObject(probe, "Configure ReflectionProbe");
            if (!string.IsNullOrEmpty(r.Mode) && System.Enum.TryParse<UnityEngine.Rendering.ReflectionProbeMode>(r.Mode, true, out var mode)) { probe.mode = mode; effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "mode", Value = r.Mode }); }
            if (r.Resolution.HasValue) { probe.resolution = r.Resolution.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "resolution", Value = r.Resolution.Value.ToString() }); }
            if (r.Bounces.HasValue) { SetBounces(probe, r.Bounces.Value); effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "bounces", Value = r.Bounces.Value.ToString() }); }
            if (r.Hdr.HasValue) { probe.hdr = r.Hdr.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "hdr", Value = r.Hdr.Value.ToString().ToLowerInvariant() }); }
            if (!string.IsNullOrEmpty(r.BoxSize)) { probe.size = GameObjectToolSupport.ParseVector3(r.BoxSize); effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "boxSize", Value = r.BoxSize }); }
            if (!string.IsNullOrEmpty(r.BoxOffset)) { probe.center = GameObjectToolSupport.ParseVector3(r.BoxOffset); effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "boxOffset", Value = r.BoxOffset }); }
            UnityCliSceneMutation.MarkSceneObjectDirty(probe);
            UnityCliSceneMutation.MarkSceneDirty(go.scene);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Probe = GraphicsToolSupport.BuildReflectionProbeInfo(probe), PlannedEffects = effects });
        }
        private static void SetBounces(ReflectionProbe p, int v)
        {
#if UNITY_2023_2_OR_NEWER
            p.bounces = v;
#else
            RenderSettings.reflectionBounces = v;
#endif
        }
    }
}
#endif
