#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>editor.stop — 退出 Play Mode。</summary>
    public class EditorStopTool : IUnityCliTool<EditorStopTool.Request, EditorStopTool.Response>
    {
        public sealed class Request { }
        public sealed class Response { public PlayModeResult Result { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.stop", Summary = "Exit Unity Play Mode.", Description = "Set EditorApplication.isPlaying=false. Returns immediately; poll editor.state to confirm.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "session-affecting" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Exit play mode", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Response> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.stop");
            var before = EditorApplication.isPlaying;
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = false;
            return new System.Threading.Tasks.ValueTask<Response>(new Response { Result = new PlayModeResult { Requested = "stop", Changed = before != EditorApplication.isPlaying, IsPlaying = EditorApplication.isPlaying, IsPaused = EditorApplication.isPaused }, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.stop" } } });
        }
    }
}
#endif
