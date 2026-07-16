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
    public class ProfilerFrameDebuggerDisableTool : IUnityCliTool<ProfilerFrameDebuggerDisableTool.Request, ProfilerFrameDebuggerDisableTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public string Command { get; set; } public bool Enabled { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.frame-debugger.disable",
            Summary = "Disable the Unity Frame Debugger.",
            Description = "Disable the Unity Frame Debugger for the current Editor session. Use this after profiler.frame-debugger.events when draw-call inspection is complete; it returns enabled=false without modifying project assets.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Disable Frame Debugger", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!FrameDebuggerReflection.Available)
            {
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_UNAVAILABLE",
                    "FrameDebuggerUtility not found via reflection.",
                    "Run in a Unity Editor with Frame Debugger support.");
            }
            try { FrameDebuggerReflection.SetEnabled(false); }
            catch (Exception ex)
            {
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_FAILED",
                    "Failed to disable Frame Debugger: " + ex.Message,
                    "Check Unity Console.");
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "profiler.frame-debugger.disable", Enabled = false });
        }
    }
}
#endif
