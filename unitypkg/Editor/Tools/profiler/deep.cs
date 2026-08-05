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
    public class ProfilerDeepTool : IUnityCliTool<ProfilerDeepTool.Request, ProfilerDeepTool.Result>
    {
        public sealed class Request { public bool? Enabled { get; set; } }
        public sealed class Result { public bool DeepProfiling { get; set; } public bool Previous { get; set; } public bool Changed { get; set; } public ProfilerState Profiler { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.deep", Summary = "Read or toggle Unity Deep Profiling at runtime (must be stopped to change).",
            Description = "Read or toggle Unity Deep Profiling. Pass enabled=true/false to change Profiler session state; omit enabled for a read. Toggling requires the Profiler to be stopped.",
            Group = "profiler",DestructiveHint = false, SideEffects = new[] { "profiler-session-state" }, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read deep profiling state", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
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
            if (hasEnabled) { ctx.CommitEffect(); ProfilerToolSupport.SetProfilerDeepProfiling(request.Enabled.Value); applied = request.Enabled.Value; }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                DeepProfiling = applied, Previous = previous,
                Changed = hasEnabled && applied != previous, Profiler = ProfilerToolSupport.BuildProfilerState()
            });
        }
    }
}
#endif
