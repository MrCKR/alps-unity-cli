#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>graphics.bake-reflection-probe — 烘焙单个 ReflectionProbe。</summary>
    public class GraphicsBakeReflectionProbeTool : IUnityCliTool<GraphicsBakeReflectionProbeTool.Request, GraphicsBakeReflectionProbeTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Path { get; set; } }
        public sealed class Result { public ReflectionProbeInfo Probe { get; set; } public bool Baked { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.bake-reflection-probe", Summary = "Bake a single ReflectionProbe to an .exr cubemap asset.", Description = "Resolve a ReflectionProbe by target, then bake it to an .exr path. Default output is Assets/<name>_reflection.exr.", Group = "graphics", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "bake", "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Bake reflection probe", Input = new Request { Target = "ReflectionProbe" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var probe = PhysicsToolSupport.ResolveReflectionProbe(r.Target);
            if (probe == null) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_NOT_FOUND", "ReflectionProbe not found: " + r.Target, "Run tools run graphics.reflection-probe to inspect probes or pass a GameObject name / hierarchy path.");
            var probePath = r.Path ?? ("Assets/" + probe.gameObject.name + "_reflection.exr");
            var baked = Lightmapping.BakeReflectionProbe(probe, probePath);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Probe = GraphicsToolSupport.BuildReflectionProbeInfo(probe), Baked = baked, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.bake-reflection-probe", Property = "probe", Value = GameObjectToolSupport.HierarchyPath(probe.gameObject) } } });
        }
    }
}
#endif
