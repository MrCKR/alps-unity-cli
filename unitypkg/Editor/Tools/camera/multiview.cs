#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
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
            [UnityCliRange(1L, 8192L)] public int? Width { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Height { get; set; }
            public string Format { get; set; }
            public ScreenshotBudgetInput Limits { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public List<CameraCapture> Captures { get; set; }
            public long Count { get; set; }
            public List<CameraPlannedEffect> PlannedEffects { get; set; }
            public ScreenshotCapturePlan CapturePlan { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "camera.multiview",
            Summary = "Capture one PNG/JPG artifact per named Camera into an output directory for multiview review.",
            Description = "Capture 1..16 named Cameras to PNG/JPG artifacts. Each dimension is 1..8192 and each image is at most 16777216 pixels. Before the first effect, the aggregate plan must fit default ceilings of 33554432 total pixels, 128 MiB temporary memory and 64 MiB artifacts; public maxima are 67108864, 256 MiB and 256 MiB. All textures are released after each capture. Available in Edit and Play Mode with session render effects.",
            Group = "camera",
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
            if (names.Length < 1 || names.Length > 16)
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_LIMIT_INVALID", "camera.multiview targets must contain 1..16 cameras.", "Reduce the target list before retrying.");
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
            var width = CameraToolSupport.ClampScreenshotDimension(request.Width ?? 1280, "width");
            var height = CameraToolSupport.ClampScreenshotDimension(request.Height ?? 720, "height");
            var format = CameraToolSupport.NormalizeFormat(request.Format, Path.Combine(outputDir, "capture.png"));
            var captureBudget = ScreenshotToolSupport.PrepareCaptureBudget(width, height, cameras.Count, false, request.Limits, ctx.UseReadBudget(request.Budget));
            var captures = new List<CameraCapture>();
            var effects = new List<CameraPlannedEffect>();
            var plans = new List<KeyValuePair<int, string>>();
            var outputPaths = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var camera in cameras)
            {
                var outputPath = CameraToolSupport.ResolveScreenshotOutputPath(Path.Combine(outputDir, camera.name + "." + format));
                if (!outputPaths.Add(outputPath))
                    throw new UnityCliBridge.BridgeCommandException("E_MULTIVIEW_OUTPUT_CONFLICT", "Each camera.multiview target must produce a distinct artifact path.", "Use uniquely named cameras or capture targets separately.");
                plans.Add(new KeyValuePair<int, string>(camera.GetInstanceID(), outputPath));
            }
            ctx.CommitEffect();
            foreach (var plan in plans)
            {
                var camera = EditorUtility.InstanceIDToObject(plan.Key) as Camera;
                if (camera == null) throw new UnityCliBridge.BridgeCommandException("E_CAMERA_NOT_FOUND", "Camera disappeared before capture.", "Retry after the Editor is idle.");
                var capture = CameraToolSupport.CaptureCamera(camera, width, height, false, 95, plan.Value, format, true, false, captureBudget);
                if (!File.Exists(plan.Value) || new FileInfo(plan.Value).Length == 0)
                    throw new UnityCliBridge.BridgeCommandException("E_MULTIVIEW_ARTIFACT_MISSING", "Camera capture artifact was not observed: " + plan.Value, "Check the output directory and retry.");
                captures.Add(capture);
                effects.Add(new CameraPlannedEffect { Type = "camera.multiview", Property = "capture", Path = plan.Value });
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Captures = captures, Count = captures.Count, PlannedEffects = effects, CapturePlan = captureBudget.Plan });
        }
    }
}
#endif
