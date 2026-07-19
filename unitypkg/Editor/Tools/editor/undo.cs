#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>editor.undo — 执行 Undo。</summary>
    public class EditorUndoTool : IUnityCliTool<EditorUndoTool.Request, EditorUndoTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool Performed { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.undo", Summary = "Perform exactly one entry from the shared Unity Editor Undo stack.", Description = "Call Undo.PerformUndo() exactly once against the shared Editor Undo stack. performed=true proves Unity raised undoRedoPerformed for this operation. Verify the returned state and required business effect. Do not repeat this Tool based on an assumed operation count when pre-existing Undo history is unknown. To discard all temporary in-memory scene changes and restore a clean scene from disk without saving, use scene.load once with the exact active scene path instead.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "undo-redo-replay", "undo-stack" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Undo exactly one shared Editor entry", Input = new Request { } } } };

        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.undo");
            var performed = false;
            Undo.UndoRedoCallback callback = () => performed = true;
            Undo.undoRedoPerformed += callback;
            try
            {
                ctx.CommitEffect();
                Undo.PerformUndo();
            }
            finally
            {
                Undo.undoRedoPerformed -= callback;
            }
            if (!performed)
                throw new UnityCliBridge.BridgeCommandException("E_UNDO_NOT_PERFORMED", "Unity did not perform an Undo operation.", "Ensure the shared Undo stack contains an entry, then retry.");
            await ctx.Wait.NextFrame(1, ct);
            throw new UnityCliBridge.BridgeCommandException("E_UNDO_BUSINESS_PROOF_UNAVAILABLE", "Undo changed shared Editor history, but this request cannot attribute an affected business state to itself.", "Use scene.load with the exact scene path to discard a known clean-scene fixture, or inspect the affected object with its read Context.");
        }
    }
}
#endif
