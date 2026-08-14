#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.reflection-probe — 管理 ReflectionProbe 组件。</summary>
    public class GraphicsReflectionProbeTool : IUnityCliTool<GraphicsReflectionProbeTool.Request, GraphicsReflectionProbeTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public bool? Create { get; set; } public string Mode { get; set; } public int? Resolution { get; set; } public int? Bounces { get; set; } public bool? Hdr { get; set; } public string BoxSize { get; set; } public string BoxOffset { get; set; } }
        public sealed class Result { public ReflectionProbeInfo Probe { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.reflection-probe", Summary = "Add, configure, or read a ReflectionProbe component on a target.", Description = "Resolve a target GameObject, optionally create a ReflectionProbe, and configure mode/resolution/bounces/hdr/boxSize/boxOffset.", Group = "graphics",DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read reflection probe", Input = new Request { Target = "ReflectionProbe" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var createdObject = string.IsNullOrEmpty(r.Target);
            if (createdObject && r.Create != true)
                throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_TARGET_REQUIRED", "graphics.reflection-probe requires target unless create is true.", "Pass a target or set create=true.");
            var go = createdObject ? null : GameObjectToolSupport.ResolveUniqueTarget(r.Target, "graphics.reflection-probe requires --target or create a new one.");
            var mode = default(UnityEngine.Rendering.ReflectionProbeMode);
            var hasMode = !string.IsNullOrEmpty(r.Mode);
            if (hasMode && (!Enum.TryParse<UnityEngine.Rendering.ReflectionProbeMode>(r.Mode, true, out mode) || !Enum.IsDefined(typeof(UnityEngine.Rendering.ReflectionProbeMode), mode)))
                throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_MODE_INVALID", "Unsupported ReflectionProbe mode: " + r.Mode, "Pass a valid ReflectionProbeMode enum value.");
            if (r.Resolution.HasValue && (r.Resolution.Value < 16 || r.Resolution.Value > 8192 || (r.Resolution.Value & (r.Resolution.Value - 1)) != 0)) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_RESOLUTION_INVALID", "ReflectionProbe resolution must be a power of two from 16 to 8192.", "Pass 16, 32, 64, ..., or 8192.");
            if (r.Bounces.HasValue && (r.Bounces.Value < 0 || r.Bounces.Value > 100)) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_BOUNCES_INVALID", "ReflectionProbe bounces must be from 0 to 100.", "Pass a non-negative supported bounce count.");
            var boxSize = string.IsNullOrEmpty(r.BoxSize) ? (Vector3?)null : GameObjectToolSupport.ParseVector3(r.BoxSize);
            var boxOffset = string.IsNullOrEmpty(r.BoxOffset) ? (Vector3?)null : GameObjectToolSupport.ParseVector3(r.BoxOffset);
            var probe = go == null ? null : go.GetComponent<ReflectionProbe>();
            if (!createdObject && probe == null && r.Create != true)
                throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_NOT_FOUND", "ReflectionProbe component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Pass --create to add one or choose a target with ReflectionProbe.");
            var addComponent = probe == null;
            var mutates = createdObject || addComponent || hasMode || r.Resolution.HasValue || r.Bounces.HasValue || r.Hdr.HasValue || boxSize.HasValue || boxOffset.HasValue;
            if (mutates)
            {
                ctx.CommitEffect();
                if (createdObject)
                {
                    go = new GameObject("ReflectionProbe");
                    UnityCliSceneMutation.RegisterCreatedObject(go, "Create ReflectionProbe");
                }
                if (addComponent) probe = UnityCliSceneMutation.AddComponent<ReflectionProbe>(go);
            }
            var effects = new List<GraphicsPlannedEffect>();
            if (addComponent) effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "component", Value = "added" });
            if (mutates) UnityCliSceneMutation.RecordObject(probe, "Configure ReflectionProbe");
            if (hasMode) { probe.mode = mode; effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "mode", Value = probe.mode.ToString() }); }
            if (r.Resolution.HasValue) { probe.resolution = r.Resolution.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "resolution", Value = probe.resolution.ToString() }); }
            if (r.Bounces.HasValue) { SetBounces(probe, r.Bounces.Value); effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "bounces", Value = r.Bounces.Value.ToString() }); }
            if (r.Hdr.HasValue) { probe.hdr = r.Hdr.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "hdr", Value = probe.hdr.ToString().ToLowerInvariant() }); }
            if (boxSize.HasValue) { probe.size = boxSize.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "boxSize", Value = probe.size.ToString() }); }
            if (boxOffset.HasValue) { probe.center = boxOffset.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.reflection-probe", Property = "boxOffset", Value = probe.center.ToString() }); }
            if (mutates) { UnityCliSceneMutation.MarkSceneObjectDirty(probe); UnityCliSceneMutation.MarkSceneDirty(go.scene); }
            var observedObject = GameObjectToolSupport.ResolveUniqueTarget(go.GetInstanceID().ToString(CultureInfo.InvariantCulture), "ReflectionProbe could not be re-resolved after mutation.");
            var observedProbe = observedObject.GetComponent<ReflectionProbe>();
            if (observedProbe == null) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_STATE_MISMATCH", "ReflectionProbe could not be read after mutation.", "Retry after the editor is idle.");
            var observed = GraphicsToolSupport.BuildReflectionProbeInfo(observedProbe);
            if ((hasMode && observedProbe.mode != mode) || (r.Resolution.HasValue && observedProbe.resolution != r.Resolution.Value) || (r.Bounces.HasValue && observed.Bounces != r.Bounces.Value) || (r.Hdr.HasValue && observedProbe.hdr != r.Hdr.Value) || (boxSize.HasValue && observedProbe.size != boxSize.Value) || (boxOffset.HasValue && observedProbe.center != boxOffset.Value)) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_STATE_MISMATCH", "ReflectionProbe readback did not match the request.", "Retry after the editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Probe = observed, PlannedEffects = effects });
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
