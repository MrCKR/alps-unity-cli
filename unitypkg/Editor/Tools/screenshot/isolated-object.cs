#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// screenshot.isolated-object — 在临时相机中隔离渲染单个对象。
    /// </summary>
    public class ScreenshotIsolatedObjectTool : IUnityCliTool<ScreenshotIsolatedObjectTool.Request, ScreenshotIsolatedObjectTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            [UnityCliRequired] public string Path { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Width { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Height { get; set; }
            public bool Orthographic { get; set; } = true;
            public float? Size { get; set; }
            public bool Overwrite { get; set; }
            public bool InlineBase64 { get; set; }
            public ScreenshotBudgetInput Limits { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result : ScreenshotCaptureResult { }

        public ToolDescriptor Describe() => new()
        {
            Name = "screenshot.isolated-object",
            Summary = "Render a single GameObject in isolation with a temporary camera to a PNG/JPG file.",
            Description = "Render one GameObject with a temporary Camera. Dimensions are 1..8192 with at most 16777216 pixels. Before creating the Camera or textures, the request must fit default ceilings of 33554432 total pixels, 128 MiB temporary memory and 64 MiB artifacts (public maxima 67108864, 256 MiB and 256 MiB). The temporary Camera and texture are released in finally. Editor-only because it creates a temporary scene object.",
            Group = "screenshot",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Isolate an object", Input = new Request { Target = "Player", Path = "Assets/Shots/player.png" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var requestBudget = ctx.UseReadBudget(request.Budget);
            var go = UnityCliBridge.ObjectRefResolver.FindGameObjectByExactPath(request.Target, requestBudget);
            if (go == null)
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "screenshot.isolated-object requires a target GameObject.", "Pass --target <path>.");
            var outputPath = ScreenshotToolSupport.ResolveScreenshotOutputPath(request.Path);
            if (File.Exists(outputPath) && !request.Overwrite)
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Output exists: " + outputPath);
            var width = request.Width ?? 512;
            var height = request.Height ?? 512;
            if (request.Size.HasValue && (float.IsNaN(request.Size.Value) || float.IsInfinity(request.Size.Value) || request.Size.Value <= 0f))
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_LIMIT_INVALID", "size must be finite and greater than zero.", "Pass a positive finite orthographic size.");
            var captureBudget = ScreenshotToolSupport.PrepareCaptureBudget(width, height, 1, false, request.Limits, requestBudget);
            var format = ScreenshotToolSupport.NormalizeFormat(null, outputPath);
            var bounds = ScreenshotToolSupport.TargetRenderBounds(go, requestBudget);
            ctx.CommitEffect();
            ScreenshotToolSupport.CreateScreenshotDirectory(outputPath);

            GameObject temporaryCameraObject = null;
            byte[] bytes;
            ScreenshotArtifactProof artifact;
            try
            {
                temporaryCameraObject = new GameObject("__unitycli_isolated_cam__");
                var camera = temporaryCameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 1f);
                camera.orthographic = request.Orthographic;
                camera.orthographicSize = request.Size ?? 2f;
                temporaryCameraObject.transform.position = bounds.center + new Vector3(0, 0, -5);
                temporaryCameraObject.transform.LookAt(bounds.center);
                var texture = ScreenshotToolSupport.RenderScreenshotTexture(camera, width, height, captureBudget);
                try
                {
                    bytes = format == "jpg" ? texture.EncodeToJPG(95) : texture.EncodeToPNG();
                    artifact = ScreenshotToolSupport.WriteScreenshotBytes(outputPath, bytes, captureBudget);
                }
                finally { UnityEngine.Object.DestroyImmediate(texture); }
            }
            finally
            {
                if (temporaryCameraObject != null) UnityEngine.Object.DestroyImmediate(temporaryCameraObject);
            }

            var result = new Result
            {
                ScreenshotPath = outputPath, ArtifactPath = outputPath, Source = "isolated-object",
                SizeBytes = artifact.SizeBytes, Sha256 = artifact.Sha256, CapturePlan = captureBudget.Plan
            };
            ScreenshotToolSupport.MaybeAppendInline(result, bytes, width, height, format == "jpg" ? "image/jpeg" : "image/png", request.InlineBase64, captureBudget);
            return new System.Threading.Tasks.ValueTask<Result>(result);
        }
    }
}
#endif
