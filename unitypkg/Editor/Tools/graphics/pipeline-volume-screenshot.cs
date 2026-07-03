#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.pipeline — 读取渲染管线摘要与 QualitySettings(只读别名)。</summary>
    public class GraphicsPipelineTool : IUnityCliTool<GraphicsPipelineTool.Request, GraphicsPipelineTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public GraphicsSummary Graphics { get; set; } public QualitySettingsInfo QualitySettings { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.pipeline", Summary = "Read the active render pipeline and quality settings summary.", Description = "Read-only alias: returns render pipeline name/asset and current QualitySettings.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read pipeline info", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx) => new Result { Graphics = GraphicsToolSupport.BuildGraphicsSummary(), QualitySettings = GraphicsToolSupport.BuildQualitySettings() };
    }

    /// <summary>graphics.pipeline-settings — 设置 GraphicsSettings.defaultRenderPipeline。</summary>
    public class GraphicsPipelineSettingsTool : IUnityCliTool<GraphicsPipelineSettingsTool.Request, GraphicsPipelineSettingsTool.Result>
    {
        public sealed class Request { public string RenderPipeline { get; set; } }
        public sealed class Result { public GraphicsSummary Graphics { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.pipeline-settings", Summary = "Set the default render pipeline asset for the project.", Description = "Set GraphicsSettings.defaultRenderPipeline. Pass 'built-in' or 'null' to reset to built-in.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set URP pipeline", Input = new Request { RenderPipeline = "Assets/Settings/URP.asset" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(r.RenderPipeline))
            {
                if (string.Equals(r.RenderPipeline, "null", System.StringComparison.OrdinalIgnoreCase) || string.Equals(r.RenderPipeline, "built-in", System.StringComparison.OrdinalIgnoreCase))
                    GraphicsSettings.defaultRenderPipeline = null;
                else
                {
                    var pipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(SceneToolSupport.NormalizeAssetPath(r.RenderPipeline));
                    if (pipeline == null) throw new UnityCliBridge.BridgeCommandException("E_RENDER_PIPELINE_NOT_FOUND", "RenderPipelineAsset not found: " + r.RenderPipeline, "Pass an Assets/*.asset RenderPipelineAsset path or use --render-pipeline built-in.");
                    GraphicsSettings.defaultRenderPipeline = pipeline;
                }
                effects.Add(new GraphicsPlannedEffect { Type = "graphics.pipeline-settings", Property = "renderPipeline", Value = r.RenderPipeline });
            }
            return new Result { Graphics = GraphicsToolSupport.BuildGraphicsSummary(), PlannedEffects = effects };
        }
    }

    /// <summary>graphics.pipeline-quality — 设置 QualityLevel。</summary>
    public class GraphicsPipelineQualityTool : IUnityCliTool<GraphicsPipelineQualityTool.Request, GraphicsPipelineQualityTool.Result>
    {
        public sealed class Request { public string QualityLevel { get; set; } }
        public sealed class Result { public QualitySettingsInfo QualitySettings { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.pipeline-quality", Summary = "Set the active quality level by name or index.", Description = "Set QualitySettings quality level. Accepts a numeric index or quality level name.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set quality to Ultra", Input = new Request { QualityLevel = "Ultra" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(r.QualityLevel)) { var level = GraphicsToolSupport.ResolveQualityLevel(r.QualityLevel); QualitySettings.SetQualityLevel(level, true); AssetDatabase.SaveAssets(); effects.Add(new GraphicsPlannedEffect { Type = "graphics.pipeline-quality", Property = "qualityLevel", Value = level.ToString() }); }
            return new Result { QualitySettings = GraphicsToolSupport.BuildQualitySettings(), PlannedEffects = effects };
        }
    }

    /// <summary>graphics.pipeline-info — 读取渲染管线摘要与 QualitySettings(只读)。</summary>
    public class GraphicsPipelineInfoTool : IUnityCliTool<GraphicsPipelineInfoTool.Request, GraphicsPipelineInfoTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public GraphicsSummary Graphics { get; set; } public QualitySettingsInfo QualitySettings { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.pipeline-info", Summary = "Read the active render pipeline asset and quality settings.", Description = "Read-only: returns render pipeline summary and current QualitySettings.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read pipeline info", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx) => new Result { Graphics = GraphicsToolSupport.BuildGraphicsSummary(), QualitySettings = GraphicsToolSupport.BuildQualitySettings() };
    }

    /// <summary>graphics.volume — 列出场景中的 Volume 组件(通过反射,兼容 URP/HDRP)。</summary>
    public class GraphicsVolumeTool : IUnityCliTool<GraphicsVolumeTool.Request, GraphicsVolumeTool.Result>
    {
        public sealed class Request { public string Target { get; set; } }
        public sealed class Result { public List<VolumeInfo> Volumes { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.volume", Summary = "List Volume components in the scene, optionally filtered by target.", Description = "Find Volume components (URP/HDRP) via reflection. Filter by GameObject name or hierarchy path.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "List all volumes", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var volumes = new List<VolumeInfo>();
            var volumeType = GraphicsToolSupport.FindType("UnityEngine.Rendering.Volume");
            if (volumeType == null) return new Result { Volumes = volumes };
            foreach (var component in Resources.FindObjectsOfTypeAll(volumeType))
            {
                var typed = component as Component;
                if (typed == null || typed.gameObject == null) continue;
                if (EditorUtility.IsPersistent(typed.gameObject)) continue;
                if (!string.IsNullOrEmpty(r.Target) && !GameObjectToolSupport.HierarchyPath(typed.gameObject).Equals(r.Target, System.StringComparison.OrdinalIgnoreCase) && !typed.gameObject.name.Equals(r.Target, System.StringComparison.OrdinalIgnoreCase)) continue;
                var type = typed.GetType();
                var enabledProp = type.GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
                var isGlobalField = type.GetField("isGlobal", BindingFlags.Instance | BindingFlags.Public);
                var priorityField = type.GetField("priority", BindingFlags.Instance | BindingFlags.Public);
                var weightField = type.GetField("weight", BindingFlags.Instance | BindingFlags.Public);
                var profile = GraphicsToolSupport.MemberValue(type, typed, "profile") as UnityEngine.Object;
                var sharedProfile = GraphicsToolSupport.MemberValue(type, typed, "sharedProfile") as UnityEngine.Object;
                var effectiveProfile = profile ?? sharedProfile;
                volumes.Add(new VolumeInfo
                {
                    Target = new GameObjectTargetRef { Id = typed.gameObject.GetInstanceID(), Name = typed.gameObject.name, Path = GameObjectToolSupport.HierarchyPath(typed.gameObject) },
                    Enabled = enabledProp == null || (bool)enabledProp.GetValue(typed, null),
                    IsGlobal = isGlobalField != null && (bool)isGlobalField.GetValue(typed),
                    Priority = priorityField == null ? 0 : (float)priorityField.GetValue(typed),
                    Weight = weightField == null ? 0 : (float)weightField.GetValue(typed),
                    Profile = effectiveProfile == null ? null : new AssetRef { Name = effectiveProfile.name, Path = AssetDatabase.GetAssetPath(effectiveProfile) }
                });
            }
            return new Result { Volumes = volumes };
        }
    }

    /// <summary>graphics.screenshot — 捕获 SceneView 截图。</summary>
    public class GraphicsScreenshotTool : IUnityCliTool<GraphicsScreenshotTool.Request, GraphicsScreenshotTool.Result>
    {
        public sealed class Request { public string Output { get; set; } public int? Width { get; set; } public int? Height { get; set; } public string Format { get; set; } public bool? Overwrite { get; set; } public bool? Transparent { get; set; } public int? Quality { get; set; } public bool? InlineBase64 { get; set; } public bool? IncludeDiagnostics { get; set; } }
        public sealed class Result { public GraphicsSummary Diagnostics { get; set; } public QualitySettingsInfo QualitySettings { get; set; } public ScreenshotData Screenshot { get; set; } public string ScreenshotPath { get; set; } public string ArtifactPath { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.screenshot", Summary = "Capture a screenshot from the active SceneView with diagnostics.", Description = "Capture the last active SceneView camera. Falls back to the main Camera if no SceneView is open.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "file-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Capture scene view", Input = new Request { Output = "Assets/Screenshot.png" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var camera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : CameraToolSupport.ResolveCamera(null);
            if (camera == null) throw new UnityCliBridge.BridgeCommandException("E_SCENE_VIEW_NOT_AVAILABLE", "No SceneView or Camera is available for graphics screenshot capture.", "Open a SceneView or create an active Camera in the scene.");
            var width = r.Width ?? 1280; var height = r.Height ?? 720;
            var format = CameraToolSupport.NormalizeFormat(r.Format, r.Output ?? "graphics.png");
            var outputPath = CameraToolSupport.ResolveScreenshotOutputPath(r.Output ?? "graphics.png");
            var capture = CameraToolSupport.CaptureCamera(camera, CameraToolSupport.ClampScreenshotDimension(width, "width"), CameraToolSupport.ClampScreenshotDimension(height, "height"), r.Transparent ?? false, r.Quality ?? 90, outputPath, format, r.Overwrite ?? false, r.InlineBase64 ?? false);
            return new Result
            {
                Diagnostics = GraphicsToolSupport.BuildGraphicsSummary(), QualitySettings = GraphicsToolSupport.BuildQualitySettings(),
                Screenshot = new ScreenshotData { ScreenshotPath = capture.ScreenshotPath, ArtifactPath = capture.ArtifactPath, Format = capture.Format, Width = capture.Width, Height = capture.Height },
                ScreenshotPath = capture.ScreenshotPath, ArtifactPath = capture.ArtifactPath,
                PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.screenshot", Property = "path", Value = capture.Path } }
            };
        }
    }
}
#endif
