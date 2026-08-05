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
        public ToolDescriptor Describe() => new() { Name = "editor.play", Summary = "Enter Unity Play Mode and return only after the Editor reaches a stable playing state.", Description = "Set EditorApplication.isPlaying=true, absorb the PlayMode transition or same-session reload, and return the stable Editor state. Success proves isPlaying=true and the central PlayMode transition flag is false.", Group = "editor",DestructiveHint = false, SideEffects = new[] { "session-affecting" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Enter play mode", Input = new Request { } } } };
        public async System.Threading.Tasks.ValueTask<Response> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            // before 采集一次后按 requestId 存 SessionState：PlayMode 过渡触发 domain reload，
            // 同一请求重跑时 before 必须仍是第一次 effect 前的值，Changed 语义才跨 reload 成立。
            var beforeKey = "unity-cli.play.before." + ctx.RequestId;
            var rawBefore = SessionState.GetString(beforeKey, null);
            bool before;
            if (string.IsNullOrEmpty(rawBefore))
            {
                before = EditorApplication.isPlaying;
                SessionState.SetString(beforeKey, before ? "1" : "0");
            }
            else before = rawBefore == "1";
            EditorToolSupport.EnsureEditorNotBusy("editor.play");
            ctx.CommitEffect();
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = true;
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
