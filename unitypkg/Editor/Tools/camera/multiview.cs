#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// camera.multiview — 为每个命名 Camera 捕获一张 PNG/JPG 截图到输出目录,用于多视角审阅。
    /// </summary>
    public class CameraMultiviewTool : IUnityCliTool<CameraMultiviewTool.Request, CameraMultiviewTool.Result>
    {
        public sealed class Request
        {
            public string Targets { get; set; }
            public string Output { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public string Format { get; set; }
        }

        public sealed class Result
        {
            public List<CameraCapture> Captures { get; set; }
            public long Count { get; set; }
            public List<CameraPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "camera.multiview",
            Summary = "Capture one PNG/JPG artifact per named Camera into an output directory for multiview review.",
            Description = "Capture one PNG/JPG artifact per named Camera into an output directory for side-by-side scene review. Returns artifact paths instead of inline image bytes; keep target count and resolution small for token and disk efficiency.",
            Group = "camera",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "artifact-write", "render-texture" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Capture from two cameras", Input = new Request { Targets = "Main Camera,UnityCliAlignmentCamera", Output = "Artifacts/camera-multiview", Width = 320, Height = 180, Format = "png" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(request.Targets))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "camera.multiview requires targets.", "Run tools run camera.list, then pass comma-separated camera names, hierarchy paths, or instance ids.");
            }
            var names = request.Targets.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
            var cameras = new List<Camera>();
            foreach (var name in names)
            {
                var camera = CameraToolSupport.ResolveCamera(name.Trim());
                if (camera == null)
                {
                    throw new UnityCliBridge.BridgeCommandException("E_CAMERA_NOT_FOUND", "Camera not found: " + name.Trim(), "Run tools run camera.list, then retry with camera names, hierarchy paths, or instance ids from the result.");
                }
                cameras.Add(camera);
            }
            var outputDir = string.IsNullOrEmpty(request.Output) ? "Screenshots" : request.Output;
            var width = request.Width ?? 1280;
            var height = request.Height ?? 720;
            var format = string.IsNullOrEmpty(request.Format) ? "png" : request.Format.ToLowerInvariant();
            if (format == "jpeg") format = "jpg";
            var captures = new List<CameraCapture>();
            var effects = new List<CameraPlannedEffect>();
            foreach (var camera in cameras)
            {
                var outputPath = Path.Combine(outputDir, camera.name + "." + format);
                var capture = CameraToolSupport.CaptureCamera(camera, width, height, false, 95, outputPath, format, true, false);
                captures.Add(capture);
                effects.Add(new CameraPlannedEffect { Type = "camera.multiview", Property = "capture", Path = outputPath });
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Captures = captures, Count = captures.Count, PlannedEffects = effects });
        }
    }
}
#endif
