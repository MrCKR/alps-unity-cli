#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>editor.redo — 执行 Redo。</summary>
    public class EditorRedoTool : IUnityCliTool<EditorRedoTool.Request, EditorRedoTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool Performed { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.redo", Summary = "Perform one entry from the shared Unity Editor redo stack.", Description = "Call Undo.PerformRedo() once against the shared Editor Undo stack. performed=true proves Unity raised undoRedoPerformed for this operation.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "undo-redo-replay", "undo-stack" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Redo", Input = new Request { } } } };

        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.redo");
            var performed = false;
            Undo.UndoRedoCallback callback = () => performed = true;
            Undo.undoRedoPerformed += callback;
            try
            {
                ctx.CommitEffect();
                Undo.PerformRedo();
            }
            finally
            {
                Undo.undoRedoPerformed -= callback;
            }
            if (!performed)
                throw new UnityCliBridge.BridgeCommandException("E_REDO_NOT_PERFORMED", "Unity did not perform a Redo operation.", "Ensure the shared Redo stack contains an entry, then retry.");
            await ctx.Wait.NextFrame(1, ct);
            throw new UnityCliBridge.BridgeCommandException("E_REDO_BUSINESS_PROOF_UNAVAILABLE", "Redo changed shared Editor history, but this request cannot attribute an affected business state to itself.", "Inspect the affected object with its read Context before treating the shared history operation as complete.");
        }
    }
}
#endif
