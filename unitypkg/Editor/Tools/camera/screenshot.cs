#if UNITY_EDITOR
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// camera.screenshot — 从指定 Camera 渲染 PNG/JPG 截图到文件。
    /// </summary>
    public class CameraScreenshotTool : IUnityCliTool<CameraScreenshotTool.Request, CameraCapture>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Path { get; set; }
            public string Output { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public string Format { get; set; }
            public bool Transparent { get; set; }
            public int? Quality { get; set; }
            public bool Overwrite { get; set; }
            public bool InlineBase64 { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "camera.screenshot",
            Summary = "Render a PNG/JPG artifact from one specific Camera; use screenshot.* for view-based captures.",
            Description = "Render a screenshot from a specific Camera into a PNG/JPG artifact file. Defaults return artifact paths only; inlineBase64 must be explicit and is omitted when the image exceeds the bridge inline limit. Restores camera render state after capture.",
            Group = "camera",
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
            var camera = CameraToolSupport.ResolveCamera(target);
            if (camera == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "Camera not found: " + target, "Run tools run camera.list, then retry with a camera name, hierarchy path, or instance id from the result.");
            }
            var outputPath = CameraToolSupport.ResolveScreenshotOutputPath(request.Path ?? request.Output);
            var format = CameraToolSupport.NormalizeFormat(request.Format, outputPath);
            var width = CameraToolSupport.ClampScreenshotDimension(request.Width ?? (camera.pixelWidth > 0 ? camera.pixelWidth : 1280), "width");
            var height = CameraToolSupport.ClampScreenshotDimension(request.Height ?? (camera.pixelHeight > 0 ? camera.pixelHeight : 720), "height");
            var quality = Mathf.Clamp(request.Quality ?? 95, 1, 100);
            ctx.CommitEffect();
            return new System.Threading.Tasks.ValueTask<CameraCapture>(CameraToolSupport.CaptureCamera(camera, width, height, request.Transparent, quality, outputPath, format, request.Overwrite, request.InlineBase64));
        }
    }
}
#endif
