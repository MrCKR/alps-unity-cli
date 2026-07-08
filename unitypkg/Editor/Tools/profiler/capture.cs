#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace UnityCli
{
    /// <summary>
    /// profiler.capture — 采集 Profiler 帧数据到文件。
    /// 实现 IUnityCliDeferredTool:多帧(durationMs&gt;0 或 frames&gt;1,format=raw)走 deferred 跨帧采集;
    /// 单帧走同步 Execute。迁移自旧 RegisterCommand(ProfilerCommands.CaptureRawProfilerDeferred/Sync)。
    /// 设计依据:.plan/重构设计总文档.md "异步工具:deferred 子接口" 节。
    /// </summary>
    public class ProfilerCaptureTool : IUnityCliDeferredTool<ProfilerCaptureTool.Request, ProfilerCaptureTool.Result>
    {
        /// <summary>采集参数。path 必填;format=raw 时 durationMs/frames 控制多帧采集。</summary>
        public sealed class Request
        {
            [UnityCliRequired] public string Path { get; set; }
            public bool Overwrite { get; set; }
            public string Format { get; set; }
            public int? DurationMs { get; set; }
            public int? Frames { get; set; }
            public string CaptureTag { get; set; }
        }

        /// <summary>采集结果:产物路径、格式、帧范围、profiler 状态、计划副作用。</summary>
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
            Summary = "Capture profiler frame data to a file (multi-frame deferred or single-frame sync), with optional tag and frame-range metadata.",
            Description = "Capture profiler frame data to a file. format=raw with durationMs>0 or frames>1 triggers multi-frame deferred capture via non-blocking frame polling; single-frame captures complete synchronously. The response includes startFrame/endFrame so the raw capture can be aligned to logical sampling points. Pass captureTag to also write a <path>.metadata.json sidecar for archival and correlation.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = true, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Capture 30 raw frames with tag", Input = new Request { Path = "baseline.raw", Format = "raw", Frames = 30, CaptureTag = "baseline-before-opt" } } }
        };

        /// <summary>
        /// 多帧 raw 采集挂起为 deferred;单帧或 json 走同步 Execute(返回 false)。
        /// deferred 的 IsComplete 每帧采集一帧并检查内部截止;满足或内部超时则恢复 profiler 状态并完成。
        /// </summary>
        public bool TryDefer(Request request, UnityCliProjectToolContext ctx, out DeferredCompletion<Result> completion)
        {
            var outputPath = ProfilerToolSupport.ResolveOutputPath(request.Path);
            var format = ProfilerToolSupport.NormalizeProfilerFormat(request.Format, outputPath);
            var durationMs = Math.Max(0, request.DurationMs ?? 0);
            var frames = Math.Max(0, request.Frames ?? 1);

            // 仅 raw 多帧走 deferred;json 或单帧 raw 走同步
            if (format != "raw" || (durationMs <= 0 && frames <= 1))
            {
                completion = null;
                return false;
            }

            var totalFrames = Math.Max(1, frames);
            var startFrame = (long)Time.frameCount;
            var startedAt = DateTime.UtcNow;
            // 内部截止:min 30s,避免慢帧过早超时;框架 TimeoutMs 再加缓冲,保证 IsComplete 先于框架超时恢复 profiler。
            var internalDeadlineMs = Math.Max(durationMs, 30000);
            var internalDeadline = startedAt.AddMilliseconds(internalDeadlineMs);

            // 保存并开启 profiler 二进制日志
            var previousLogFile = Profiler.logFile;
            var previousEnabled = Profiler.enabled;
            var previousBinaryLog = Profiler.enableBinaryLog;
            Profiler.logFile = outputPath;
            Profiler.enableBinaryLog = true;
            Profiler.enabled = true;

            var capturedFrames = 0;
            var timedOut = false;

            completion = new DeferredCompletion<Result>
            {
                TimeoutMs = internalDeadlineMs + 5000,
                IsComplete = () =>
                {
                    if (capturedFrames >= totalFrames)
                    {
                        RestoreProfiler(previousLogFile, previousEnabled, previousBinaryLog);
                        return true;
                    }
                    if (DateTime.UtcNow > internalDeadline)
                    {
                        // 内部截止:恢复 profiler,标记超时,让 OnComplete 抛结构化错误
                        RestoreProfiler(previousLogFile, previousEnabled, previousBinaryLog);
                        timedOut = true;
                        return true;
                    }
                    // 每帧采集一个 sample
                    Profiler.BeginSample("UnityCliProfilerCapture");
                    Profiler.EndSample();
                    capturedFrames++;
                    return false;
                },
                OnComplete = () =>
                {
                    if (timedOut)
                    {
                        throw new UnityCliBridge.BridgeCommandException("E_PROFILER_CAPTURE_TIMEOUT",
                            "Profiler capture did not complete all frames before the deadline.",
                            "Reduce the requested frame count or duration, or check Unity Editor performance.");
                    }
                    EnsureRawFile(outputPath);
                    var finishedAt = DateTime.UtcNow;
                    var endFrame = (long)Time.frameCount;
                    var captureTag = request.CaptureTag;
                    if (!string.IsNullOrEmpty(captureTag))
                    {
                        ProfilerToolSupport.WriteCaptureMetadataSidecar(outputPath, ctx.RequestId, captureTag, startFrame, endFrame, startedAt, finishedAt);
                    }
                    return BuildResult(outputPath, format, durationMs, totalFrames, startFrame, endFrame, captureTag, startedAt, finishedAt);
                },
                TimeoutErrorCode = "E_PROFILER_CAPTURE_TIMEOUT",
                TimeoutErrorMessage = "Profiler capture did not complete all frames before the timeout.",
                TimeoutNextAction = "Reduce the requested frame count or duration, or check Unity Editor performance."
            };
            return true;
        }

        /// <summary>同步单帧采集(json 摘要或单帧 raw)。TryDefer 返回 false 时框架调用。</summary>
        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var outputPath = ProfilerToolSupport.ResolveOutputPath(request.Path);
            if (File.Exists(outputPath) && !request.Overwrite)
            {
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS",
                    "Profiler artifact already exists: " + outputPath,
                    "Pass overwrite=true or choose a different output path.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            var startedAt = DateTime.UtcNow;
            var format = ProfilerToolSupport.NormalizeProfilerFormat(request.Format, outputPath);
            var durationMs = Math.Max(0, request.DurationMs ?? 0);
            var frames = Math.Max(0, request.Frames ?? 1);
            var captureTag = request.CaptureTag;

            if (format == "raw")
            {
                ProfilerToolSupport.CaptureRawProfilerSync(outputPath);
            }
            else
            {
                File.WriteAllText(outputPath, BuildJsonArtifact(ctx.RequestId, startedAt, durationMs, frames), System.Text.Encoding.UTF8);
            }

            var finishedAt = DateTime.UtcNow;
            var startFrame = (long)Time.frameCount;
            var endFrame = startFrame;
            if (!string.IsNullOrEmpty(captureTag))
            {
                ProfilerToolSupport.WriteCaptureMetadataSidecar(outputPath, ctx.RequestId, captureTag, startFrame, endFrame, startedAt, finishedAt);
            }
            return BuildResult(outputPath, format, durationMs, frames, startFrame, endFrame, captureTag, startedAt, finishedAt);
        }

        // 恢复 profiler 采集状态(deferred 完成或内部超时时调用)
        private static void RestoreProfiler(string previousLogFile, bool previousEnabled, bool previousBinaryLog)
        {
            Profiler.enabled = previousEnabled;
            Profiler.enableBinaryLog = previousBinaryLog;
            Profiler.logFile = previousLogFile;
        }

        // raw 采集可能未产生二进制数据(版本差异),写一个 fallback 占位文件
        private static void EnsureRawFile(string outputPath)
        {
            if (!File.Exists(outputPath))
            {
                File.WriteAllText(outputPath, "Profiler raw capture was requested; Unity did not emit binary data for this editor version." + Environment.NewLine, System.Text.Encoding.UTF8);
            }
        }

        // 构造结果 DTO(deferred OnComplete 与同步 Execute 共享)
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

        // json 格式单帧采集的文件内容(状态/内存/帧摘要)
        private static string BuildJsonArtifact(string requestId, DateTime startedAt, int durationMs, int frames)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("requestId", requestId)
                .Prop("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture))
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
