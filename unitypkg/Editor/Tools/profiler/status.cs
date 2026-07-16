#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    public class ProfilerStatusTool : IUnityCliTool<ProfilerStatusTool.Request, ProfilerStatusTool.Result>
    {
        public sealed class Request { }
        public sealed class Result
        {
            public ProfilerState Profiler { get; set; }
            public List<string> Modules { get; set; }
            public string Platform { get; set; }
            public bool FrameTimingAvailable { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.status", Summary = "Check whether the Unity profiler is currently active, with max-used memory and platform support.",
            Description = "Read the current Unity Profiler state, available modules, max-used memory and platform support.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Check profiler status", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) => new System.Threading.Tasks.ValueTask<Result>(new Result
        {
            Profiler = ProfilerToolSupport.BuildProfilerState(),
            Modules = ProfilerToolSupport.BuildProfilerModules(),
            Platform = Application.platform.ToString(),
            FrameTimingAvailable = ProfilerToolSupport.IsFrameTimingAvailable()
        });
    }
}
#endif
