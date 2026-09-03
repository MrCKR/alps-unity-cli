#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.render-preview - 将 UIDocument 的真实 visual tree 渲染为 PNG。</summary>
    public class UiRenderPreviewTool : IUnityCliTool<UiRenderPreviewTool.Request, UiRenderPreviewTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Output { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Width { get; set; }
            [UnityCliRange(1L, 8192L)] public int? Height { get; set; }
            public bool Overwrite { get; set; }
            public ScreenshotBudgetInput Limits { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public bool DryRun { get; set; }
            public string OutputPath { get; set; }
            public long SizeBytes { get; set; }
            public string Sha256 { get; set; }
            public List<UiPlannedEffect> PlannedEffects { get; set; }
        }

        // 描述真实的 UIDocument 离屏渲染及文件覆盖约束。
        public ToolDescriptor Describe() => new()
        {
            Name = "ui.render-preview",
            Summary = "Render a UIDocument visual tree to a PNG file through a temporary target texture; dry-run supported.",
            Description = "Render the current visual tree of a UIDocument GameObject to a PNG through a temporary PanelSettings target texture. Defaults to 1280x720. Refuses to overwrite an existing file unless overwrite=true; dry-run returns the planned output without rendering or writing.",
            Group = "ui",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "file-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Preview a UIDocument without writing",
                    Input = new Request { Target = "UIDocument", Output = "Assets/UI/Preview.png", Width = 1280, Height = 720 },
                    Execution = new ToolExampleExecution { DryRun = true }
                },
                new ToolExample
                {
                    Description = "Render and overwrite a UIDocument preview",
                    Input = new Request { Target = "UIDocument", Output = "Assets/UI/Preview.png", Width = 1280, Height = 720, Overwrite = true }
                }
            }
        };

        /// <summary>临时接管 PanelSettings 目标纹理，等待 UI 完成布局后写出 PNG，并始终恢复原状态。</summary>
        public async ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            request = request ?? new Request();
            var go = UiToolSupport.ResolveGameObjectTarget(request.Target);
            var document = go.GetComponent<UIDocument>();
            if (document == null) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_NOT_FOUND", "No UIDocument on target.", "Pass a GameObject containing UIDocument.");

            var width = request.Width ?? 1280;
            var height = request.Height ?? 720;
            var outputPath = ScreenshotToolSupport.ResolveScreenshotOutputPath(request.Output ?? "Assets/UI/preview.png");
            if (File.Exists(outputPath) && !request.Overwrite)
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "UI preview output already exists: " + outputPath, "Pass overwrite=true or choose a different output path.");

            var captureBudget = ScreenshotToolSupport.PrepareCaptureBudget(width, height, 1, false, request.Limits, ctx.UseReadBudget(request.Budget));
            var effects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.render-preview", Target = request.Target, Path = outputPath } };
            if (ctx.DryRun)
            {
                return new Result { DryRun = true, OutputPath = outputPath, PlannedEffects = effects };
            }

            ctx.CommitEffect();
            captureBudget.BeginCapture(width, height);
            var originalPanelSettings = document.panelSettings;
            var originalEnabled = document.enabled;
            RenderTexture renderTexture = null;
            PanelSettings panelSettings = null;
            Texture2D texture = null;
            var previousActive = RenderTexture.active;
            try
            {
                renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
                renderTexture.Create();
                panelSettings.targetTexture = renderTexture;
                panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
                document.panelSettings = panelSettings;
                document.enabled = true;
                await ctx.Wait.NextFrame(2, ct);
                ctx.ReadBudget.ResumeAfterWait();
                ct.ThrowIfCancellationRequested();

                RenderTexture.active = renderTexture;
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                texture.Apply(false, false);
                var bytes = texture.EncodeToPNG();
                var artifact = ScreenshotToolSupport.WriteScreenshotBytes(outputPath, bytes, captureBudget);
                return new Result
                {
                    DryRun = false,
                    OutputPath = outputPath,
                    SizeBytes = artifact.SizeBytes,
                    Sha256 = artifact.Sha256,
                    PlannedEffects = effects
                };
            }
            finally
            {
                RenderTexture.active = previousActive;
                document.panelSettings = originalPanelSettings;
                document.enabled = originalEnabled;
                if (texture != null) Object.DestroyImmediate(texture);
                if (renderTexture != null) { renderTexture.Release(); Object.DestroyImmediate(renderTexture); }
                if (panelSettings != null) Object.DestroyImmediate(panelSettings);
            }
        }
    }
}
#endif
