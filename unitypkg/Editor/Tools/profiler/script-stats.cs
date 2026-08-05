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
    public class ProfilerScriptStatsTool : IUnityCliTool<ProfilerScriptStatsTool.Request, ProfilerScriptStatsTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public long GcAllocs { get; set; } public long MonoHeapSize { get; set; } public long MonoUsedSize { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.script-stats", Summary = "Script/GC memory sample statistics (gcAllocs, monoHeap, monoUsed).",
            Description = "Read Script/GC memory sample statistics from Unity Profiler counters. Read-only.",
            Group = "profiler",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read script stats", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) => new System.Threading.Tasks.ValueTask<Result>(new Result
        {
            GcAllocs = (long)Profiler.GetTotalAllocatedMemoryLong(),
            MonoHeapSize = (long)Profiler.GetMonoHeapSizeLong(),
            MonoUsedSize = (long)Profiler.GetMonoUsedSizeLong()
        });
    }
}
#endif
