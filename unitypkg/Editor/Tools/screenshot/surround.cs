#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// screenshot.surround — 6 角度环绕截图并生成 contact sheet。破坏性(asset-write)。
    /// </summary>
    public class ScreenshotSurroundTool : IUnityCliTool<ScreenshotSurroundTool.Request, ScreenshotSurroundTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public string OutputDir { get; set; } = "Assets/Screenshots/Surround";
            public string ContactSheetPath { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public float? Radius { get; set; }
            public float? Fov { get; set; }
            public bool Overwrite { get; set; }
            public bool InlineBase64 { get; set; }
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
            Summary = "Capture six fixed-angle screenshots and compose a 3x2 contact sheet; dry-run supported.",
            Description = "Capture six fixed angles (front, back, left, right, top, bird_eye) around a GameObject and compose a 3x2 contact sheet PNG. Supports --dry-run to preview planned artifacts. Editor-only.",
            Group = "screenshot",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            SupportsDryRun = true,
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Surround a target (dry-run)", Input = new Request { Target = "Player" }, Execution = new ToolExampleExecution { DryRun = true } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(request.Target);
            if (go == null)
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "screenshot.surround requires a target GameObject.", "Pass --target <path>.");
            var outputDir = request.OutputDir ?? "Assets/Screenshots/Surround";
            var outputFullDir = ScreenshotToolSupport.AssetFullPath(outputDir);
            var contactSheetPath = ScreenshotToolSupport.ResolveScreenshotOutputPath(request.ContactSheetPath ?? Path.Combine(outputDir, "surround_contact_sheet.png"));
            var width = Math.Max(1, request.Width ?? 512);
            var height = Math.Max(1, request.Height ?? 512);
            var bounds = ScreenshotToolSupport.TargetRenderBounds(go);
            var center = bounds.center;
            var radius = request.Radius ?? Mathf.Max(bounds.extents.magnitude * 2.5f, 5f);

            var labels = new[] { "front", "back", "left", "right", "top", "bird_eye" };
            var positions = new[]
            {
                center + new Vector3(0, 0, -radius),
                center + new Vector3(0, 0, radius),
                center + new Vector3(-radius, 0, 0),
                center + new Vector3(radius, 0, 0),
                center + new Vector3(0, radius, 0),
                center + new Vector3(radius * 0.7f, radius * 0.7f, -radius * 0.7f)
            };
            var shotPaths = new List<string>();
            for (var i = 0; i < labels.Length; i++)
            {
                var path = Path.Combine(outputFullDir, "surround_" + labels[i] + ".png");
                if (File.Exists(path) && !request.Overwrite)
                    throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + path, "Pass overwrite=true or choose a different output-dir.");
                shotPaths.Add(path);
            }
            if (File.Exists(contactSheetPath) && !request.Overwrite)
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Contact sheet output already exists: " + contactSheetPath, "Pass overwrite=true or choose a different contactSheetPath.");

            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    DryRun = true,
                    Center = new double[] { center.x, center.y, center.z },
                    ContactSheet = new ScreenshotArtifactEntry { Source = "surround-contact-sheet", Path = contactSheetPath, Width = width * 3, Height = height * 2, Format = "png" },
                    Artifacts = BuildPlannedShots(labels, shotPaths, width, height, positions)
                });
            }

            ctx.CommitEffect();
            Directory.CreateDirectory(outputFullDir);
            ScreenshotToolSupport.CreateScreenshotDirectory(contactSheetPath);
            var tempGo = new GameObject("__unity_cli_surround_cam__");
            var cam = tempGo.AddComponent<Camera>();
            cam.fieldOfView = Mathf.Clamp(request.Fov ?? 60f, 10f, 120f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = Mathf.Max(radius * 6f, 100f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);
            var tiles = new List<Texture2D>();
            var artifacts = new List<SurroundShot>();
            byte[] contactBytes = null;
            try
            {
                for (var i = 0; i < labels.Length; i++)
                {
                    cam.transform.position = positions[i];
                    cam.transform.LookAt(center);
                    var tile = ScreenshotToolSupport.RenderScreenshotTexture(cam, width, height);
                    tiles.Add(tile);
                    var proof = ScreenshotToolSupport.WriteScreenshotBytes(shotPaths[i], tile.EncodeToPNG());
                    artifacts.Add(new SurroundShot
                    {
                        Angle = labels[i],
                        Artifact = new ScreenshotArtifactEntry { Source = "surround", Path = shotPaths[i], Camera = "surround:" + labels[i], Width = width, Height = height, Format = "png", SizeBytes = proof.SizeBytes, Sha256 = proof.Sha256 },
                        CameraPosition = new double[] { positions[i].x, positions[i].y, positions[i].z }
                    });
                }
                var contact = ScreenshotToolSupport.ComposeScreenshotContactSheet(tiles, width, height);
                try { contactBytes = contact.EncodeToPNG(); ScreenshotToolSupport.WriteScreenshotBytes(contactSheetPath, contactBytes); }
                finally { UnityEngine.Object.DestroyImmediate(contact); }
            }
            finally
            {
                foreach (var tile in tiles) UnityEngine.Object.DestroyImmediate(tile);
                UnityEngine.Object.DestroyImmediate(tempGo);
            }

            var contactProof = ScreenshotToolSupport.VerifyScreenshotArtifact(contactSheetPath, "png");
            var result = new Result
            {
                Center = new double[] { center.x, center.y, center.z }, Count = 6,
                ContactSheet = new ScreenshotArtifactEntry { Source = "surround-contact-sheet", Path = contactSheetPath, Width = width * 3, Height = height * 2, Format = "png", SizeBytes = contactProof.SizeBytes, Sha256 = contactProof.Sha256 },
                Artifacts = artifacts
            };
            if (request.InlineBase64 && contactBytes != null && contactBytes.Length <= 512 * 1024)
            {
                result.ImageBase64 = Convert.ToBase64String(contactBytes);
                result.ImageWidth = width * 3;
                result.ImageHeight = height * 2;
                result.ImageMimeType = "image/png";
            }
            return new System.Threading.Tasks.ValueTask<Result>(result);
        }

        private static List<SurroundShot> BuildPlannedShots(string[] labels, List<string> shotPaths, int width, int height, Vector3[] positions)
        {
            var shots = new List<SurroundShot>();
            for (var i = 0; i < labels.Length; i++)
            {
                shots.Add(new SurroundShot
                {
                    Angle = labels[i],
                    Artifact = new ScreenshotArtifactEntry { Source = "surround", Path = shotPaths[i], Camera = "surround:" + labels[i], Width = width, Height = height, Format = "png" },
                    CameraPosition = new double[] { positions[i].x, positions[i].y, positions[i].z }
                });
            }
            return shots;
        }
    }
}
#endif
