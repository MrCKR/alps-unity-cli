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
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) => new System.Threading.Tasks.ValueTask<Result>(new Result { State = EditorToolSupport.BuildEditorState() });
    }
}
#endif
