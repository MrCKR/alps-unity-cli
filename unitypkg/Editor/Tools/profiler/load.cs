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
        public sealed class Result { public string Path { get; set; } public long InputSizeBytes { get; set; } public string InputSha256 { get; set; } public long FirstFrameIndex { get; set; } public long LastFrameIndex { get; set; } public bool Loaded { get; set; } public List<ProfilerPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.load", Summary = "Load a saved profiler data file (.raw/.data) into the Profiler window.",
            Description = "Stop Profiler recording, clear the current frame buffer, then load one saved profiler data file. Success requires an attributable non-empty frame range after the empty-buffer barrier while the input size and SHA-256 remain unchanged.",
            Group = "profiler",DestructiveHint = false, SideEffects = new[] { "profiler-loaded-data-session", "profiler-recording-stop" }, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Load profiler data", Input = new Request { Path = "Artifacts/profiler/baseline.raw" } } }
        };

        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(request.Path))
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_PATH_REQUIRED", "profiler.load requires path.", "Pass a .raw or .data file path.");
            var resolvedPath = System.IO.Path.GetFullPath(request.Path);
            if (!File.Exists(resolvedPath))
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_CAPTURE_NOT_FOUND", "Profiler capture file not found: " + request.Path, "Use profiler.capture or profiler.export to create a capture file.");
            var inputSizeBytes = new FileInfo(resolvedPath).Length;
            if (inputSizeBytes <= 0)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_CAPTURE_EMPTY", "Profiler capture file is empty: " + resolvedPath, "Pass a non-empty capture produced by profiler.capture or profiler.export.");
            var inputSha256 = ProfilerToolSupport.FileSha256(resolvedPath);
            var driverType = ProfilerToolSupport.FindType("UnityEditorInternal.ProfilerDriver");
            if (driverType == null)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_LOAD_UNSUPPORTED", "UnityEditorInternal.ProfilerDriver is unavailable.", "Use a Unity Editor version that supports loading profiler captures.");
            var loadMethod = driverType.GetMethod("LoadProfile", new Type[] { typeof(string), typeof(bool) });
            var clearMethod = driverType.GetMethod("ClearAllFrames", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (loadMethod == null || clearMethod == null)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_LOAD_UNSUPPORTED", "ProfilerDriver.LoadProfile or ClearAllFrames is unavailable.", "Use a supported Unity Editor version that exposes isolated profiler capture loading.");

            // 停止采集并建立空 frame buffer barrier，后续出现的 frame 只能归因于本次 LoadProfile。
            ctx.CommitEffect();
            ProfilerToolSupport.SetProfilerSessionEnabled(false);
            ProfilerToolSupport.SetProfilerEditorRecording(false);
            clearMethod.Invoke(null, null);
            var cleared = await ctx.Wait.UntilStable(() =>
                ProfilerToolSupport.ProfilerDriverIntProperty("firstFrameIndex", -1) < 0
                && ProfilerToolSupport.ProfilerDriverIntProperty("lastFrameIndex", -1) < 0,
                TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct: ct);
            if (!cleared)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_LOAD_PRECONDITION_FAILED", "Profiler frame buffer could not be cleared before loading the capture.", "Close active Profiler recording and retry.");
            try { loadMethod.Invoke(null, new object[] { resolvedPath, false }); }
            catch (TargetInvocationException exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_LOAD_FAILED", "Profiler capture could not be loaded: " + (exception.InnerException ?? exception).Message, "Check that the capture file is valid and compatible with this Unity Editor version.");
            }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_LOAD_FAILED", "Profiler capture could not be loaded: " + exception.Message, "Check that the capture file is valid and compatible with this Unity Editor version.");
            }
            var loaded = await ctx.Wait.UntilStable(() =>
            {
                var first = ProfilerToolSupport.ProfilerDriverIntProperty("firstFrameIndex", -1);
                var last = ProfilerToolSupport.ProfilerDriverIntProperty("lastFrameIndex", -1);
                return !ProfilerToolSupport.GetProfilerSessionEnabled()
                    && first >= 0 && last >= first
                    && new FileInfo(resolvedPath).Length == inputSizeBytes
                    && string.Equals(ProfilerToolSupport.FileSha256(resolvedPath), inputSha256, StringComparison.Ordinal);
            }, TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct: ct);
            var observedFirst = ProfilerToolSupport.ProfilerDriverIntProperty("firstFrameIndex", -1);
            var observedLast = ProfilerToolSupport.ProfilerDriverIntProperty("lastFrameIndex", -1);
            if (!loaded || ProfilerToolSupport.GetProfilerSessionEnabled() || observedFirst < 0 || observedLast < observedFirst)
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_LOAD_PROOF_MISSING", "ProfilerDriver did not expose an attributable loaded frame range for " + resolvedPath + ".", "Verify that the capture contains readable frames and matches this Unity version.");
            if (new FileInfo(resolvedPath).Length != inputSizeBytes || !string.Equals(ProfilerToolSupport.FileSha256(resolvedPath), inputSha256, StringComparison.Ordinal))
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_LOAD_PROOF_MISSING", "Profiler capture input changed while loading: " + resolvedPath, "Keep the capture file unchanged and retry.");
            return new Result { Path = resolvedPath, InputSizeBytes = inputSizeBytes, InputSha256 = inputSha256, FirstFrameIndex = observedFirst, LastFrameIndex = observedLast, Loaded = true, PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.load", Path = resolvedPath, SideEffects = "sha256=" + inputSha256 } } };
        }
    }
}
#endif
