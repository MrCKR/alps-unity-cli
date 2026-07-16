#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// shader.delete — 删除 Shader 资产。destructive,支持 dryRun。
    /// </summary>
    public class ShaderDeleteTool : IUnityCliTool<ShaderDeleteTool.Request, ShaderDeleteTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Target { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool? DryRun { get; set; }
            public ShaderInfo Shader { get; set; }
            public List<ShaderPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "shader.delete",
            Summary = "Delete an Assets/*.shader or *.shadergraph asset; destructive, supports --dry-run preview.",
            Description = "Delete an Assets/*.shader or *.shadergraph asset through AssetDatabase. Destructive: removes a persisted shader and may break materials referencing it. Supports --dry-run to preview the planned deletion without removing anything.",
            Group = "shader",
            Version = "1.0.0",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "asset-delete" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Preview deleting a shader", Input = new Request { Path = "Assets/Shaders/Old.shader" }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ShaderToolSupport.EnsureEditorNotBusy("shader.delete");
            var path = ShaderToolSupport.ResolveShaderPath(request.Path ?? request.Target);
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Command = "shader.delete",
                    DryRun = true,
                    Shader = ShaderToolSupport.BuildShaderInfo(path, AssetDatabase.LoadAssetAtPath<Shader>(path), false),
                    PlannedEffects = new List<ShaderPlannedEffect> { new ShaderPlannedEffect { Type = "shader.delete", Path = path } }
                });
            }
            if (!AssetDatabase.DeleteAsset(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SHADER_DELETE_FAILED", "Failed to delete shader asset: " + path, "Check Unity Console and file permissions.");
            }
            AssetDatabase.Refresh();
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "shader.delete",
                Shader = new ShaderInfo { Path = path, Exists = false },
                PlannedEffects = new List<ShaderPlannedEffect> { new ShaderPlannedEffect { Type = "shader.delete", Path = path } }
            });
        }
    }
}
#endif
