#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// runtime.wait — Play Mode 下等待若干帧或若干秒的跨帧原语。
    /// 实现 IUnityCliDeferredTool&lt;,&gt;:frame-count/duration 走 deferred(每帧轮询),next-frame 走同步 Execute。
    /// 设计依据:.plan/重构设计总文档.md "异步工具:deferred 子接口" 节(废除旧 marker 特殊处理,统一接口)。
    /// </summary>
    public class RuntimeWaitTool : IUnityCliDeferredTool<RuntimeWaitTool.Request, RuntimeWaitTool.Result>
    {
        /// <summary>等待参数。condition 默认 next-frame;frames/seconds 二选一。</summary>
        public sealed class Request
        {
            /// <summary>等待帧数(默认 1,condition=frame-count 时生效)。</summary>
            public int? Frames { get; set; }

            /// <summary>等待秒数(condition=duration 时生效)。</summary>
            public double? Seconds { get; set; }

            /// <summary>等待条件:frame-count / duration / next-frame(默认 next-frame)。</summary>
            public string Condition { get; set; }
        }

        /// <summary>等待结果:实际等待帧数、耗时、当前帧与 Play Mode 状态。</summary>
        public sealed class Result
        {
            public long FramesWaited { get; set; }
            public long ElapsedMs { get; set; }
            public string Condition { get; set; }
            public long RequestedFrames { get; set; }
            public bool IsPlaying { get; set; }
            public long FrameCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "runtime.wait",
            Summary = "Wait for a number of frames or seconds in Play Mode (cross-frame deferred primitive).",
            Description = "Waits for a specified number of frames (condition=frame-count) or seconds (condition=duration) in Play Mode via non-blocking frame polling; condition=next-frame returns immediately. Returns the actual frames waited and elapsed time. runtime-only: requires an active Play Mode session.",
            Group = "runtime", Version = "1.0.0", DestructiveHint = false, Target = "runtime", PlayModeSupport = "runtime-only",
            CapabilityType = "project-read",
            DefaultTimeoutMs = 10000, MaxTimeoutMs = 120000,
            Examples = new[] { new ToolExample { Description = "Wait 60 frames", Input = new Request { Frames = 60, Condition = "frame-count" } } }
        };

        /// <summary>
        /// 尝试挂起为跨帧等待。frame-count/duration 返回 true + completion(next-frame 返回 false 走同步 Execute)。
        /// completion.IsComplete 同时处理 Play Mode 结束(立即完成,停止等待)。
        /// </summary>
        public bool TryDefer(Request request, UnityCliProjectToolContext ctx, out DeferredCompletion<Result> completion)
        {
            var condition = string.IsNullOrEmpty(request.Condition) ? "next-frame" : request.Condition;
            var requestedFrames = request.Frames ?? 1;
            var requestedSeconds = request.Seconds;

            if (condition == "frame-count" && requestedFrames > 0)
            {
                // 帧数等待:上限 7200 帧(~2 分钟 @60fps)
                var framesToWait = Math.Min(requestedFrames, 7200);
                var startFrame = Time.frameCount;
                var startedAt = DateTime.UtcNow;
                completion = new DeferredCompletion<Result>
                {
                    TimeoutMs = 120000,
                    IsComplete = () => !EditorApplication.isPlaying || (Time.frameCount - startFrame) >= framesToWait,
                    OnComplete = () => BuildResult(condition, requestedFrames, startFrame, startedAt),
                    TimeoutErrorCode = "E_RUNTIME_TIMEOUT",
                    TimeoutErrorMessage = "Runtime wait did not complete before the timeout.",
                    TimeoutNextAction = "Reduce the requested frames or seconds, or increase the timeout."
                };
                return true;
            }

            if (condition == "duration" && requestedSeconds.HasValue && requestedSeconds.Value > 0)
            {
                // 时长等待:上限 120 秒
                var requestedDurationMs = Math.Min((long)(requestedSeconds.Value * 1000), 120000);
                var startFrame = Time.frameCount;
                var startedAt = DateTime.UtcNow;
                completion = new DeferredCompletion<Result>
                {
                    TimeoutMs = (int)Math.Min(requestedDurationMs + 5000, 120000),
                    IsComplete = () => !EditorApplication.isPlaying || (long)(DateTime.UtcNow - startedAt).TotalMilliseconds >= requestedDurationMs,
                    OnComplete = () => BuildResult(condition, requestedFrames, startFrame, startedAt),
                    TimeoutErrorCode = "E_RUNTIME_TIMEOUT",
                    TimeoutErrorMessage = "Runtime wait did not complete before the timeout.",
                    TimeoutNextAction = "Reduce the requested frames or seconds, or increase the timeout."
                };
                return true;
            }

            // next-frame 或无效参数:走同步 Execute 立即返回
            completion = null;
            return false;
        }

        /// <summary>同步路径:next-frame 立即返回当前帧状态(TryDefer 返回 false 时框架调用)。</summary>
        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var condition = string.IsNullOrEmpty(request.Condition) ? "next-frame" : request.Condition;
            var requestedFrames = request.Frames ?? 1;
            var startFrame = Time.frameCount;
            var startedAt = DateTime.UtcNow;
            return BuildResult(condition, requestedFrames, startFrame, startedAt);
        }

        // 构造等待结果。framesWaited 至少 1;Play Mode 结束时反映退出状态。
        private static Result BuildResult(string condition, long requestedFrames, int startFrame, DateTime startedAt)
        {
            return new Result
            {
                FramesWaited = Math.Max(1, Time.frameCount - startFrame),
                ElapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                Condition = condition,
                RequestedFrames = requestedFrames,
                IsPlaying = EditorApplication.isPlaying,
                FrameCount = Time.frameCount
            };
        }
    }
}
#endif
