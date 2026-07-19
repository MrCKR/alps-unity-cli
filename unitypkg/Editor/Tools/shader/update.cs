#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// shader.update — 用新 content 覆盖 Shader 源码并重新编译。
    /// </summary>
    public class ShaderUpdateTool : IUnityCliTool<ShaderUpdateTool.Request, ShaderUpdateTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Target { get; set; }
            public string Content { get; set; }
        }

        public sealed class Result
        {
            public ShaderInfo Shader { get; set; }
            public List<ShaderPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "shader.update",
            Summary = "Overwrite an existing shader's source content and force-recompile it.",
            Description = "Overwrite an existing Assets/*.shader (or .shadergraph) source with new content and force-reimport it. The shader must already exist. Updating triggers shader recompilation; check shader.get-data diagnostics afterward.",
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
                new ToolExample { Description = "Update shader source", Input = new Request { Path = "Assets/Shaders/Example.shader", Content = "Shader \"UnityCli/Example\" { SubShader { Pass {} } }" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ShaderToolSupport.EnsureEditorNotBusy("shader.update");
            var path = ShaderToolSupport.ResolveShaderPath(request.Path ?? request.Target);
            ShaderToolSupport.LoadShaderLikeAsset(path);
            var content = request.Content;
            if (content == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SHADER_CONTENT_REQUIRED", "Shader update requires content or content-file.", "Pass replacement Shader content via --content-file.");
            }

            ctx.CommitEffect();
            AssetToolSupport.WriteTextAsset(path, content, true);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            if (!File.Exists(AssetToolSupport.AssetFullPath(path)) || File.ReadAllText(AssetToolSupport.AssetFullPath(path)) != content)
                throw new UnityCliBridge.BridgeCommandException("E_SHADER_UPDATE_STATE_MISMATCH", "Fresh shader bytes did not match the requested content: " + path, "Check file permissions and retry.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Shader = ShaderToolSupport.BuildShaderInfo(path, AssetDatabase.LoadAssetAtPath<Shader>(path), false),
                PlannedEffects = new List<ShaderPlannedEffect> { new ShaderPlannedEffect { Type = "shader.update", Path = path, Refresh = true } }
            });
        }
    }
}
#endif
