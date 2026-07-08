#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// vfx.graph - 读取或创建 VFX Graph 资产。
    /// </summary>
    public class VfxGraphTool : IUnityCliTool<VfxGraphTool.Request, VfxGraphTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Action { get; set; }
            public bool? Create { get; set; }
            public bool? Overwrite { get; set; }
        }

        public sealed class Result
        {
            public VfxGraphInfo Graph { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.graph",
            Summary = "Read a VFX Graph asset or create a new Assets/*.vfx file with overwrite support.",
            Description = "Read a VFX Graph asset by path, or create a new Assets/*.vfx asset when action=create or create=true. Uses Unity's VFX asset authoring utility when available and falls back to minimal YAML bootstrapping when the package API differs.",
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
                new ToolExample { Description = "Read an existing graph", Input = new Request { Path = "Assets/VFX/Explosion.vfx" } },
                new ToolExample { Description = "Create a new graph", Input = new Request { Path = "Assets/VFX/Explosion.vfx", Action = "create", Overwrite = true } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            VfxToolSupport.EnsureEditorReady("vfx.graph");
            var path = VfxToolSupport.RequireVfxGraphPath(request.Path);
            var assetType = VfxToolSupport.RequireVfxAssetType();
            var create = request.Create == true || string.Equals(request.Action, "create", System.StringComparison.OrdinalIgnoreCase);
            if (!create)
            {
                var asset = AssetDatabase.LoadAssetAtPath(path, assetType);
                if (asset == null)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_VFX_GRAPH_NOT_FOUND",
                        "VFX Graph asset not found: " + path,
                        "Check the path or create one with unity_cli tools run vfx.graph --input '{\"path\":\"Assets/VFX/Effect.vfx\",\"action\":\"create\"}'.");
                }

                return new Result
                {
                    Graph = VfxToolSupport.BuildGraphInfo(path, asset),
                    LogPath = ctx.LogPath
                };
            }

            var overwrite = request.Overwrite == true;
            if (AssetDatabase.LoadAssetAtPath(path, assetType) != null && !overwrite)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_GRAPH_EXISTS",
                    "VFX Graph already exists: " + path,
                    "Pass overwrite=true or choose a different Assets/*.vfx path.");
            }

            AssetToolSupport.CreateAssetDirectory(path);
            if (AssetDatabase.LoadAssetAtPath(path, assetType) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }

            var created = VfxToolSupport.CreateVfxGraphAsset(path, assetType);
            if (created == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_GRAPH_CREATE_FAILED",
                    "VFX Graph asset creation did not produce a readable VisualEffectAsset: " + path,
                    "Check Unity Console and UnityCliBridge.log; the Visual Effect Graph package may have changed its internal authoring API.");
            }

            AssetDatabase.SaveAssets();
            return new Result
            {
                Graph = VfxToolSupport.BuildGraphInfo(path, created),
                PlannedEffects = new List<VfxPlannedEffect>
                {
                    new VfxPlannedEffect { Type = "vfx.graph.create", Path = path }
                },
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
