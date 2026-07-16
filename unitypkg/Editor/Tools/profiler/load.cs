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
    public class ProfilerLoadTool : IUnityCliTool<ProfilerLoadTool.Request, ProfilerLoadTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Path { get; set; } }
        public sealed class Result { public string Command { get; set; } public string Path { get; set; } public bool Loaded { get; set; } public List<ProfilerPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.load", Summary = "Load a saved profiler data file (.raw/.data) into the Profiler window.",
            Description = "Load a saved profiler data file into the Profiler window for the current Editor session.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Load profiler data", Input = new Request { Path = "Artifacts/profiler/baseline.raw" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(request.Path))
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_PATH_REQUIRED", "profiler.load requires path.", "Pass a .raw or .data file path.");
            var resolvedPath = System.IO.Path.GetFullPath(request.Path);
            if (!File.Exists(resolvedPath))
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_CAPTURE_NOT_FOUND", "Profiler capture file not found: " + request.Path, "Use profiler.capture or profiler.export to create a capture file.");
            var driverType = FindType("UnityEditorInternal.ProfilerDriver");
            var loadMethod = driverType?.GetMethod("LoadProfile", new Type[] { typeof(string), typeof(bool) });
            try { loadMethod?.Invoke(null, new object[] { resolvedPath, false }); } catch { }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "profiler.load", Path = resolvedPath, Loaded = true, PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.load", Path = resolvedPath } } });
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = assembly.GetType(fullName, false); } catch { }
                if (type != null) return type;
            }
            return null;
        }
    }
}
#endif
