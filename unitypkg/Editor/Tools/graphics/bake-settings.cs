#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>graphics.bake-settings — 读取/写入 LightingSettings。</summary>
    public class GraphicsBakeSettingsTool : IUnityCliTool<GraphicsBakeSettingsTool.Request, GraphicsBakeSettingsTool.Result>
    {
        public sealed class Request { public string Scene { get; set; } public string LightingMode { get; set; } public bool? BakedGI { get; set; } public bool? RealtimeGI { get; set; } public int? DirectSamples { get; set; } public int? IndirectSamples { get; set; } public int? EnvironmentSamples { get; set; } public int? Bounces { get; set; } public string Filtering { get; set; } }
        public sealed class Result { public LightingSettingsInfo Settings { get; set; } public string ScenePath { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.bake-settings", Summary = "Read or update LightingSettings for a scene.", Description = "Read or write LightingSettings (lightingMode, bakedGI, realtimeGI, samples, bounces, filtering). scene defaults to the active scene.", Group = "graphics", DestructiveHint = true, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read bake settings", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var scene = ResolveLightingScene(r.Scene);
            var ls = Lightmapping.GetLightingSettingsForScene(scene);
            var effects = new List<GraphicsPlannedEffect>();
            var hasWrite = !string.IsNullOrEmpty(r.LightingMode) || r.BakedGI.HasValue || r.RealtimeGI.HasValue || r.DirectSamples.HasValue || r.IndirectSamples.HasValue || r.EnvironmentSamples.HasValue || r.Bounces.HasValue || !string.IsNullOrEmpty(r.Filtering);
#if UNITY_2023_2_OR_NEWER
            var lightingMode = string.IsNullOrEmpty(r.LightingMode) ? (LightingMode?)null : GraphicsToolSupport.ParseEnum<LightingMode>(r.LightingMode, "lightingMode");
#else
            var lightingMode = string.IsNullOrEmpty(r.LightingMode) ? (MixedLightingMode?)null : GraphicsToolSupport.ParseEnum<MixedLightingMode>(r.LightingMode, "lightingMode");
#endif
            var filtering = string.IsNullOrEmpty(r.Filtering) ? (LightingSettings.FilterType?)null : GraphicsToolSupport.ParseEnum<LightingSettings.FilterType>(r.Filtering, "filtering");
            if ((r.DirectSamples.HasValue && r.DirectSamples.Value <= 0) || (r.IndirectSamples.HasValue && r.IndirectSamples.Value <= 0) || (r.EnvironmentSamples.HasValue && r.EnvironmentSamples.Value <= 0) || (r.Bounces.HasValue && r.Bounces.Value < 0)) throw new UnityCliBridge.BridgeCommandException("E_BAKE_SETTINGS_RANGE_INVALID", "Bake samples must be positive and bounces non-negative.", "Pass valid bake sample and bounce values.");
            if (hasWrite) ctx.CommitEffect();
            if (ls == null && hasWrite) { ls = new LightingSettings(); effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "lightingSettings", Value = "created" }); }
            if (ls != null)
            {
                if (!string.IsNullOrEmpty(r.LightingMode))
                {
#if UNITY_2023_2_OR_NEWER
                    ls.lightingMode = lightingMode.Value;
#else
                    ls.mixedBakeMode = lightingMode.Value;
#endif
                    effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "lightingMode", Value = r.LightingMode });
                }
                if (r.BakedGI.HasValue) { ls.bakedGI = r.BakedGI.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "bakedGI", Value = r.BakedGI.Value.ToString().ToLowerInvariant() }); }
                if (r.RealtimeGI.HasValue) { ls.realtimeGI = r.RealtimeGI.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "realtimeGI", Value = r.RealtimeGI.Value.ToString().ToLowerInvariant() }); }
                if (r.DirectSamples.HasValue) { SetDirectSamples(ls, r.DirectSamples.Value); effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "directSamples", Value = r.DirectSamples.Value.ToString() }); }
                if (r.IndirectSamples.HasValue) { SetIndirectSamples(ls, r.IndirectSamples.Value); effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "indirectSamples", Value = r.IndirectSamples.Value.ToString() }); }
                if (r.EnvironmentSamples.HasValue) { SetEnvSamples(ls, r.EnvironmentSamples.Value); effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "environmentSamples", Value = r.EnvironmentSamples.Value.ToString() }); }
                if (r.Bounces.HasValue) { SetBounces(ls, r.Bounces.Value); effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "bounces", Value = r.Bounces.Value.ToString() }); }
                if (filtering.HasValue) { SetFiltering(ls, filtering.Value); effects.Add(new GraphicsPlannedEffect { Type = "graphics.bake-settings", Property = "filtering", Value = r.Filtering }); }
                if (hasWrite)
                {
                    Lightmapping.SetLightingSettingsForScene(scene, ls);
                    EditorUtility.SetDirty(ls);
                    UnityCliSceneMutation.MarkSceneDirty(scene);
                    var lightingSettingsPath = AssetDatabase.GetAssetPath(ls);
                    if (string.IsNullOrEmpty(lightingSettingsPath)) throw new UnityCliBridge.BridgeCommandException("E_BAKE_SETTINGS_STATE_MISMATCH", "LightingSettings was not persisted at an asset path.", "Save the scene and retry after the editor is idle.");
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    LightingSettings fresh = null;
                    foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(lightingSettingsPath)) { fresh = asset as LightingSettings; if (fresh != null) break; }
                    if (fresh == null) throw new UnityCliBridge.BridgeCommandException("E_BAKE_SETTINGS_STATE_MISMATCH", "LightingSettings fresh asset readback failed.", "Retry after the editor is idle.");
                    ls = fresh;
                }
                var observed = Lightmapping.GetLightingSettingsForScene(scene);
                if (hasWrite && (observed == null || observed.GetInstanceID() != ls.GetInstanceID())) throw new UnityCliBridge.BridgeCommandException("E_BAKE_SETTINGS_STATE_MISMATCH", "Scene does not bind the freshly reloaded LightingSettings asset.", "Retry after the editor is idle.");
                if (observed != null) ls = observed;
                var readback = BuildLightingSettingsInfo(ls);
                if ((r.BakedGI.HasValue && readback.BakedGI != r.BakedGI.Value) || (r.RealtimeGI.HasValue && readback.RealtimeGI != r.RealtimeGI.Value) || (r.DirectSamples.HasValue && readback.DirectSamples != r.DirectSamples.Value) || (r.IndirectSamples.HasValue && readback.IndirectSamples != r.IndirectSamples.Value) || (r.EnvironmentSamples.HasValue && readback.EnvironmentSamples != r.EnvironmentSamples.Value) || (r.Bounces.HasValue && readback.Bounces != r.Bounces.Value) || (lightingMode.HasValue && readback.LightingMode != lightingMode.Value.ToString()) || (filtering.HasValue && readback.Filtering != filtering.Value.ToString())) throw new UnityCliBridge.BridgeCommandException("E_BAKE_SETTINGS_STATE_MISMATCH", "LightingSettings readback did not match the request.", "Retry after the editor is idle.");
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Settings = BuildLightingSettingsInfo(ls), ScenePath = scene.path, PlannedEffects = effects });
        }
        private static Scene ResolveLightingScene(string path)
        {
            if (string.IsNullOrEmpty(path)) return SceneManager.GetActiveScene();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.path == path || scene.name == path) return scene;
            }
            throw new UnityCliBridge.BridgeCommandException("E_BAKE_SCENE_NOT_FOUND", "Scene is not loaded: " + path, "Pass the path or name of a currently loaded scene.");
        }
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
