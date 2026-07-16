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
    public class ProfilerCounterTool : IUnityCliTool<ProfilerCounterTool.Request, ProfilerCounterTool.Result>
    {
        public sealed class Request { public string Category { get; set; } public string Counter { get; set; } }
        public sealed class Result { public ProfilerState Profiler { get; set; } public string Category { get; set; } public string Counter { get; set; } public List<ProfilerCounterEntry> Counters { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.counter", Summary = "Read current profiler counter values, optionally narrowed by category and counter name.",
            Description = "Read current profiler counter values and available counter metadata, optionally narrowed by category and counter name. Read-only.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read memory counters", Input = new Request { Category = "memory" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) => new System.Threading.Tasks.ValueTask<Result>(new Result
        {
            Profiler = ProfilerToolSupport.BuildProfilerState(),
            Category = request.Category, Counter = request.Counter,
            Counters = ProfilerToolSupport.BuildProfilerCounters(request.Category, request.Counter)
        });
    }
}
#endif
