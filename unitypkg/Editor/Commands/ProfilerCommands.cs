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

        private static bool GetProfilerSessionEnabled()
        {
            var property = ProfilerDriverEnabledProperty();
            if (property != null && property.CanRead)
            {
                return (bool)property.GetValue(null, null);
            }
            return UnityEngine.Profiling.Profiler.enabled;
        }

        private static void SetProfilerSessionEnabled(bool enabled)
        {
            var property = ProfilerDriverEnabledProperty();
            if (property != null && property.CanWrite)
            {
                property.SetValue(null, enabled, null);
                return;
            }
            UnityEngine.Profiling.Profiler.enabled = enabled;
        }

        private static PropertyInfo ProfilerDriverEnabledProperty()
        {
            var type = FindType("UnityEditorInternal.ProfilerDriver");
            return type == null ? null : type.GetProperty("enabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private const string ProfilerFrozenDecision = "ALP-165-Q4";
        private const string ProfilerCoverageScopeJson = "[\"session\",\"counter\",\"memory\",\"frame-debugger\",\"export\"]";
        private const string ProfilerHandlerFingerprint = "alp340-profiler-driver-session-v2";

        private static string ProfilerStateJson()
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("enabled", GetProfilerSessionEnabled())
                .Prop("enableBinaryLog", UnityEngine.Profiling.Profiler.enableBinaryLog)
                .Prop("isPaused", EditorApplication.isPaused)
                .Prop("supported", true)
                .Prop("architectureSensitive", true)
                .Prop("frozenDecision", ProfilerFrozenDecision)
                .PropRaw("coverageScope", ProfilerCoverageScopeJson)
                .Prop("handlerFingerprint", ProfilerHandlerFingerprint)
                .EndObject().ToString();
        }

        private static string ProfilerModulesJson()
        {
            return new UnityCliJsonWriter().BeginArray()
                .Value("CPU Usage")
                .Value("Rendering")
                .Value("Memory")
                .Value("Audio")
                .Value("Physics")
                .Value("UI")
                .Value("Global Illumination")
                .EndArray().ToString();
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
            var w = new UnityCliJsonWriter().BeginArray();
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
                w.BeginObject()
                    .Prop("category", item.Category)
                    .Prop("name", item.Name)
                    .Prop("value", item.Value)
                    .Prop("unit", item.Unit)
                    .EndObject();
            }
            return w.EndArray().ToString();
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
            return new UnityCliJsonWriter().BeginObject()
                .Prop("monoUsedBytes", (long)UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong())
                .Prop("monoHeapBytes", (long)UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong())
                .Prop("totalAllocatedBytes", (long)UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong())
                .Prop("totalReservedBytes", (long)UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong())
                .Prop("totalUnusedReservedBytes", (long)UnityEngine.Profiling.Profiler.GetTotalUnusedReservedMemoryLong())
                .EndObject().ToString();
        }

        private static string ProfilerFrameJson(string module)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("frameCount", (long)Time.frameCount)
                .Prop("renderedFrameCount", (long)Time.renderedFrameCount)
                .Prop("realtimeSinceStartup", Time.realtimeSinceStartup)
                .Prop("deltaTime", Time.deltaTime)
                .Prop("module", module)
                .PropRaw("modules", ProfilerModulesJson())
                .EndObject().ToString();
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

        /// <summary>
        /// Synchronous single-frame profiler capture. Used when durationMs == 0 and frames <= 1.
        /// No Thread.Sleep — the capture completes within the current frame.
        /// </summary>
        private static void CaptureRawProfilerSync(string outputPath)
        {
            var previousLogFile = UnityEngine.Profiling.Profiler.logFile;
            var previousEnabled = UnityEngine.Profiling.Profiler.enabled;
            var previousBinaryLog = UnityEngine.Profiling.Profiler.enableBinaryLog;
            try
            {
                UnityEngine.Profiling.Profiler.logFile = outputPath;
                UnityEngine.Profiling.Profiler.enableBinaryLog = true;
                UnityEngine.Profiling.Profiler.enabled = true;
                UnityEngine.Profiling.Profiler.BeginSample("UnityCliProfilerCapture");
                UnityEngine.Profiling.Profiler.EndSample();
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

        /// <summary>
        /// Deferred multi-frame profiler capture. Uses EditorApplication.update callbacks
        /// to spread profiler samples across real frames without blocking the main thread.
        /// Returns DeferredResponseMarker — the BridgeWorkItem is completed asynchronously
        /// when all frames have been captured or an error occurs.
        /// </summary>
        private static string CaptureRawProfilerDeferred(string outputPath, int durationMs, int totalFrames, string requestId, string effectType, DateTime startedAt, string format)
        {
            var item = currentWorkItem;
            var previousLogFile = UnityEngine.Profiling.Profiler.logFile;
            var previousEnabled = UnityEngine.Profiling.Profiler.enabled;
            var previousBinaryLog = UnityEngine.Profiling.Profiler.enableBinaryLog;

            UnityEngine.Profiling.Profiler.logFile = outputPath;
            UnityEngine.Profiling.Profiler.enableBinaryLog = true;
            UnityEngine.Profiling.Profiler.enabled = true;

            var capturedFrames = 0;
            // Deadline: use the requested duration as the capture budget, with a minimum
            // of 30 seconds to avoid premature timeouts on slow editor frames.
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(durationMs, 30000));
            EditorApplication.CallbackFunction waiter = null;
            waiter = () =>
            {
                try
                {
                    if (capturedFrames < totalFrames)
                    {
                        // Timeout check — if the deadline has passed, stop capturing.
                        if (DateTime.UtcNow > deadline)
                        {
                            EditorApplication.update -= waiter;
                            UnityEngine.Profiling.Profiler.enabled = previousEnabled;
                            UnityEngine.Profiling.Profiler.enableBinaryLog = previousBinaryLog;
                            UnityEngine.Profiling.Profiler.logFile = previousLogFile;
                            WriteLog("profiler_capture_deferred_timeout artifactPath=" + outputPath + " capturedFrames=" + capturedFrames.ToString(CultureInfo.InvariantCulture) + " totalFrames=" + totalFrames.ToString(CultureInfo.InvariantCulture));
                            item.Response = ErrorJson("E_PROFILER_CAPTURE_TIMEOUT", "Profiler capture did not complete all frames before the deadline.", "Reduce the requested frame count or duration, or check Unity Editor performance.");
                            UnregisterDeferredItem(item);
                            item.Completed.Set();
                            return;
                        }

                        UnityEngine.Profiling.Profiler.BeginSample("UnityCliProfilerCapture");
                        UnityEngine.Profiling.Profiler.EndSample();
                        capturedFrames++;
                        return;
                    }

                    // Capture complete — restore profiler state and build response.
                    EditorApplication.update -= waiter;
                    UnityEngine.Profiling.Profiler.enabled = previousEnabled;
                    UnityEngine.Profiling.Profiler.enableBinaryLog = previousBinaryLog;
                    UnityEngine.Profiling.Profiler.logFile = previousLogFile;

                    if (!File.Exists(outputPath))
                    {
                        File.WriteAllText(outputPath, "Profiler raw capture was requested; Unity did not emit binary data for this editor version." + Environment.NewLine, Encoding.UTF8);
                    }

                    var finishedAt = DateTime.UtcNow;
                    var elapsedMs = (long)(finishedAt - startedAt).TotalMilliseconds;
                    WriteLog(effectType.Replace('.', '_') + "_deferred artifactPath=" + outputPath + " durationMs=" + elapsedMs.ToString(CultureInfo.InvariantCulture));
                    item.Response = SuccessJson(new UnityCliJsonWriter().BeginObject()
                        .Prop("artifactPath", outputPath)
                        .Prop("profilerPath", outputPath)
                        .Prop("format", format)
                        .Prop("durationMs", elapsedMs)
                        .Prop("requestedDurationMs", (long)durationMs)
                        .Prop("frames", (long)totalFrames)
                        .PropRaw("profiler", ProfilerStateJson())
                        .PropArray("plannedEffects", arr =>
                        {
                            arr.BeginObject().Prop("type", effectType).Prop("path", outputPath).EndObject();
                        })
                        .Prop("logPath", LogPath())
                        .EndObject().ToString());
                    UnregisterDeferredItem(item);
                    item.Completed.Set();
                }
                catch (Exception exception)
                {
                    EditorApplication.update -= waiter;
                    UnityEngine.Profiling.Profiler.enabled = previousEnabled;
                    UnityEngine.Profiling.Profiler.enableBinaryLog = previousBinaryLog;
                    UnityEngine.Profiling.Profiler.logFile = previousLogFile;
                    item.Response = ErrorJson("E_PROFILER_CAPTURE_FAILED", "Profiler artifact write failed: " + exception.Message, "Check output path permissions and Unity Profiler availability, then retry.");
                    UnregisterDeferredItem(item);
                    item.Completed.Set();
                }
            };

            item.MarkDeferred();
            lock (DeferredItems) { DeferredItems.Add(item); }
            EditorApplication.update += waiter;
            WriteLog("profiler_capture_deferred_started artifactPath=" + outputPath + " requestedFrames=" + totalFrames.ToString(CultureInfo.InvariantCulture) + " requestedDurationMs=" + durationMs.ToString(CultureInfo.InvariantCulture));
            return DeferredResponseMarker;
        }

        private static string ProfilerArtifactJson(string requestId, DateTime startedAt, int durationMs, int frames)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("requestId", requestId)
                .Prop("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture))
                .Prop("durationMs", (long)durationMs)
                .Prop("frames", (long)frames)
                .PropRaw("profiler", ProfilerStateJson())
                .PropRaw("memory", ProfilerMemoryJson())
                .PropRaw("frame", ProfilerFrameJson("CPU Usage"))
                .PropRaw("counters", ProfilerCountersJson(null, null))
                .EndObject().ToString();
        }

        private static string ProfilerStart(BridgeRequest request)
        {
            var deepProfiling = request.BoolArg("deepProfiling", false);
            SetProfilerSessionEnabled(true);
            SetProfilerDeepProfiling(deepProfiling);
            WriteLog("profiler_started deepProfiling=" + Bool(deepProfiling));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("profiler", ProfilerStateJson())
                .PropArray("plannedEffects", arr =>
                {
                    arr.BeginObject().Prop("type", "profiler.start").EndObject();
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ProfilerStop(BridgeRequest request)
        {
            SetProfilerSessionEnabled(false);
            UnityEngine.Profiling.Profiler.enableBinaryLog = false;
            WriteLog("profiler_stopped");
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("profiler", ProfilerStateJson())
                .PropArray("plannedEffects", arr =>
                {
                    arr.BeginObject().Prop("type", "profiler.stop").EndObject();
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ProfilerPause(BridgeRequest request)
        {
            var wasEnabled = GetProfilerSessionEnabled();
            var paused = request.BoolArg("paused", wasEnabled);
            SetProfilerSessionEnabled(!paused);
            var isEnabled = GetProfilerSessionEnabled();
            WriteLog("profiler_pause paused=" + Bool(paused) + " profilerEnabled=" + Bool(isEnabled));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("profiler", ProfilerStateJson())
                .Prop("paused", paused)
                .PropArray("plannedEffects", arr =>
                {
                    arr.BeginObject().Prop("type", "profiler.pause").Prop("profilerEnabledFrom", wasEnabled).Prop("profilerEnabledTo", isEnabled).EndObject();
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ProfilerStatus(BridgeRequest request)
        {
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("profiler", ProfilerStateJson())
                .PropRaw("modules", ProfilerModulesJson())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ProfilerCounter(BridgeRequest request)
        {
            var category = request.StringArg("category");
            var counter = request.StringArg("counter");
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("profiler", ProfilerStateJson())
                .PropObject("query", q =>
                {
                    q.Prop("category", category).Prop("counter", counter);
                })
                .PropRaw("counters", ProfilerCountersJson(category, counter))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ProfilerMemory(BridgeRequest request)
        {
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("profiler", ProfilerStateJson())
                .PropRaw("memory", ProfilerMemoryJson())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ProfilerFrame(BridgeRequest request)
        {
            var module = request.StringArg("module") ?? "CPU Usage";
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("profiler", ProfilerStateJson())
                .PropRaw("frame", ProfilerFrameJson(module))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
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
                    // Multi-frame or timed captures require the main thread to yield between
                    // frames for realistic profiling data. Use deferred frame-based capture
                    // that spreads samples across EditorApplication.update callbacks.
                    if (durationMs > 0 || frames > 1)
                    {
                        return CaptureRawProfilerDeferred(outputPath, durationMs, Math.Max(1, frames), request.RequestId, effectType, startedAt, format);
                    }
                    // Single-frame capture: synchronous, no Thread.Sleep needed.
                    CaptureRawProfilerSync(outputPath);
                }
                else
                {
                    File.WriteAllText(outputPath, ProfilerArtifactJson(request.RequestId, startedAt, durationMs, frames), Encoding.UTF8);
                }
                var finishedAt = DateTime.UtcNow;
                var elapsedMs = (long)(finishedAt - startedAt).TotalMilliseconds;
                WriteLog(effectType.Replace('.', '_') + " artifactPath=" + outputPath + " durationMs=" + elapsedMs.ToString(CultureInfo.InvariantCulture));
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("artifactPath", outputPath)
                    .Prop("profilerPath", outputPath)
                    .Prop("format", format)
                    .Prop("durationMs", elapsedMs)
                    .Prop("requestedDurationMs", (long)durationMs)
                    .Prop("frames", (long)frames)
                    .PropRaw("profiler", ProfilerStateJson())
                    .PropArray("plannedEffects", arr =>
                    {
                        arr.BeginObject().Prop("type", effectType).Prop("path", outputPath).EndObject();
                    })
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
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
