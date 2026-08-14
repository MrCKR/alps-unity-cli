#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>
    /// shader.get-data — 读取 Shader 属性/subshaders/passes/编译消息。可按 path 或 name(Shader.Find)解析。
    /// </summary>
    public class ShaderGetDataTool : IUnityCliTool<ShaderGetDataTool.Request, ShaderGetDataTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string AssetPath { get; set; }
            public string Name { get; set; }
            public bool? IncludeMessages { get; set; }
            public bool? IncludeProperties { get; set; }
            public bool? IncludeSubshaders { get; set; }
            public bool? IncludeSourceCode { get; set; }
            public int? MaxSubshaders { get; set; }
            public int? MaxPasses { get; set; }
        }

        public sealed class Result
        {
            public long RenderQueue { get; set; }
            public bool IsSupported { get; set; }
            public long PassCount { get; set; }
            public long PropertyCount { get; set; }
            public ShaderDiagnostics Diagnostics { get; set; }
            public List<ShaderProperty> Properties { get; set; }
            public bool SubshaderDataAvailable { get; set; }
            public List<ShaderSubshader> Subshaders { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "shader.get-data",
            Summary = "Read shader properties, subshaders/passes and compile diagnostics; resolve by path or Shader.Find name.",
            Description = "Read structured shader data: render queue, supported flag, pass count, properties (with type/flags/range/attributes), subshader/pass structure via ShaderUtil reflection, and compile diagnostics. Resolve by path/assetPath or by name (Shader.Find). includeSourceCode is opt-in (default false). Keep maxSubshaders/maxPasses bounded.",
            Group = "shader",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Read shader properties", Input = new Request { Path = "Assets/Shaders/Example.shader", IncludeProperties = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = request.Path ?? request.AssetPath;
            Shader shader;
            if (!string.IsNullOrEmpty(path))
            {
                shader = AssetDatabase.LoadMainAssetAtPath(path) as Shader;
            }
            else
            {
                shader = Shader.Find(request.Name);
            }
            if (shader == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SHADER_NOT_FOUND", "Shader not found.", "Run tools run shader.list --input '{\"keyword\":\"<keyword>\"}' to discover shader paths.");
            }
            var includeMessages = request.IncludeMessages ?? true;
            var includeProperties = request.IncludeProperties ?? true;
            var includeSubshaders = (request.IncludeSubshaders ?? true) || (request.IncludeSourceCode ?? false);
            var includeSourceCode = request.IncludeSourceCode ?? false;
            var maxSubshaders = Math.Max(0, Math.Min(request.MaxSubshaders ?? 8, 64));
            var maxPasses = Math.Max(0, Math.Min(request.MaxPasses ?? 16, 128));

            var properties = new List<ShaderProperty>();
            if (includeProperties)
            {
                for (var i = 0; i < shader.GetPropertyCount(); i++)
                {
                    var propType = shader.GetPropertyType(i);
                    var prop = new ShaderProperty
                    {
                        Name = shader.GetPropertyName(i),
                        Description = shader.GetPropertyDescription(i),
                        Type = propType.ToString(),
                        Flags = shader.GetPropertyFlags(i).ToString(),
                        NameId = shader.GetPropertyNameId(i),
                        DefaultTextureName = propType == UnityEngine.Rendering.ShaderPropertyType.Texture ? shader.GetPropertyTextureDefaultName(i) : null,
                        Attributes = new List<string>(shader.GetPropertyAttributes(i))
                    };
                    if (propType == UnityEngine.Rendering.ShaderPropertyType.Range)
                    {
                        var range = shader.GetPropertyRangeLimits(i);
                        prop.Range = new ShaderPropertyRange { Min = range.x, Max = range.y };
                    }
                    properties.Add(prop);
                }
            }

            var subshaders = includeSubshaders
                ? ShaderToolSupport.BuildSubshaders(shader, includeSourceCode, maxSubshaders, maxPasses)
                : new List<ShaderSubshader>();

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                RenderQueue = shader.renderQueue,
                IsSupported = shader.isSupported,
                PassCount = shader.passCount,
                PropertyCount = shader.GetPropertyCount(),
                Diagnostics = includeMessages
                    ? ShaderToolSupport.BuildDiagnostics(shader)
                    : ShaderToolSupport.BuildDiagnosticsSummary(shader),
                Properties = properties,
                SubshaderDataAvailable = ShaderToolSupport.SubshaderDataAvailable(shader),
                Subshaders = subshaders
            });
        }
    }
}
#endif
