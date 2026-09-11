#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>editor.play-mode.stop — 退出 Play Mode。</summary>
    public class EditorPlayModeStopTool : IUnityCliTool<EditorPlayModeStopTool.Request, EditorPlayModeStopTool.Response>
    {
        public sealed class Request { }
        public sealed class Response { public PlayModeResult Result { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.play-mode.stop", Summary = "Exit Unity Play Mode and return only after the Editor reaches stable Edit Mode.", Description = "Set EditorApplication.isPlaying=false, absorb the PlayMode transition or same-session reload, and return the stable Editor state. Success proves isPlaying=false and the central PlayMode transition flag is false.", Group = "editor",DestructiveHint = false, SideEffects = new[] { "session-affecting" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Exit play mode", Input = new Request { } } } };
        public async System.Threading.Tasks.ValueTask<Response> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            // 首次effect前状态由固定direct root持久化；Tool重入不再创建逐请求SessionState键。
            var before = EditorApplication.isPlaying;
            EditorToolSupport.EnsureEditorNotBusy("editor.play-mode.stop");
            ctx.CommitEffect();
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = false;
            var stable = await ctx.Wait.UntilStable(
                () => !EditorApplication.isPlaying && !UnityCliBridge.IsPlayModeChanging(),
                TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)),
                ct: ct);
            if (!stable || EditorApplication.isPlaying || UnityCliBridge.IsPlayModeChanging())
                throw new UnityCliBridge.BridgeCommandException("E_EDIT_MODE_TERMINAL_NOT_OBSERVED", "The Editor did not reach stable Edit Mode.", "Inspect editor.state before deciding whether to submit a new request.");
            return new Response { Result = new PlayModeResult { Changed = before, IsPlaying = false, IsPaused = false }, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.play-mode.stop" } } };
        }
    }
}
#endif
