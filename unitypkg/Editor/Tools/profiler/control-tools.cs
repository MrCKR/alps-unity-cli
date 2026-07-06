#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

namespace UnityCli
{
    /// <summary>
    /// profiler.start / profiler.stop / profiler.pause / profiler.deep —
    /// Profiler 会话控制工具。迁移为 IUnityCliTool 自包含。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProfilerToolSupport.PreviousProfilerEditorRecording.HasValue)
                ProfilerToolSupport.PreviousProfilerEditorRecording = ProfilerToolSupport.GetProfilerEditorRecording();
            ProfilerToolSupport.SetProfilerEditorRecording(true);
            ProfilerToolSupport.SetProfilerSessionEnabled(true);
            ProfilerToolSupport.SetProfilerDeepProfiling(request.DeepProfiling);
            if (!string.IsNullOrEmpty(request.LogFile)) { Profiler.logFile = request.LogFile; Profiler.enableBinaryLog = true; }
            if (request.EnableCallstacks) Profiler.enableAllocationCallstacks = true;
            return new Result
            {
                Profiler = ProfilerToolSupport.BuildProfilerState(),
                PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.start" } }
            };
        }
    }

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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            ProfilerToolSupport.SetProfilerSessionEnabled(false);
            if (ProfilerToolSupport.PreviousProfilerEditorRecording.HasValue)
            {
                ProfilerToolSupport.SetProfilerEditorRecording(ProfilerToolSupport.PreviousProfilerEditorRecording.Value);
                ProfilerToolSupport.PreviousProfilerEditorRecording = null;
            }
            Profiler.enableBinaryLog = false;
            return new Result
            {
                Profiler = ProfilerToolSupport.BuildProfilerState(),
                PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.stop" } }
            };
        }
    }

    public class ProfilerPauseTool : IUnityCliTool<ProfilerPauseTool.Request, ProfilerPauseTool.Result>
    {
        public sealed class Request { public bool? Paused { get; set; } }
        public sealed class Result { public ProfilerState Profiler { get; set; } public bool Paused { get; set; } public List<ProfilerPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.pause", Summary = "Pause or resume Profiler data collection by toggling Unity Profiler.enabled.",
            Description = "Pause or resume Profiler data collection. Pass paused=true to pause, false to resume; omit to toggle the current state.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Pause profiler", Input = new Request { Paused = true } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var wasEnabled = ProfilerToolSupport.GetProfilerSessionEnabled();
            var paused = request.Paused ?? wasEnabled;
            ProfilerToolSupport.SetProfilerSessionEnabled(!paused);
            var isEnabled = ProfilerToolSupport.GetProfilerSessionEnabled();
            return new Result
            {
                Profiler = ProfilerToolSupport.BuildProfilerState(),
                Paused = paused,
                PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.pause", SideEffects = "profilerEnabledFrom=" + wasEnabled + ",profilerEnabledTo=" + isEnabled } }
            };
        }
    }

    public class ProfilerDeepTool : IUnityCliTool<ProfilerDeepTool.Request, ProfilerDeepTool.Result>
    {
        public sealed class Request { public bool? Enabled { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DeepProfiling { get; set; } public bool Previous { get; set; } public bool Changed { get; set; } public ProfilerState Profiler { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.deep", Summary = "Read or toggle Unity Deep Profiling at runtime (must be stopped to change).",
            Description = "Read or toggle Unity Deep Profiling. Pass enabled=true/false to change; omit to read. Toggling requires the Profiler to be stopped.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read deep profiling state", Input = new Request { } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var previous = ProfilerToolSupport.GetProfilerDeepProfiling();
            var hasEnabled = request.Enabled.HasValue;
            var sessionEnabled = ProfilerToolSupport.GetProfilerSessionEnabled();
            if (hasEnabled && sessionEnabled)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_DEEP_REQUIRES_STOP",
                    "Cannot change Deep Profiling while the Profiler is recording.",
                    "Run tools run profiler.stop first, then set deep profiling, then profiler.start again.");
            }
            var applied = previous;
            if (hasEnabled) { ProfilerToolSupport.SetProfilerDeepProfiling(request.Enabled.Value); applied = request.Enabled.Value; }
            return new Result
            {
                Command = "profiler.deep", DeepProfiling = applied, Previous = previous,
                Changed = hasEnabled && applied != previous, Profiler = ProfilerToolSupport.BuildProfilerState()
            };
        }
    }
}
#endif
