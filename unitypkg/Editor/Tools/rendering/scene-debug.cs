#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
namespace UnityCli
{
    public class RenderingSceneDebugTool : IUnityCliTool<RenderingSceneDebugTool.Request, RenderingSceneDebugTool.Result>
    {
        public sealed class Request { public string ShadingMode { get; set; } public string DrawMode { get; set; } }
        public sealed class Result { public bool SceneViewAvailable { get; set; } public string CurrentDrawMode { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "rendering.scene-debug", Summary = "Set the SceneView shading or draw mode for debugging.", Description = "Change the current Editor SceneView camera mode.", Group = "rendering",SideEffects = new[] { "scene-view-state" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Show SceneView wireframe", Input = new Request { DrawMode = "Wireframe" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null) return new System.Threading.Tasks.ValueTask<Result>(new Result { SceneViewAvailable = false, PlannedEffects = new List<GraphicsPlannedEffect>() });
            DrawCameraMode? shading = string.IsNullOrEmpty(r.ShadingMode) ? (DrawCameraMode?)null : GraphicsToolSupport.ParseEnum<DrawCameraMode>(r.ShadingMode, "shadingMode");
            DrawCameraMode? draw = string.IsNullOrEmpty(r.DrawMode) ? (DrawCameraMode?)null : GraphicsToolSupport.ParseEnum<DrawCameraMode>(r.DrawMode, "drawMode");
            if (!shading.HasValue && !draw.HasValue) return new System.Threading.Tasks.ValueTask<Result>(new Result { SceneViewAvailable = true, CurrentDrawMode = view.cameraMode.name, PlannedEffects = new List<GraphicsPlannedEffect>() });
            var expected = draw ?? shading.Value;
#if UNITY_2023_2_OR_NEWER
            ctx.CommitEffect();
            view.cameraMode = SceneView.GetBuiltinCameraMode(expected);
#else
            ctx.CommitEffect();
            view.cameraMode = SceneView.GetBuiltinCameraMode(expected);
#endif
            view.Repaint();
            var observed = SceneView.lastActiveSceneView;
            if (observed == null || observed.cameraMode.drawMode != expected) throw new UnityCliBridge.BridgeCommandException("E_SCENE_DEBUG_STATE_MISMATCH", "SceneView draw mode readback did not match the request.", "Retry when a SceneView is active.");
            var effects = new List<GraphicsPlannedEffect>();
            if (shading.HasValue) effects.Add(new GraphicsPlannedEffect { Type = "rendering.scene-debug", Property = "shadingMode", Value = shading.Value.ToString() });
            if (draw.HasValue) effects.Add(new GraphicsPlannedEffect { Type = "rendering.scene-debug", Property = "drawMode", Value = draw.Value.ToString() });
            return new System.Threading.Tasks.ValueTask<Result>(new Result { SceneViewAvailable = true, CurrentDrawMode = observed.cameraMode.name, PlannedEffects = effects });
        }
    }
}
#endif
