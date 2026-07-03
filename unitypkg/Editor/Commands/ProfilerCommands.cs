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
        // profiler.* 部分已迁移为 IUnityCliTool(unitypkg/Editor/Tools/profiler/*.cs):
        // status/counter/memory/frame/modules/areas/script-stats/start/stop/pause/deep/
        // module-enable/clear/load/object-memory/export — 不再用 RegisterCommand。
        // 以下保留 deferred/complex-reflection 工具的 RegisterCommand:
        private static void RegisterProfilerCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "profiler.capture", "profiler", true, ProfilerCapture);
            RegisterCommand(registry, "profiler.hierarchy", "profiler", false, ProfilerHierarchy);
            RegisterCommand(registry, "profiler.rendering-stats", "profiler", false, ProfilerRenderingStats);
            RegisterCommand(registry, "profiler.memory-snapshot.take", "profiler", true, ProfilerMemorySnapshotTake);
            RegisterCommand(registry, "profiler.memory-snapshot.list", "profiler", false, ProfilerMemorySnapshotList);
            RegisterCommand(registry, "profiler.memory-snapshot.compare", "profiler", false, ProfilerMemorySnapshotCompare);
            RegisterCommand(registry, "profiler.frame-debugger.enable", "profiler", false, ProfilerFrameDebuggerEnable);
            RegisterCommand(registry, "profiler.frame-debugger.disable", "profiler", false, ProfilerFrameDebuggerDisable);
            RegisterCommand(registry, "profiler.frame-debugger.events", "profiler", false, ProfilerFrameDebuggerEvents);
        }

        private static void SetProfilerDeepProfiling(bool enabled)
        {
            var property = typeof(UnityEngine.Profiling.Profiler).GetProperty("deepProfiling", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                property.SetValue(null, enabled, null);
            }
        }

        private static bool? previousProfilerEditorRecording;

        private static bool GetProfilerSessionEnabled()
        {
            var property = ProfilerDriverEnabledProperty();
            if (property != null && property.CanRead)
            {
                return (bool)property.GetValue(null, null);
            }
            return UnityEngine.Profiling.Profiler.enabled;
        }

        private static bool GetProfilerEditorRecording()
        {
            var type = FindType("UnityEditorInternal.ProfilerDriver");
            var property = type == null ? null : type.GetProperty("profileEditor", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanRead)
            {
                try { return (bool)property.GetValue(null, null); } catch { }
            }
            return false;
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

        // Editor Profiler 历史帧需要同时打开 Profile Editor，否则 hierarchy 读不到 frame data。
        private static void SetProfilerEditorRecording(bool enabled)
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

        private static int ProfilerDriverIntProperty(string name, int fallback)
        {
            var type = FindType("UnityEditorInternal.ProfilerDriver");
            var property = type == null ? null : type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanRead)
            {
                return fallback;
            }
            try
            {
                return (int)property.GetValue(null, null);
            }
            catch
            {
                return fallback;
            }
        }

        private const string ProfilerHandlerFingerprint = "profiler-driver-session-v2";

        private static string ProfilerStateJson()
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("enabled", GetProfilerSessionEnabled())
                .Prop("enableBinaryLog", UnityEngine.Profiling.Profiler.enableBinaryLog)
                .Prop("logFile", UnityEngine.Profiling.Profiler.logFile)
                .Prop("enableAllocationCallstacks", UnityEngine.Profiling.Profiler.enableAllocationCallstacks)
                .Prop("profileEditor", GetProfilerEditorRecording())
                .Prop("isPaused", EditorApplication.isPaused)
                .Prop("supported", UnityEngine.Profiling.Profiler.supported)
                .Prop("maxUsedMemoryBytes", (long)UnityEngine.Profiling.Profiler.maxUsedMemory)
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
                .PropRaw("frameTiming", ProfilerFrameTimingJson())
                .EndObject().ToString();
        }

        /// <summary>
        /// 采集 FrameTimingManager 当前帧的 12 个 CPU/GPU 计时字段。对齐 CoplayDev get_frame_timing。
        /// 需 Project Settings > Player > Frame Timing Stats 开启（Development Build 总是开启）。
        /// </summary>
        private static string ProfilerFrameTimingJson()
        {
            var w = new UnityCliJsonWriter().BeginObject();
#if UNITY_2022_2_OR_NEWER
            bool featureEnabled;
            try { featureEnabled = FrameTimingManager.IsFeatureEnabled(); }
            catch { featureEnabled = false; }
            if (!featureEnabled)
            {
                w.Prop("available", false);
                w.Prop("note", "Frame Timing Stats is not enabled. Enable in Project Settings > Player > Other Settings > Frame Timing Stats, or use a Development Build.");
                return w.EndObject().ToString();
            }
            FrameTimingManager.CaptureFrameTimings();
            var timings = new FrameTiming[1];
            uint count = FrameTimingManager.GetLatestTimings(1, timings);
            if (count == 0)
            {
                w.Prop("available", false);
                w.Prop("note", "No frame timing data available yet; capture needs a few frames.");
                return w.EndObject().ToString();
            }
            var t = timings[0];
            w.Prop("available", true);
            w.Prop("cpuFrameTimeMs", (double)t.cpuFrameTime);
            w.Prop("cpuMainThreadFrameTimeMs", (double)t.cpuMainThreadFrameTime);
            w.Prop("cpuMainThreadPresentWaitTimeMs", (double)t.cpuMainThreadPresentWaitTime);
            w.Prop("cpuRenderThreadFrameTimeMs", (double)t.cpuRenderThreadFrameTime);
            w.Prop("gpuFrameTimeMs", (double)t.gpuFrameTime);
            w.Prop("frameStartTimestamp", (long)t.frameStartTimestamp);
            w.Prop("firstSubmitTimestamp", (long)t.firstSubmitTimestamp);
            w.Prop("cpuTimePresentCalled", (double)t.cpuTimePresentCalled);
            w.Prop("cpuTimeFrameComplete", (double)t.cpuTimeFrameComplete);
            w.Prop("heightScale", (double)t.heightScale);
            w.Prop("widthScale", (double)t.widthScale);
            w.Prop("syncInterval", (long)t.syncInterval);
#else
            w.Prop("available", false);
            w.Prop("note", "FrameTimingManager requires Unity 2022.2 or newer.");
#endif
            return w.EndObject().ToString();
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
        private static string CaptureRawProfilerDeferred(string outputPath, int durationMs, int totalFrames, string requestId, string effectType, DateTime startedAt, string format, string captureTag, long startFrame)
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
                    var endFrame = (long)Time.frameCount;
                    if (!string.IsNullOrEmpty(captureTag))
                    {
                        WriteCaptureMetadataSidecar(outputPath, requestId, captureTag, startFrame, endFrame, startedAt, finishedAt);
                    }
                    WriteLog(effectType.Replace('.', '_') + "_deferred artifactPath=" + outputPath + " durationMs=" + elapsedMs.ToString(CultureInfo.InvariantCulture));
                    item.Response = SuccessJson(new UnityCliJsonWriter().BeginObject()
                        .Prop("artifactPath", outputPath)
                        .Prop("profilerPath", outputPath)
                        .Prop("format", format)
                        .Prop("durationMs", elapsedMs)
                        .Prop("requestedDurationMs", (long)durationMs)
                        .Prop("frames", (long)totalFrames)
                        .Prop("startFrame", startFrame)
                        .Prop("endFrame", endFrame)
                        .Prop("captureTag", captureTag)
                        .Prop("metadataSidecar", string.IsNullOrEmpty(captureTag) ? null : outputPath + ".metadata.json")
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

        private static string ProfilerStart_Removed(BridgeRequest request)
        {
            var deepProfiling = request.BoolArg("deepProfiling", false);
            var logFile = request.StringArg("logFile");
            var enableCallstacks = request.BoolArg("enableCallstacks", false);
            if (!previousProfilerEditorRecording.HasValue)
            {
                previousProfilerEditorRecording = GetProfilerEditorRecording();
            }
            SetProfilerEditorRecording(true);
            SetProfilerSessionEnabled(true);
            SetProfilerDeepProfiling(deepProfiling);
            // 可选：启动时指定二进制日志文件，等价 Profiler.logFile + enableBinaryLog。
            if (!string.IsNullOrEmpty(logFile))
            {
                UnityEngine.Profiling.Profiler.logFile = logFile;
                UnityEngine.Profiling.Profiler.enableBinaryLog = true;
            }
            // 可选：启用分配调用栈采集，便于内存分析。
            if (enableCallstacks)
            {
                UnityEngine.Profiling.Profiler.enableAllocationCallstacks = true;
            }
            WriteLog("profiler_started deepProfiling=" + Bool(deepProfiling) + " logFile=" + (logFile ?? "") + " callstacks=" + Bool(enableCallstacks));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("profiler", ProfilerStateJson())
                .PropArray("plannedEffects", arr =>
                {
                    arr.BeginObject().Prop("type", "profiler.start").EndObject();
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ProfilerStop_Removed(BridgeRequest request)
        {
            SetProfilerSessionEnabled(false);
            if (previousProfilerEditorRecording.HasValue)
            {
                SetProfilerEditorRecording(previousProfilerEditorRecording.Value);
                previousProfilerEditorRecording = null;
            }
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

        private static string ProfilerPause_Removed(BridgeRequest request)
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

        private static string ProfilerStatus_Removed(BridgeRequest request)
        {
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("profiler", ProfilerStateJson())
                .PropRaw("modules", ProfilerModulesJson())
                .Prop("platform", Application.platform.ToString())
                .Prop("frameTimingAvailable", IsFrameTimingAvailable())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        /// <summary>检测 FrameTimingManager 特性是否启用（Unity 2022.2+）。</summary>
        private static bool IsFrameTimingAvailable()
        {
#if UNITY_2022_2_OR_NEWER
            try { return FrameTimingManager.IsFeatureEnabled(); }
            catch { return false; }
#else
            return false;
#endif
        }

        private static string ProfilerModules_Removed(BridgeRequest request)
        {
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "profiler.modules")
                .PropRaw("modules", ProfilerModulesJson())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ProfilerModuleEnable_Removed(BridgeRequest request)
        {
            var moduleName = request.StringArg("module") ?? request.StringArg("name");
            var enabled = request.BoolArg("enabled", true);
            var areaType = FindProfilerAreaType();
            if (areaType == null || !areaType.IsEnum)
            {
                return ErrorJson("E_PROFILER_AREAS_UNAVAILABLE", "ProfilerArea enum not available in this Unity version.", "Run in a Unity Editor with profiling support; Unity 2022+ normally exposes UnityEngine.Profiling.ProfilerArea.");
            }
            if (string.IsNullOrEmpty(moduleName))
            {
                throw new BridgeCommandException("E_PROFILER_MODULE_REQUIRED", "profiler.module-enable requires --module.", "Pass a Profiler area name; run profiler.areas to list valid names.");
            }
            object areaValue;
            try { areaValue = System.Enum.Parse(areaType, moduleName, true); }
            catch
            {
                return ErrorJson("E_PROFILER_MODULE_UNKNOWN", "Unknown Profiler area: " + moduleName, "Run profiler.areas to see valid area names.");
            }
            // 反射调用 Profiler.SetAreaEnabled(ProfilerArea, bool) 切换模块可见性，避免 asmdef 对版本差异产生编译期耦合。
            var setAreaMethod = typeof(UnityEngine.Profiling.Profiler).GetMethod("SetAreaEnabled", BindingFlags.Static | BindingFlags.Public, null, new[] { areaType, typeof(bool) }, null);
            var getAreaMethod = typeof(UnityEngine.Profiling.Profiler).GetMethod("GetAreaEnabled", BindingFlags.Static | BindingFlags.Public, null, new[] { areaType }, null);
            if (setAreaMethod == null)
            {
                return ErrorJson("E_PROFILER_MODULE_UNSUPPORTED", "Profiler.SetAreaEnabled is not available in this Unity version.", "Use a Unity Editor version that exposes Profiler.SetAreaEnabled(ProfilerArea,bool).");
            }
            var wasEnabled = getAreaMethod != null && (bool)getAreaMethod.Invoke(null, new object[] { areaValue });
            try { setAreaMethod.Invoke(null, new object[] { areaValue, enabled }); }
            catch (Exception ex) { return ErrorJson("E_PROFILER_MODULE_FAILED", "Failed to set Profiler area: " + ex.Message, "Check Unity Console for details."); }
            var isEnabled = getAreaMethod != null && (bool)getAreaMethod.Invoke(null, new object[] { areaValue });
            WriteLog("profiler_module_enable module=" + moduleName + " enabled=" + Bool(enabled));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "profiler.module-enable")
                .Prop("module", moduleName).Prop("enabled", enabled).Prop("wasEnabled", wasEnabled).Prop("isEnabled", isEnabled)
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        private static string ProfilerClear_Removed(BridgeRequest request)
        {
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "profiler.clear").Prop("dryRun", true)
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "profiler.clear").Prop("sideEffects", "clears-profiler-data").EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            var driverType = FindType("UnityEditorInternal.ProfilerDriver");
            var clearMethod = driverType?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public);
            try { clearMethod?.Invoke(null, null); } catch { /* 旧版 API 可能不存在 */ }
            return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "profiler.clear").Prop("cleared", true)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "profiler.clear").EndObject())
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        private static string ProfilerLoad_Removed(BridgeRequest request)
        {
            var path = request.StringArg("path");
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_PROFILER_PATH_REQUIRED", "profiler.load requires --path.", "Pass a .raw or .data file path.");
            }
            var resolvedPath = Path.GetFullPath(path);
            if (!File.Exists(resolvedPath))
            {
                return ErrorJsonWithDetails("E_PROFILER_CAPTURE_NOT_FOUND",
                    "Profiler capture file not found: " + path,
                    new UnityCliJsonWriter().BeginObject()
                        .Prop("command", "profiler.load")
                        .Prop("path", path)
                        .Prop("resolvedPath", resolvedPath)
                        .EndObject().ToString(),
                    "Use profiler.capture or profiler.export to create a capture file, then pass its path to profiler.load.");
            }
            var driverType = FindType("UnityEditorInternal.ProfilerDriver");
            var loadMethod = driverType?.GetMethod("LoadProfile", new System.Type[] { typeof(string), typeof(bool) });
            try { loadMethod?.Invoke(null, new object[] { resolvedPath, false }); } catch { /* 版本差异 */ }
            return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "profiler.load").Prop("path", resolvedPath).Prop("loaded", true)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "profiler.load").Prop("path", resolvedPath).EndObject())
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        private static string ProfilerAreas_Removed(BridgeRequest request)
        {
            var areaType = FindProfilerAreaType();
            if (areaType == null || !areaType.IsEnum)
            {
                return ErrorJson("E_PROFILER_AREAS_UNAVAILABLE", "ProfilerArea enum not available.", "Run in a Unity Editor with profiling support.");
            }
            var names = System.Enum.GetNames(areaType);
            return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "profiler.areas")
                .Prop("count", (long)names.Length)
                .Prop("enumType", areaType.FullName)
                .PropArray("areas", arr => { foreach (var n in names) arr.Value(n); })
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>按 Unity 版本差异解析 ProfilerArea 枚举；Unity 2022+ 在 UnityEngine.Profiling 命名空间。</summary>
        private static Type FindProfilerAreaType()
        {
            return FindType("UnityEngine.Profiling.ProfilerArea") ?? FindType("UnityEditorInternal.ProfilerArea");
        }

        private static string ProfilerObjectMemory_Removed(BridgeRequest request)
        {
            var target = request.StringArg("target") ?? request.StringArg("path");
            var go = string.IsNullOrEmpty(target) ? null : ObjectRefResolver.FindGameObjectByPath(target);
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "profiler.object-memory");
            w.Prop("target", target);
            if (go != null)
            {
                long total = 0;
                w.PropArray("objects", arr =>
                {
                    foreach (var c in go.GetComponents<Component>())
                    {
                        if (c == null) continue;
                        var size = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(c);
                        total += size;
                        arr.BeginObject().Prop("name", c.GetType().Name).Prop("instanceId", c.GetInstanceID()).Prop("sizeBytes", size).EndObject();
                    }
                });
                w.Prop("totalBytes", total);
            }
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        /// <summary>渲染采样统计。profiler.* 表示采样/性能。</summary>
        private static string ProfilerRenderingStats(BridgeRequest request)
        {
            var statsType = FindType("UnityEditor.UnityStats");
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "profiler.rendering-stats");
            if (statsType == null)
            {
                // UnityStats 是 internal Editor 类型，不同 Unity 版本/反射策略下可能取不到。
                w.Prop("available", false);
                w.Prop("diagnostics", "UnityStats type is not available in this Unity version.");
                w.PropArray("next_actions", arr =>
                {
                    arr.Value("Use a Unity 2022.x Editor where UnityEditor.UnityStats is reachable via reflection.");
                    arr.Value("For per-frame GPU/render counts prefer profiler.frame or profiler.hierarchy while the Profiler is recording.");
                });
            }
            else
            {
                // UnityStats 字段只在 Game View/实际渲染帧被填充。非 Play 或无 Game View 时为 null，
                // 这里显式标注采样状态，避免使用者把空值当成数据丢失。
                var anyValue = false;
                var fields = new[] { "batches", "triangles", "vertices", "setPassCalls", "screenRes", "usedTextureMemory", "renderTextureCount" };
                w.PropObject("stats", stats =>
                {
                    foreach (var name in fields)
                    {
                        var value = StringMemberValue(statsType, null, name);
                        if (!string.IsNullOrEmpty(value)) anyValue = true;
                        stats.Prop(name, value);
                    }
                });
                w.Prop("available", anyValue);
                if (!anyValue)
                {
                    w.Prop("diagnostics", "UnityStats fields are null for the sampled frame. Rendering statistics are only populated while the Game View is actively rendering (enter Play Mode or focus the Game View and retry).");
                    w.PropArray("next_actions", arr =>
                    {
                        arr.Value("Enter Play Mode or make the Game View render at least one frame, then retry.");
                        arr.Value("For stable per-frame timings use profiler.frame (FrameTimingManager) or profiler.hierarchy.");
                    });
                }
            }
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        private static string ProfilerScriptStats_Removed(BridgeRequest request)
        {
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "profiler.script-stats");
            w.Prop("gcAllocs", (long)UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong());
            w.Prop("monoHeapSize", (long)UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong());
            w.Prop("monoUsedSize", (long)UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong());
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        private static string ProfilerDeep_Removed(BridgeRequest request)
        {
            var previous = GetProfilerDeepProfiling();
            var hasEnabled = request.OptionalBoolArg("enabled").HasValue;
            var desired = request.OptionalBoolArg("enabled");
            var sessionEnabled = GetProfilerSessionEnabled();
            if (hasEnabled && sessionEnabled)
            {
                return ErrorJsonWithDetails("E_PROFILER_DEEP_REQUIRES_STOP",
                    "Cannot change Deep Profiling while the Profiler is recording.",
                    new UnityCliJsonWriter().BeginObject()
                        .Prop("command", "profiler.deep")
                        .Prop("deepProfiling", previous)
                        .Prop("profilerRecording", true)
                        .EndObject().ToString(),
                    "Run tools run profiler.stop first, then set deep profiling, then profiler.start again.");
            }
            var applied = previous;
            if (hasEnabled)
            {
                SetProfilerDeepProfiling(desired.Value);
                applied = desired.Value;
                WriteLog("profiler_deep enabled=" + applied.ToString().ToLowerInvariant() + " previous=" + previous.ToString().ToLowerInvariant());
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "profiler.deep")
                .Prop("deepProfiling", applied)
                .Prop("previous", previous)
                .Prop("changed", hasEnabled && applied != previous)
                .PropRaw("profiler", ProfilerStateJson())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static bool GetProfilerDeepProfiling()
        {
            var property = typeof(UnityEngine.Profiling.Profiler).GetProperty("deepProfiling", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanRead)
            {
                try { return (bool)property.GetValue(null, null); } catch { return false; }
            }
            return false;
        }

        // C2: 导出某一帧某线程的 CPU 调用树（逐 marker），对齐用户期望的 profiler.hierarchy。
        // 使用 UnityEditorInternal.ProfilerDriver.GetHierarchyFrameDataView 拿到 HierarchyFrameDataView，
        // 递归 GetItemChildren 输出 name/depth/ms/children，限定 top/depth 防止单帧调用树过大。
        private static string ProfilerHierarchy(BridgeRequest request)
        {
            var driverType = FindType("UnityEditorInternal.ProfilerDriver");
            if (driverType == null)
            {
                return ErrorJson("E_PROFILER_HIERARCHY_UNAVAILABLE", "UnityEditorInternal.ProfilerDriver is not available; hierarchy requires the Editor Profiler.", "Run this in the Unity Editor (not batchmode without profiler support).");
            }
            var frame = request.OptionalIntArg("frame") ?? ProfilerDriverIntProperty("lastFrameIndex", -1); // 省略 frame 时读取最新已采集帧
            var threadIndex = request.OptionalIntArg("thread") ?? 0;
            var top = Math.Max(1, Math.Min(request.OptionalIntArg("top") ?? 20, 5000));
            var maxDepth = Math.Max(1, Math.Min(request.OptionalIntArg("depth") ?? 5, 32));

            // Unity 2022.3 当前类型位于 UnityEditor.Profiling；保留旧命名空间作为兼容回退。
            var hierarchyViewType = FindType("UnityEditor.Profiling.HierarchyFrameDataView")
                ?? FindType("UnityEditorInternal.HierarchyFrameDataView")
                ?? FindType("UnityEngine.Profiling.HierarchyFrameDataView");
            if (hierarchyViewType == null)
            {
                return ErrorJson("E_PROFILER_HIERARCHY_UNAVAILABLE", "HierarchyFrameDataView type not available in this Unity version.", "Use an Editor version that exposes UnityEditor.Profiling.HierarchyFrameDataView, then record profiler frames before retrying.");
            }
            var viewModesType = hierarchyViewType.GetTypeInfo().GetDeclaredNestedType("ViewModes")?.AsType();
            var getHierarchy = viewModesType == null
                ? null
                : driverType.GetMethod("GetHierarchyFrameDataView", new[] { typeof(int), typeof(int), viewModesType, typeof(int), typeof(bool) });
            // 不同 Unity 版本签名不同；退回 (int,int) 最简重载。
            if (getHierarchy == null)
            {
                getHierarchy = driverType.GetMethod("GetHierarchyFrameDataView", new[] { typeof(int), typeof(int) });
            }
            if (getHierarchy == null)
            {
                return ErrorJson("E_PROFILER_HIERARCHY_UNAVAILABLE", "ProfilerDriver.GetHierarchyFrameDataView overload not found.", "Unity version may not expose this API via reflection.");
            }

            object view = null;
            try
            {
                view = getHierarchy.GetParameters().Length == 2
                    ? getHierarchy.Invoke(null, new object[] { frame, threadIndex })
                    : getHierarchy.Invoke(null, new object[] { frame, threadIndex, 0, 0, false });
            }
            catch (Exception exception)
            {
                return ErrorJsonWithDetails("E_PROFILER_HIERARCHY_UNAVAILABLE",
                    "Failed to acquire hierarchy frame data: " + exception.Message,
                    new UnityCliJsonWriter().BeginObject()
                        .Prop("command", "profiler.hierarchy")
                        .Prop("frame", (long)frame)
                        .Prop("thread", (long)threadIndex)
                        .Prop("profilerRecording", GetProfilerSessionEnabled())
                        .EndObject().ToString(),
                    "Make sure the Profiler has recorded frames; pass profiler.start then profiler.stop, then query a recorded frame (or omit frame to use the latest).");
            }
            if (view == null)
            {
                return ErrorJson("E_PROFILER_HIERARCHY_NO_DATA", "No hierarchy data available for the requested frame/thread.", "Record profiler frames first (profiler.start → play → profiler.stop), then query a valid frame.");
            }

            var validProp = hierarchyViewType.GetProperty("valid");
            if (validProp != null && !(bool)validProp.GetValue(view, null))
            {
                return ErrorJson("E_PROFILER_HIERARCHY_NO_DATA", "HierarchyFrameDataView is invalid for the requested frame/thread.", "Record profiler frames first, then query a valid frame.");
            }

            var resolvedFrame = frame < 0 ? (int)hierarchyViewType.GetProperty("frameIndex").GetValue(view, null) : frame;
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "profiler.hierarchy");
            w.Prop("frame", (long)resolvedFrame);
            w.Prop("thread", (long)threadIndex);
            w.Prop("top", (long)top);
            w.Prop("depth", (long)maxDepth);
            w.PropArray("rootItems", arr => { BuildHierarchyItems(view, hierarchyViewType, 0, top, maxDepth, 0, arr); });
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        // 递归输出调用树节点，按 top 限制每层子项数量、depth 限制最大深度，避免单帧树过大。
        private static void BuildHierarchyItems(object view, Type viewType, int itemId, int top, int maxDepth, int depth, UnityCliJsonWriter arr)
        {
            if (depth > maxDepth) return;
            // 子项集合通过 GetItemChildren(int, List<int>) 获取
            var childrenProp = viewType.GetMethod("GetItemChildren", new[] { typeof(int), typeof(List<int>) });
            var nameMethod = viewType.GetMethod("GetItemName", new[] { typeof(int) });
            var msMethod = viewType.GetMethod("GetItemThreadMS", new[] { typeof(int) });
            var instanceMethod = viewType.GetMethod("GetItemInstanceMetadata", Type.EmptyTypes);
            var descendantsMethod = viewType.GetMethod("GetItemDescendantsThatHaveChildren", new[] { typeof(int), typeof(List<int>) });
            var childIds = new List<int>();
            try
            {
                if (childrenProp != null) childrenProp.Invoke(view, new object[] { itemId, childIds });
            }
            catch { return; }

            var emitted = 0;
            foreach (var childId in childIds)
            {
                if (emitted >= top) break;
                string name = null;
                double ms = 0;
                try { if (nameMethod != null) name = nameMethod.Invoke(view, new object[] { childId }) as string; } catch { }
                try { if (msMethod != null) { var v = msMethod.Invoke(view, new object[] { childId }); if (v is double d) ms = d; else if (v is float f) ms = f; } } catch { }
                var grandChildren = new List<int>();
                try { if (childrenProp != null) childrenProp.Invoke(view, new object[] { childId, grandChildren }); } catch { }
                arr.BeginObject()
                    .Prop("id", (long)childId)
                    .Prop("name", name ?? "(unknown)")
                    .Prop("depth", (long)depth)
                    .Prop("ms", ms)
                    .Prop("siblingCount", (long)childIds.Count)
                    .Prop("childCount", (long)grandChildren.Count)
                    .Prop("childrenTruncated", grandChildren.Count > top || (grandChildren.Count > 0 && depth + 1 > maxDepth));
                // 递归子项写入 children 数组
                if (grandChildren.Count > 0 && depth + 1 <= maxDepth)
                {
                    arr.PropArray("children", childArr => BuildHierarchyItems(view, viewType, childId, top, maxDepth, depth + 1, childArr));
                }
                arr.EndObject();
                emitted++;
            }
        }

        private static string ProfilerCounter_Removed(BridgeRequest request)
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

        private static string ProfilerMemory_Removed(BridgeRequest request)
        {
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("profiler", ProfilerStateJson())
                .PropRaw("memory", ProfilerMemoryJson())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ProfilerFrame_Removed(BridgeRequest request)
        {
            var module = request.StringArg("module") ?? "CPU Usage";
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("profiler", ProfilerStateJson())
                .PropRaw("frame", ProfilerFrameJson(module))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ProfilerExport_Removed(BridgeRequest request)
        {
            return ProfilerWriteArtifact(request, "profiler.export", 0, 1);
        }

        private static string ProfilerCapture(BridgeRequest request)
        {
            var durationMs = Math.Max(0, request.IntArg("durationMs", 0));
            var frames = Math.Max(0, request.IntArg("frames", 1));
            return ProfilerWriteArtifact(request, "profiler.capture", durationMs, frames);
        }

        // C4: 采集起始/结束帧与 tag，便于外部脚本把 raw 采集与逻辑采样点对齐归档。
        // 返回帧范围字段；若调用方传入 captureTag，则在 artifact 旁写一份 <path>.metadata.json sidecar。
        private static void WriteCaptureMetadataSidecar(string outputPath, string requestId, string captureTag, long startFrame, long endFrame, DateTime startedAt, DateTime finishedAt)
        {
            try
            {
                var sidecarPath = outputPath + ".metadata.json";
                File.WriteAllText(sidecarPath, new UnityCliJsonWriter().BeginObject()
                    .Prop("requestId", requestId)
                    .Prop("captureTag", captureTag)
                    .Prop("artifactPath", outputPath)
                    .Prop("startFrame", startFrame)
                    .Prop("endFrame", endFrame)
                    .Prop("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture))
                    .Prop("finishedAt", finishedAt.ToString("O", CultureInfo.InvariantCulture))
                    .Prop("sidecarVersion", "unity-cli-profiler-capture-v1")
                    .EndObject().ToString(), Encoding.UTF8);
            }
            catch (Exception exception)
            {
                WriteLog("profiler_capture_metadata_failed artifactPath=" + outputPath + " " + exception.GetType().Name + ": " + exception.Message);
            }
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
                        return CaptureRawProfilerDeferred(outputPath, durationMs, Math.Max(1, frames), request.RequestId, effectType, startedAt, format, request.OptionalStringArg("captureTag"), (long)Time.frameCount);
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
                var captureTag = request.OptionalStringArg("captureTag");
                var startFrame = (long)Time.frameCount;
                // 单帧同步采集：起止帧为同一帧。
                var endFrame = startFrame;
                if (!string.IsNullOrEmpty(captureTag))
                {
                    WriteCaptureMetadataSidecar(outputPath, request.RequestId, captureTag, startFrame, endFrame, startedAt, finishedAt);
                }
                WriteLog(effectType.Replace('.', '_') + " artifactPath=" + outputPath + " durationMs=" + elapsedMs.ToString(CultureInfo.InvariantCulture));
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("artifactPath", outputPath)
                    .Prop("profilerPath", outputPath)
                    .Prop("format", format)
                    .Prop("durationMs", elapsedMs)
                    .Prop("requestedDurationMs", (long)durationMs)
                    .Prop("frames", (long)frames)
                    .Prop("startFrame", startFrame)
                    .Prop("endFrame", endFrame)
                    .Prop("captureTag", captureTag)
                    .Prop("metadataSidecar", string.IsNullOrEmpty(captureTag) ? null : outputPath + ".metadata.json")
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

        #region Memory Snapshot — 对齐 CoplayDev memory_take/list/compare，需要 com.unity.memoryprofiler 包

        /// <summary>查找 MemoryProfiler 类型（包 com.unity.memoryprofiler）。</summary>
        private static Type MemoryProfilerType()
        {
            return FindType("Unity.Profiling.Memory.MemoryProfiler")
                ?? FindType("UnityEngine.Profiling.Memory.Experimental.MemoryProfiler");
        }

        /// <summary>采集内存快照 .snap。TakeSnapshot 回调在主线程未来帧触发，用 deferred wait 等待。</summary>
        private static string ProfilerMemorySnapshotTake(BridgeRequest request)
        {
            var profilerType = MemoryProfilerType();
            if (profilerType == null)
            {
                return ErrorJson("E_PROFILER_PACKAGE_MISSING", "Package com.unity.memoryprofiler is required for memory snapshots.", "Install com.unity.memoryprofiler via the Package Manager.");
            }
            var outputPath = request.StringArg("path");
            if (string.IsNullOrEmpty(outputPath))
            {
                var dir = Path.Combine(Application.temporaryCachePath, "MemoryCaptures");
                Directory.CreateDirectory(dir);
                outputPath = Path.Combine(dir, "snapshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".snap");
            }
            // 查找 TakeSnapshot 重载：优先 3 参 (string, Action<string,bool>, CaptureFlags)，回退 2 参 (string, Action<string,bool>)。
            var callbackType = typeof(Action<string, bool>);
            var captureFlagsType = FindType("Unity.Profiling.Memory.CaptureFlags")
                ?? FindType("UnityEngine.Profiling.Memory.Experimental.CaptureFlags");
            MethodInfo takeMethod = null;
            object[] invokeArgs = null;
            if (captureFlagsType != null)
            {
                takeMethod = profilerType.GetMethod("TakeSnapshot", new[] { typeof(string), callbackType, captureFlagsType });
            }
            if (takeMethod != null)
            {
                invokeArgs = new object[] { outputPath, (Action<string, bool>)null, System.Enum.ToObject(captureFlagsType, 0) };
            }
            else
            {
                takeMethod = profilerType.GetMethod("TakeSnapshot", new[] { typeof(string), callbackType });
            }
            if (takeMethod == null)
            {
                return ErrorJson("E_PROFILER_API_UNSUPPORTED", "MemoryProfiler.TakeSnapshot API not found.", "The installed com.unity.memoryprofiler version may be incompatible.");
            }

            // 闭包状态：回调设置 flag + 路径/结果。主线程未来帧触发。
            string capturedPath = null;
            bool captureResult = false;
            bool callbackFired = false;
            Action<string, bool> callback = (path, result) =>
            {
                capturedPath = path;
                captureResult = result;
                callbackFired = true;
            };
            if (invokeArgs != null) { invokeArgs[1] = callback; }
            else { invokeArgs = new object[] { outputPath, callback }; }
            try { takeMethod.Invoke(null, invokeArgs); }
            catch (Exception ex) { return ErrorJson("E_PROFILER_SNAPSHOT_FAILED", "Snapshot capture failed: " + ex.Message, "Check Unity Console and the Memory Profiler package."); }

            // TakeSnapshot 回调可能同步触发或在未来帧；用 deferred wait 轮询，不阻塞主线程。
            var item = currentWorkItem;
            return StartDeferredWait(item,
                () => callbackFired,
                30000,
                () =>
                {
                    if (!captureResult)
                    {
                        return ErrorJson("E_PROFILER_SNAPSHOT_FAILED", "Snapshot capture reported failure for path: " + outputPath, "Check Unity Console for Memory Profiler errors.");
                    }
                    var finalPath = capturedPath ?? outputPath;
                    var fi = new FileInfo(finalPath);
                    WriteLog("profiler_memory_snapshot_take path=" + finalPath);
                    return SuccessJson(new UnityCliJsonWriter().BeginObject()
                        .Prop("command", "profiler.memory-snapshot.take")
                        .Prop("path", finalPath)
                        .Prop("sizeBytes", fi.Exists ? (long)fi.Length : 0L)
                        .Prop("logPath", LogPath())
                        .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "profiler.memory-snapshot.take").Prop("path", finalPath).EndObject())
                        .EndObject().ToString());
                },
                "E_PROFILER_SNAPSHOT_TIMEOUT", "Memory snapshot timed out after 30 seconds.", "Reduce scene complexity or retry.");
        }

        /// <summary>列出 .snap 快照文件。对齐 CoplayDev memory_list_snapshots。</summary>
        private static string ProfilerMemorySnapshotList(BridgeRequest request)
        {
            var searchPath = request.StringArg("path");
            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(searchPath))
            {
                dirs.Add(searchPath);
            }
            else
            {
                dirs.Add(Path.Combine(Application.temporaryCachePath, "MemoryCaptures"));
                dirs.Add(Path.Combine(Application.dataPath, "..", "MemoryCaptures"));
            }
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "profiler.memory-snapshot.list");
            w.PropArray("snapshots", arr =>
            {
                foreach (var dir in dirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var file in Directory.GetFiles(dir, "*.snap"))
                    {
                        var fi = new FileInfo(file);
                        arr.BeginObject()
                            .Prop("path", fi.FullName)
                            .Prop("sizeBytes", (long)fi.Length)
                            .Prop("created", fi.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture))
                            .EndObject();
                    }
                }
            });
            w.PropArray("searchedDirs", arr => { foreach (var d in dirs) arr.Value(d); });
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        /// <summary>比较两个 .snap 快照的文件级元数据。对齐 CoplayDev memory_compare_snapshots。</summary>
        private static string ProfilerMemorySnapshotCompare(BridgeRequest request)
        {
            var pathA = request.StringArg("snapshotA") ?? request.StringArg("pathA");
            var pathB = request.StringArg("snapshotB") ?? request.StringArg("pathB");
            if (string.IsNullOrEmpty(pathA) || string.IsNullOrEmpty(pathB))
            {
                throw new BridgeCommandException("E_PROFILER_SNAPSHOT_PATH_REQUIRED", "profiler.memory-snapshot.compare requires --snapshot-a and --snapshot-b.", "Pass two .snap file paths.");
            }
            if (!File.Exists(pathA))
            {
                return ErrorJson("E_PROFILER_SNAPSHOT_NOT_FOUND", "Snapshot file not found: " + pathA, "Check the path or run profiler.memory-snapshot.list.");
            }
            if (!File.Exists(pathB))
            {
                return ErrorJson("E_PROFILER_SNAPSHOT_NOT_FOUND", "Snapshot file not found: " + pathB, "Check the path or run profiler.memory-snapshot.list.");
            }
            var fiA = new FileInfo(pathA);
            var fiB = new FileInfo(pathB);
            long deltaBytes = fiB.Length - fiA.Length;
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "profiler.memory-snapshot.compare");
            w.PropObject("snapshotA", o => o.Prop("path", fiA.FullName).Prop("sizeBytes", (long)fiA.Length).Prop("created", fiA.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture)));
            w.PropObject("snapshotB", o => o.Prop("path", fiB.FullName).Prop("sizeBytes", (long)fiB.Length).Prop("created", fiB.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture)));
            w.PropObject("delta", o => o.Prop("sizeDeltaBytes", deltaBytes).Prop("timeDeltaSeconds", (fiB.CreationTimeUtc - fiA.CreationTimeUtc).TotalSeconds));
            w.Prop("note", "File-level metadata comparison. For object-level diff, open both snapshots in the Memory Profiler window.");
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        #endregion

        #region Frame Debugger — 对齐 CoplayDev frame_debugger，反射 UnityEditorInternal.FrameDebuggerUtility

        // 缓存 FrameDebuggerUtility 反射成员，避免每次调用重复查找。
        private static readonly Type FrameDebuggerUtilityType = FindType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility, UnityEditor")
            ?? FindType("UnityEditorInternal.FrameDebuggerUtility, UnityEditor");
        private static readonly PropertyInfo FrameDebuggerEventCountProp = FrameDebuggerUtilityType?.GetProperty("count", BindingFlags.Public | BindingFlags.Static)
            ?? FrameDebuggerUtilityType?.GetProperty("eventsCount", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo FrameDebuggerEnableMethod = FrameDebuggerUtilityType?.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool), typeof(int) }, null)
            ?? FrameDebuggerUtilityType?.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo FrameDebuggerGetEventsMethod = FrameDebuggerUtilityType?.GetMethod("GetFrameEvents", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo FrameDebuggerEventNameMethod = FrameDebuggerUtilityType?.GetMethod("GetFrameEventInfoName", BindingFlags.Public | BindingFlags.Static);
        private static readonly Type FrameDebuggerEventDataType = FindType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData, UnityEditor")
            ?? FindType("UnityEditorInternal.FrameDebuggerEventData, UnityEditor");
        private static readonly MethodInfo FrameDebuggerGetEventDataMethod = FrameDebuggerEventDataType != null
            ? FrameDebuggerUtilityType?.GetMethod("GetFrameEventData", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), FrameDebuggerEventDataType }, null)
            : FrameDebuggerUtilityType?.GetMethod("GetFrameEventData", BindingFlags.Public | BindingFlags.Static);
        private static bool FrameDebuggerAvailable => FrameDebuggerUtilityType != null && FrameDebuggerEventCountProp != null && FrameDebuggerEnableMethod != null;

        private static void InvokeFrameDebuggerSetEnabled(bool value)
        {
            var paramCount = FrameDebuggerEnableMethod.GetParameters().Length;
            if (paramCount == 2) { FrameDebuggerEnableMethod.Invoke(null, new object[] { value, 0 }); }
            else if (paramCount == 1) { FrameDebuggerEnableMethod.Invoke(null, new object[] { value }); }
            else { throw new InvalidOperationException("SetEnabled has unexpected " + paramCount + " parameters."); }
        }

        private static int GetFrameDebuggerEventCount()
        {
            try { return (int)FrameDebuggerEventCountProp.GetValue(null); }
            catch { return 0; }
        }

        /// <summary>启用 Frame Debugger。对齐 CoplayDev frame_debugger_enable，需先暂停 Play Mode。</summary>
        private static string ProfilerFrameDebuggerEnable(BridgeRequest request)
        {
            if (!FrameDebuggerAvailable)
            {
                return ErrorJson("E_FRAME_DEBUGGER_UNAVAILABLE", "FrameDebuggerUtility not found via reflection.", "Run in a Unity Editor with Frame Debugger support.");
            }
            // 打开 Frame Debugger 窗口（事件采集所需）。
            EditorApplication.ExecuteMenuItem("Window/Analysis/Frame Debugger");
            // Play Mode 下必须暂停才能采集事件。
            if (EditorApplication.isPlaying && !EditorApplication.isPaused)
            {
                return ErrorJson("E_FRAME_DEBUGGER_REQUIRES_PAUSE", "Game must be paused before enabling Frame Debugger.", "Pause the editor (editor.pause) first, then retry profiler.frame-debugger.enable.");
            }
            try { InvokeFrameDebuggerSetEnabled(true); }
            catch (Exception ex) { return ErrorJson("E_FRAME_DEBUGGER_FAILED", "Failed to enable Frame Debugger: " + ex.Message, "Check Unity Console."); }
            WriteLog("profiler_frame_debugger_enable eventCount=" + GetFrameDebuggerEventCount().ToString(CultureInfo.InvariantCulture));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "profiler.frame-debugger.enable")
                .Prop("enabled", true)
                .Prop("eventCount", GetFrameDebuggerEventCount())
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>禁用 Frame Debugger。</summary>
        private static string ProfilerFrameDebuggerDisable(BridgeRequest request)
        {
            if (!FrameDebuggerAvailable)
            {
                return ErrorJson("E_FRAME_DEBUGGER_UNAVAILABLE", "FrameDebuggerUtility not found via reflection.", "Run in a Unity Editor with Frame Debugger support.");
            }
            try { InvokeFrameDebuggerSetEnabled(false); }
            catch (Exception ex) { return ErrorJson("E_FRAME_DEBUGGER_FAILED", "Failed to disable Frame Debugger: " + ex.Message, "Check Unity Console."); }
            WriteLog("profiler_frame_debugger_disable");
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "profiler.frame-debugger.disable")
                .Prop("enabled", false)
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>读取 Frame Debugger 事件（draw calls），分页。对齐 CoplayDev frame_debugger_get_events。</summary>
        private static string ProfilerFrameDebuggerEvents(BridgeRequest request)
        {
            if (!FrameDebuggerAvailable)
            {
                return ErrorJson("E_FRAME_DEBUGGER_UNAVAILABLE", "FrameDebuggerUtility not found via reflection.", "Run in a Unity Editor with Frame Debugger support.");
            }
            var pageSize = request.IntArg("pageSize", 50);
            var cursor = request.IntArg("cursor", 0);
            var totalEvents = GetFrameDebuggerEventCount();
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "profiler.frame-debugger.events");
            w.Prop("totalEvents", totalEvents);
            w.Prop("pageSize", pageSize);
            w.Prop("cursor", cursor);
            if (totalEvents == 0)
            {
                w.PropArray("events", arr => { });
                w.Prop("note", "Frame Debugger has no events. Is it enabled?");
                w.Prop("logPath", LogPath());
                return SuccessJson(w.EndObject().ToString());
            }
            // 反射读取事件描述数组（含 type/name 信息）。
            object[] frameEvents = null;
            if (FrameDebuggerGetEventsMethod != null)
            {
                try
                {
                    var raw = FrameDebuggerGetEventsMethod.Invoke(null, null);
                    if (raw is Array arr) { frameEvents = new object[arr.Length]; arr.CopyTo(frameEvents, 0); }
                }
                catch { /* 忽略，逐个读取 */ }
            }
            var end = Math.Min(cursor + pageSize, totalEvents);
            w.PropArray("events", arr =>
            {
                for (int i = cursor; i < end; i++)
                {
                    var entry = arr.BeginObject();
                    entry.Prop("index", i);
                    if (FrameDebuggerEventNameMethod != null)
                    {
                        try { entry.Prop("name", (string)FrameDebuggerEventNameMethod.Invoke(null, new object[] { i })); }
                        catch { /* 跳过 name */ }
                    }
                    if (frameEvents != null && i < frameEvents.Length)
                    {
                        TryAddFrameDebuggerField(frameEvents[i], "type", entry, "eventType");
                        TryAddFrameDebuggerField(frameEvents[i], "gameObjectInstanceID", entry);
                    }
                    if (FrameDebuggerGetEventDataMethod != null)
                    {
                        try
                        {
                            var paramInfos = FrameDebuggerGetEventDataMethod.GetParameters();
                            object eventData;
                            if (paramInfos.Length == 2 && FrameDebuggerEventDataType != null)
                            {
                                eventData = Activator.CreateInstance(FrameDebuggerEventDataType);
                                var args = new object[] { i, eventData };
                                var ok = FrameDebuggerGetEventDataMethod.Invoke(null, args);
                                eventData = (ok is true) ? args[1] : null;
                            }
                            else
                            {
                                eventData = FrameDebuggerGetEventDataMethod.Invoke(null, new object[] { i });
                            }
                            if (eventData != null)
                            {
                                TryAddFrameDebuggerField(eventData, "shaderName", entry);
                                TryAddFrameDebuggerField(eventData, "passName", entry);
                                TryAddFrameDebuggerField(eventData, "vertexCount", entry);
                                TryAddFrameDebuggerField(eventData, "indexCount", entry);
                                TryAddFrameDebuggerField(eventData, "instanceCount", entry);
                                TryAddFrameDebuggerField(eventData, "meshName", entry);
                            }
                        }
                        catch { /* 跳过该事件详细数据 */ }
                    }
                    entry.EndObject();
                }
            });
            if (end < totalEvents) { w.Prop("nextCursor", end); }
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        /// <summary>反射读取 Frame Debugger 事件对象的字段/属性。</summary>
        private static void TryAddFrameDebuggerField(object obj, string fieldName, UnityCliJsonWriter entry, string outputKey = null)
        {
            try
            {
                var type = obj.GetType();
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance)
                         ?? type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                var prop = type.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance)
                        ?? type.GetProperty(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                object val = field != null ? field.GetValue(obj) : prop != null ? prop.GetValue(obj) : null;
                if (val != null)
                {
                    entry.Prop(outputKey ?? fieldName, val.ToString());
                }
            }
            catch { /* 跳过不可用字段 */ }
        }

        #endregion

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
