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
    public class ProfilerClearTool : IUnityCliTool<ProfilerClearTool.Request, ProfilerClearTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool DryRun { get; set; } public bool Cleared { get; set; } public List<ProfilerPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.clear", Summary = "Clear all collected profiler data.",
            Description = "Clear all collected profiler data from the current Editor session. Supports dry-run.",
            Group = "profiler",DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "profiler-data-clear" }, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Clear profiler data", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (ctx.DryRun)
                return new System.Threading.Tasks.ValueTask<Result>(new Result { DryRun = true, PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.clear", SideEffects = "clears-profiler-data" } } });
            // Unity 2022.3 通过 ClearAllFrames 清空当前 session，并以帧范围读回作为完成证明。
            var driverType = FindType("UnityEditorInternal.ProfilerDriver");
            var clearMethod = driverType?.GetMethod("ClearAllFrames", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (clearMethod == null)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_CLEAR_UNSUPPORTED", "ProfilerDriver.ClearAllFrames is unavailable.", "Use a supported Unity Editor version that exposes ProfilerDriver.ClearAllFrames.");
            ctx.CommitEffect();
            clearMethod.Invoke(null, null);
            var firstFrameIndex = ProfilerToolSupport.ProfilerDriverIntProperty("firstFrameIndex", -1);
            var lastFrameIndex = ProfilerToolSupport.ProfilerDriverIntProperty("lastFrameIndex", -1);
            if (firstFrameIndex >= 0 || lastFrameIndex >= 0)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_CLEAR_STATE_MISMATCH", "ProfilerDriver still exposes collected frames after clearing.", "Inspect the Profiler window and retry.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Cleared = true, PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.clear" } } });
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = assembly.GetType(fullName, false); } catch { }
                if (type != null) return type;
            }
            return null;
        }
    }
}
#endif
