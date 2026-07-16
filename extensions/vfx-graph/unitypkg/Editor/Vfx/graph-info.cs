#if UNITY_EDITOR
using UnityEditor;

namespace UnityCli
{
    /// <summary>读取 VFX Graph 资产元数据。</summary>
    public sealed class VfxGraphInfoTool : IUnityCliTool<VfxGraphInfoTool.Request, VfxGraphInfoTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Path { get; set; } }
        public sealed class Result { public VfxGraphInfo Graph { get; set; } public string LogPath { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.graph.info", Summary = "Read a VFX Graph asset.",
            Description = "Read metadata for one existing Assets/*.vfx Visual Effect Graph asset without modifying it.",
            Group = "vfx", Version = "1.0.0", RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new string[0], Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Read the matrix graph", Input = new Request { Path = "Assets/VFX/Explosion.vfx" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.EnsureEditorReady("vfx.graph.info");
            var path = VfxToolSupport.RequireVfxGraphPath(request.Path);
            var graph = AssetDatabase.LoadAssetAtPath(path, VfxToolSupport.RequireVfxAssetType());
            if (graph == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_GRAPH_NOT_FOUND", "VFX Graph asset not found: " + path, "Create it with vfx.graph.create or pass an existing Assets/*.vfx path.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Graph = VfxToolSupport.BuildGraphInfo(path, graph), LogPath = context.LogPath });
        }
    }
}
#endif
