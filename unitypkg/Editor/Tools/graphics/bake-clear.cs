#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>graphics.bake-clear — 清除光照烘焙数据。</summary>
    public class GraphicsBakeClearTool : IUnityCliTool<GraphicsBakeClearTool.Request, GraphicsBakeClearTool.Result>
    {
        public sealed class Request { public bool? BakedData { get; set; } public bool? RealtimeData { get; set; } }
        public sealed class Result { public bool ClearedBakedData { get; set; } public bool ClearedRealtimeData { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.bake-clear", Summary = "Clear baked and/or realtime lighting data.", Description = "Clear lighting data assets. bakedData=true (default) clears baked data; realtimeData=true (default) clears realtime.", Group = "graphics", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "bake" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Clear all lighting data", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var clearedBaked = r.BakedData ?? true; var clearedRealtime = r.RealtimeData ?? true;
            if (clearedBaked) Lightmapping.ClearLightingDataAsset();
            if (clearedRealtime) Lightmapping.Clear();
            AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { ClearedBakedData = clearedBaked, ClearedRealtimeData = clearedRealtime, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.bake-clear", Property = "lightingData", Value = "cleared" } } });
        }
    }
}
#endif
