#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// vfx.template - 列出或复制内置 VFX Graph 模板。
    /// </summary>
    public class VfxTemplateTool : IUnityCliTool<VfxTemplateTool.Request, VfxTemplateTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            [UnityCliRequired]
            public string Template { get; set; }
            public bool? Overwrite { get; set; }
        }

        public sealed class Result
        {
            public VfxGraphInfo Graph { get; set; }
            public string TemplateName { get; set; }
            public List<string> Templates { get; set; }
            public long? Count { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.template",
            Summary = "List available VFX Graph templates or clone one into an Assets/*.vfx destination.",
            Description = "List template names by passing template=list, or copy a built-in VFX Graph template into an Assets/*.vfx destination path. The tool searches the package Editor/Templates, Samples, and Samples~ folders.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = true,
            RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List templates", Input = new Request { Template = "list" } },
                new ToolExample { Description = "Clone a template", Input = new Request { Template = "SimpleParticle", Path = "Assets/VFX/SimpleParticle.vfx", Overwrite = true } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            VfxToolSupport.EnsureEditorReady("vfx.template");
            VfxToolSupport.RequireVfxPackageInstalled();
            if (string.Equals(request.Template, "list", StringComparison.OrdinalIgnoreCase))
            {
                var templates = VfxToolSupport.DiscoverVfxTemplates();
                return new Result
                {
                    Templates = templates,
                    Count = templates.Count,
                    LogPath = ctx.LogPath
                };
            }

            var destinationPath = VfxToolSupport.RequireVfxGraphPath(request.Path);
            var assetType = VfxToolSupport.RequireVfxAssetType();
            if (AssetDatabase.LoadAssetAtPath(destinationPath, assetType) != null && request.Overwrite != true)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_GRAPH_EXISTS",
                    "VFX Graph already exists: " + destinationPath,
                    "Pass overwrite=true or choose a different path.");
            }

            var templatePath = VfxToolSupport.ResolveVfxTemplatePath(request.Template);
            if (string.IsNullOrEmpty(templatePath))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_TEMPLATE_NOT_FOUND",
                    "VFX Graph template not found: " + request.Template,
                    "Use template=list to discover available templates.");
            }

            AssetToolSupport.CreateAssetDirectory(destinationPath);
            if (!AssetDatabase.CopyAsset(templatePath, destinationPath))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_TEMPLATE_COPY_FAILED",
                    "Failed to copy template to: " + destinationPath,
                    "Check destination path and Unity Console for errors.");
            }

            AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceUpdate);
            var created = AssetDatabase.LoadAssetAtPath(destinationPath, assetType);
            return new Result
            {
                Graph = VfxToolSupport.BuildGraphInfo(destinationPath, created),
                TemplateName = request.Template,
                PlannedEffects = new List<VfxPlannedEffect>
                {
                    new VfxPlannedEffect { Type = "vfx.template", Template = request.Template, Path = destinationPath }
                },
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
