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
            public int? Width { get; set; }
            public int? Height { get; set; }
            public bool Overwrite { get; set; }
            public bool InlineBase64 { get; set; }
        }

        public sealed class Result : ScreenshotCaptureResult { }

        public ToolDescriptor Describe() => new()
        {
            Name = "screenshot.game-view",
            Summary = "Capture a Game View screenshot by rendering all active cameras to a PNG/JPG file.",
            Description = "Capture a Game View screenshot by rendering all active cameras to a temporary RenderTexture and saving to a PNG/JPG file. Defaults to 1280x720.",
            Group = "screenshot",
            Version = "1.0.0",
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
            ScreenshotToolSupport.CreateScreenshotDirectory(outputPath);
            var width = Math.Max(1, request.Width ?? 1280);
            var height = Math.Max(1, request.Height ?? 720);
            var format = outputPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";

            var rt = new RenderTexture(width, height, 24);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            byte[] bytes;
            try
            {
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var cameras = Camera.allCamerasCount > 0 ? Camera.allCameras : new Camera[0];
                foreach (var cam in cameras)
                {
                    if (!cam.gameObject.activeInHierarchy) continue;
                    var prevTarget = cam.targetTexture;
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.targetTexture = prevTarget;
                }
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                RenderTexture.active = prev;
                bytes = format == "jpg" ? texture.EncodeToJPG(95) : texture.EncodeToPNG();
                ScreenshotToolSupport.WriteScreenshotBytes(outputPath, bytes);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }

            var result = new Result
            {
                ScreenshotPath = outputPath, ArtifactPath = outputPath, Format = format,
                Width = width, Height = height, Quality = 95, Source = "game-view", Target = "Camera.main/allCameras"
            };
            ScreenshotToolSupport.MaybeAppendInline(result, bytes, width, height, format == "jpg" ? "image/jpeg" : "image/png", request.InlineBase64);
            return new System.Threading.Tasks.ValueTask<Result>(result);
        }
    }
}
#endif
