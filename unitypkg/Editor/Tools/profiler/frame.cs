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
    public class ProfilerFrameTool : IUnityCliTool<ProfilerFrameTool.Request, ProfilerFrameTool.Result>
    {
        public sealed class Request { public string Module { get; set; } = "CPU Usage"; }
        public sealed class Result { public ProfilerState Profiler { get; set; } public ProfilerFrameInfo Frame { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.frame", Summary = "Read current frame count, timing, module diagnostics and FrameTimingManager CPU/GPU fields.",
            Description = "Capture FrameTimingManager timing, then return current frame count, selected profiler module diagnostics, and CPU/GPU timing fields.",
            Group = "profiler",DestructiveHint = false, SideEffects = new[] { "frame-timing-capture" }, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read frame timing", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ctx.CommitEffect();
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
            Profiler = ProfilerToolSupport.BuildProfilerState(),
            Frame = ProfilerToolSupport.BuildProfilerFrame(request.Module ?? "CPU Usage")
            });
        }
    }
}
#endif
