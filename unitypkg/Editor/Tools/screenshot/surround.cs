#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityCli
{
    /// <summary>screenshot.surround — 六张逐次释放的捕获与一个 aggregate contact sheet。</summary>
    public class ScreenshotSurroundTool : IUnityCliTool<ScreenshotSurroundTool.Request, ScreenshotSurroundTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public string OutputDir { get; set; } = "Assets/Screenshots/Surround";
            public string ContactSheetPath { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Width { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Height { get; set; }
            public float? Radius { get; set; }
            public float? Fov { get; set; }
            public bool Overwrite { get; set; }
            public bool InlineBase64 { get; set; }
            public ScreenshotBudgetInput Limits { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public bool DryRun { get; set; }
            public double[] Center { get; set; }
            public long Count { get; set; }
            public ScreenshotArtifactEntry ContactSheet { get; set; }
            public List<SurroundShot> Artifacts { get; set; }
            public string ImageBase64 { get; set; }
            public long? ImageWidth { get; set; }
            public long? ImageHeight { get; set; }
            public string ImageMimeType { get; set; }
            public ScreenshotCapturePlan CapturePlan { get; set; }
        }

        public sealed class SurroundShot
        {
            public string Angle { get; set; }
            public ScreenshotArtifactEntry Artifact { get; set; }
            public double[] CameraPosition { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "screenshot.surround",
            Summary = "Capture six fixed views and one bounded contact sheet while releasing every tile immediately.",
            Description = "Capture front/back/left/right/top/bird_eye PNGs plus a 3x2 contact sheet. Tile and contact-sheet dimensions each must satisfy 1..8192 and 16777216 pixels per image. Before dry-run or the first effect, the seven-output aggregate plan must fit default ceilings of 33554432 total pixels, 128 MiB peak temporary memory and 64 MiB artifacts; public maxima are 67108864, 256 MiB and 256 MiB. radius must be positive and fov must be 10..120. Each tile is copied into the sheet, encoded, written and destroyed before the next capture. Editor-only; dry-run returns the same frozen plan without effects.",
            Group = "screenshot", DestructiveHint = true, SideEffects = new[] { "asset-write" },
            Target = "editor", PlayModeSupport = "editor-only", SupportsDryRun = true,
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Preview surround artifacts", Input = new Request { Target = "Player" }, Execution = new ToolExampleExecution { DryRun = true } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var requestBudget = ctx.UseReadBudget(request.Budget);
            var gameObject = UnityCliBridge.ObjectRefResolver.FindGameObjectByExactPath(request.Target, requestBudget);
            if (gameObject == null) throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "screenshot.surround requires a target GameObject.", "Pass an exact hierarchy path.");
            var outputDir = request.OutputDir ?? "Assets/Screenshots/Surround";
            var outputFullDir = ScreenshotToolSupport.AssetFullPath(outputDir);
            var contactSheetPath = ScreenshotToolSupport.ResolveScreenshotOutputPath(request.ContactSheetPath ?? Path.Combine(outputDir, "surround_contact_sheet.png"));
            var width = request.Width ?? 512;
            var height = request.Height ?? 512;
            var bounds = ScreenshotToolSupport.TargetRenderBounds(gameObject, requestBudget);
            var center = bounds.center;
            var radius = request.Radius ?? Mathf.Max(bounds.extents.magnitude * 2.5f, 5f);
            var fov = request.Fov ?? 60f;
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0f)
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_LIMIT_INVALID", "radius must be finite and greater than zero.", "Pass a positive finite radius.");
            if (float.IsNaN(fov) || float.IsInfinity(fov) || fov < 10f || fov > 120f)
                throw new UnityCliBridge.BridgeCommandException("E_SCREENSHOT_LIMIT_INVALID", "fov must be finite in 10..120.", "Pass a supported field of view.");
            var captureBudget = ScreenshotToolSupport.PrepareCaptureBudget(width, height, 6, true, request.Limits, requestBudget);

            var labels = new[] { "front", "back", "left", "right", "top", "bird_eye" };
            var positions = new[]
            {
                center + new Vector3(0, 0, -radius), center + new Vector3(0, 0, radius),
                center + new Vector3(-radius, 0, 0), center + new Vector3(radius, 0, 0),
                center + new Vector3(0, radius, 0), center + new Vector3(radius * 0.7f, radius * 0.7f, -radius * 0.7f)
            };
            var shotPaths = new List<string>();
            for (var index = 0; index < labels.Length; index++)
            {
                var path = Path.Combine(outputFullDir, "surround_" + labels[index] + ".png");
                if (File.Exists(path) && !request.Overwrite) throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + path, "Pass overwrite=true or choose a different outputDir.");
                shotPaths.Add(path);
            }
            if (File.Exists(contactSheetPath) && !request.Overwrite) throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Contact sheet output already exists: " + contactSheetPath, "Pass overwrite=true or choose another path.");
            var planned = BuildPlannedShots(labels, shotPaths, width, height, positions);
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    DryRun = true, Center = new[] { (double)center.x, center.y, center.z }, Count = 6,
                    ContactSheet = new ScreenshotArtifactEntry { Source = "surround-contact-sheet", Path = contactSheetPath, Width = width * 3L, Height = height * 2L, Format = "png" },
                    Artifacts = planned, CapturePlan = captureBudget.Plan
                });
            }

            ctx.CommitEffect();
            Directory.CreateDirectory(outputFullDir);
            ScreenshotToolSupport.CreateScreenshotDirectory(contactSheetPath);
            GameObject cameraObject = null;
            Camera camera = null;
            Texture2D sheet = null;
            var artifacts = new List<SurroundShot>();
            byte[] contactBytes = null;
            ScreenshotArtifactProof contactProof = null;
            try
            {
                cameraObject = new GameObject("__unitycli_surround_cam__");
                camera = cameraObject.AddComponent<Camera>();
                camera.fieldOfView = fov;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = Mathf.Max(radius * 6f, 100f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);
                sheet = ScreenshotToolSupport.CreateContactSheet(width, height, captureBudget);
                for (var index = 0; index < labels.Length; index++)
                {
                    camera.transform.position = positions[index];
                    camera.transform.LookAt(center);
                    var tile = ScreenshotToolSupport.RenderScreenshotTexture(camera, width, height, captureBudget);
                    try
                    {
                        ScreenshotToolSupport.CopyContactSheetTile(sheet, tile, index, width, height);
                        var proof = ScreenshotToolSupport.WriteScreenshotBytes(shotPaths[index], tile.EncodeToPNG(), captureBudget);
                        artifacts.Add(new SurroundShot
                        {
                            Angle = labels[index],
                            Artifact = new ScreenshotArtifactEntry { Source = "surround", Path = shotPaths[index], Camera = "surround:" + labels[index], Width = width, Height = height, Format = "png", SizeBytes = proof.SizeBytes, Sha256 = proof.Sha256 },
                            CameraPosition = new[] { (double)positions[index].x, positions[index].y, positions[index].z }
                        });
                    }
                    finally { UnityEngine.Object.DestroyImmediate(tile); }
                }
                sheet.Apply(false, false);
                contactBytes = sheet.EncodeToPNG();
                contactProof = ScreenshotToolSupport.WriteScreenshotBytes(contactSheetPath, contactBytes, captureBudget);
            }
            finally
            {
                if (sheet != null) UnityEngine.Object.DestroyImmediate(sheet);
                if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
            }

            var result = new Result
            {
                Center = new[] { (double)center.x, center.y, center.z }, Count = 6,
                ContactSheet = new ScreenshotArtifactEntry { Source = "surround-contact-sheet", Path = contactSheetPath, Width = width * 3L, Height = height * 2L, Format = "png", SizeBytes = contactProof.SizeBytes, Sha256 = contactProof.Sha256 },
                Artifacts = artifacts, CapturePlan = captureBudget.Plan
            };
            string encoded;
            if (request.InlineBase64 && contactBytes != null && contactBytes.Length <= 512 * 1024 && captureBudget.TryCreateInlineBase64(contactBytes, out encoded))
            {
                result.ImageBase64 = encoded;
                result.ImageWidth = width * 3L;
                result.ImageHeight = height * 2L;
                result.ImageMimeType = "image/png";
            }
            return new System.Threading.Tasks.ValueTask<Result>(result);
        }

        private static List<SurroundShot> BuildPlannedShots(string[] labels, List<string> paths, int width, int height, Vector3[] positions)
        {
            var result = new List<SurroundShot>();
            for (var index = 0; index < labels.Length; index++)
            {
                result.Add(new SurroundShot
                {
                    Angle = labels[index],
                    Artifact = new ScreenshotArtifactEntry { Source = "surround", Path = paths[index], Camera = "surround:" + labels[index], Width = width, Height = height, Format = "png" },
                    CameraPosition = new[] { (double)positions[index].x, positions[index].y, positions[index].z }
                });
            }
            return result;
        }
    }
}
#endif
