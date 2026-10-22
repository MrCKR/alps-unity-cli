#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>editor.redo — 执行 Redo。</summary>
    public class EditorRedoTool : IUnityCliTool<EditorRedoTool.Request, EditorRedoTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public UnityCliCompletionProbe CompletionProbe { get; set; } }
        public sealed class Result { public bool Performed { get; set; } public EditorStateInfo State { get; set; } public UnityCliCompletionProbeReadback Completion { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new()
        {
            Name = "editor.redo", Summary = "Perform one shared Redo entry and prove the declared object state.",
            Description = "Call Undo.PerformRedo() exactly once. completionProbe must identify an eligible read Tool and a before/after state for the affected resource; the Redo callback alone is not completion proof.",
            Group = "editor", DestructiveHint = false, SideEffects = new[] { "undo-redo-replay", "undo-stack" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Redo an Undo-recorded Main Camera x move from 0 to 1.",
                    Input = new Request
                    {
                        CompletionProbe = new UnityCliCompletionProbe
                        {
                            ReadTool = "scene.gameobject", Input = new Dictionary<string, object> { { "target", "Main Camera" } },
                            Assertions = new List<UnityCliCompletionAssertion>
                            {
                                new UnityCliCompletionAssertion { Path = "/gameObject/transform/position/x", Operator = "approx", Before = 0.0, After = 1.0, Tolerance = 0.0001 }
                            }
                        }
                    }
                }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.redo");
            var performed = false;
            Undo.UndoRedoCallback callback = () => performed = true;
            Undo.undoRedoPerformed += callback;
            try
            {
                var completion = ctx.RunCompletionProbe(r == null ? null : r.CompletionProbe, () =>
                {
                    Undo.PerformRedo();
                    if (!performed) throw new UnityCliBridge.BridgeCommandException("E_REDO_NOOP", "Unity did not perform a Redo operation.", "Ensure the shared Redo stack contains an entry, then retry.");
                });
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Performed = true, State = EditorToolSupport.BuildEditorState(), Completion = completion, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "undo-redo-replay", Action = "redo" } } });
            }
            finally { Undo.undoRedoPerformed -= callback; }
        }
    }
}
#endif
