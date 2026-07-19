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
    public class ProfilerExportTool : IUnityCliTool<ProfilerExportTool.Request, ProfilerExportTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Path { get; set; } public string Format { get; set; } public bool Overwrite { get; set; } public string CaptureTag { get; set; } }
        public sealed class Result { public string ArtifactPath { get; set; } public string ProfilerPath { get; set; } public string Format { get; set; } public long DurationMs { get; set; } public long Frames { get; set; } public long StartFrame { get; set; } public long EndFrame { get; set; } public string CaptureTag { get; set; } public string MetadataSidecar { get; set; } public ProfilerState Profiler { get; set; } public List<ProfilerPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.export", Summary = "Export a profiler snapshot artifact in JSON or raw profiler format.",
            Description = "Export a profiler snapshot artifact in JSON or raw profiler format to the requested path. Single-frame synchronous export.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "persistent-artifact-write" }, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Export profiler JSON", Input = new Request { Path = "profile.json", Overwrite = true } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var outputPath = ResolveOutputPath(request.Path);
            if (File.Exists(outputPath) && !request.Overwrite)
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Profiler artifact already exists: " + outputPath, "Pass overwrite=true or choose a different output path.");
            ctx.CommitEffect();
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
                ProfilerToolSupport.WriteAndVerifyJsonProfilerArtifact(outputPath, BuildProfilerArtifactJson(ctx.RequestId, startedAt, 0, 1));
            }
            var finishedAt = DateTime.UtcNow;
            var elapsedMs = (long)(finishedAt - startedAt).TotalMilliseconds;
            var startFrame = (long)Time.frameCount;
            var endFrame = startFrame;
            var captureTag = request.CaptureTag;
            if (!string.IsNullOrEmpty(captureTag))
                ProfilerToolSupport.WriteCaptureMetadataSidecar(outputPath, ctx.RequestId, captureTag, startFrame, endFrame, startedAt, finishedAt);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                ArtifactPath = outputPath, ProfilerPath = outputPath, Format = format, DurationMs = elapsedMs,
                Frames = 1, StartFrame = startFrame, EndFrame = endFrame, CaptureTag = captureTag,
                MetadataSidecar = string.IsNullOrEmpty(captureTag) ? null : outputPath + ".metadata.json",
                Profiler = ProfilerToolSupport.BuildProfilerState(),
                PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.export", Path = outputPath } }
            });
        }

        private static string ResolveOutputPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_PATH_REQUIRED", "Profiler export requires a path.", "Pass --path <path>.");
            var normalized = rawPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(ProfilerToolSupport.ProjectRoot(), normalized));
        }

        private static string BuildProfilerArtifactJson(string requestId, DateTime startedAt, int durationMs, int frames)
        {
            return UnityCliSerializer.Serialize(new ProfilerArtifactFile
            {
                RequestId = requestId,
                StartedAt = startedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                DurationMs = durationMs,
                Frames = frames,
                Profiler = ProfilerToolSupport.BuildProfilerState(),
                Memory = ProfilerToolSupport.BuildProfilerMemory()
            });
        }
    }
}
#endif
