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

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var areaType = ProfilerToolSupport.FindProfilerAreaType();
            if (areaType == null || !areaType.IsEnum)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_AREAS_UNAVAILABLE", "ProfilerArea enum not available.", "Run in a Unity Editor with profiling support.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "profiler.areas", Count = System.Enum.GetNames(areaType).Length, EnumType = areaType.FullName, Areas = new List<string>(System.Enum.GetNames(areaType)) });
        }
    }
}
#endif
