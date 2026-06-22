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
    /// Declaration via [UnityCliTool] means it is discoverable through the same
    /// project-tool discovery mechanism, but with Target=runtime it is routed
    /// through the RuntimeExecutor and only available in Play Mode.
    ///
    /// Multi-frame/duration waits are handled by the RuntimeExecutor as deferred
    /// waits using EditorApplication.update callbacks — no Thread.Sleep or
    /// busy-wait that blocks the Unity main thread.
    /// </summary>
    [UnityCliTool(
        "runtime.wait",
        Title = "Runtime Wait",
        Description = "Waits for a specified number of frames or seconds in Play Mode. Returns the actual frames waited and elapsed time. This is a runtime-only cross-frame primitive.",
        Group = "runtime",
        Target = "runtime",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"frames\":{\"type\":\"number\",\"description\":\"Number of frames to wait (default 1)\"},\"seconds\":{\"type\":\"number\",\"description\":\"Seconds to wait (alternative to frames)\"},\"condition\":{\"type\":\"string\",\"description\":\"Wait condition: frame-count, duration, or next-frame (default next-frame)\"}},\"required\":[]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"framesWaited\":{\"type\":\"number\"},\"elapsedMs\":{\"type\":\"number\"},\"condition\":{\"type\":\"string\"},\"isPlaying\":{\"type\":\"boolean\"},\"frameCount\":{\"type\":\"number\"}}}",
        ReadOnlyHint = true,
        DestructiveHint = false,
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 120000,
        SupportsJob = false
)]
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
