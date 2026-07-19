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
        public ToolDescriptor Describe() => new() { Name = "editor.play", Summary = "Enter Unity Play Mode and return only after the Editor reaches a stable playing state.", Description = "Set EditorApplication.isPlaying=true, absorb the PlayMode transition or same-session reload, and return the stable Editor state. Success proves isPlaying=true and the central PlayMode transition flag is false.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "session-affecting" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Enter play mode", Input = new Request { } } } };
        public async System.Threading.Tasks.ValueTask<Response> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var continuation = ctx.ProofOnly ? ctx.RequireProofContinuation("playmode-v1") : ctx.ProofContinuation;
            object expectedPlaying;
            object beforePlaying;
            if (continuation == null
                || !continuation.TryGetValue("expectedPlaying", out expectedPlaying) || !(expectedPlaying is bool) || !(bool)expectedPlaying
                || !continuation.TryGetValue("beforePlaying", out beforePlaying) || !(beforePlaying is bool))
                throw new UnityCliBridge.BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "Play Mode continuation is missing or does not target Play Mode.", "Inspect the request journal before submitting a new request.");
            var before = (bool)beforePlaying;
            if (!ctx.ProofOnly)
            {
                EditorToolSupport.EnsureEditorNotBusy("editor.play");
                ctx.CommitEffect();
                EditorApplication.isPaused = false;
                EditorApplication.isPlaying = true;
            }
            var stable = await ctx.Wait.UntilStable(
                () => EditorApplication.isPlaying && !UnityCliBridge.IsPlayModeChanging(),
                TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)),
                ct: ct);
            if (!stable || !EditorApplication.isPlaying || UnityCliBridge.IsPlayModeChanging())
                throw new UnityCliBridge.BridgeCommandException("E_PLAY_MODE_TERMINAL_NOT_OBSERVED", "Play Mode did not reach a stable playing state.", "Inspect editor.state before deciding whether to submit a new request.");
            return new Response { Result = new PlayModeResult { Changed = !before, IsPlaying = true, IsPaused = EditorApplication.isPaused }, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.play" } } };
        }
    }
}
#endif
