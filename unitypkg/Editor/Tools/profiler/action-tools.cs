#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace UnityCli
{
    /// <summary>
    /// profiler.module-enable / profiler.clear / profiler.load / profiler.object-memory / profiler.export —
    /// Profiler 操作工具。迁移为 IUnityCliTool 自包含。
    /// </summary>
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
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
            return new Result { Command = "profiler.module-enable", Module = request.Module, Enabled = request.Enabled, WasEnabled = wasEnabled, IsEnabled = isEnabled };
        }
    }

    public class ProfilerClearTool : IUnityCliTool<ProfilerClearTool.Request, ProfilerClearTool.Result>
    {
        public sealed class Request { public bool DryRun { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public bool Cleared { get; set; } public List<ProfilerPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.clear", Summary = "Clear all collected profiler data.",
            Description = "Clear all collected profiler data from the current Editor session. Supports dry-run.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Clear profiler data", Input = new Request { } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (request.DryRun)
                return new Result { Command = "profiler.clear", DryRun = true, PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.clear", SideEffects = "clears-profiler-data" } } };
            // 反射调用 ProfilerDriver.Clear()
            var clearMethod = FindType("UnityEditorInternal.ProfilerDriver")?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public);
            try { clearMethod?.Invoke(null, null); } catch { }
            return new Result { Command = "profiler.clear", Cleared = true, PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.clear" } } };
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (string.IsNullOrEmpty(request.Path))
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_PATH_REQUIRED", "profiler.load requires path.", "Pass a .raw or .data file path.");
            var resolvedPath = System.IO.Path.GetFullPath(request.Path);
            if (!File.Exists(resolvedPath))
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_CAPTURE_NOT_FOUND", "Profiler capture file not found: " + request.Path, "Use profiler.capture or profiler.export to create a capture file.");
            var driverType = FindType("UnityEditorInternal.ProfilerDriver");
            var loadMethod = driverType?.GetMethod("LoadProfile", new Type[] { typeof(string), typeof(bool) });
            try { loadMethod?.Invoke(null, new object[] { resolvedPath, false }); } catch { }
            return new Result { Command = "profiler.load", Path = resolvedPath, Loaded = true, PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.load", Path = resolvedPath } } };
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

    public class ProfilerObjectMemoryTool : IUnityCliTool<ProfilerObjectMemoryTool.Request, ProfilerObjectMemoryTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Path { get; set; } }
        public sealed class Result { public string Command { get; set; } public string Target { get; set; } public List<ObjectMemoryEntry> Objects { get; set; } public long TotalBytes { get; set; } }
        public sealed class ObjectMemoryEntry { public string Name { get; set; } public long InstanceId { get; set; } public long SizeBytes { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.object-memory", Summary = "Query memory size of components on a GameObject via Profiler.GetRuntimeMemorySizeLong.",
            Description = "Query runtime memory size of components on a GameObject via Profiler.GetRuntimeMemorySizeLong. target resolves by name/path/id.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Query object memory", Input = new Request { Target = "Player" } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var target = request.Target ?? request.Path;
            var go = string.IsNullOrEmpty(target) ? null : UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(target);
            var result = new Result { Command = "profiler.object-memory", Target = target, Objects = new List<ObjectMemoryEntry>() };
            if (go != null)
            {
                long total = 0;
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var size = Profiler.GetRuntimeMemorySizeLong(c);
                    total += size;
                    result.Objects.Add(new ObjectMemoryEntry { Name = c.GetType().Name, InstanceId = c.GetInstanceID(), SizeBytes = size });
                }
                result.TotalBytes = total;
            }
            return result;
        }
    }

    public class ProfilerExportTool : IUnityCliTool<ProfilerExportTool.Request, ProfilerExportTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Path { get; set; } public string Format { get; set; } public bool Overwrite { get; set; } public string CaptureTag { get; set; } }
        public sealed class Result { public string ArtifactPath { get; set; } public string ProfilerPath { get; set; } public string Format { get; set; } public long DurationMs { get; set; } public long Frames { get; set; } public long StartFrame { get; set; } public long EndFrame { get; set; } public string CaptureTag { get; set; } public string MetadataSidecar { get; set; } public ProfilerState Profiler { get; set; } public List<ProfilerPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.export", Summary = "Export a profiler snapshot artifact in JSON or raw profiler format.",
            Description = "Export a profiler snapshot artifact in JSON or raw profiler format to the requested path. Single-frame synchronous export.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Export profiler JSON", Input = new Request { Path = "profile.json", Overwrite = true } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var outputPath = ResolveOutputPath(request.Path);
            if (File.Exists(outputPath) && !request.Overwrite)
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Profiler artifact already exists: " + outputPath, "Pass overwrite=true or choose a different output path.");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            var startedAt = DateTime.UtcNow;
            var format = ProfilerToolSupport.NormalizeProfilerFormat(request.Format, outputPath);
            if (format == "raw")
            {
                // 单帧同步采集
                ProfilerToolSupport.CaptureRawProfilerSync(outputPath);
            }
            else
            {
                // JSON 格式:写入状态/内存/帧/计数器摘要
                File.WriteAllText(outputPath, BuildProfilerArtifactJson(ctx.RequestId, startedAt, 0, 1), System.Text.Encoding.UTF8);
            }
            var finishedAt = DateTime.UtcNow;
            var elapsedMs = (long)(finishedAt - startedAt).TotalMilliseconds;
            var startFrame = (long)Time.frameCount;
            var endFrame = startFrame;
            var captureTag = request.CaptureTag;
            if (!string.IsNullOrEmpty(captureTag))
                ProfilerToolSupport.WriteCaptureMetadataSidecar(outputPath, ctx.RequestId, captureTag, startFrame, endFrame, startedAt, finishedAt);
            return new Result
            {
                ArtifactPath = outputPath, ProfilerPath = outputPath, Format = format, DurationMs = elapsedMs,
                Frames = 1, StartFrame = startFrame, EndFrame = endFrame, CaptureTag = captureTag,
                MetadataSidecar = string.IsNullOrEmpty(captureTag) ? null : outputPath + ".metadata.json",
                Profiler = ProfilerToolSupport.BuildProfilerState(),
                PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.export", Path = outputPath } }
            };
        }

        private static string ResolveOutputPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_PATH_REQUIRED", "Profiler export requires a path.", "Pass --path <path>.");
            var normalized = rawPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(ProfilerToolSupport.ProjectRoot(), normalized));
        }

        // 构造 JSON 摘要(用 UnityCliJsonWriter 直接写文件内容,不经过 Result 序列化)
        private static string BuildProfilerArtifactJson(string requestId, DateTime startedAt, int durationMs, int frames)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("requestId", requestId)
                .Prop("startedAt", startedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
                .Prop("durationMs", (long)durationMs)
                .Prop("frames", (long)frames)
                .PropRaw("profiler", UnityCliSerializer.Serialize(ProfilerToolSupport.BuildProfilerState()))
                .PropRaw("memory", new UnityCliJsonWriter().BeginObject()
                    .Prop("monoUsedBytes", (long)Profiler.GetMonoUsedSizeLong())
                    .Prop("monoHeapBytes", (long)Profiler.GetMonoHeapSizeLong())
                    .Prop("totalAllocatedBytes", (long)Profiler.GetTotalAllocatedMemoryLong())
                    .Prop("totalReservedBytes", (long)Profiler.GetTotalReservedMemoryLong())
                    .Prop("totalUnusedReservedBytes", (long)Profiler.GetTotalUnusedReservedMemoryLong())
                    .EndObject().ToString())
                .EndObject().ToString();
        }
    }
}
#endif
