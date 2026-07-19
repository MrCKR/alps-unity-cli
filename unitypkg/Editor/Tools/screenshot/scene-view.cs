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
            public int? Width { get; set; }
            public int? Height { get; set; }
            public bool Transparent { get; set; }
            public int? Quality { get; set; }
            public bool Overwrite { get; set; }
            public bool InlineBase64 { get; set; }
        }

        public sealed class Result : ScreenshotCaptureResult { }

        public ToolDescriptor Describe() => new()
        {
            Name = "screenshot.scene-view",
            Summary = "Capture the active SceneView camera to a PNG/JPG file.",
            Description = "Capture the active SceneView camera to a PNG/JPG file. Returns E_SCENE_VIEW_NOT_AVAILABLE when no SceneView is open.",
            Group = "screenshot",
            Version = "1.0.0",
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
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null || sceneView.camera == null)
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_VIEW_NOT_AVAILABLE", "No active SceneView camera.", "Open a SceneView window.");
            var camera = sceneView.camera;
            var outputPath = ScreenshotToolSupport.ResolveScreenshotOutputPath(request.Path);
            var format = ScreenshotToolSupport.NormalizeFormat(null, outputPath);
            var width = Math.Max(1, request.Width ?? (camera.pixelWidth > 0 ? camera.pixelWidth : 1280));
            var height = Math.Max(1, request.Height ?? (camera.pixelHeight > 0 ? camera.pixelHeight : 720));
            var quality = Mathf.Clamp(request.Quality ?? 95, 1, 100);

            if (File.Exists(outputPath) && !request.Overwrite)
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + outputPath, "Pass overwrite=true or choose a different output path.");
            ctx.CommitEffect();
            ScreenshotToolSupport.CreateScreenshotDirectory(outputPath);

            var prevTarget = camera.targetTexture;
            var prevActive = RenderTexture.active;
            var prevClear = camera.clearFlags;
            var prevBg = camera.backgroundColor;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(width, height, request.Transparent ? TextureFormat.ARGB32 : TextureFormat.RGB24, false);
            byte[] bytes;
            try
            {
                camera.targetTexture = rt;
                if (request.Transparent) { camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0, 0, 0, 0); }
                camera.Render();
                RenderTexture.active = rt;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                bytes = format == "jpg" ? texture.EncodeToJPG(quality) : texture.EncodeToPNG();
                ScreenshotToolSupport.WriteScreenshotBytes(outputPath, bytes);
            }
            finally
            {
                RenderTexture.active = prevActive;
                camera.targetTexture = prevTarget;
                camera.clearFlags = prevClear;
                camera.backgroundColor = prevBg;
                UnityEngine.Object.DestroyImmediate(texture);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }

            var artifact = ScreenshotToolSupport.VerifyScreenshotArtifact(outputPath, format);
            var result = new Result
            {
                ScreenshotPath = outputPath, ArtifactPath = outputPath, Format = format,
                Width = width, Height = height, Transparent = request.Transparent, Quality = quality,
                Source = "scene-view", Target = "SceneView.camera", SizeBytes = artifact.SizeBytes, Sha256 = artifact.Sha256
            };
            ScreenshotToolSupport.MaybeAppendInline(result, bytes, width, height, format == "jpg" ? "image/jpeg" : "image/png", request.InlineBase64);
            return new System.Threading.Tasks.ValueTask<Result>(result);
        }
    }
}
#endif
