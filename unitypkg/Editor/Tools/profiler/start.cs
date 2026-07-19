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
    public class ProfilerStartTool : IUnityCliTool<ProfilerStartTool.Request, ProfilerStartTool.Result>
    {
        public sealed class Request { public bool DeepProfiling { get; set; } public string LogFile { get; set; } public bool EnableCallstacks { get; set; } }
        public sealed class Result { public ProfilerState Profiler { get; set; } public List<ProfilerPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.start", Summary = "Begin profiler data collection with optional deep profiling, binary log file and allocation callstacks.",
            Description = "Begin Unity Profiler data collection in the Editor session. May enable deep profiling, binary logging or allocation callstacks.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "profiler-session-state" }, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Start profiler", Input = new Request { } } }
        };

        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ctx.CommitEffect();
            if (!ProfilerToolSupport.PreviousProfilerEditorRecording.HasValue)
                ProfilerToolSupport.PreviousProfilerEditorRecording = ProfilerToolSupport.GetProfilerEditorRecording();
            ProfilerToolSupport.SetProfilerEditorRecording(true);
            ProfilerToolSupport.SetProfilerSessionEnabled(true);
            ProfilerToolSupport.SetProfilerDeepProfiling(request.DeepProfiling);
            if (!string.IsNullOrEmpty(request.LogFile)) { Profiler.logFile = request.LogFile; Profiler.enableBinaryLog = true; }
            if (request.EnableCallstacks) Profiler.enableAllocationCallstacks = true;
            var stable = await ctx.Wait.UntilStable(() =>
            {
                var state = ProfilerToolSupport.BuildProfilerState();
                return state.Enabled && state.ProfileEditor && ProfilerToolSupport.GetProfilerDeepProfiling() == request.DeepProfiling
                    && (string.IsNullOrEmpty(request.LogFile) || (state.EnableBinaryLog && string.Equals(state.LogFile, request.LogFile, StringComparison.Ordinal)))
                    && (!request.EnableCallstacks || state.EnableAllocationCallstacks);
            }, TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct: ct);
            var observed = ProfilerToolSupport.BuildProfilerState();
            if (!stable || !observed.Enabled || !observed.ProfileEditor || ProfilerToolSupport.GetProfilerDeepProfiling() != request.DeepProfiling || (!string.IsNullOrEmpty(request.LogFile) && (!observed.EnableBinaryLog || !string.Equals(observed.LogFile, request.LogFile, StringComparison.Ordinal))) || (request.EnableCallstacks && !observed.EnableAllocationCallstacks))
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_STATE_MISMATCH", "Profiler start write did not produce the requested stable readback.", "Inspect Profiler availability and Editor session state, then retry.");
            return new Result
            {
                Profiler = observed,
                PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.start" } }
            };
        }
    }
}
#endif
