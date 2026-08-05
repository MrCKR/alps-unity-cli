#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>editor.resolution — 设置 PlayerSettings 分辨率。</summary>
    public class EditorResolutionTool : IUnityCliTool<EditorResolutionTool.Request, EditorResolutionTool.Result>
    {
        public sealed class Request { public int? Width { get; set; } public int? Height { get; set; } public bool? Fullscreen { get; set; } }
        public sealed class Result { public ResolutionInfo Resolution { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.resolution", Summary = "Set PlayerSettings default screen width, height, and fullscreen mode.", Description = "Update PlayerSettings.defaultScreenWidth/Height and fullScreenMode.", Group = "editor",DestructiveHint = true, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set 1920x1080", Input = new Request { Width = 1920, Height = 1080 } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.resolution");
            var width = Math.Max(1, r.Width ?? PlayerSettings.defaultScreenWidth);
            var height = Math.Max(1, r.Height ?? PlayerSettings.defaultScreenHeight);
            ctx.CommitEffect();
            PlayerSettings.defaultScreenWidth = width;
            PlayerSettings.defaultScreenHeight = height;
            if (r.Fullscreen.HasValue) PlayerSettings.fullScreenMode = r.Fullscreen.Value ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            var observedWidth = PlayerSettings.defaultScreenWidth;
            var observedHeight = PlayerSettings.defaultScreenHeight;
            var observedMode = PlayerSettings.fullScreenMode;
            if (observedWidth != width || observedHeight != height || (r.Fullscreen.HasValue && observedMode != (r.Fullscreen.Value ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed)))
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_RESOLUTION_STATE_MISMATCH", "PlayerSettings resolution readback did not match the request.", "Inspect Player Settings and retry after the Editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Resolution = new ResolutionInfo { Width = observedWidth, Height = observedHeight, FullScreenMode = observedMode.ToString() }, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.resolution" } } });
        }
    }
}
#endif
