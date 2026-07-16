#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>
    /// prefab.info — 读取 Prefab 资产摘要(变体来源/依赖/嵌套 Prefab)。
    /// </summary>
    public class PrefabInfoTool : IUnityCliTool<PrefabInfoTool.Request, PrefabInfoTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
        }

        public sealed class Result
        {
            public PrefabInfo Prefab { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "prefab.info",
            Summary = "Return path, guid, root name, asset type, variant source, dependency and nested-prefab summary for an Assets/*.prefab.",
            Description = "Return path, guid, root name, asset type, variant source, dependency and nested-prefab summary for an Assets/*.prefab. Read-only; use prefab.hierarchy for the child/component tree and prefab.modify to edit contents.",
            Group = "prefab",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Inspect a prefab", Input = new Request { Path = "Assets/Prefabs/Player.prefab" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = PrefabToolSupport.RequirePrefabPath(request.Path);
            var prefab = PrefabToolSupport.LoadPrefabAsset(path);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Prefab = PrefabToolSupport.BuildPrefabInfo(path, prefab, null, true) });
        }
    }
}
#endif
