#if UNITY_EDITOR
using System.Collections.Generic;
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx) => new Result
        {
            Profiler = ProfilerToolSupport.BuildProfilerState(),
            Category = request.Category, Counter = request.Counter,
            Counters = ProfilerToolSupport.BuildProfilerCounters(request.Category, request.Counter)
        };
    }

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

        public Result Execute(Request request, UnityCliProjectToolContext ctx) => new Result
        {
            Profiler = ProfilerToolSupport.BuildProfilerState(),
            Memory = ProfilerToolSupport.BuildProfilerMemory()
        };
    }

    public class ProfilerFrameTool : IUnityCliTool<ProfilerFrameTool.Request, ProfilerFrameTool.Result>
    {
        public sealed class Request { public string Module { get; set; } = "CPU Usage"; }
        public sealed class Result { public ProfilerState Profiler { get; set; } public ProfilerFrameInfo Frame { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.frame", Summary = "Read current frame count, timing, module diagnostics and FrameTimingManager CPU/GPU fields.",
            Description = "Read current frame count, timing, selected profiler module diagnostics and FrameTimingManager CPU/GPU timing fields.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read frame timing", Input = new Request { } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx) => new Result
        {
            Profiler = ProfilerToolSupport.BuildProfilerState(),
            Frame = ProfilerToolSupport.BuildProfilerFrame(request.Module ?? "CPU Usage")
        };
    }

    public class ProfilerModulesTool : IUnityCliTool<ProfilerModulesTool.Request, ProfilerModulesTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public string Command { get; set; } public List<string> Modules { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.modules", Summary = "List available Profiler modules and their visibility state.",
            Description = "List available Profiler modules. Use the returned module names with profiler.module-enable.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "List modules", Input = new Request { } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx) => new Result
        {
            Command = "profiler.modules", Modules = ProfilerToolSupport.BuildProfilerModules()
        };
    }

    public class ProfilerAreasTool : IUnityCliTool<ProfilerAreasTool.Request, ProfilerAreasTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public string Command { get; set; } public long Count { get; set; } public string EnumType { get; set; } public List<string> Areas { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.areas", Summary = "List all Profiler area enum names.",
            Description = "List all Profiler area enum names accepted by profiler.module-enable. Read-only.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "List areas", Input = new Request { } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var areaType = ProfilerToolSupport.FindProfilerAreaType();
            if (areaType == null || !areaType.IsEnum)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_AREAS_UNAVAILABLE", "ProfilerArea enum not available.", "Run in a Unity Editor with profiling support.");
            return new Result { Command = "profiler.areas", Count = System.Enum.GetNames(areaType).Length, EnumType = areaType.FullName, Areas = new List<string>(System.Enum.GetNames(areaType)) };
        }
    }

    public class ProfilerScriptStatsTool : IUnityCliTool<ProfilerScriptStatsTool.Request, ProfilerScriptStatsTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public string Command { get; set; } public long GcAllocs { get; set; } public long MonoHeapSize { get; set; } public long MonoUsedSize { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.script-stats", Summary = "Script/GC memory sample statistics (gcAllocs, monoHeap, monoUsed).",
            Description = "Read Script/GC memory sample statistics from Unity Profiler counters. Read-only.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read script stats", Input = new Request { } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx) => new Result
        {
            Command = "profiler.script-stats",
            GcAllocs = (long)Profiler.GetTotalAllocatedMemoryLong(),
            MonoHeapSize = (long)Profiler.GetMonoHeapSizeLong(),
            MonoUsedSize = (long)Profiler.GetMonoUsedSizeLong()
        };
    }
}
#endif
