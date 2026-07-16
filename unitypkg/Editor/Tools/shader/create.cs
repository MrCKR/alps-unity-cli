#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// shader.create — 创建 Shader 资产(.shader/.shadergraph)。拒绝覆盖(除非 overwrite)。
    /// </summary>
    public class ShaderCreateTool : IUnityCliTool<ShaderCreateTool.Request, ShaderCreateTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Content { get; set; }
            public string Name { get; set; }
            public bool? Overwrite { get; set; }
        }

        public sealed class Result
        {
            public ShaderInfo Shader { get; set; }
            public List<ShaderPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "shader.create",
            Summary = "Create a new Assets/*.shader (or .shadergraph) asset from source content or a default unlit template.",
            Description = "Create a new Shader asset at an Assets/*.shader path. With no content, a default unlit UnityCli/ shader is generated. For .shadergraph, content is required (pass a ShaderGraph JSON payload). Refuses to overwrite an existing shader unless overwrite=true. Creating a shader triggers recompilation.",
            Group = "shader",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write", "shader-recompile" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Create a default shader", Input = new Request { Path = "Assets/Shaders/New.shader" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ShaderToolSupport.EnsureEditorNotBusy("shader.create");
            var path = ShaderToolSupport.RequireShaderPath(request.Path);
            var overwrite = request.Overwrite ?? false;
            if (AssetToolSupport.AssetExists(path) && !overwrite)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SHADER_OUTPUT_EXISTS", "Shader output already exists: " + path, "Pass --overwrite or choose a different Assets/*.shader path.");
            }

            var content = request.Content;
            if (string.IsNullOrEmpty(content))
            {
                if (path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnityCliBridge.BridgeCommandException("E_SHADER_CONTENT_REQUIRED", "ShaderGraph creation requires content or content-file.", "Pass a ShaderGraph JSON payload via --content-file.");
                }
                content = ShaderToolSupport.DefaultShaderContent(request.Name, path);
            }

            AssetToolSupport.WriteTextAsset(path, content, overwrite);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Shader = ShaderToolSupport.BuildShaderInfo(path, AssetDatabase.LoadAssetAtPath<Shader>(path), false),
                PlannedEffects = new List<ShaderPlannedEffect> { new ShaderPlannedEffect { Type = "shader.create", Path = path, Refresh = true } }
            });
        }
    }
}
#endif
