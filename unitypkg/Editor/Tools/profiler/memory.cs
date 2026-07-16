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
    public class ProfilerMemoryTool : IUnityCliTool<ProfilerMemoryTool.Request, ProfilerMemoryTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public ProfilerState Profiler { get; set; } public ProfilerMemory Memory { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.memory", Summary = "Query Unity memory usage from the live Editor.",
            Description = "Query Unity memory usage from the live Editor, including values exposed by UnityEngine.Profiling.Profiler.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Query memory", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) => new System.Threading.Tasks.ValueTask<Result>(new Result
        {
            Profiler = ProfilerToolSupport.BuildProfilerState(),
            Memory = ProfilerToolSupport.BuildProfilerMemory()
        });
    }
}
#endif
