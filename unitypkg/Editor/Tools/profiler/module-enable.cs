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
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "profiler-area-state" }, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Enable CPU area", Input = new Request { Module = "CPU", Enabled = true } } }
        };

        // 仅在 Profiler API 可读回并匹配请求时返回成功。
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
            if (setAreaMethod == null || getAreaMethod == null)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_MODULE_UNSUPPORTED", "Profiler.SetAreaEnabled or GetAreaEnabled is not available in this Unity version.", "Use a Unity Editor version that exposes Profiler.SetAreaEnabled(ProfilerArea,bool) and GetAreaEnabled(ProfilerArea).");
            var wasEnabled = (bool)getAreaMethod.Invoke(null, new object[] { areaValue });
            ctx.CommitEffect();
            setAreaMethod.Invoke(null, new object[] { areaValue, request.Enabled });
            var isEnabled = (bool)getAreaMethod.Invoke(null, new object[] { areaValue });
            if (isEnabled != request.Enabled)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_MODULE_STATE_MISMATCH", "Profiler area did not reach the requested enabled state.", "Inspect the Profiler module state and retry.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "profiler.module-enable", Module = request.Module, Enabled = isEnabled, WasEnabled = wasEnabled, IsEnabled = isEnabled });
        }
    }
}
#endif
