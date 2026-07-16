#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>editor.play — 进入 Play Mode。</summary>
    public class EditorPlayTool : IUnityCliTool<EditorPlayTool.Request, EditorPlayTool.Response>
    {
        public sealed class Request { }
        public sealed class Response { public PlayModeResult Result { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.play", Summary = "Enter Unity Play Mode.", Description = "Set EditorApplication.isPlaying=true. Returns immediately; poll editor.state to confirm.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "session-affecting" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Enter play mode", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Response> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.play");
            var before = EditorApplication.isPlaying;
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = true;
            return new System.Threading.Tasks.ValueTask<Response>(new Response { Result = new PlayModeResult { Requested = "play", Changed = before != EditorApplication.isPlaying, IsPlaying = EditorApplication.isPlaying, IsPaused = EditorApplication.isPaused }, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.play" } } });
        }
    }
}
#endif
