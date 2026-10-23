#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>rendering.pipeline-settings — 设置 GraphicsSettings.defaultRenderPipeline。</summary>
    public class RenderingPipelineSettingsTool : IUnityCliTool<RenderingPipelineSettingsTool.Request, RenderingPipelineSettingsTool.Result>
    {
        public sealed class Request { public string RenderPipeline { get; set; } }
        public sealed class Result { public GraphicsSummary Graphics { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "rendering.pipeline-settings", Summary = "Read or set the project's default render pipeline asset.", Description = "Dedicated GraphicsSettings.defaultRenderPipeline reader/writer for Edit and Play mode. Omit renderPipeline for a pure read; pass one imported Assets/*.asset RenderPipelineAsset, or built-in/null to reset. Success re-resolves the asset by path and GUID before returning the fresh pipeline summary; QualitySettings belong to rendering.quality.", Group = "rendering",DestructiveHint = true, SideEffects = new[] { "settings-write", "asset-import", "session-affecting" }, Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set URP pipeline", Input = new Request { RenderPipeline = "Assets/Settings/URP.asset" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (string.IsNullOrEmpty(r.RenderPipeline))
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Graphics = GraphicsToolSupport.BuildGraphicsSummary(), PlannedEffects = effects });
            }

            var resetToBuiltIn = string.Equals(r.RenderPipeline, "null", StringComparison.OrdinalIgnoreCase) || string.Equals(r.RenderPipeline, "built-in", StringComparison.OrdinalIgnoreCase);
            var pipelinePath = resetToBuiltIn ? null : GraphicsToolSupport.NormalizeAssetPathRequired(r.RenderPipeline, ".asset");
            var pipelineGuid = resetToBuiltIn ? null : AssetDatabase.AssetPathToGUID(pipelinePath);
            var pipeline = resetToBuiltIn ? null : AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(pipelinePath);
            if (!resetToBuiltIn && (pipeline == null || string.IsNullOrEmpty(pipelineGuid)))
                throw new UnityCliBridge.BridgeCommandException("E_RENDER_PIPELINE_NOT_FOUND", "RenderPipelineAsset not found: " + r.RenderPipeline, "Pass an imported Assets/*.asset RenderPipelineAsset path or use --render-pipeline built-in.");

            ctx.CommitEffect();
            GraphicsSettings.defaultRenderPipeline = pipeline;
            AssetDatabase.SaveAssets();
            if (!resetToBuiltIn) AssetDatabase.ImportAsset(pipelinePath, ImportAssetOptions.ForceUpdate);
            var reloadedPipeline = resetToBuiltIn ? null : GraphicsToolSupport.ReloadAsset<RenderPipelineAsset>(pipelinePath, pipelineGuid);
            var observedPipeline = GraphicsSettings.defaultRenderPipeline;
            if ((resetToBuiltIn && observedPipeline != null) || (!resetToBuiltIn && (reloadedPipeline == null || observedPipeline != reloadedPipeline)))
                throw new UnityCliBridge.BridgeCommandException("E_RENDER_PIPELINE_STATE_MISMATCH", "GraphicsSettings.defaultRenderPipeline did not match the requested asset after path/GUID reload.", "Check Unity Console diagnostics and retry after the editor is idle.");

            effects.Add(new GraphicsPlannedEffect { Type = "rendering.pipeline-settings", Property = "renderPipeline", Value = resetToBuiltIn ? "built-in" : pipelinePath });
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Graphics = GraphicsToolSupport.BuildGraphicsSummary(), PlannedEffects = effects });
        }
    }
}
#endif
