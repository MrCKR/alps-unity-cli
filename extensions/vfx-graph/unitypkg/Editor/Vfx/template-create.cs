#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>从 package 模板创建一个 VFX Graph 资产。</summary>
    public sealed class VfxTemplateCreateTool : IUnityCliTool<VfxTemplateCreateTool.Request, VfxTemplateCreateTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Template { get; set; } [UnityCliRequired] public string Path { get; set; } public bool? Overwrite { get; set; } }
        public sealed class Result { public VfxGraphInfo Graph { get; set; } public string TemplateName { get; set; } public bool DryRun { get; set; } public List<VfxPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.template.create", Summary = "Create a VFX Graph from a package template.",
            Description = "Copy one installed VFX Graph template to an Assets/*.vfx destination. Supports --dry-run and explicit overwrite.",
            Group = "vfx",DestructiveHint = true, SupportsDryRun = true,
            RequiresPackages = new[] { "com.unity.visualeffectgraph" }, SideEffects = new[] { "asset-write" },
            Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Create a simple particle graph", Input = new Request { Template = "SimpleParticleSystem", Path = "Assets/VFX/SimpleParticle.vfx", Overwrite = true } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.EnsureEditorReady("vfx.template.create");
            VfxToolSupport.RequireVfxPackageInstalled();
            var destinationPath = VfxToolSupport.RequireVfxGraphPath(request.Path);
            var graphType = VfxToolSupport.RequireVfxAssetType();
            var existing = AssetDatabase.LoadAssetAtPath(destinationPath, graphType);
            if (existing != null && request.Overwrite != true)
                throw new UnityCliBridge.BridgeCommandException("E_VFX_GRAPH_EXISTS", "VFX Graph already exists: " + destinationPath, "Pass overwrite=true or choose a different Assets/*.vfx path.");
            var templatePath = VfxToolSupport.ResolveVfxTemplatePath(request.Template);
            if (string.IsNullOrEmpty(templatePath))
                throw new UnityCliBridge.BridgeCommandException("E_VFX_TEMPLATE_NOT_FOUND", "VFX Graph template not found: " + request.Template, "Run vfx.template.list to discover available templates.");

            var effects = new List<VfxPlannedEffect> { new VfxPlannedEffect { Type = "vfx.template.create", Template = request.Template, Path = destinationPath } };
            if (ctx.DryRun)
                return new System.Threading.Tasks.ValueTask<Result>(new Result { TemplateName = request.Template, DryRun = true, PlannedEffects = effects });

            ctx.CommitEffect();
            AssetToolSupport.CreateAssetDirectory(destinationPath);
            if (existing != null) AssetDatabase.DeleteAsset(destinationPath);
            if (!AssetDatabase.CopyAsset(templatePath, destinationPath))
                throw new UnityCliBridge.BridgeCommandException("E_VFX_TEMPLATE_COPY_FAILED", "Failed to copy template to: " + destinationPath, "Check the destination path and Unity Console errors.");
            AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            var created = AssetDatabase.LoadAssetAtPath(destinationPath, graphType);
            if (created == null || !graphType.IsInstanceOfType(created))
                throw new UnityCliBridge.BridgeCommandException("E_VFX_TEMPLATE_COPY_FAILED", "Copied template did not produce a readable VisualEffectAsset: " + destinationPath, "Check the template and Unity Console errors.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Graph = VfxToolSupport.BuildGraphInfo(destinationPath, created), TemplateName = request.Template,
                PlannedEffects = effects
            });
        }
    }
}
#endif
