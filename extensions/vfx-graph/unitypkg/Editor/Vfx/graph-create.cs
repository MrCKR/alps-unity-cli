#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace UnityCli
{
    /// <summary>创建独立 VFX Graph 资产。</summary>
    public sealed class VfxGraphCreateTool : IUnityCliTool<VfxGraphCreateTool.Request, VfxGraphCreateTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Path { get; set; } public bool? Overwrite { get; set; } }
        public sealed class Result { public VfxGraphInfo Graph { get; set; } public bool DryRun { get; set; } public List<VfxPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.graph.create", Summary = "Create a VFX Graph asset.",
            Description = "Create one Assets/*.vfx Visual Effect Graph asset, optionally replacing an existing graph. Supports --dry-run without writing the asset.",
            Group = "vfx",DestructiveHint = true, SupportsDryRun = true,
            RequiresPackages = new[] { "com.unity.visualeffectgraph" }, SideEffects = new[] { "asset-write" },
            Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Create a matrix graph", Input = new Request { Path = "Assets/VFX/UnityCliCreatedExplosion.vfx" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.EnsureEditorReady("vfx.graph.create");
            var path = VfxToolSupport.RequireVfxGraphPath(request.Path);
            var graphType = VfxToolSupport.RequireVfxAssetType();
            VfxToolSupport.PrepareVfxGraphFactory();
            var existing = AssetDatabase.LoadAssetAtPath(path, graphType);
            if (existing != null && request.Overwrite != true)
                throw new UnityCliBridge.BridgeCommandException("E_VFX_GRAPH_EXISTS", "VFX Graph already exists: " + path, "Pass overwrite=true or choose a different Assets/*.vfx path.");

            var effects = new List<VfxPlannedEffect> { new VfxPlannedEffect { Type = "vfx.graph.create", Path = path } };
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Graph = new VfxGraphInfo { Path = path, Name = Path.GetFileNameWithoutExtension(path), Type = graphType.Name },
                    DryRun = true,
                    PlannedEffects = effects
                });
            }

            ctx.CommitEffect();
            AssetToolSupport.CreateAssetDirectory(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            VfxToolSupport.CreateVfxGraphAsset(path, graphType);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var created = AssetDatabase.LoadAssetAtPath(path, graphType);
            if (created == null || !graphType.IsInstanceOfType(created))
                throw new UnityCliBridge.BridgeCommandException("E_VFX_GRAPH_CREATE_FAILED", "VFX Graph asset creation did not produce a readable VisualEffectAsset: " + path, "Check Unity Console and UnityCliBridge.log for package authoring API errors.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Graph = VfxToolSupport.BuildGraphInfo(path, created),
                PlannedEffects = effects
            });
        }
    }
}
#endif
