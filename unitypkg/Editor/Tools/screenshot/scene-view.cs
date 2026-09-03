#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// screenshot.scene-view — 捕获激活 SceneView 到文件。
    /// </summary>
    public class ScreenshotSceneViewTool : IUnityCliTool<ScreenshotSceneViewTool.Request, ScreenshotSceneViewTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Path { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Width { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Height { get; set; }
            public bool Transparent { get; set; }
            [UnityCliRange(1L, 100L)] public int? Quality { get; set; }
            public bool Overwrite { get; set; }
            public bool InlineBase64 { get; set; }
            public ScreenshotBudgetInput Limits { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result : ScreenshotCaptureResult { }

        public ToolDescriptor Describe() => new()
        {
            Name = "screenshot.scene-view",
            Summary = "Capture the active SceneView camera to a PNG/JPG file.",
            Description = "Capture the active SceneView camera to PNG/JPG. Dimensions are 1..8192 with at most 16777216 pixels; quality is 1..100. Before the first effect, the request must fit default ceilings of 33554432 total pixels, 128 MiB temporary memory and 64 MiB artifacts (public maxima 67108864, 256 MiB and 256 MiB). Returns E_SCENE_VIEW_NOT_AVAILABLE when no SceneView is open. Read state is restored after capture; available in Edit and Play Mode.",
            Group = "screenshot",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Capture scene view", Input = new Request { Path = "Assets/Shots/scene.png" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var requestBudget = ctx.UseReadBudget(request.Budget);
            if (!requestBudget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(requestBudget.ExhaustedDimension);
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null || sceneView.camera == null)
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_VIEW_NOT_AVAILABLE", "No active SceneView camera.", "Open a SceneView window.");
            var camera = sceneView.camera;
            var outputPath = ScreenshotToolSupport.ResolveScreenshotOutputPath(request.Path);
            var format = ScreenshotToolSupport.NormalizeFormat(null, outputPath);
            var width = request.Width ?? (camera.pixelWidth > 0 ? camera.pixelWidth : 1280);
            var height = request.Height ?? (camera.pixelHeight > 0 ? camera.pixelHeight : 720);
            var quality = request.Quality ?? 95;

            if (File.Exists(outputPath) && !request.Overwrite)
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + outputPath, "Pass overwrite=true or choose a different output path.");
            var captureBudget = ScreenshotToolSupport.PrepareCaptureBudget(width, height, 1, false, request.Limits, requestBudget);
            ctx.CommitEffect();
            ScreenshotToolSupport.CreateScreenshotDirectory(outputPath);

            var prevTarget = camera.targetTexture;
            var prevActive = RenderTexture.active;
            var prevClear = camera.clearFlags;
            var prevBg = camera.backgroundColor;
            RenderTexture renderTexture = null;
            Texture2D texture = null;
            byte[] bytes;
            ScreenshotArtifactProof artifact;
            try
            {
                captureBudget.BeginCapture(width, height);
                renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                texture = new Texture2D(width, height, request.Transparent ? TextureFormat.ARGB32 : TextureFormat.RGB24, false);
                camera.targetTexture = renderTexture;
                if (request.Transparent) { camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0, 0, 0, 0); }
                camera.Render();
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                bytes = format == "jpg" ? texture.EncodeToJPG(quality) : texture.EncodeToPNG();
                artifact = ScreenshotToolSupport.WriteScreenshotBytes(outputPath, bytes, captureBudget);
            }
            finally
            {
                RenderTexture.active = prevActive;
                camera.targetTexture = prevTarget;
                camera.clearFlags = prevClear;
                camera.backgroundColor = prevBg;
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                if (renderTexture != null) { renderTexture.Release(); UnityEngine.Object.DestroyImmediate(renderTexture); }
            }

            var result = new Result
            {
                ScreenshotPath = outputPath, ArtifactPath = outputPath, Source = "scene-view",
                SizeBytes = artifact.SizeBytes, Sha256 = artifact.Sha256, CapturePlan = captureBudget.Plan
            };
            ScreenshotToolSupport.MaybeAppendInline(result, bytes, width, height, format == "jpg" ? "image/jpeg" : "image/png", request.InlineBase64, captureBudget);
            return new System.Threading.Tasks.ValueTask<Result>(result);
        }
    }
}
#endif
