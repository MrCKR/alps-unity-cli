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
        public ToolDescriptor Describe() => new() { Name = "editor.stop", Summary = "Exit Unity Play Mode and return only after the Editor reaches stable Edit Mode.", Description = "Set EditorApplication.isPlaying=false, absorb the PlayMode transition or same-session reload, and return the stable Editor state. Success proves isPlaying=false and the central PlayMode transition flag is false.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "session-affecting" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Exit play mode", Input = new Request { } } } };
        public async System.Threading.Tasks.ValueTask<Response> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var continuation = ctx.ProofOnly ? ctx.RequireProofContinuation("playmode-v1") : ctx.ProofContinuation;
            object expectedPlaying;
            object beforePlaying;
            if (continuation == null
                || !continuation.TryGetValue("expectedPlaying", out expectedPlaying) || !(expectedPlaying is bool) || (bool)expectedPlaying
                || !continuation.TryGetValue("beforePlaying", out beforePlaying) || !(beforePlaying is bool))
                throw new UnityCliBridge.BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "Play Mode continuation is missing or does not target Edit Mode.", "Inspect the request journal before submitting a new request.");
            var before = (bool)beforePlaying;
            if (!ctx.ProofOnly)
            {
                EditorToolSupport.EnsureEditorNotBusy("editor.stop");
                ctx.CommitEffect();
                EditorApplication.isPaused = false;
                EditorApplication.isPlaying = false;
            }
            var stable = await ctx.Wait.UntilStable(
                () => !EditorApplication.isPlaying && !UnityCliBridge.IsPlayModeChanging(),
                TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)),
                ct: ct);
            if (!stable || EditorApplication.isPlaying || UnityCliBridge.IsPlayModeChanging())
                throw new UnityCliBridge.BridgeCommandException("E_EDIT_MODE_TERMINAL_NOT_OBSERVED", "The Editor did not reach stable Edit Mode.", "Inspect editor.state before deciding whether to submit a new request.");
            return new Response { Result = new PlayModeResult { Changed = before, IsPlaying = false, IsPaused = false }, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.stop" } } };
        }
    }
}
#endif
