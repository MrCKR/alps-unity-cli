#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>editor.redo — 执行 Redo。</summary>
    public class EditorRedoTool : IUnityCliTool<EditorRedoTool.Request, EditorRedoTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool Performed { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.redo", Summary = "Perform an Unity Editor redo operation.", Description = "Call Undo.PerformRedo().", Group = "editor", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Redo", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.redo");
            Undo.PerformRedo();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Performed = true, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.redo" } } });
        }
    }
}
#endif
