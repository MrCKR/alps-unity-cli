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
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Stop profiler", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ProfilerToolSupport.SetProfilerSessionEnabled(false);
            if (ProfilerToolSupport.PreviousProfilerEditorRecording.HasValue)
            {
                ProfilerToolSupport.SetProfilerEditorRecording(ProfilerToolSupport.PreviousProfilerEditorRecording.Value);
                ProfilerToolSupport.PreviousProfilerEditorRecording = null;
            }
            Profiler.enableBinaryLog = false;
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Profiler = ProfilerToolSupport.BuildProfilerState(),
                PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.stop" } }
            });
        }
    }
}
#endif
