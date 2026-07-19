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
    public class ProfilerPauseTool : IUnityCliTool<ProfilerPauseTool.Request, ProfilerPauseTool.Result>
    {
        public sealed class Request { public bool? Paused { get; set; } }
        public sealed class Result { public ProfilerState Profiler { get; set; } public bool Paused { get; set; } public List<ProfilerPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.pause", Summary = "Pause or resume Profiler data collection by toggling Unity Profiler.enabled.",
            Description = "Pause or resume Profiler data collection. Pass paused=true to pause, false to resume; omit to toggle the current state.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "profiler-session-state" }, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Pause profiler", Input = new Request { Paused = true } } }
        };

        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var wasEnabled = ProfilerToolSupport.GetProfilerSessionEnabled();
            var paused = request.Paused ?? wasEnabled;
            ctx.CommitEffect();
            ProfilerToolSupport.SetProfilerSessionEnabled(!paused);
            var stable = await ctx.Wait.UntilStable(
                () => ProfilerToolSupport.GetProfilerSessionEnabled() == !paused,
                TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct: ct);
            var isEnabled = ProfilerToolSupport.GetProfilerSessionEnabled();
            var observed = ProfilerToolSupport.BuildProfilerState();
            if (!stable || isEnabled != !paused)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_STATE_MISMATCH", "Profiler pause write did not produce the requested stable readback.", "Inspect Profiler availability and Editor session state, then retry.");
            return new Result
            {
                Profiler = observed,
                Paused = !isEnabled,
                PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.pause", SideEffects = "profilerEnabledFrom=" + wasEnabled + ",profilerEnabledTo=" + isEnabled } }
            };
        }
    }
}
#endif
