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

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) => new System.Threading.Tasks.ValueTask<Result>(new Result
        {
            Command = "profiler.modules", Modules = ProfilerToolSupport.BuildProfilerModules()
        });
    }
}
#endif
