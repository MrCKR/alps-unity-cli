#if UNITY_EDITOR
using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// Built-in runtime wait tool. Waits for a specified number of frames or a
    /// duration in seconds while in Play Mode. This is the minimal cross-frame
    /// async primitive required for runtime tool orchestration.
    ///
    /// 特殊处理:本工具不走 IUnityCliTool 泛型接口(同步 Execute(Request):Result 契约),
    /// 因为它需要返回 deferred-wait marker 字符串,由 RuntimeExecutor 拦截并启动
    /// EditorApplication.update 回调式非阻塞等待。改用直接注册(RegisterProjectTool),
    /// 保留 [UnityCliTool] attribute 已移除(Stage 3 零 attribute 残留要求)。
    /// 设计依据:.plan/自定义工具定义规范.md(例外:deferred 机制不兼容同步契约)。
    /// </summary>
    public static class RuntimeWaitTool
    {
        /// <summary>
        /// Marker returned by Execute when a multi-frame or duration wait is needed.
        /// The RuntimeExecutor recognizes this and initiates a deferred EditorApplication.update
        /// callback-based wait that does not block the Unity main thread.
        /// </summary>
        public static readonly string DeferredWaitMarker = "\0RUNTIME_WAIT_DEFERRED";

        /// <summary>
        /// Deserialized wait parameters, passed from Execute to RuntimeExecutor
        /// so the deferred wait can be configured without blocking the main thread.
        /// </summary>
        public sealed class WaitRequest
        {
            public string Condition;
            public int RequestedFrames;
            public int FramesToWait;
            public long RequestedDurationMs;
            public int StartFrame;
            public DateTime StartedAt;
        }

        public static string Execute(UnityCliProjectToolContext context)
        {
            var inputJson = context.InputJson ?? "{}";
            var condition = ExtractString(inputJson, "condition") ?? "next-frame";
            var requestedFrames = ExtractInt(inputJson, "frames") ?? 1;
            var requestedSeconds = ExtractDouble(inputJson, "seconds");

            // Determine actual wait parameters
            int framesToWait;
            long requestedDurationMs = 0;
            if (condition == "duration" && requestedSeconds.HasValue && requestedSeconds.Value > 0)
            {
                // Convert seconds to approximate frame count (assume 60fps as baseline)
                framesToWait = Math.Max(1, (int)(requestedSeconds.Value * 60));
                requestedDurationMs = Math.Min((long)(requestedSeconds.Value * 1000), 120000);
            }
            else if (condition == "frame-count" && requestedFrames > 0)
            {
                framesToWait = requestedFrames;
                requestedDurationMs = framesToWait * 17; // approximate at ~60fps
            }
            else
            {
                // next-frame: just return immediately — no blocking wait needed
                var startFrame = Time.frameCount;
                var startedAt = DateTime.UtcNow;
                var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                return new UnityCliJsonWriter().BeginObject()
                    .Prop("framesWaited", 1)
                    .Prop("elapsedMs", elapsedMs)
                    .Prop("condition", condition)
                    .Prop("requestedFrames", requestedFrames)
                    .Prop("isPlaying", EditorApplication.isPlaying)
                    .Prop("frameCount", Time.frameCount)
                    .EndObject()
                    .ToString();
            }

            // Clamp to reasonable maximum
            framesToWait = Math.Min(framesToWait, 7200); // ~2 minutes at 60fps

            // Multi-frame or duration wait: return a deferred marker so the
            // RuntimeExecutor can initiate a non-blocking EditorApplication.update
            // callback-based wait. This replaces the previous Thread.Sleep
            // busy-wait which blocked the Unity editor main thread.
            LastWaitRequest = new WaitRequest
            {
                Condition = condition,
                RequestedFrames = requestedFrames,
                FramesToWait = framesToWait,
                RequestedDurationMs = requestedDurationMs,
                StartFrame = Time.frameCount,
                StartedAt = DateTime.UtcNow
            };

            return DeferredWaitMarker;
        }

        /// <summary>
        /// The most recent WaitRequest from Execute, used by RuntimeExecutor
        /// to configure the deferred wait parameters.
        /// </summary>
        public static WaitRequest LastWaitRequest;

        private static string ExtractString(string json, string name)
        {
            var pattern = "\"" + name + "\"\\s*:\\s*\"((?:\\\\.|[^\\\"])*)\"";
            var match = System.Text.RegularExpressions.Regex.Match(json ?? string.Empty, pattern);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static int? ExtractInt(string json, string name)
        {
            var pattern = "\"" + name + "\"\\s*:\\s*(-?\\d+)";
            var match = System.Text.RegularExpressions.Regex.Match(json ?? string.Empty, pattern);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
            return null;
        }

        private static double? ExtractDouble(string json, string name)
        {
            var pattern = "\"" + name + "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)";
            var match = System.Text.RegularExpressions.Regex.Match(json ?? string.Empty, pattern);
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
            return null;
        }
    }
}
#endif
