#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace UnityCli
{
    /// <summary>
    /// profiler.capture — 采集 Profiler 帧数据到文件。
    /// 多帧/跨帧采集通过统一 wait scheduler 驱动，不再暴露 deferred 子接口。
    /// </summary>
    public class ProfilerCaptureTool : IUnityCliTool<ProfilerCaptureTool.Request, ProfilerCaptureTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Path { get; set; }
            public bool Overwrite { get; set; }
            public string Format { get; set; }
            public int? DurationMs { get; set; }
            public int? Frames { get; set; }
            public string CaptureTag { get; set; }
        }

        public sealed class Result
        {
            public string ArtifactPath { get; set; }
            public string ProfilerPath { get; set; }
            public string Format { get; set; }
            public long DurationMs { get; set; }
            public long RequestedDurationMs { get; set; }
            public long Frames { get; set; }
            public long StartFrame { get; set; }
            public long EndFrame { get; set; }
            public string CaptureTag { get; set; }
            public string MetadataSidecar { get; set; }
            public ProfilerState Profiler { get; set; }
            public List<ProfilerPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.capture",
            Summary = "Capture profiler frame data to a file (single-frame or cross-frame) via the unified wait scheduler.",
            Description = "Capture profiler frame data to a file. format=raw with durationMs>0 or frames>1 performs cross-frame capture through the framework wait scheduler; single-frame captures complete synchronously. startFrame/endFrame report Time.frameCount at the beginning and end of capture, so they advance naturally in Play Mode and may stay unchanged when Unity is only idling in the Editor. Pass captureTag to also write a <path>.metadata.json sidecar for archival and correlation.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "persistent-artifact-write" }, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Capture 30 raw frames with tag", Input = new Request { Path = "baseline.raw", Format = "raw", Frames = 30, CaptureTag = "baseline-before-opt" } } }
        };

        public async ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            var outputPath = ProfilerToolSupport.ResolveOutputPath(request.Path);
            if (File.Exists(outputPath) && !request.Overwrite)
            {
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS",
                    "Profiler artifact already exists: " + outputPath,
                    "Pass overwrite=true or choose a different output path.");
            }

            ctx.CommitEffect();
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            if (File.Exists(outputPath))
            {
                try { File.Delete(outputPath); }
                catch (Exception exception)
                {
                    throw new UnityCliBridge.BridgeCommandException("E_PROFILER_ARTIFACT_PREPARE_FAILED",
                        "Failed to replace existing profiler artifact: " + exception.Message,
                        "Close programs using the output file or choose a different path.");
                }
            }
            var startedAt = DateTime.UtcNow;
            var format = ProfilerToolSupport.NormalizeProfilerFormat(request.Format, outputPath);
            var durationMs = Math.Max(0, request.DurationMs ?? 0);
            var frames = Math.Max(0, request.Frames ?? 1);
            var captureTag = request.CaptureTag;

            if (format == "raw" && (durationMs > 0 || frames > 1))
            {
                var totalFrames = Math.Max(1, frames);
                var startFrame = (long)Time.frameCount;
                var internalDeadlineMs = Math.Max(durationMs, 30000);
                var internalDeadline = startedAt.AddMilliseconds(internalDeadlineMs);
                var previousLogFile = Profiler.logFile;
                var previousEnabled = Profiler.enabled;
                var previousBinaryLog = Profiler.enableBinaryLog;
                Profiler.logFile = outputPath;
                Profiler.enableBinaryLog = true;
                Profiler.enabled = true;

                var capturedFrames = 0;
                try
                {
                    while (capturedFrames < totalFrames)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (DateTime.UtcNow > internalDeadline)
                        {
                            throw new UnityCliBridge.BridgeCommandException("E_PROFILER_CAPTURE_TIMEOUT",
                                "Profiler capture did not complete all frames before the deadline.",
                                "Reduce the requested frame count or duration, or check Unity Editor performance.");
                        }

                        Profiler.BeginSample("UnityCliProfilerCapture");
                        Profiler.EndSample();
                        capturedFrames += 1;

                        if (capturedFrames < totalFrames)
                        {
                            await ctx.Wait.NextFrame(1, ct);
                        }
                    }
                }
                finally
                {
                    RestoreProfiler(previousLogFile, previousEnabled, previousBinaryLog);
                }

                ProfilerToolSupport.EnsureRawProfilerArtifact(outputPath);
                var finishedAt = DateTime.UtcNow;
                var endFrame = (long)Time.frameCount;
                if (!string.IsNullOrEmpty(captureTag))
                {
                    ProfilerToolSupport.WriteCaptureMetadataSidecar(outputPath, ctx.RequestId, captureTag, startFrame, endFrame, startedAt, finishedAt);
                }
                return BuildResult(outputPath, format, durationMs, totalFrames, startFrame, endFrame, captureTag, startedAt, finishedAt);
            }

            if (format == "raw")
            {
                ProfilerToolSupport.CaptureRawProfilerSync(outputPath);
            }
            else
            {
                ProfilerToolSupport.WriteAndVerifyJsonProfilerArtifact(outputPath, BuildJsonArtifact(ctx.RequestId, startedAt, durationMs, frames));
            }

            var finishedSyncAt = DateTime.UtcNow;
            var syncStartFrame = (long)Time.frameCount;
            var syncEndFrame = syncStartFrame;
            if (!string.IsNullOrEmpty(captureTag))
            {
                ProfilerToolSupport.WriteCaptureMetadataSidecar(outputPath, ctx.RequestId, captureTag, syncStartFrame, syncEndFrame, startedAt, finishedSyncAt);
            }
            return BuildResult(outputPath, format, durationMs, frames, syncStartFrame, syncEndFrame, captureTag, startedAt, finishedSyncAt);
        }

        private static void RestoreProfiler(string previousLogFile, bool previousEnabled, bool previousBinaryLog)
        {
            Profiler.enabled = previousEnabled;
            Profiler.enableBinaryLog = previousBinaryLog;
            Profiler.logFile = previousLogFile;
        }

        private static Result BuildResult(string outputPath, string format, int durationMs, long frames, long startFrame, long endFrame, string captureTag, DateTime startedAt, DateTime finishedAt)
        {
            return new Result
            {
                ArtifactPath = outputPath,
                ProfilerPath = outputPath,
                Format = format,
                DurationMs = (long)(finishedAt - startedAt).TotalMilliseconds,
                RequestedDurationMs = (long)durationMs,
                Frames = frames,
                StartFrame = startFrame,
                EndFrame = endFrame,
                CaptureTag = captureTag,
                MetadataSidecar = string.IsNullOrEmpty(captureTag) ? null : outputPath + ".metadata.json",
                Profiler = ProfilerToolSupport.BuildProfilerState(),
                PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.capture", Path = outputPath } }
            };
        }

        private static string BuildJsonArtifact(string requestId, DateTime startedAt, int durationMs, int frames)
        {
            return UnityCliSerializer.Serialize(new ProfilerArtifactFile
            {
                RequestId = requestId,
                StartedAt = startedAt.ToString("O", CultureInfo.InvariantCulture),
                DurationMs = durationMs,
                Frames = frames,
                Profiler = ProfilerToolSupport.BuildProfilerState(),
                Memory = ProfilerToolSupport.BuildProfilerMemory()
            });
        }
    }
}
#endif
