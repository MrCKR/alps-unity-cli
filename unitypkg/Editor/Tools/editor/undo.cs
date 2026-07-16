#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>editor.undo — 执行 Undo。</summary>
    public class EditorUndoTool : IUnityCliTool<EditorUndoTool.Request, EditorUndoTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool Performed { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.undo", Summary = "Perform exactly one entry from the shared Unity Editor Undo stack.", Description = "Call Undo.PerformUndo() exactly once against the shared Editor Undo stack. performed=true means the call was issued; it does not prove that a caller's own change was undone or that the scene became clean. Verify the returned state and required business effect. Do not repeat this Tool based on an assumed operation count when pre-existing Undo history is unknown. To discard all temporary in-memory scene changes and restore a clean scene from disk without saving, use scene.load once with the exact active scene path instead.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Undo exactly one shared Editor entry", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.undo");
            Undo.PerformUndo();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Performed = true, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.undo" } } });
        }
    }
}
#endif
