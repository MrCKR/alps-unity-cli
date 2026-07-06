#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>editor.state — 读取编辑器状态(playMode/busy/activeScene/prefabStage)。</summary>
    public class EditorStateTool : IUnityCliTool<EditorStateTool.Request, EditorStateTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public EditorStateInfo State { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.state", Summary = "Read editor play mode, busy state, active scene, and prefab stage status.", Description = "Read-only: returns isPlaying, isPaused, isCompiling, isUpdating, isBuilding, activeScene, and prefabStage.", Group = "editor", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read editor state", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx) => new Result { State = EditorToolSupport.BuildEditorState() };
    }

    /// <summary>editor.play — 进入 Play Mode。</summary>
    public class EditorPlayTool : IUnityCliTool<EditorPlayTool.Request, EditorPlayTool.Response>
    {
        public sealed class Request { public int? TimeoutMs { get; set; } }
        public sealed class Response { public PlayModeResult Result { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.play", Summary = "Enter Unity Play Mode.", Description = "Set EditorApplication.isPlaying=true. Returns immediately; poll editor.state to confirm.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "session-affecting" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Enter play mode", Input = new Request { } } } };
        public Response Execute(Request r, UnityCliProjectToolContext ctx)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.play");
            var before = EditorApplication.isPlaying;
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = true;
            return new Response { Result = new PlayModeResult { Requested = "play", Changed = before != EditorApplication.isPlaying, IsPlaying = EditorApplication.isPlaying, IsPaused = EditorApplication.isPaused }, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.play" } } };
        }
    }

    /// <summary>editor.pause — 切换暂停状态。</summary>
    public class EditorPauseTool : IUnityCliTool<EditorPauseTool.Request, EditorPauseTool.Response>
    {
        public sealed class Request { public int? TimeoutMs { get; set; } }
        public sealed class Response { public PlayModeResult Result { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.pause", Summary = "Toggle Play Mode pause state.", Description = "Toggle EditorApplication.isPaused.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "session-affecting" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Toggle pause", Input = new Request { } } } };
        public Response Execute(Request r, UnityCliProjectToolContext ctx)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.pause");
            var before = EditorApplication.isPaused;
            EditorApplication.isPaused = !EditorApplication.isPaused;
            return new Response { Result = new PlayModeResult { Requested = "pause", Changed = before != EditorApplication.isPaused, IsPlaying = EditorApplication.isPlaying, IsPaused = EditorApplication.isPaused }, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.pause" } } };
        }
    }

    /// <summary>editor.stop — 退出 Play Mode。</summary>
    public class EditorStopTool : IUnityCliTool<EditorStopTool.Request, EditorStopTool.Response>
    {
        public sealed class Request { public int? TimeoutMs { get; set; } }
        public sealed class Response { public PlayModeResult Result { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.stop", Summary = "Exit Unity Play Mode.", Description = "Set EditorApplication.isPlaying=false. Returns immediately; poll editor.state to confirm.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "session-affecting" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Exit play mode", Input = new Request { } } } };
        public Response Execute(Request r, UnityCliProjectToolContext ctx)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.stop");
            var before = EditorApplication.isPlaying;
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = false;
            return new Response { Result = new PlayModeResult { Requested = "stop", Changed = before != EditorApplication.isPlaying, IsPlaying = EditorApplication.isPlaying, IsPaused = EditorApplication.isPaused }, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.stop" } } };
        }
    }

    /// <summary>editor.undo — 执行 Undo。</summary>
    public class EditorUndoTool : IUnityCliTool<EditorUndoTool.Request, EditorUndoTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool Performed { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.undo", Summary = "Perform an Unity Editor undo operation.", Description = "Call Undo.PerformUndo().", Group = "editor", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Undo", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.undo");
            Undo.PerformUndo();
            return new Result { Performed = true, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.undo" } } };
        }
    }

    /// <summary>editor.redo — 执行 Redo。</summary>
    public class EditorRedoTool : IUnityCliTool<EditorRedoTool.Request, EditorRedoTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool Performed { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.redo", Summary = "Perform an Unity Editor redo operation.", Description = "Call Undo.PerformRedo().", Group = "editor", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Redo", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.redo");
            Undo.PerformRedo();
            return new Result { Performed = true, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.redo" } } };
        }
    }

    /// <summary>editor.resolution — 设置 PlayerSettings 分辨率。</summary>
    public class EditorResolutionTool : IUnityCliTool<EditorResolutionTool.Request, EditorResolutionTool.Result>
    {
        public sealed class Request { public int? Width { get; set; } public int? Height { get; set; } public bool? Fullscreen { get; set; } public int? TimeoutMs { get; set; } }
        public sealed class Result { public ResolutionInfo Resolution { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.resolution", Summary = "Set PlayerSettings default screen width, height, and fullscreen mode.", Description = "Update PlayerSettings.defaultScreenWidth/Height and fullScreenMode.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set 1920x1080", Input = new Request { Width = 1920, Height = 1080 } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.resolution");
            var width = Math.Max(1, r.Width ?? PlayerSettings.defaultScreenWidth);
            var height = Math.Max(1, r.Height ?? PlayerSettings.defaultScreenHeight);
            PlayerSettings.defaultScreenWidth = width;
            PlayerSettings.defaultScreenHeight = height;
            if (r.Fullscreen.HasValue) PlayerSettings.fullScreenMode = r.Fullscreen.Value ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            return new Result { Resolution = new ResolutionInfo { Width = width, Height = height, FullScreenMode = PlayerSettings.fullScreenMode.ToString() }, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.resolution" } } };
        }
    }

    public sealed class ResolutionInfo { public long Width { get; set; } public long Height { get; set; } public string FullScreenMode { get; set; } }
}
#endif
