#if UNITY_EDITOR
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// shader.info — 读取 Shader 资产摘要(含编译诊断)。
    /// 对齐旧 ShaderInfo handler;只读。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class ShaderInfoTool : IUnityCliTool<ShaderInfoTool.Request, ShaderInfoTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Target { get; set; }
        }

        public sealed class Result
        {
            public ShaderInfo Shader { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "shader.info",
            Summary = "Return path, guid, name, asset type, size and compile diagnostics for an Assets/*.shader or *.shadergraph.",
            Description = "Return path, guid, name, asset type, ShaderGraph flag, byte size and compile diagnostics for an Assets/*.shader or *.shadergraph. Read-only; use shader.read for source text and shader.get-data for properties/subshader structure.",
            Group = "shader",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Inspect a shader", Input = new Request { Path = "Assets/Shaders/Example.shader" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var path = ShaderToolSupport.ResolveShaderPath(request.Path ?? request.Target);
            var shader = ShaderToolSupport.LoadShaderLikeAsset(path) as Shader;
            return new Result { Shader = ShaderToolSupport.BuildShaderInfo(path, shader, false) };
        }
    }
}
#endif
