#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterProfilerCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "profiler.start", "profiler", true, ProfilerStart);
            RegisterCommand(registry, "profiler.stop", "profiler", true, ProfilerStop);
            RegisterCommand(registry, "profiler.pause", "profiler", true, ProfilerPause);
            RegisterCommand(registry, "profiler.counter", "profiler", false, ProfilerCounter);
            RegisterCommand(registry, "profiler.memory", "profiler", false, ProfilerMemory);
            RegisterCommand(registry, "profiler.frame", "profiler", false, ProfilerFrame);
            RegisterCommand(registry, "profiler.capture", "profiler", true, ProfilerCapture);
            RegisterCommand(registry, "profiler.export", "profiler", true, ProfilerExport);
            RegisterCommand(registry, "profiler.status", "profiler", false, ProfilerStatus);
        }

        private static void SetProfilerDeepProfiling(bool enabled)
        {
            var property = typeof(UnityEngine.Profiling.Profiler).GetProperty("deepProfiling", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                property.SetValue(null, enabled, null);
            }
        }

        private const string ProfilerFrozenDecision = "ALP-165-Q4";
        private const string ProfilerCoverageScopeJson = "[\"session\",\"counter\",\"memory\",\"frame-debugger\",\"export\"]";

        private static string ProfilerStateJson()
        {
            return "{\"enabled\":" + Bool(UnityEngine.Profiling.Profiler.enabled)
                + ",\"enableBinaryLog\":" + Bool(UnityEngine.Profiling.Profiler.enableBinaryLog)
                + ",\"isPaused\":" + Bool(EditorApplication.isPaused)
                + ",\"supported\":true"
                + ",\"architectureSensitive\":true"
                + ",\"frozenDecision\":" + Quote(ProfilerFrozenDecision)
                + ",\"coverageScope\":" + ProfilerCoverageScopeJson
                + "}";
        }

        private static string ProfilerModulesJson()
        {
            return "[\"CPU Usage\",\"Rendering\",\"Memory\",\"Audio\",\"Physics\",\"UI\",\"Global Illumination\"]";
        }

        private static string ProfilerCountersJson(string category, string counter)
        {
            var samples = new List<ProfilerCounterSample>();
            AddProfilerCounter(samples, "memory", "MonoUsedSizeLong", UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(), "bytes");
            AddProfilerCounter(samples, "memory", "MonoHeapSizeLong", UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong(), "bytes");
            AddProfilerCounter(samples, "memory", "TotalAllocatedMemoryLong", UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(), "bytes");
            AddProfilerCounter(samples, "memory", "TotalReservedMemoryLong", UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong(), "bytes");
            AddProfilerCounter(samples, "memory", "TotalUnusedReservedMemoryLong", UnityEngine.Profiling.Profiler.GetTotalUnusedReservedMemoryLong(), "bytes");
            AddProfilerCounter(samples, "frame", "TimeFrameCount", Time.frameCount, "frames");
            AddProfilerCounter(samples, "frame", "RealtimeSinceStartup", Time.realtimeSinceStartup, "seconds");
            var categoryFilter = string.IsNullOrEmpty(category) ? null : category.Trim();
            var counterFilter = string.IsNullOrEmpty(counter) ? null : counter.Trim();
            var items = new List<string>();
            foreach (var item in samples)
            {
                if (!string.IsNullOrEmpty(categoryFilter) && !string.Equals(item.Category, categoryFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(counterFilter) && item.Name.IndexOf(counterFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                items.Add("{\"category\":" + Quote(item.Category) + ",\"name\":" + Quote(item.Name) + ",\"value\":" + Quote(item.Value) + ",\"unit\":" + Quote(item.Unit) + "}");
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static void AddProfilerCounter(List<ProfilerCounterSample> items, string category, string name, long value, string unit)
        {
            items.Add(new ProfilerCounterSample(category, name, value.ToString(CultureInfo.InvariantCulture), unit));
        }

        private static void AddProfilerCounter(List<ProfilerCounterSample> items, string category, string name, float value, string unit)
        {
            items.Add(new ProfilerCounterSample(category, name, Number(value), unit));
        }

        private static string ProfilerMemoryJson()
        {
            return "{"
                + "\"monoUsedBytes\":" + UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong().ToString(CultureInfo.InvariantCulture) + ","
                + "\"monoHeapBytes\":" + UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong().ToString(CultureInfo.InvariantCulture) + ","
                + "\"totalAllocatedBytes\":" + UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong().ToString(CultureInfo.InvariantCulture) + ","
                + "\"totalReservedBytes\":" + UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong().ToString(CultureInfo.InvariantCulture) + ","
                + "\"totalUnusedReservedBytes\":" + UnityEngine.Profiling.Profiler.GetTotalUnusedReservedMemoryLong().ToString(CultureInfo.InvariantCulture)
                + "}";
        }

        private static string ProfilerFrameJson(string module)
        {
            return "{\"frameCount\":" + Time.frameCount.ToString(CultureInfo.InvariantCulture) + ",\"renderedFrameCount\":" + Time.renderedFrameCount.ToString(CultureInfo.InvariantCulture) + ",\"realtimeSinceStartup\":" + Number(Time.realtimeSinceStartup) + ",\"deltaTime\":" + Number(Time.deltaTime) + ",\"module\":" + Quote(module) + ",\"modules\":" + ProfilerModulesJson() + "}";
        }

        private static string NormalizeProfilerFormat(string rawFormat, string outputPath)
        {
            var format = string.IsNullOrEmpty(rawFormat) ? Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant() : rawFormat.ToLowerInvariant();
            if (string.IsNullOrEmpty(format))
            {
                format = "json";
            }
            if (format == "data")
            {
                format = "raw";
            }
            if (format != "json" && format != "raw")
            {
                throw new BridgeCommandException("E_PROFILER_FORMAT_UNSUPPORTED", "Profiler capture format must be json or raw.", "Use --format json or --format raw.");
            }
            return format;
        }

        private static void CaptureRawProfiler(string outputPath, int durationMs, int frames)
        {
            var previousLogFile = UnityEngine.Profiling.Profiler.logFile;
            var previousEnabled = UnityEngine.Profiling.Profiler.enabled;
            var previousBinaryLog = UnityEngine.Profiling.Profiler.enableBinaryLog;
            try
            {
                UnityEngine.Profiling.Profiler.logFile = outputPath;
                UnityEngine.Profiling.Profiler.enableBinaryLog = true;
                UnityEngine.Profiling.Profiler.enabled = true;
                for (var i = 0; i < Math.Max(1, frames); i++)
                {
                    UnityEngine.Profiling.Profiler.BeginSample("UnityCliProfilerCapture");
                    UnityEngine.Profiling.Profiler.EndSample();
                    if (durationMs > 0)
                    {
                        Thread.Sleep(Math.Max(1, durationMs / Math.Max(1, frames)));
                    }
                }
            }
            finally
            {
                UnityEngine.Profiling.Profiler.enabled = previousEnabled;
                UnityEngine.Profiling.Profiler.enableBinaryLog = previousBinaryLog;
                UnityEngine.Profiling.Profiler.logFile = previousLogFile;
            }
            if (!File.Exists(outputPath))
            {
                File.WriteAllText(outputPath, "Profiler raw capture was requested; Unity did not emit binary data for this editor version." + Environment.NewLine, Encoding.UTF8);
            }
        }

        private static string ProfilerArtifactJson(string requestId, DateTime startedAt, int durationMs, int frames)
        {
            return "{"
                + "\"requestId\":" + Quote(requestId) + ","
                + "\"startedAt\":" + Quote(startedAt.ToString("O", CultureInfo.InvariantCulture)) + ","
                + "\"durationMs\":" + durationMs.ToString(CultureInfo.InvariantCulture) + ","
                + "\"frames\":" + frames.ToString(CultureInfo.InvariantCulture) + ","
                + "\"profiler\":" + ProfilerStateJson() + ","
                + "\"memory\":" + ProfilerMemoryJson() + ","
                + "\"frame\":" + ProfilerFrameJson("CPU Usage") + ","
                + "\"counters\":" + ProfilerCountersJson(null, null)
                + "}";
        }

        private static string ProfilerStart(BridgeRequest request)
        {
            var deepProfiling = request.BoolArg("deepProfiling", false);
            UnityEngine.Profiling.Profiler.enableBinaryLog = true;
            UnityEngine.Profiling.Profiler.enabled = true;
            SetProfilerDeepProfiling(deepProfiling);
            WriteLog("profiler_started deepProfiling=" + Bool(deepProfiling));
            return SuccessJson("{\"profiler\":" + ProfilerStateJson() + ",\"plannedEffects\":[{" + QuotePair("type", "profiler.start") + "}],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string ProfilerStop(BridgeRequest request)
        {
            UnityEngine.Profiling.Profiler.enabled = false;
            UnityEngine.Profiling.Profiler.enableBinaryLog = false;
            WriteLog("profiler_stopped");
            return SuccessJson("{\"profiler\":" + ProfilerStateJson() + ",\"plannedEffects\":[{" + QuotePair("type", "profiler.stop") + "}],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string ProfilerPause(BridgeRequest request)
        {
            var wasEnabled = UnityEngine.Profiling.Profiler.enabled;
            var paused = request.BoolArg("paused", wasEnabled);
            UnityEngine.Profiling.Profiler.enabled = !paused;
            if (!paused)
            {
                UnityEngine.Profiling.Profiler.enableBinaryLog = true;
            }
            WriteLog("profiler_pause paused=" + Bool(paused) + " profilerEnabled=" + Bool(UnityEngine.Profiling.Profiler.enabled));
            return SuccessJson("{\"profiler\":" + ProfilerStateJson() + ",\"paused\":" + Bool(paused) + ",\"plannedEffects\":[{\"type\":\"profiler.pause\",\"profilerEnabledFrom\":" + Bool(wasEnabled) + ",\"profilerEnabledTo\":" + Bool(UnityEngine.Profiling.Profiler.enabled) + "}],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string ProfilerStatus(BridgeRequest request)
        {
            return SuccessJson("{\"profiler\":" + ProfilerStateJson() + ",\"modules\":" + ProfilerModulesJson() + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string ProfilerCounter(BridgeRequest request)
        {
            var category = request.StringArg("category");
            var counter = request.StringArg("counter");
            return SuccessJson("{\"profiler\":" + ProfilerStateJson() + ",\"query\":{\"category\":" + Quote(category) + ",\"counter\":" + Quote(counter) + "},\"counters\":" + ProfilerCountersJson(category, counter) + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string ProfilerMemory(BridgeRequest request)
        {
            return SuccessJson("{\"profiler\":" + ProfilerStateJson() + ",\"memory\":" + ProfilerMemoryJson() + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string ProfilerFrame(BridgeRequest request)
        {
            var module = request.StringArg("module") ?? "CPU Usage";
            return SuccessJson("{\"profiler\":" + ProfilerStateJson() + ",\"frame\":" + ProfilerFrameJson(module) + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string ProfilerExport(BridgeRequest request)
        {
            return ProfilerWriteArtifact(request, "profiler.export", 0, 1);
        }

        private static string ProfilerCapture(BridgeRequest request)
        {
            var durationMs = Math.Max(0, request.IntArg("durationMs", 0));
            var frames = Math.Max(0, request.IntArg("frames", 1));
            return ProfilerWriteArtifact(request, "profiler.capture", durationMs, frames);
        }

        private static string ProfilerWriteArtifact(BridgeRequest request, string effectType, int durationMs, int frames)
        {
            var outputPath = ResolveOutputPath(request.StringArg("path"));
            var overwrite = request.BoolArg("overwrite", false);
            if (File.Exists(outputPath) && !overwrite)
            {
                return ErrorJson("E_OUTPUT_EXISTS", "Profiler artifact already exists: " + outputPath, "Pass --overwrite or choose a different output path.");
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                var startedAt = DateTime.UtcNow;
                var format = NormalizeProfilerFormat(request.StringArg("format"), outputPath);
                if (format == "raw")
                {
                    CaptureRawProfiler(outputPath, durationMs, frames);
                }
                else
                {
                    File.WriteAllText(outputPath, ProfilerArtifactJson(request.RequestId, startedAt, durationMs, frames), Encoding.UTF8);
                }
                var finishedAt = DateTime.UtcNow;
                var elapsedMs = (long)(finishedAt - startedAt).TotalMilliseconds;
                WriteLog(effectType.Replace('.', '_') + " artifactPath=" + outputPath + " durationMs=" + elapsedMs.ToString(CultureInfo.InvariantCulture));
                return SuccessJson("{\"artifactPath\":" + Quote(outputPath) + ",\"profilerPath\":" + Quote(outputPath) + ",\"format\":" + Quote(format) + ",\"durationMs\":" + elapsedMs.ToString(CultureInfo.InvariantCulture) + ",\"requestedDurationMs\":" + durationMs.ToString(CultureInfo.InvariantCulture) + ",\"frames\":" + frames.ToString(CultureInfo.InvariantCulture) + ",\"profiler\":" + ProfilerStateJson() + ",\"plannedEffects\":[{\"type\":" + Quote(effectType) + ",\"path\":" + Quote(outputPath) + "}],\"logPath\":" + Quote(LogPath()) + "}");
            }
            catch (BridgeCommandException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return ErrorJson("E_PROFILER_CAPTURE_FAILED", "Profiler artifact write failed: " + exception.Message, "Check output path permissions and Unity Profiler availability, then retry.");
            }
        }

        private sealed class ProfilerCounterSample
        {
            public ProfilerCounterSample(string category, string name, string value, string unit)
            {
                Category = category;
                Name = name;
                Value = value;
                Unit = unit;
            }

            public string Category { get; private set; }
            public string Name { get; private set; }
            public string Value { get; private set; }
            public string Unit { get; private set; }
        }
    }
}
#endif
