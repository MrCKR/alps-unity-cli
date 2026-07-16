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
        public sealed class Result { public string Command { get; set; } public bool Enabled { get; set; } public long EventCount { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.frame-debugger.enable",
            Summary = "Enable the Unity Frame Debugger to capture draw call events.",
            Description = "Enable the Unity Frame Debugger to capture draw call events for the current Editor session. In Play Mode the game must be paused first with editor.pause; this opens the Frame Debugger window and returns the captured event count.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Enable Frame Debugger", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!FrameDebuggerReflection.Available)
            {
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_UNAVAILABLE",
                    "FrameDebuggerUtility not found via reflection.",
                    "Run in a Unity Editor with Frame Debugger support.");
            }
            // 打开 Frame Debugger 窗口(事件采集所需)
            EditorApplication.ExecuteMenuItem("Window/Analysis/Frame Debugger");
            // Play Mode 下必须暂停才能采集事件
            if (EditorApplication.isPlaying && !EditorApplication.isPaused)
            {
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_REQUIRES_PAUSE",
                    "Game must be paused before enabling Frame Debugger.",
                    "Pause the editor (editor.pause) first, then retry profiler.frame-debugger.enable.");
            }
            try { FrameDebuggerReflection.SetEnabled(true); }
            catch (Exception ex)
            {
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_FAILED",
                    "Failed to enable Frame Debugger: " + ex.Message,
                    "Check Unity Console.");
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "profiler.frame-debugger.enable",
                Enabled = true,
                EventCount = FrameDebuggerReflection.GetEventCount()
            });
        }
    }
}
#endif
