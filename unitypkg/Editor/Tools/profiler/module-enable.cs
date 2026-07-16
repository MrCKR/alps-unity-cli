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
    public class ProfilerModuleEnableTool : IUnityCliTool<ProfilerModuleEnableTool.Request, ProfilerModuleEnableTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Module { get; set; } public bool Enabled { get; set; } = true; }
        public sealed class Result { public string Command { get; set; } public string Module { get; set; } public bool Enabled { get; set; } public bool WasEnabled { get; set; } public bool IsEnabled { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.module-enable", Summary = "Enable or disable a Profiler module (area) by name via Profiler.SetAreaEnabled.",
            Description = "Enable or disable a Profiler module (area) by name via Profiler.SetAreaEnabled. Run profiler.areas to list valid area names.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Enable CPU area", Input = new Request { Module = "CPU", Enabled = true } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var areaType = ProfilerToolSupport.FindProfilerAreaType();
            if (areaType == null || !areaType.IsEnum)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_AREAS_UNAVAILABLE", "ProfilerArea enum not available in this Unity version.", "Run in a Unity Editor with profiling support.");
            if (string.IsNullOrEmpty(request.Module))
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_MODULE_REQUIRED", "profiler.module-enable requires module.", "Pass a Profiler area name; run profiler.areas to list valid names.");
            object areaValue;
            try { areaValue = Enum.Parse(areaType, request.Module, true); }
            catch { throw new UnityCliBridge.BridgeCommandException("E_PROFILER_MODULE_UNKNOWN", "Unknown Profiler area: " + request.Module, "Run profiler.areas to see valid area names."); }
            var setAreaMethod = typeof(Profiler).GetMethod("SetAreaEnabled", BindingFlags.Static | BindingFlags.Public, null, new[] { areaType, typeof(bool) }, null);
            var getAreaMethod = typeof(Profiler).GetMethod("GetAreaEnabled", BindingFlags.Static | BindingFlags.Public, null, new[] { areaType }, null);
            if (setAreaMethod == null)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_MODULE_UNSUPPORTED", "Profiler.SetAreaEnabled is not available in this Unity version.", "Use a Unity Editor version that exposes Profiler.SetAreaEnabled(ProfilerArea,bool).");
            var wasEnabled = getAreaMethod != null && (bool)getAreaMethod.Invoke(null, new object[] { areaValue });
            setAreaMethod.Invoke(null, new object[] { areaValue, request.Enabled });
            var isEnabled = getAreaMethod != null && (bool)getAreaMethod.Invoke(null, new object[] { areaValue });
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "profiler.module-enable", Module = request.Module, Enabled = request.Enabled, WasEnabled = wasEnabled, IsEnabled = isEnabled });
        }
    }
}
#endif
