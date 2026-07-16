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
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Start profiler", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProfilerToolSupport.PreviousProfilerEditorRecording.HasValue)
                ProfilerToolSupport.PreviousProfilerEditorRecording = ProfilerToolSupport.GetProfilerEditorRecording();
            ProfilerToolSupport.SetProfilerEditorRecording(true);
            ProfilerToolSupport.SetProfilerSessionEnabled(true);
            ProfilerToolSupport.SetProfilerDeepProfiling(request.DeepProfiling);
            if (!string.IsNullOrEmpty(request.LogFile)) { Profiler.logFile = request.LogFile; Profiler.enableBinaryLog = true; }
            if (request.EnableCallstacks) Profiler.enableAllocationCallstacks = true;
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Profiler = ProfilerToolSupport.BuildProfilerState(),
                PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.start" } }
            });
        }
    }
}
#endif
