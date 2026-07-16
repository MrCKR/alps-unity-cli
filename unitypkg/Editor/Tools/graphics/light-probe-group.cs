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
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var createdObject = string.IsNullOrEmpty(r.Target);
            if (createdObject && r.Create != true)
                throw new UnityCliBridge.BridgeCommandException("E_LIGHT_PROBE_GROUP_TARGET_REQUIRED", "graphics.light-probe-group requires target unless create is true.", "Pass a target or set create=true.");
            var go = createdObject ? new GameObject("LightProbeGroup") : GameObjectToolSupport.ResolveUniqueTarget(r.Target, "graphics.light-probe-group requires --target or create a new one.");
            if (createdObject) UnityCliSceneMutation.RegisterCreatedObject(go, "Create LightProbeGroup");
            var probe = go.GetComponent<LightProbeGroup>();
            var effects = new List<GraphicsPlannedEffect>();
            if (probe == null && r.Create == true) { probe = UnityCliSceneMutation.AddComponent<LightProbeGroup>(go); effects.Add(new GraphicsPlannedEffect { Type = "graphics.light-probe-group", Property = "component", Value = "added" }); }
            if (probe == null) throw new UnityCliBridge.BridgeCommandException("E_LIGHT_PROBE_GROUP_NOT_FOUND", "LightProbeGroup component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Pass --create to add one or choose a target with LightProbeGroup.");
            if (!string.IsNullOrEmpty(r.Positions)) { var positions = GraphicsToolSupport.ParseVector3Array(r.Positions); UnityCliSceneMutation.RecordObject(probe, "Set light probe positions"); probe.probePositions = positions; effects.Add(new GraphicsPlannedEffect { Type = "graphics.light-probe-group", Property = "probePositions", Value = positions.Length.ToString() }); }
            UnityCliSceneMutation.MarkSceneObjectDirty(probe);
            UnityCliSceneMutation.MarkSceneDirty(go.scene);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, ProbeCount = probe.probePositions == null ? 0 : probe.probePositions.Length, PlannedEffects = effects });
        }
    }
}
#endif
