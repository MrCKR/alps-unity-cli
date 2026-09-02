#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// screenshot.screen — 用 ScreenCapture 抓最终合成画面（含屏幕空间 UI）到文件。
    /// </summary>
    public class ScreenshotScreenTool : IUnityCliTool<ScreenshotScreenTool.Request, ScreenshotScreenTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Path { get; set; }
            public bool Overwrite { get; set; }
            public bool InlineBase64 { get; set; }
            [UnityCliRange(1L, 4L)] public int? SuperSize { get; set; }
            public ScreenshotBudgetInput Limits { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result : ScreenshotCaptureResult { }

        public ToolDescriptor Describe() => new()
        {
            Name = "screenshot.screen",
            Summary = "Capture the final composited screen including screen-space UI to a PNG file.",
            Description = "Capture the final composited frame including screen-space UI. superSize is 1..4 and the resulting Game View dimensions must each be 1..8192 with at most 16777216 pixels. Before ScreenCapture, the plan must fit default ceilings of 33554432 total pixels, 128 MiB temporary memory and 64 MiB artifacts (public maxima 67108864, 256 MiB and 256 MiB). The file is produced at frame end; available in Edit and Play Mode but most reliable in Play Mode.",
            Group = "screenshot",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Capture the final screen with UI", Input = new Request { Path = "Assets/Shots/screen.png" } } }
        };

        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var outputPath = ScreenshotToolSupport.ResolveScreenshotOutputPath(request.Path);
            if (!outputPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_FORMAT_UNSUPPORTED", "screenshot.screen only writes .png output.", "Pass a .png output path.");
            if (File.Exists(outputPath) && !request.Overwrite)
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + outputPath, "Pass overwrite=true or choose a different output path.");
            var superSize = request.SuperSize ?? 1;
            var width = checked(Screen.width * superSize);
            var height = checked(Screen.height * superSize);
            var captureBudget = ScreenshotToolSupport.PrepareCaptureBudget(width, height, 1, false, request.Limits, ctx.UseReadBudget(request.Budget));
            ctx.CommitEffect();
            ScreenshotToolSupport.CreateScreenshotDirectory(outputPath);
            captureBudget.BeginCapture(width, height);

            // ScreenCapture 抓最终合成画面（含屏幕空间 UI），在帧末异步落盘。
            ScreenCapture.CaptureScreenshot(outputPath, superSize);

            var written = await ctx.Wait.Until(() => File.Exists(outputPath) && new FileInfo(outputPath).Length > 0, TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct);
            ctx.ReadBudget.ResumeAfterWait();
            if (!written)
                throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "ScreenCapture did not write the screenshot before the deadline.", "Retry with a larger timeout, or use screenshot.game-view for camera-only capture.");

            ScreenshotArtifactProof artifact;
            try
            {
                captureBudget.ChargeArtifact(new FileInfo(outputPath).Length);
                artifact = ScreenshotToolSupport.VerifyScreenshotArtifact(outputPath, "png", captureBudget);
            }
            catch
            {
                try { File.Delete(outputPath); } catch { }
                throw;
            }
            var dimensions = ReadPngDimensions(outputPath);
            var result = new Result
            {
                ScreenshotPath = outputPath, ArtifactPath = outputPath, Source = "screen",
                SizeBytes = artifact.SizeBytes, Sha256 = artifact.Sha256, CapturePlan = captureBudget.Plan
            };
            if (request.InlineBase64 && artifact.SizeBytes <= 512 * 1024)
            {
                var bytes = File.ReadAllBytes(outputPath);
                ScreenshotToolSupport.MaybeAppendInline(result, bytes, (int)dimensions.width, (int)dimensions.height, "image/png", true);
            }
            return result;
        }

        // 从落盘 PNG 的 IHDR 读取实际宽高（大端），用于报告 Game View 实际分辨率。
        private static (long width, long height) ReadPngDimensions(string path)
        {
            try
            {
                var bytes = new byte[24];
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Read(bytes, 0, bytes.Length) < bytes.Length) return (0, 0);
                }
                if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47)
                {
                    long width = ((long)bytes[16] << 24) | ((long)bytes[17] << 16) | ((long)bytes[18] << 8) | bytes[19];
                    long height = ((long)bytes[20] << 24) | ((long)bytes[21] << 16) | ((long)bytes[22] << 8) | bytes[23];
                    return (width, height);
                }
            }
            catch { /* 尺寸读取失败返回 0，artifact proof 仍由 VerifyScreenshotArtifact 保证 */ }
            return (0, 0);
        }
    }
}
#endif
