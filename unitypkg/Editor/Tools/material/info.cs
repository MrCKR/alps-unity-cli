#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// material.info — 读取 Material 摘要(shader/属性/纹理引用/依赖),支持 namePattern 轻量读取。
    /// 对齐旧 MaterialInfo handler;只读。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
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
            Version = "1.0.0",
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var path = MaterialToolSupport.RequireMaterialPath(request.Path ?? request.Material);
            var material = MaterialToolSupport.LoadMaterialAsset(path);
            var nameFilter = MaterialToolSupport.ParseNameFilter(request.NamePattern);
            return new Result
            {
                Material = MaterialToolSupport.BuildMaterialInfo(path, material, nameFilter),
                NameFilterApplied = nameFilter != null
            };
        }
    }
}
#endif
