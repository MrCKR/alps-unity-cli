#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>graphics.scene-debug — 设置 SceneView 着色/绘制模式。</summary>
    public class GraphicsSceneDebugTool : IUnityCliTool<GraphicsSceneDebugTool.Request, GraphicsSceneDebugTool.Result>
    {
        public sealed class Request { public string ShadingMode { get; set; } public string DrawMode { get; set; } }
        public sealed class Result { public bool SceneViewAvailable { get; set; } public string CurrentDrawMode { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.scene-debug", Summary = "Set the SceneView shading or draw mode for debugging.", Description = "Set the last active SceneView's camera mode. shadingMode (Unity 6 ShadingMode) or drawMode (DrawCameraMode enum).", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set wireframe mode", Input = new Request { DrawMode = "Wireframe" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(r.ShadingMode) && SceneView.lastActiveSceneView != null)
            {
#if UNITY_2023_2_OR_NEWER
                if (System.Enum.TryParse<SceneView.ShadingMode>(r.ShadingMode, true, out var sm)) { SceneView.lastActiveSceneView.SetSceneViewMode(sm, true); effects.Add(new GraphicsPlannedEffect { Type = "graphics.scene-debug", Property = "shadingMode", Value = r.ShadingMode }); }
#else
                if (System.Enum.TryParse<DrawCameraMode>(r.ShadingMode, true, out var dm)) { SceneView.lastActiveSceneView.cameraMode = SceneView.GetBuiltinCameraMode(dm); effects.Add(new GraphicsPlannedEffect { Type = "graphics.scene-debug", Property = "shadingMode", Value = r.ShadingMode }); }
#endif
            }
            if (!string.IsNullOrEmpty(r.DrawMode) && SceneView.lastActiveSceneView != null)
            {
                if (System.Enum.TryParse<DrawCameraMode>(r.DrawMode, true, out var parsed)) { SceneView.lastActiveSceneView.cameraMode = SceneView.GetBuiltinCameraMode(parsed); effects.Add(new GraphicsPlannedEffect { Type = "graphics.scene-debug", Property = "drawMode", Value = r.DrawMode }); }
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { SceneViewAvailable = SceneView.lastActiveSceneView != null, CurrentDrawMode = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.cameraMode.name : null, PlannedEffects = effects });
        }
    }
}
#endif
