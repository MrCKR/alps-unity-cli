#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>lighting.bake-clear — 清除光照烘焙数据。</summary>
    public class LightingBakeClearTool : IUnityCliTool<LightingBakeClearTool.Request, LightingBakeClearTool.Result>
    {
        public sealed class Request { public bool? BakedData { get; set; } public bool? RealtimeData { get; set; } }
        public sealed class Result { public bool ClearedBakedData { get; set; } public bool ClearedRealtimeData { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "lighting.bake-clear", Summary = "Clear the observable baked lighting data asset.", Description = "Clear the baked lighting data asset and verify Lightmapping.lightingDataAsset is null. realtimeData=true is rejected because Unity exposes no reliable realtime-data completion readback.", Group = "lighting",DestructiveHint = true, SideEffects = new[] { "bake" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Clear baked lighting data", Input = new Request { BakedData = true } } } };
        // 清除可验证的烘焙数据，并拒绝无法可靠证明的实时数据请求。
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (r.RealtimeData == true)
                throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_REALTIME_CLEAR_UNPROVABLE", "Unity does not expose a reliable readback proving realtime lighting data was cleared.", "Clear baked lighting data only, or inspect realtime lighting data manually in the Unity Editor.");
            var clearedBaked = r.BakedData ?? true;
            ctx.CommitEffect();
            if (clearedBaked) Lightmapping.ClearLightingDataAsset();
            AssetDatabase.SaveAssets();
            var observedBakedCleared = Lightmapping.lightingDataAsset == null;
            if (clearedBaked && !observedBakedCleared)
                throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_BAKED_CLEAR_STATE_MISMATCH", "Unity still exposes a baked lighting data asset after clearing.", "Inspect the Lighting window and retry after the Editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { ClearedBakedData = clearedBaked && observedBakedCleared, ClearedRealtimeData = false, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "lighting.bake-clear", Property = "lightingData", Value = "cleared" } } });
        }
    }
}
#endif
