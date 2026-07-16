#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// screenshot.orbit — 从多个角度环绕截图同一对象。
    /// </summary>
    public class ScreenshotOrbitTool : IUnityCliTool<ScreenshotOrbitTool.Request, ScreenshotOrbitTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public string OutputDir { get; set; } = "Assets/Screenshots";
            public int? Count { get; set; }
            public float? Radius { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string Target { get; set; }
            public long Count { get; set; }
            public List<ScreenshotArtifactEntry> Artifacts { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "screenshot.orbit",
            Summary = "Capture N orbital screenshots around a GameObject at evenly spaced angles.",
            Description = "Capture N orbital screenshots (default 8, max 36) around a target GameObject at evenly spaced angles. Saves individual PNGs to an output directory. Editor-only.",
            Group = "screenshot",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Orbit a target", Input = new Request { Target = "Player", Count = 8 } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(request.Target);
            if (go == null)
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "screenshot.orbit requires a target GameObject.", "Pass --target <path>.");
            var outputDir = request.OutputDir ?? "Assets/Screenshots";
            var angleCount = Math.Max(2, Math.Min(request.Count ?? 8, 36));
            var radius = request.Radius ?? 5f;
            var width = Math.Max(1, request.Width ?? 512);
            var height = Math.Max(1, request.Height ?? 512);
            var center = go.transform.position;
            var artifacts = new List<ScreenshotArtifactEntry>();

            var cam = Camera.main;
            if (cam == null) { var tempGo = new GameObject("__unity_cli_orbit_cam__"); cam = tempGo.AddComponent<Camera>(); }
            var tempCreated = cam.gameObject.name == "__unity_cli_orbit_cam__";
            try
            {
                for (var i = 0; i < angleCount; i++)
                {
                    var angle = (360f / angleCount) * i;
                    var rad = angle * Mathf.Deg2Rad;
                    cam.transform.position = center + new Vector3(Mathf.Cos(rad) * radius, 0, Mathf.Sin(rad) * radius);
                    cam.transform.LookAt(center);
                    var path = Path.Combine(ScreenshotToolSupport.AssetFullPath(outputDir), "orbit_" + i.ToString("00", CultureInfo.InvariantCulture) + ".png");
                    ScreenshotToolSupport.CreateScreenshotDirectory(path);
                    var texture = ScreenshotToolSupport.RenderScreenshotTexture(cam, width, height);
                    ScreenshotToolSupport.WriteScreenshotBytes(path, texture.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(texture);
                    artifacts.Add(new ScreenshotArtifactEntry { Source = "orbit", Path = path, Camera = "orbit:" + angle.ToString("F0", CultureInfo.InvariantCulture), Width = width, Height = height, Format = "png" });
                }
            }
            finally { if (tempCreated) UnityEngine.Object.DestroyImmediate(cam.gameObject); }

            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "screenshot.orbit", Target = go.name, Count = artifacts.Count, Artifacts = artifacts });
        }
    }
}
#endif
