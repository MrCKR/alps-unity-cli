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
            [UnityCliRange(2L, 36L)] public int? Count { get; set; }
            public float? Radius { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Width { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Height { get; set; }
            public bool Overwrite { get; set; }
            public ScreenshotBudgetInput Limits { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public List<ScreenshotArtifactEntry> Artifacts { get; set; }
            public ScreenshotCapturePlan CapturePlan { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "screenshot.orbit",
            Summary = "Capture N orbital screenshots around a GameObject at evenly spaced angles.",
            Description = "Capture 2..36 orbital PNGs (default 8). Dimensions are 1..8192 and each image is at most 16777216 pixels. Before the first effect, the complete orbit must fit default ceilings of 33554432 total pixels, 128 MiB peak temporary memory and 64 MiB artifacts; public maxima are 67108864, 256 MiB and 256 MiB. Each texture is encoded, written and released before the next capture. radius must be finite and positive. Editor-only because a temporary Camera may be created.",
            Group = "screenshot",
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
            var requestBudget = ctx.UseReadBudget(request.Budget);
            var go = UnityCliBridge.ObjectRefResolver.FindGameObjectByExactPath(request.Target, requestBudget);
            if (go == null)
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "screenshot.orbit requires a target GameObject.", "Pass --target <path>.");
            var outputDir = request.OutputDir ?? "Assets/Screenshots";
            var angleCount = request.Count ?? 8;
            var radius = request.Radius ?? 5f;
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0f)
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_LIMIT_INVALID", "radius must be finite and greater than zero.", "Pass a positive finite orbit radius.");
            var width = request.Width ?? 512;
            var height = request.Height ?? 512;
            var captureBudget = ScreenshotToolSupport.PrepareCaptureBudget(width, height, angleCount, false, request.Limits, requestBudget);
            var center = go.transform.position;
            var artifacts = new List<ScreenshotArtifactEntry>();
            for (var i = 0; i < angleCount; i++)
            {
                var candidate = Path.Combine(ScreenshotToolSupport.AssetFullPath(outputDir), "orbit_" + i.ToString("00", CultureInfo.InvariantCulture) + ".png");
                if (File.Exists(candidate) && !request.Overwrite)
                    throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + candidate, "Pass overwrite=true or choose another outputDir.");
            }

            ctx.CommitEffect();
            Camera camera = null;
            GameObject temporaryCameraObject = null;
            var previousPosition = Vector3.zero;
            var previousRotation = Quaternion.identity;
            try
            {
                camera = Camera.main;
                if (camera == null)
                {
                    temporaryCameraObject = new GameObject("__unitycli_orbit_cam__");
                    camera = temporaryCameraObject.AddComponent<Camera>();
                }
                else
                {
                    previousPosition = camera.transform.position;
                    previousRotation = camera.transform.rotation;
                }
                for (var i = 0; i < angleCount; i++)
                {
                    var angle = (360f / angleCount) * i;
                    var rad = angle * Mathf.Deg2Rad;
                    camera.transform.position = center + new Vector3(Mathf.Cos(rad) * radius, 0, Mathf.Sin(rad) * radius);
                    camera.transform.LookAt(center);
                    var path = Path.Combine(ScreenshotToolSupport.AssetFullPath(outputDir), "orbit_" + i.ToString("00", CultureInfo.InvariantCulture) + ".png");
                    ScreenshotToolSupport.CreateScreenshotDirectory(path);
                    var texture = ScreenshotToolSupport.RenderScreenshotTexture(camera, width, height, captureBudget);
                    ScreenshotArtifactProof proof;
                    try { proof = ScreenshotToolSupport.WriteScreenshotBytes(path, texture.EncodeToPNG(), captureBudget); }
                    finally { UnityEngine.Object.DestroyImmediate(texture); }
                    artifacts.Add(new ScreenshotArtifactEntry { Source = "orbit", Path = path, Camera = "orbit:" + angle.ToString("F0", CultureInfo.InvariantCulture), Width = width, Height = height, Format = "png", SizeBytes = proof.SizeBytes, Sha256 = proof.Sha256 });
                }
            }
            finally
            {
                if (temporaryCameraObject != null) UnityEngine.Object.DestroyImmediate(temporaryCameraObject);
                else if (camera != null) { camera.transform.position = previousPosition; camera.transform.rotation = previousRotation; }
            }

            return new System.Threading.Tasks.ValueTask<Result>(new Result { Artifacts = artifacts, CapturePlan = captureBudget.Plan });
        }
    }
}
#endif
