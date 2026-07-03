#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>graphics.bake-start — 异步启动光照烘焙。</summary>
    public class GraphicsBakeStartTool : IUnityCliTool<GraphicsBakeStartTool.Request, GraphicsBakeStartTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool Baking { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.bake-start", Summary = "Start asynchronous lightmap baking.", Description = "Start Lightmapping.BakeAsync. Returns error if a bake is already running.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "bake" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Start baking", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            if (Lightmapping.isRunning) throw new UnityCliBridge.BridgeCommandException("E_BAKE_ALREADY_RUNNING", "Lightmapping bake is already running.", "Run tools run graphics.bake-status to inspect progress or graphics.bake-cancel to stop it.");
            Lightmapping.BakeAsync();
            return new Result { Baking = Lightmapping.isRunning, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.bake-start", Property = "bake", Value = "async" } } };
        }
    }

    /// <summary>graphics.bake-cancel — 取消正在运行的光照烘焙。</summary>
    public class GraphicsBakeCancelTool : IUnityCliTool<GraphicsBakeCancelTool.Request, GraphicsBakeCancelTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool Baking { get; set; } public bool Cancelled { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.bake-cancel", Summary = "Cancel a running lightmap bake.", Description = "Cancel Lightmapping if running. Returns cancelled=true if a bake was in progress.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Cancel bake", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var wasRunning = Lightmapping.isRunning;
            if (wasRunning) Lightmapping.Cancel();
            return new Result { Baking = Lightmapping.isRunning, Cancelled = wasRunning, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.bake-cancel", Property = "bake", Value = "cancelled" } } };
        }
    }

    /// <summary>graphics.bake-status — 读取光照烘焙状态。</summary>
    public class GraphicsBakeStatusTool : IUnityCliTool<GraphicsBakeStatusTool.Request, GraphicsBakeStatusTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool Baking { get; set; } public double Progress { get; set; } public BakeModeInfo Mode { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.bake-status", Summary = "Read lightmap baking progress and mode.", Description = "Read-only: returns baking state, progress (0-1), and lighting mode.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Check bake status", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
#if UNITY_2023_2_OR_NEWER
            var progress = Lightmapping.bakeProgress;
            var mode = Lightmapping.lightingMode;
            var modeStr = mode == LightingMode.Baked ? "baked" : mode == LightingMode.Mixed ? "mixed" : mode == LightingMode.Realtime ? "realtime" : mode.ToString();
#else
            var progress = Lightmapping.buildProgress;
            var modeStr = "unknown";
            var ls = Lightmapping.GetLightingSettingsForScene(SceneManager.GetActiveScene());
            if (ls != null) modeStr = ls.mixedBakeMode.ToString();
#endif
            return new Result { Baking = Lightmapping.isRunning, Progress = progress, Mode = new BakeModeInfo { LightingMode = modeStr, GiWorkflowMode = (long)Lightmapping.giWorkflowMode } };
        }
    }

    /// <summary>graphics.bake-clear — 清除光照烘焙数据。</summary>
    public class GraphicsBakeClearTool : IUnityCliTool<GraphicsBakeClearTool.Request, GraphicsBakeClearTool.Result>
    {
        public sealed class Request { public bool? BakedData { get; set; } public bool? RealtimeData { get; set; } }
        public sealed class Result { public bool ClearedBakedData { get; set; } public bool ClearedRealtimeData { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.bake-clear", Summary = "Clear baked and/or realtime lighting data.", Description = "Clear lighting data assets. bakedData=true (default) clears baked data; realtimeData=true (default) clears realtime.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "bake" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Clear all lighting data", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var clearedBaked = r.BakedData ?? true; var clearedRealtime = r.RealtimeData ?? true;
            if (clearedBaked) Lightmapping.ClearLightingDataAsset();
            if (clearedRealtime) Lightmapping.Clear();
            AssetDatabase.SaveAssets();
            return new Result { ClearedBakedData = clearedBaked, ClearedRealtimeData = clearedRealtime, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.bake-clear", Property = "lightingData", Value = "cleared" } } };
        }
    }

    /// <summary>graphics.bake-reflection-probe — 烘焙单个 ReflectionProbe。</summary>
    public class GraphicsBakeReflectionProbeTool : IUnityCliTool<GraphicsBakeReflectionProbeTool.Request, GraphicsBakeReflectionProbeTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Path { get; set; } }
        public sealed class Result { public ReflectionProbeInfo Probe { get; set; } public bool Baked { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.bake-reflection-probe", Summary = "Bake a single ReflectionProbe to an .exr cubemap asset.", Description = "Resolve a ReflectionProbe by target, then bake it to an .exr path. Default output is Assets/<name>_reflection.exr.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "bake", "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Bake reflection probe", Input = new Request { Target = "ReflectionProbe" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var probe = PhysicsToolSupport.ResolveReflectionProbe(r.Target);
            if (probe == null) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_NOT_FOUND", "ReflectionProbe not found: " + r.Target, "Run tools run graphics.reflection-probe to inspect probes or pass a GameObject name / hierarchy path.");
            var probePath = r.Path ?? ("Assets/" + probe.gameObject.name + "_reflection.exr");
            var baked = Lightmapping.BakeReflectionProbe(probe, probePath);
            return new Result { Probe = BuildReflectionProbeInfo(probe), Baked = baked, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.bake-reflection-probe", Property = "probe", Value = GameObjectToolSupport.HierarchyPath(probe.gameObject) } } };
        }
        internal static ReflectionProbeInfo BuildReflectionProbeInfo(ReflectionProbe probe)
        {
#if UNITY_2023_2_OR_NEWER
            return new ReflectionProbeInfo { Mode = probe.mode.ToString(), Resolution = probe.resolution, Bounces = probe.bounces, Hdr = probe.hdr, Importance = probe.importance, Size = new Vector3Data(probe.size), Center = new Vector3Data(probe.center) };
#else
            return new ReflectionProbeInfo { Mode = probe.mode.ToString(), Resolution = probe.resolution, Bounces = RenderSettings.reflectionBounces, Hdr = probe.hdr, Importance = probe.importance, Size = new Vector3Data(probe.size), Center = new Vector3Data(probe.center) };
#endif
        }
    }

    /// <summary>graphics.bake-settings — 读取/写入 LightingSettings。</summary>
    public class GraphicsBakeSettingsTool : IUnityCliTool<GraphicsBakeSettingsTool.Request, GraphicsBakeSettingsTool.Result>
    {
        public sealed class Request { public string Scene { get; set; } public string LightingMode { get; set; } public bool? BakedGI { get; set; } public bool? RealtimeGI { get; set; } public int? DirectSamples { get; set; } public int? IndirectSamples { get; set; } public int? EnvironmentSamples { get; set; } public int? Bounces { get; set; } public string Filtering { get; set; } }
        public sealed class Result { public LightingSettingsInfo Settings { get; set; } public string ScenePath { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.bake-settings", Summary = "Read or update LightingSettings for a scene.", Description = "Read or write LightingSettings (lightingMode, bakedGI, realtimeGI, samples, bounces, filtering). scene defaults to the active scene.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read bake settings", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var scene = ResolveLightingScene(r.Scene);
            var ls = Lightmapping.GetLightingSettingsForScene(scene);
            var effects = new List<GraphicsPlannedEffect>();
            var hasWrite = !string.IsNullOrEmpty(r.LightingMode) || r.BakedGI.HasValue || r.RealtimeGI.HasValue || r.DirectSamples.HasValue || r.IndirectSamples.HasValue || r.EnvironmentSamples.HasValue || r.Bounces.HasValue || !string.IsNullOrEmpty(r.Filtering);
            if (ls == null && hasWrite) { ls = new LightingSettings(); effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "lightingSettings", Value = "created" }); }
            if (ls != null)
            {
                if (!string.IsNullOrEmpty(r.LightingMode))
                {
#if UNITY_2023_2_OR_NEWER
                    if (System.Enum.TryParse<LightingMode>(r.LightingMode, true, out var lm)) ls.lightingMode = lm;
#else
                    if (System.Enum.TryParse<MixedLightingMode>(r.LightingMode, true, out var mm)) ls.mixedBakeMode = mm;
#endif
                    effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "lightingMode", Value = r.LightingMode });
                }
                if (r.BakedGI.HasValue) { ls.bakedGI = r.BakedGI.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "bakedGI", Value = r.BakedGI.Value.ToString().ToLowerInvariant() }); }
                if (r.RealtimeGI.HasValue) { ls.realtimeGI = r.RealtimeGI.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "realtimeGI", Value = r.RealtimeGI.Value.ToString().ToLowerInvariant() }); }
                if (r.DirectSamples.HasValue) { SetDirectSamples(ls, r.DirectSamples.Value); effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "directSamples", Value = r.DirectSamples.Value.ToString() }); }
                if (r.IndirectSamples.HasValue) { SetIndirectSamples(ls, r.IndirectSamples.Value); effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "indirectSamples", Value = r.IndirectSamples.Value.ToString() }); }
                if (r.EnvironmentSamples.HasValue) { SetEnvSamples(ls, r.EnvironmentSamples.Value); effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "environmentSamples", Value = r.EnvironmentSamples.Value.ToString() }); }
                if (r.Bounces.HasValue) { SetBounces(ls, r.Bounces.Value); effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "bounces", Value = r.Bounces.Value.ToString() }); }
                if (!string.IsNullOrEmpty(r.Filtering) && System.Enum.TryParse<LightingSettings.FilterType>(r.Filtering, true, out var ft)) { SetFiltering(ls, ft); effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "filtering", Value = r.Filtering }); }
                if (hasWrite) { Lightmapping.SetLightingSettingsForScene(scene, ls); UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene); }
            }
            return new Result { Settings = BuildLightingSettingsInfo(ls), ScenePath = scene.path, PlannedEffects = effects };
        }
        private static Scene ResolveLightingScene(string path)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++) { var s = SceneManager.GetSceneAt(i); if (string.IsNullOrEmpty(path) || s.path == path || s.name == path) return s; }
            return SceneManager.GetActiveScene();
        }
        // 版本适配:通过 #if 在方法内部分支,避免 duplicate method signature 检测误报
        private static void SetDirectSamples(LightingSettings ls, int v)
        {
#if UNITY_2023_2_OR_NEWER
            ls.directSamples = v;
#else
            ls.directSampleCount = v;
#endif
        }
        private static void SetIndirectSamples(LightingSettings ls, int v)
        {
#if UNITY_2023_2_OR_NEWER
            ls.indirectSamples = v;
#else
            ls.indirectSampleCount = v;
#endif
        }
        private static void SetEnvSamples(LightingSettings ls, int v)
        {
#if UNITY_2023_2_OR_NEWER
            ls.environmentSamples = v;
#else
            ls.environmentSampleCount = v;
#endif
        }
        private static void SetBounces(LightingSettings ls, int v)
        {
#if UNITY_2023_2_OR_NEWER
            ls.bounces = v;
#else
            ls.maxBounces = v;
#endif
        }
        private static void SetFiltering(LightingSettings ls, LightingSettings.FilterType ft)
        {
#if UNITY_2023_2_OR_NEWER
            ls.filtering = ft;
#else
            ls.filterTypeDirect = ft; ls.filterTypeIndirect = ft; ls.filterTypeAO = ft;
#endif
        }
        private static LightingSettingsInfo BuildLightingSettingsInfo(LightingSettings ls)
        {
            if (ls == null) return new LightingSettingsInfo();
#if UNITY_2023_2_OR_NEWER
            return new LightingSettingsInfo { LightingMode = ls.lightingMode.ToString(), BakedGI = ls.bakedGI, RealtimeGI = ls.realtimeGI, DirectSamples = ls.directSamples, IndirectSamples = ls.indirectSamples, EnvironmentSamples = ls.environmentSamples, Bounces = ls.bounces, Filtering = ls.filtering.ToString(), IndirectResolution = ls.indirectResolution };
#else
            return new LightingSettingsInfo { LightingMode = ls.mixedBakeMode.ToString(), BakedGI = ls.bakedGI, RealtimeGI = ls.realtimeGI, DirectSamples = ls.directSampleCount, IndirectSamples = ls.indirectSampleCount, EnvironmentSamples = ls.environmentSampleCount, Bounces = ls.maxBounces, Filtering = ls.filterTypeDirect.ToString(), IndirectResolution = ls.indirectResolution };
#endif
        }
    }
}
#endif
