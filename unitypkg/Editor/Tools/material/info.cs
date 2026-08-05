#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// material.info — 读取 Material 摘要(shader/属性/纹理引用/依赖),支持 namePattern 轻量读取。
    /// </summary>
    public class MaterialInfoTool : IUnityCliTool<MaterialInfoTool.Request, MaterialInfoTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Material { get; set; }
            public string NamePattern { get; set; }
        }

        public sealed class Result
        {
            public MaterialInfo Material { get; set; }
            public bool NameFilterApplied { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "material.info",
            Summary = "Return shader, properties, texture references and dependencies for an Assets/*.mat; use namePattern for token-safe reads.",
            Description = "Return shader, render queue, keywords, properties, texture references and dependencies for an Assets/*.mat. Pass namePattern (comma-separated property names) for a token-safe lightweight read that skips full property expansion.",
            Group = "material",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Inspect a material", Input = new Request { Path = "Assets/Materials/Probe.mat" } },
                new ToolExample { Description = "Read select properties only", Input = new Request { Path = "Assets/Materials/Probe.mat", NamePattern = "_Color,_MainTex" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = MaterialToolSupport.RequireMaterialPath(request.Path ?? request.Material);
            var material = MaterialToolSupport.LoadMaterialAsset(path);
            var nameFilter = MaterialToolSupport.ParseNameFilter(request.NamePattern);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Material = MaterialToolSupport.BuildMaterialInfo(path, material, nameFilter),
                NameFilterApplied = nameFilter != null
            });
        }
    }
}
#endif
