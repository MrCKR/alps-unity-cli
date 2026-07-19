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
    public class ProfilerStopTool : IUnityCliTool<ProfilerStopTool.Request, ProfilerStopTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public ProfilerState Profiler { get; set; } public List<ProfilerPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.stop", Summary = "Stop profiler data collection.",
            Description = "Stop Unity Profiler data collection and restore the previous Editor profiling state.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "profiler-session-state" }, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Stop profiler", Input = new Request { } } }
        };

        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ctx.CommitEffect();
            ProfilerToolSupport.SetProfilerSessionEnabled(false);
            if (ProfilerToolSupport.PreviousProfilerEditorRecording.HasValue)
            {
                ProfilerToolSupport.SetProfilerEditorRecording(ProfilerToolSupport.PreviousProfilerEditorRecording.Value);
                ProfilerToolSupport.PreviousProfilerEditorRecording = null;
            }
            Profiler.enableBinaryLog = false;
            var stable = await ctx.Wait.UntilStable(() =>
            {
                var state = ProfilerToolSupport.BuildProfilerState();
                return !state.Enabled && !state.EnableBinaryLog;
            }, TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct: ct);
            var observed = ProfilerToolSupport.BuildProfilerState();
            if (!stable || observed.Enabled || observed.EnableBinaryLog)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_STATE_MISMATCH", "Profiler stop write did not produce the requested stable readback.", "Inspect Profiler availability and Editor session state, then retry.");
            return new Result
            {
                Profiler = observed,
                PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.stop" } }
            };
        }
    }
}
#endif
