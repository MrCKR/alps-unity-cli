#if UNITY_EDITOR
using System.IO;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// screenshot.from-camera — 从指定 Camera 渲染 PNG/JPG 截图到文件。
    /// </summary>
    public class ScreenshotFromCameraTool : IUnityCliTool<ScreenshotFromCameraTool.Request, CameraCapture>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Path { get; set; }
            public string Output { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Width { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Height { get; set; }
            public string Format { get; set; }
            public bool Transparent { get; set; }
            [UnityCliRange(1L, 100L)] public int? Quality { get; set; }
            public bool Overwrite { get; set; }
            public bool InlineBase64 { get; set; }
            public ScreenshotBudgetInput Limits { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "screenshot.from-camera",
            Summary = "Render a PNG/JPG artifact from one specific Camera; use screenshot.* for view-based captures.",
            Description = "Render one Camera to PNG/JPG. Before the first effect, dimensions must be 1..8192 with at most 16777216 pixels, and the aggregate plan must fit default ceilings of 33554432 total pixels, 128 MiB temporary memory and 64 MiB artifacts (public maxima 67108864, 256 MiB and 256 MiB). limits may only tighten or raise within those maxima; zero, negative and excessive values fail before capture. The texture and Camera state are released/restored in finally. Available in Edit and Play Mode with session render effects.",
            Group = "screenshot",
            DestructiveHint = true,
            SideEffects = new[] { "artifact-write", "render-texture" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Capture from Main Camera", Input = new Request { Target = "Main Camera", Path = "Artifacts/camera-main.png", Width = 640, Height = 360, Overwrite = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<CameraCapture> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var target = request.Target;
            if (string.IsNullOrEmpty(target))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "Camera screenshot requires a target Camera.", "Pass a Camera name, hierarchy path or instance id. Run tools run camera.list before retrying.");
            }
            var requestBudget = ctx.UseReadBudget(request.Budget);
            var camera = CameraToolSupport.ResolveCamera(target, requestBudget);
            if (camera == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "Camera not found: " + target, "Run tools run camera.list, then retry with a camera name, hierarchy path, or instance id from the result.");
            }
            var outputPath = CameraToolSupport.ResolveScreenshotOutputPath(request.Path ?? request.Output);
            var format = CameraToolSupport.NormalizeFormat(request.Format, outputPath);
            var width = CameraToolSupport.ClampScreenshotDimension(request.Width ?? (camera.pixelWidth > 0 ? camera.pixelWidth : 1280), "width");
            var height = CameraToolSupport.ClampScreenshotDimension(request.Height ?? (camera.pixelHeight > 0 ? camera.pixelHeight : 720), "height");
            var quality = request.Quality ?? 95;
            if (File.Exists(outputPath) && !request.Overwrite)
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + outputPath, "Pass overwrite=true or choose a different output path.");
            var captureBudget = ScreenshotToolSupport.PrepareCaptureBudget(width, height, 1, false, request.Limits, requestBudget);
            ctx.CommitEffect();
            return new System.Threading.Tasks.ValueTask<CameraCapture>(CameraToolSupport.CaptureCamera(camera, width, height, request.Transparent, quality, outputPath, format, request.Overwrite, request.InlineBase64, captureBudget));
        }
    }
}
#endif
