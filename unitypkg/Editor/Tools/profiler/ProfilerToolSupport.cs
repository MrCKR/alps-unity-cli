#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace UnityCli
{
    /// <summary>
    /// profiler 域 IUnityCliTool 共享的基础设施 helper:Profiler 状态反射、帧计时、
    /// 内存计数器、模块列表、Frame Debugger 反射。仅供 profiler 域自包含工具复用;
    /// 不实现 IUnityCliTool,不参与工具发现。
    /// </summary>
    public static class ProfilerToolSupport
    {
        /// <summary>profiler.start 保存的旧 profileEditor 状态,供 profiler.stop 恢复。</summary>
        public static bool? PreviousProfilerEditorRecording;

        // ── ProfilerDriver 反射(UnityEditorInternal) ──

        /// <summary>
        /// 跨程序集按全名解析类型。profiler 域各工具反射 UnityEditorInternal/UnityEditor 内部类型时复用。
        /// </summary>
        public static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            try
            {
                var direct = Type.GetType(fullName, false);
                if (direct != null) return direct;
            }
            catch { }

            var separator = fullName.IndexOf(',');
            var typeName = separator < 0 ? fullName : fullName.Substring(0, separator).Trim();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = assembly.GetType(typeName, false); } catch { }
                if (type != null) return type;
            }
            return null;
        }

        /// <summary>
        /// 反射读取类型静态/实例 string 成员，属性与字段均可作为当前 API 成员。
        /// 供 profiler.rendering-stats 读取 UnityEditor.UnityStats 字段等场景复用。
        /// </summary>
        public static string StringMemberValue(Type type, object instance, string memberName)
        {
            var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                try { return prop.GetValue(instance, null) as string; } catch { return null; }
            }
            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try { return field.GetValue(instance) as string; } catch { return null; }
            }
            return null;
        }

        /// <summary>通过反射读取 ProfilerDriver.enabled(UnityEditorInternal)。</summary>
        public static bool GetProfilerSessionEnabled()
        {
            var property = ProfilerDriverEnabledProperty();
            if (property != null && property.CanRead)
            {
                try { return (bool)property.GetValue(null, null); } catch { }
            }
            return Profiler.enabled;
        }

        /// <summary>通过反射设置 ProfilerDriver.enabled。</summary>
        public static void SetProfilerSessionEnabled(bool enabled)
        {
            var property = ProfilerDriverEnabledProperty();
            if (property != null && property.CanWrite)
            {
                property.SetValue(null, enabled, null);
                return;
            }
            Profiler.enabled = enabled;
        }

        /// <summary>通过反射读取 ProfilerDriver.profileEditor。</summary>
        public static bool GetProfilerEditorRecording()
        {
            var type = FindType("UnityEditorInternal.ProfilerDriver");
            var property = type == null ? null : type.GetProperty("profileEditor", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanRead)
            {
                try { return (bool)property.GetValue(null, null); } catch { }
            }
            return false;
        }

        /// <summary>通过反射设置 ProfilerDriver.profileEditor。</summary>
        public static void SetProfilerEditorRecording(bool enabled)
        {
            var type = FindType("UnityEditorInternal.ProfilerDriver");
            var property = type == null ? null : type.GetProperty("profileEditor", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                property.SetValue(null, enabled, null);
            }
        }

        private static PropertyInfo ProfilerDriverEnabledProperty()
        {
            var type = FindType("UnityEditorInternal.ProfilerDriver");
            return type == null ? null : type.GetProperty("enabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }

        /// <summary>读取 ProfilerDriver 整数属性(lastFrameIndex 等)。</summary>
        public static int ProfilerDriverIntProperty(string name, int fallback)
        {
            var type = FindType("UnityEditorInternal.ProfilerDriver");
            var property = type == null ? null : type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanRead) return fallback;
            try { return (int)property.GetValue(null, null); } catch { return fallback; }
        }

        /// <summary>通过反射读取/设置 deepProfiling。</summary>
        public static bool GetProfilerDeepProfiling()
        {
            var property = typeof(Profiler).GetProperty("deepProfiling", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanRead)
            {
                try { return (bool)property.GetValue(null, null); } catch { return false; }
            }
            return false;
        }

        public static void SetProfilerDeepProfiling(bool enabled)
        {
            var property = typeof(Profiler).GetProperty("deepProfiling", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                property.SetValue(null, enabled, null);
            }
        }

        /// <summary>按 Unity 版本差异解析 ProfilerArea 枚举。</summary>
        public static Type FindProfilerAreaType()
        {
            return FindType("UnityEngine.Profiling.ProfilerArea") ?? FindType("UnityEditorInternal.ProfilerArea");
        }

        /// <summary>检测 FrameTimingManager 特性是否启用(Unity 2022.2+)。</summary>
        public static bool IsFrameTimingAvailable()
        {
#if UNITY_2022_2_OR_NEWER
            try { return FrameTimingManager.IsFeatureEnabled(); } catch { return false; }
#else
            return false;
#endif
        }

        // ── Profiler 状态 DTO ──

        /// <summary>构造 Profiler 状态 DTO。</summary>
        public static ProfilerState BuildProfilerState()
        {
            return new ProfilerState
            {
                Enabled = GetProfilerSessionEnabled(),
                EnableBinaryLog = Profiler.enableBinaryLog,
                LogFile = Profiler.logFile,
                EnableAllocationCallstacks = Profiler.enableAllocationCallstacks,
                ProfileEditor = GetProfilerEditorRecording(),
                IsPaused = EditorApplication.isPaused,
                Supported = Profiler.supported,
                MaxUsedMemoryBytes = (long)Profiler.maxUsedMemory,
                HandlerFingerprint = "profiler-driver-session-v2"
            };
        }

        /// <summary>Profiler 模块列表(固定)。</summary>
        public static List<string> BuildProfilerModules()
        {
            return new List<string> { "CPU Usage", "Rendering", "Memory", "Audio", "Physics", "UI", "Global Illumination" };
        }

        /// <summary>读取内存计数器 DTO。</summary>
        public static ProfilerMemory BuildProfilerMemory()
        {
            return new ProfilerMemory
            {
                MonoUsedBytes = (long)Profiler.GetMonoUsedSizeLong(),
                MonoHeapBytes = (long)Profiler.GetMonoHeapSizeLong(),
                TotalAllocatedBytes = (long)Profiler.GetTotalAllocatedMemoryLong(),
                TotalReservedBytes = (long)Profiler.GetTotalReservedMemoryLong(),
                TotalUnusedReservedBytes = (long)Profiler.GetTotalUnusedReservedMemoryLong()
            };
        }

        /// <summary>读取 profiler 计数器列表。</summary>
        public static List<ProfilerCounterEntry> BuildProfilerCounters(string categoryFilter, string counterFilter)
        {
            var samples = new List<ProfilerCounterEntry>
            {
                new ProfilerCounterEntry("memory", "MonoUsedSizeLong", (long)Profiler.GetMonoUsedSizeLong(), "bytes"),
                new ProfilerCounterEntry("memory", "MonoHeapSizeLong", (long)Profiler.GetMonoHeapSizeLong(), "bytes"),
                new ProfilerCounterEntry("memory", "TotalAllocatedMemoryLong", (long)Profiler.GetTotalAllocatedMemoryLong(), "bytes"),
                new ProfilerCounterEntry("memory", "TotalReservedMemoryLong", (long)Profiler.GetTotalReservedMemoryLong(), "bytes"),
                new ProfilerCounterEntry("memory", "TotalUnusedReservedMemoryLong", (long)Profiler.GetTotalUnusedReservedMemoryLong(), "bytes"),
                new ProfilerCounterEntry("frame", "TimeFrameCount", Time.frameCount, "frames"),
                new ProfilerCounterEntry("frame", "RealtimeSinceStartup", Time.realtimeSinceStartup, "seconds")
            };
            var filtered = new List<ProfilerCounterEntry>();
            foreach (var item in samples)
            {
                if (!string.IsNullOrEmpty(categoryFilter) && !string.Equals(item.Category, categoryFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(counterFilter) && item.Name.IndexOf(counterFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                filtered.Add(item);
            }
            return filtered;
        }

        /// <summary>采集 FrameTimingManager 当前帧计时 DTO。</summary>
        public static FrameTimingInfo BuildFrameTiming()
        {
#if UNITY_2022_2_OR_NEWER
            bool featureEnabled;
            try { featureEnabled = FrameTimingManager.IsFeatureEnabled(); } catch { featureEnabled = false; }
            if (!featureEnabled)
            {
                return new FrameTimingInfo { Available = false, Note = "Frame Timing Stats is not enabled. Enable in Project Settings > Player > Other Settings > Frame Timing Stats, or use a Development Build." };
            }
            FrameTimingManager.CaptureFrameTimings();
            var timings = new FrameTiming[1];
            uint count = FrameTimingManager.GetLatestTimings(1, timings);
            if (count == 0)
            {
                return new FrameTimingInfo { Available = false, Note = "No frame timing data available yet; capture needs a few frames." };
            }
            var t = timings[0];
            return new FrameTimingInfo
            {
                Available = true,
                CpuFrameTimeMs = (double)t.cpuFrameTime,
                CpuMainThreadFrameTimeMs = (double)t.cpuMainThreadFrameTime,
                CpuMainThreadPresentWaitTimeMs = (double)t.cpuMainThreadPresentWaitTime,
                CpuRenderThreadFrameTimeMs = (double)t.cpuRenderThreadFrameTime,
                GpuFrameTimeMs = (double)t.gpuFrameTime,
                FrameStartTimestamp = (long)t.frameStartTimestamp,
                FirstSubmitTimestamp = (long)t.firstSubmitTimestamp,
                CpuTimePresentCalled = (double)t.cpuTimePresentCalled,
                CpuTimeFrameComplete = (double)t.cpuTimeFrameComplete,
                HeightScale = (double)t.heightScale,
                WidthScale = (double)t.widthScale,
                SyncInterval = (long)t.syncInterval
            };
#else
            return new FrameTimingInfo { Available = false, Note = "FrameTimingManager requires Unity 2022.2 or newer." };
#endif
        }

        /// <summary>读取帧信息 DTO。</summary>
        public static ProfilerFrameInfo BuildProfilerFrame(string module)
        {
            return new ProfilerFrameInfo
            {
                FrameCount = (long)Time.frameCount,
                RenderedFrameCount = (long)Time.renderedFrameCount,
                RealtimeSinceStartup = Time.realtimeSinceStartup,
                DeltaTime = Time.deltaTime,
                Module = module,
                Modules = BuildProfilerModules(),
                FrameTiming = BuildFrameTiming()
            };
        }

        /// <summary>Profiler 采集格式规范化:json 或 raw。</summary>
        public static string NormalizeProfilerFormat(string rawFormat, string outputPath)
        {
            var format = string.IsNullOrEmpty(rawFormat) ? Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant() : rawFormat.ToLowerInvariant();
            if (string.IsNullOrEmpty(format)) format = "json";
            if (format == "data") format = "raw";
            if (format != "json" && format != "raw")
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_FORMAT_UNSUPPORTED", "Profiler capture format must be json or raw.", "Use --format json or --format raw.");
            }
            return format;
        }

        /// <summary>查找 MemoryProfiler 类型(包 com.unity.memoryprofiler)。找不到返回 null。</summary>
        public static Type MemoryProfilerType()
        {
            return FindType("Unity.Profiling.Memory.MemoryProfiler")
                ?? FindType("UnityEngine.Profiling.Memory.Experimental.MemoryProfiler");
        }

        /// <summary>
        /// 解析输出路径:相对路径基于工程根;空则抛 E_OUTPUT_PATH_REQUIRED。
        /// profiler.capture/export 共享,保证路径解析一致。
        /// </summary>
        public static string ResolveOutputPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_PATH_REQUIRED", "Profiler output requires a path.", "Pass --path <path>.");
            var normalized = rawPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(ProjectRoot(), normalized));
        }

        /// <summary>同步单帧 raw 采集。</summary>
        public static void CaptureRawProfilerSync(string outputPath)
        {
            var previousLogFile = Profiler.logFile;
            var previousEnabled = Profiler.enabled;
            var previousBinaryLog = Profiler.enableBinaryLog;
            try
            {
                Profiler.logFile = outputPath;
                Profiler.enableBinaryLog = true;
                Profiler.enabled = true;
                Profiler.BeginSample("UnityCliProfilerCapture");
                Profiler.EndSample();
            }
            finally
            {
                Profiler.enabled = previousEnabled;
                Profiler.enableBinaryLog = previousBinaryLog;
                Profiler.logFile = previousLogFile;
            }
            if (!File.Exists(outputPath))
            {
                File.WriteAllText(outputPath, "Profiler raw capture was requested; Unity did not emit binary data for this editor version." + Environment.NewLine, System.Text.Encoding.UTF8);
            }
        }

        /// <summary>写入采集元数据 sidecar。</summary>
        public static void WriteCaptureMetadataSidecar(string outputPath, string requestId, string captureTag, long startFrame, long endFrame, DateTime startedAt, DateTime finishedAt)
        {
            try
            {
                var sidecarPath = outputPath + ".metadata.json";
                var sidecar = new ProfilerCaptureMetadataSidecar
                {
                    RequestId = requestId,
                    CaptureTag = captureTag,
                    ArtifactPath = outputPath,
                    StartFrame = startFrame,
                    EndFrame = endFrame,
                    StartedAt = startedAt.ToString("O", CultureInfo.InvariantCulture),
                    FinishedAt = finishedAt.ToString("O", CultureInfo.InvariantCulture),
                    SidecarVersion = "unity-cli-profiler-capture-v1"
                };
                File.WriteAllText(sidecarPath, UnityCliSerializer.Serialize(sidecar), System.Text.Encoding.UTF8);
            }
            catch (Exception exception)
            {
                // sidecar 写入失败不阻塞主流程
                System.Diagnostics.Debug.WriteLine("profiler_capture_metadata_failed: " + exception.Message);
            }
        }

        /// <summary>Unity 工程根目录。</summary>
        public static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }
    }

    // ── profiler 域 DTO ──

    /// <summary>profiler.export / profiler.capture 的 JSON 摘要文件 DTO。</summary>
    public sealed class ProfilerArtifactFile
    {
        public string RequestId { get; set; }
        public string StartedAt { get; set; }
        public long DurationMs { get; set; }
        public long Frames { get; set; }
        public ProfilerState Profiler { get; set; }
        public ProfilerMemory Memory { get; set; }
    }

    /// <summary>captureTag sidecar 的稳定归档元数据。</summary>
    public sealed class ProfilerCaptureMetadataSidecar
    {
        public string RequestId { get; set; }
        public string CaptureTag { get; set; }
        public string ArtifactPath { get; set; }
        public long StartFrame { get; set; }
        public long EndFrame { get; set; }
        public string StartedAt { get; set; }
        public string FinishedAt { get; set; }
        public string SidecarVersion { get; set; }
    }

    /// <summary>profiler.hierarchy 错误详情 DTO，保持 command/frame/thread shape。</summary>
    public sealed class ProfilerHierarchyDetails
    {
        public string Command { get; set; }
        public long Frame { get; set; }
        public long Thread { get; set; }
        public bool ProfilerRecording { get; set; }
    }

    public sealed class ProfilerState
    {
        public bool Enabled { get; set; }
        public bool EnableBinaryLog { get; set; }
        public string LogFile { get; set; }
        public bool EnableAllocationCallstacks { get; set; }
        public bool ProfileEditor { get; set; }
        public bool IsPaused { get; set; }
        public bool Supported { get; set; }
        public long MaxUsedMemoryBytes { get; set; }
        public string HandlerFingerprint { get; set; }
    }

    public sealed class ProfilerMemory
    {
        public long MonoUsedBytes { get; set; }
        public long MonoHeapBytes { get; set; }
        public long TotalAllocatedBytes { get; set; }
        public long TotalReservedBytes { get; set; }
        public long TotalUnusedReservedBytes { get; set; }
    }

    public sealed class ProfilerCounterEntry
    {
        // 供严格 schema 推导器实例化 DTO。
        public ProfilerCounterEntry() { }

        // 构造一次 profiler counter 采样结果。
        public ProfilerCounterEntry(string category, string name, double value, string unit) { Category = category; Name = name; Value = value; Unit = unit; }
        public string Category { get; set; }
        public string Name { get; set; }
        public double Value { get; set; }
        public string Unit { get; set; }
    }

    public sealed class FrameTimingInfo
    {
        public bool Available { get; set; }
        public string Note { get; set; }
        public double CpuFrameTimeMs { get; set; }
        public double CpuMainThreadFrameTimeMs { get; set; }
        public double CpuMainThreadPresentWaitTimeMs { get; set; }
        public double CpuRenderThreadFrameTimeMs { get; set; }
        public double GpuFrameTimeMs { get; set; }
        public long FrameStartTimestamp { get; set; }
        public long FirstSubmitTimestamp { get; set; }
        public double CpuTimePresentCalled { get; set; }
        public double CpuTimeFrameComplete { get; set; }
        public double HeightScale { get; set; }
        public double WidthScale { get; set; }
        public long SyncInterval { get; set; }
    }

    public sealed class ProfilerFrameInfo
    {
        public long FrameCount { get; set; }
        public long RenderedFrameCount { get; set; }
        public double RealtimeSinceStartup { get; set; }
        public double DeltaTime { get; set; }
        public string Module { get; set; }
        public List<string> Modules { get; set; }
        public FrameTimingInfo FrameTiming { get; set; }
    }

    public sealed class ProfilerPlannedEffect
    {
        public string Type { get; set; }
        public string Path { get; set; }
        public string SideEffects { get; set; }
    }
}
#endif
