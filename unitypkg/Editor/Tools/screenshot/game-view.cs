#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// screenshot.game-view — 模拟 Game View 渲染所有相机到文件。
    /// </summary>
    public class ScreenshotGameViewTool : IUnityCliTool<ScreenshotGameViewTool.Request, ScreenshotGameViewTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Path { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Width { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Height { get; set; }
            public bool Overwrite { get; set; }
            public bool InlineBase64 { get; set; }
            public ScreenshotBudgetInput Limits { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public string ScreenshotPath { get; set; }
            public string ArtifactPath { get; set; }
            public string Format { get; set; }
            public long Quality { get; set; }
            public string Source { get; set; }
            public string Target { get; set; }
            public string ImageBase64 { get; set; }
            public long? ImageWidth { get; set; }
            public long? ImageHeight { get; set; }
            public string ImageMimeType { get; set; }
            public long SizeBytes { get; set; }
            public string Sha256 { get; set; }
            public ScreenshotCapturePlan CapturePlan { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "screenshot.game-view",
            Summary = "Capture a Game View screenshot by rendering all active cameras to a PNG/JPG file.",
            Description = "Render active Cameras to one camera-only PNG/JPG (screen-space overlay UI is excluded). Dimensions default to 1280x720, each is 1..8192, and per-image pixels cannot exceed 16777216. Before the first effect, the plan must fit default ceilings of 33554432 total pixels, 128 MiB temporary memory and 64 MiB artifacts; public maxima are 67108864, 256 MiB and 256 MiB. Available in Edit and Play Mode.",
            Group = "screenshot",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Capture game view", Input = new Request { Path = "Assets/Shots/game.png" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var outputPath = ScreenshotToolSupport.ResolveScreenshotOutputPath(request.Path);
            if (File.Exists(outputPath) && !request.Overwrite)
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + outputPath, "Pass overwrite=true or choose a different output path.");
            var width = request.Width ?? 1280;
            var height = request.Height ?? 720;
            var format = ScreenshotToolSupport.NormalizeFormat(null, outputPath);
            var requestBudget = ctx.UseReadBudget(request.Budget);
            if (!requestBudget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(requestBudget.ExhaustedDimension);
            var cameraCount = Camera.allCamerasCount;
            if (cameraCount > ScreenshotToolSupport.MaxCaptureCount)
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_LIMIT_INVALID", "screenshot.game-view found more than 36 active Cameras.", "Disable unrelated Cameras or capture one Camera explicitly.");
            var cameras = cameraCount > 0 ? Camera.allCameras : new Camera[0];
            foreach (var camera in cameras)
                if (!requestBudget.TryConsumeScannedRecord() || !requestBudget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(requestBudget.ExhaustedDimension);
            var captureBudget = ScreenshotToolSupport.PrepareCaptureBudget(width, height, Math.Max(1, cameraCount), false, request.Limits, requestBudget);
            ctx.CommitEffect();
            ScreenshotToolSupport.CreateScreenshotDirectory(outputPath);

            RenderTexture renderTexture = null;
            Texture2D texture = null;
            byte[] bytes;
            ScreenshotArtifactProof artifact;
            var previousActive = RenderTexture.active;
            try
            {
                captureBudget.BeginCapture(width, height);
                renderTexture = new RenderTexture(width, height, 24);
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                RenderTexture.active = renderTexture;
                foreach (var cam in cameras)
                {
                    if (!cam.gameObject.activeInHierarchy) continue;
                    var prevTarget = cam.targetTexture;
                    try { cam.targetTexture = renderTexture; cam.Render(); }
                    finally { cam.targetTexture = prevTarget; }
                }
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                bytes = format == "jpg" ? texture.EncodeToJPG(95) : texture.EncodeToPNG();
                artifact = ScreenshotToolSupport.WriteScreenshotBytes(outputPath, bytes, captureBudget);
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                if (renderTexture != null) { renderTexture.Release(); UnityEngine.Object.DestroyImmediate(renderTexture); }
            }

            var result = new Result
            {
                ScreenshotPath = outputPath, ArtifactPath = outputPath, Format = format,
                Quality = 95, Source = "game-view", Target = "Camera.main/allCameras",
                SizeBytes = artifact.SizeBytes, Sha256 = artifact.Sha256, CapturePlan = captureBudget.Plan
            };
            MaybeAppendInline(result, bytes, width, height, format == "jpg" ? "image/jpeg" : "image/png", request.InlineBase64, captureBudget);
            return new System.Threading.Tasks.ValueTask<Result>(result);
        }

        private static void MaybeAppendInline(Result result, byte[] bytes, int width, int height, string mimeType, bool inlineBase64, ScreenshotCaptureBudget budget)
        {
            string encoded;
            if (inlineBase64 && bytes != null && bytes.Length <= 512 * 1024 && budget.TryCreateInlineBase64(bytes, out encoded))
            {
                result.ImageBase64 = encoded;
                result.ImageWidth = width;
                result.ImageHeight = height;
                result.ImageMimeType = mimeType;
            }
        }
    }
}
#endif
