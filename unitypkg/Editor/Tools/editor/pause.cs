#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>editor.pause — 切换暂停状态。</summary>
    public class EditorPauseTool : IUnityCliTool<EditorPauseTool.Request, EditorPauseTool.Response>
    {
        public sealed class Request { }
        public sealed class Response { public PlayModeResult Result { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.pause", Summary = "Toggle Play Mode pause state.", Description = "Toggle EditorApplication.isPaused.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "session-affecting" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Toggle pause", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Response> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.pause");
            var before = EditorApplication.isPaused;
            EditorApplication.isPaused = !EditorApplication.isPaused;
            return new System.Threading.Tasks.ValueTask<Response>(new Response { Result = new PlayModeResult { Requested = "pause", Changed = before != EditorApplication.isPaused, IsPlaying = EditorApplication.isPlaying, IsPaused = EditorApplication.isPaused }, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.pause" } } });
        }
    }
}
#endif
