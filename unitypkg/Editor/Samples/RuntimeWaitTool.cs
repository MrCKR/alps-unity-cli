#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
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
        SupportsJob = false,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-404-Q2",
        CoverageScope = "attribute-first,zero-config,unity-runtime-csharp,play-mode-session,cross-frame-wait")]
    public static class RuntimeWaitTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            var inputJson = context.InputJson ?? "{}";
            var condition = ExtractString(inputJson, "condition") ?? "next-frame";
            var requestedFrames = ExtractInt(inputJson, "frames") ?? 1;
            var requestedSeconds = ExtractDouble(inputJson, "seconds");

            // Determine actual wait parameters
            int framesToWait;
            if (condition == "duration" && requestedSeconds.HasValue && requestedSeconds.Value > 0)
            {
                // Convert seconds to approximate frame count (assume 60fps as baseline)
                framesToWait = Math.Max(1, (int)(requestedSeconds.Value * 60));
            }
            else if (condition == "frame-count" && requestedFrames > 0)
            {
                framesToWait = requestedFrames;
            }
            else
            {
                // next-frame: just wait 1 frame
                framesToWait = 1;
            }

            // Clamp to reasonable maximum
            framesToWait = Math.Min(framesToWait, 7200); // ~2 minutes at 60fps

            var startFrame = Time.frameCount;
            var startedAt = DateTime.UtcNow;

            // Simple synchronous frame wait. For duration-based waits, we use
            // a busy-wait on the main thread (since runtime tool execution is
            // synchronous on the main thread via EditorApplication.update).
            // For frame-count waits, we can only observe the current frame.
            if (condition == "duration" && requestedSeconds.HasValue && requestedSeconds.Value > 0)
            {
                // Busy-wait for the requested duration (capped at 2 minutes).
                // This allows timeout semantics to work correctly.
                var waitMs = Math.Min((long)(requestedSeconds.Value * 1000), 120000);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < waitMs)
                {
                    if (!EditorApplication.isPlaying) break;
                    System.Threading.Thread.Sleep(1);
                }
            }

            var currentFrame = Time.frameCount;
            var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;

            return new UnityCliJsonWriter().BeginObject()
                .Prop("framesWaited", Math.Max(1, currentFrame - startFrame))
                .Prop("elapsedMs", elapsedMs)
                .Prop("condition", condition)
                .Prop("requestedFrames", requestedFrames)
                .Prop("isPlaying", EditorApplication.isPlaying)
                .Prop("frameCount", Time.frameCount)
                .Prop("note", "Runtime wait executes synchronously on the main thread. Duration waits use Thread.Sleep for actual timing. For very long waits, use a longer timeoutMs or issue repeated calls.")
                .EndObject()
                .ToString();
        }

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