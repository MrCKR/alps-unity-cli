#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
            var requestBudget = ctx.UseReadBudget(request.Budget);
            var superSize = request.SuperSize ?? 1;
            if (!requestBudget.TryConsumeGetter() || !requestBudget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(requestBudget.ExhaustedDimension);
            var width = checked(Screen.width * superSize);
            var height = checked(Screen.height * superSize);
            var captureBudget = ScreenshotToolSupport.PrepareCaptureBudget(width, height, 1, false, request.Limits, requestBudget);
            ctx.CommitEffect();
            ScreenshotToolSupport.CreateScreenshotDirectory(outputPath);
            captureBudget.BeginCapture(width, height);
            ScreenshotArtifactProof artifact;
            using (var publication = new PendingCapturePublication(outputPath))
            {
                // ScreenCapture 只写本请求的唯一 pending；旧 final 与并发请求都不能满足 fresh-write proof。
                publication.BeginWrite();
                ScreenCapture.CaptureScreenshot(publication.PendingPath, superSize);
                var written = await ctx.Wait.Until(
                    () => File.Exists(publication.PendingPath) && new FileInfo(publication.PendingPath).Length > 0,
                    TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)),
                    ct);
                ctx.ReadBudget.ResumeAfterWait();
                if (!written)
                {
                    publication.TryFault();
                    throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "ScreenCapture did not write the screenshot before the deadline.", "Retry with a larger timeout, or use screenshot.game-view for camera-only capture.");
                }

                captureBudget.ChargeArtifact(new FileInfo(publication.PendingPath).Length);
                artifact = ScreenshotToolSupport.VerifyScreenshotArtifact(publication.PendingPath, "png", captureBudget);
                publication.Publish();
                artifact.Path = outputPath;
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
                ScreenshotToolSupport.MaybeAppendInline(result, bytes, (int)dimensions.width, (int)dimensions.height, "image/png", true, captureBudget);
            }
            return result;
        }

        private static readonly object PublicationLock = new object();
        private static readonly object PendingLock = new object();
        private static readonly HashSet<string> LivePending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LateWriterPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int PendingOwnerLimit = 8;
        private const int MaximumObservedPendingFiles = 1024;

        /// <summary>每次 capture 都有唯一同目录 pending；迟到 writer 的 owner 不复用，当前启动最多保留8个。</summary>
        internal sealed class PendingCapturePublication : IDisposable
        {
            private readonly string outputPath;
            private int terminal;
            private int writerStarted;

            internal PendingCapturePublication(string outputPath)
            {
                if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Output path is required.", "outputPath");
                this.outputPath = outputPath;
                SweepObservedPending(Path.GetDirectoryName(outputPath));
                PendingPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".unitycli.pending";
                lock (PendingLock)
                {
                    if (LivePending.Count + LateWriterPending.Count >= PendingOwnerLimit)
                        throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_PENDING_SLOTS_BUSY", "All 8 screenshot pending owners are waiting for capture or late-writer cleanup.", "Wait for prior ScreenCapture writes to settle, then retry.");
                    LivePending.Add(PendingPath);
                }
                try { DeletePendingStrict(PendingPath); }
                catch
                {
                    lock (PendingLock) LivePending.Remove(PendingPath);
                    throw;
                }
            }

            internal string PendingPath { get; private set; }

            internal void BeginWrite()
            {
                if (Volatile.Read(ref terminal) != 0) throw new InvalidOperationException("Screenshot pending publication already reached a terminal state.");
                Volatile.Write(ref writerStarted, 1);
            }

            internal void Publish()
            {
                if (Interlocked.CompareExchange(ref terminal, 1, 0) != 0)
                    throw new InvalidOperationException("Screenshot pending publication already reached a terminal state.");
                try { PublishPending(PendingPath, outputPath); }
                catch
                {
                    DeletePendingStrict(PendingPath);
                    throw;
                }
                finally
                {
                    lock (PendingLock) LivePending.Remove(PendingPath);
                }
            }

            internal bool TryFault()
            {
                if (Interlocked.CompareExchange(ref terminal, 2, 0) != 0) return false;
                DeletePendingStrict(PendingPath);
                lock (PendingLock)
                {
                    LivePending.Remove(PendingPath);
                    if (Volatile.Read(ref writerStarted) != 0) LateWriterPending.Add(PendingPath);
                }
                return true;
            }

            public void Dispose()
            {
                TryFault();
            }
        }

        /// <summary>只观察最多1024个产品 pending；迟到文件出现后删除并释放对应 owner。</summary>
        private static void SweepObservedPending(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
            var observed = 0;
            foreach (var path in Directory.EnumerateFiles(directory, "*.unitycli.pending", SearchOption.TopDirectoryOnly))
            {
                observed++;
                if (observed > MaximumObservedPendingFiles)
                    throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_PENDING_SCAN_LIMIT", "Screenshot pending cleanup exceeded the 1024-file observation limit.", "Remove stale *.unitycli.pending files from the output directory and retry.");
                var lateWriter = false;
                var delete = false;
                lock (PendingLock)
                {
                    lateWriter = LateWriterPending.Contains(path);
                    delete = lateWriter || !LivePending.Contains(path);
                }
                if (!delete) continue;
                DeletePendingStrict(path);
                if (lateWriter)
                {
                    lock (PendingLock) LateWriterPending.Remove(path);
                }
            }
        }

        /// <summary>同目录唯一 pending 在进程内串行原子 rename；失败时保留原 final。</summary>
        internal static void PublishPending(string pendingPath, string outputPath)
        {
            lock (PublicationLock)
            {
                try
                {
                    if (File.Exists(outputPath)) File.Replace(pendingPath, outputPath, null, true);
                    else File.Move(pendingPath, outputPath);
                }
                catch (Exception exception)
                {
                    throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_PUBLISH_FAILED", "Failed to atomically publish screenshot output: " + exception.Message, "Inspect output permissions and retry after the pending file is clean.");
                }
            }
        }

        /// <summary>pending删除失败必须显式终止，不能积累或被后续capture复用。</summary>
        internal static void DeletePendingStrict(string pendingPath)
        {
            try
            {
                if (File.Exists(pendingPath)) File.Delete(pendingPath);
                if (File.Exists(pendingPath)) throw new IOException("pending path still exists after deletion");
            }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_PENDING_CLEANUP_FAILED", "Failed to clean screenshot pending output: " + exception.Message, "Remove the reported pending file after checking its exact path, then retry.");
            }
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
