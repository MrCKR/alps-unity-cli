#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace UnityCli
{
    public class ProfilerFrameDebuggerEnableTool : IUnityCliTool<ProfilerFrameDebuggerEnableTool.Request, ProfilerFrameDebuggerEnableTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool Enabled { get; set; } public long EventCount { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.frame-debugger.enable",
            Summary = "Enable the Unity Frame Debugger to capture draw call events.",
            Description = "Enable the Unity Frame Debugger to capture draw call events for the current Editor session. In Play Mode the game must be paused first with editor.pause; this opens the Frame Debugger window and returns the captured event count.",
            Group = "profiler",DestructiveHint = false, SideEffects = new[] { "frame-debugger-state", "editor-window-state" }, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Enable Frame Debugger", Input = new Request { } } }
        };

        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!FrameDebuggerReflection.Available)
            {
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_UNAVAILABLE",
                    "FrameDebuggerUtility not found via reflection.",
                    "Run in a Unity Editor with Frame Debugger support.");
            }
            // Play Mode 下必须暂停才能采集事件；验证通过后才可打开窗口。
            if (EditorApplication.isPlaying && !EditorApplication.isPaused)
            {
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_REQUIRES_PAUSE",
                    "Game must be paused before enabling Frame Debugger.",
                    "Pause the editor (editor.pause) first, then retry profiler.frame-debugger.enable.");
            }
            ctx.CommitEffect();
            EditorApplication.ExecuteMenuItem("Window/Analysis/Frame Debugger");
            try { FrameDebuggerReflection.SetEnabled(true); }
            catch (Exception ex)
            {
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_FAILED",
                    "Failed to enable Frame Debugger: " + ex.Message,
                    "Check Unity Console.");
            }
            var stable = await ctx.Wait.UntilStable(
                () => FrameDebuggerReflection.GetEnabled(),
                TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct: ct);
            var enabled = FrameDebuggerReflection.GetEnabled();
            if (!stable || !enabled) throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_STATE_MISMATCH", "Frame Debugger enable write did not produce an enabled readback.", "Check Frame Debugger availability and retry.");
            var eventCount = FrameDebuggerReflection.GetEventCount(ctx.UseReadBudget());
            return new Result
            {
                Enabled = enabled,
                EventCount = eventCount
            };
        }
    }
}
#endif
