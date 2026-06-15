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
            RegisterCommand(registry, "profiler.capture", "profiler", true, ProfilerCapture);
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

        private static string ProfilerStateJson()
        {
            return "{\"enabled\":" + Bool(UnityEngine.Profiling.Profiler.enabled) + ",\"enableBinaryLog\":" + Bool(UnityEngine.Profiling.Profiler.enableBinaryLog) + ",\"supported\":true}";
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
                + "\"profiler\":" + ProfilerStateJson()
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

        private static string ProfilerStatus(BridgeRequest request)
        {
            return SuccessJson("{\"profiler\":" + ProfilerStateJson() + ",\"modules\":[\"CPU Usage\",\"Rendering\",\"Memory\",\"Audio\",\"Physics\",\"UI\",\"Global Illumination\"],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string ProfilerCapture(BridgeRequest request)
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
                var durationMs = Math.Max(0, request.IntArg("durationMs", 0));
                var frames = Math.Max(0, request.IntArg("frames", 1));
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
                WriteLog("profiler_capture artifactPath=" + outputPath + " durationMs=" + elapsedMs.ToString(CultureInfo.InvariantCulture));
                return SuccessJson("{\"artifactPath\":" + Quote(outputPath) + ",\"profilerPath\":" + Quote(outputPath) + ",\"format\":" + Quote(format) + ",\"durationMs\":" + elapsedMs.ToString(CultureInfo.InvariantCulture) + ",\"requestedDurationMs\":" + durationMs.ToString(CultureInfo.InvariantCulture) + ",\"frames\":" + frames.ToString(CultureInfo.InvariantCulture) + ",\"profiler\":" + ProfilerStateJson() + ",\"plannedEffects\":[{\"type\":\"profiler.capture\",\"path\":" + Quote(outputPath) + "}],\"logPath\":" + Quote(LogPath()) + "}");
            }
            catch (BridgeCommandException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return ErrorJson("E_PROFILER_CAPTURE_FAILED", "Profiler capture failed: " + exception.Message, "Check output path permissions and Unity Profiler availability, then retry.");
            }
        }
    }
}
#endif
