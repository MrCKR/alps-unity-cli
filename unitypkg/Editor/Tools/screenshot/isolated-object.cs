#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// screenshot.isolated-object — 在临时相机中隔离渲染单个对象。
    /// 内置工具(原 RegisterCommand 形态)迁移为 IUnityCliTool 自包含。
    /// </summary>
    public class ScreenshotIsolatedObjectTool : IUnityCliTool<ScreenshotIsolatedObjectTool.Request, ScreenshotIsolatedObjectTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            [UnityCliRequired] public string Path { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public bool Orthographic { get; set; } = true;
            public float? Size { get; set; }
            public bool Overwrite { get; set; }
            public bool InlineBase64 { get; set; }
        }

        public sealed class Result : ScreenshotCaptureResult { }

        public ToolDescriptor Describe() => new()
        {
            Name = "screenshot.isolated-object",
            Summary = "Render a single GameObject in isolation with a temporary camera to a PNG/JPG file.",
            Description = "Render a single GameObject in isolation using a temporary camera with a solid background. Resolves the target by hierarchy path/name. Editor-only (creates a temporary Camera).",
            Group = "screenshot",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Isolate an object", Input = new Request { Target = "Player", Path = "Assets/Shots/player.png" } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var go = UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(request.Target);
            if (go == null)
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "screenshot.isolated-object requires a target GameObject.", "Pass --target <path>.");
            var outputPath = ScreenshotToolSupport.ResolveScreenshotOutputPath(request.Path);
            if (File.Exists(outputPath) && !request.Overwrite)
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Output exists: " + outputPath);
            ScreenshotToolSupport.CreateScreenshotDirectory(outputPath);
            var width = Math.Max(1, request.Width ?? 512);
            var height = Math.Max(1, request.Height ?? 512);
            var format = outputPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";

            var tempGo = new GameObject("__unity_cli_isolated_cam__");
            var cam = tempGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 1f);
            cam.orthographic = request.Orthographic;
            cam.orthographicSize = request.Size ?? 2f;
            var bounds = go.GetComponent<Renderer>() != null ? go.GetComponent<Renderer>().bounds : new Bounds(go.transform.position, Vector3.one);
            tempGo.transform.position = bounds.center + new Vector3(0, 0, -5);
            tempGo.transform.LookAt(bounds.center);

            byte[] bytes;
            var rt = new RenderTexture(width, height, 24);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
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
                UnityEngine.Object.DestroyImmediate(tempGo);
            }

            var result = new Result
            {
                ScreenshotPath = outputPath, ArtifactPath = outputPath, Format = format,
                Width = width, Height = height, Quality = 95, Source = "isolated-object", Target = go.name
            };
            ScreenshotToolSupport.MaybeAppendInline(result, bytes, width, height, format == "jpg" ? "image/jpeg" : "image/png", request.InlineBase64);
            return result;
        }
    }
}
#endif
