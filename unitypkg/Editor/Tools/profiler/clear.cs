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
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public bool Cleared { get; set; } public List<ProfilerPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.clear", Summary = "Clear all collected profiler data.",
            Description = "Clear all collected profiler data from the current Editor session. Supports dry-run.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Clear profiler data", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (ctx.DryRun)
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "profiler.clear", DryRun = true, PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.clear", SideEffects = "clears-profiler-data" } } });
            // 反射调用 ProfilerDriver.Clear()
            var clearMethod = FindType("UnityEditorInternal.ProfilerDriver")?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public);
            try { clearMethod?.Invoke(null, null); } catch { }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "profiler.clear", Cleared = true, PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.clear" } } });
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
