#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>editor.pause — 切换暂停状态。</summary>
    public class EditorPauseTool : IUnityCliTool<EditorPauseTool.Request, EditorPauseTool.Response>
    {
        public sealed class Request { }
        public sealed class Response { public PlayModeResult Result { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.pause", Summary = "Toggle Play Mode pause state and return its observed terminal readback.", Description = "Toggle EditorApplication.isPaused and succeed only when the readback is the inverse of the pre-call state.", Group = "editor",DestructiveHint = false, SideEffects = new[] { "session-affecting" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Toggle pause", Input = new Request { } } } };

        public async System.Threading.Tasks.ValueTask<Response> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.pause");
            var before = EditorApplication.isPaused;
            var expectedPaused = !before;
            ctx.CommitEffect();
            EditorApplication.isPaused = expectedPaused;
            var stable = await ctx.Wait.UntilStable(
                () => EditorApplication.isPaused == expectedPaused,
                TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)),
                ct: ct);
            if (!stable)
                throw new UnityCliBridge.BridgeCommandException("E_PLAYMODE_PAUSE_STATE_MISMATCH", "Unity did not apply the requested pause-state toggle.", "Inspect Play Mode state before retrying.");

            // 末次读回避免把同一帧的瞬态状态作为成功结果。
            var observedPaused = EditorApplication.isPaused;
            if (observedPaused != expectedPaused)
                throw new UnityCliBridge.BridgeCommandException("E_PLAYMODE_PAUSE_STATE_MISMATCH", "Unity did not apply the requested pause-state toggle.", "Inspect Play Mode state before retrying.");
            var state = EditorToolSupport.BuildEditorState();
            return new Response { Result = new PlayModeResult { Changed = observedPaused != before, IsPlaying = state.PlayMode.IsPlaying, IsPaused = observedPaused }, State = state, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.pause" } } };
        }
    }
}
#endif
