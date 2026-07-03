#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// shader.list — 列出项目中所有 Shader 资产,可按 keyword 过滤。
    /// 对齐旧 ShaderList handler;只读,分页。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class ShaderListTool : IUnityCliTool<ShaderListTool.Request, ShaderListTool.Result>
    {
        public sealed class Request
        {
            public string Keyword { get; set; }
            public string Search { get; set; }
            public int? PageSize { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public long Count { get; set; }
            public List<ShaderListItem> Shaders { get; set; }
        }

        public sealed class ShaderListItem
        {
            public string Path { get; set; }
            public string Guid { get; set; }
            public string Name { get; set; }
            public long PropertyCount { get; set; }
            public long RenderQueue { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "shader.list",
            Summary = "List all Shader assets in the project, optionally filtered by keyword, paged.",
            Description = "List all Shader assets in the project via AssetDatabase. Optional keyword filters by asset name/path. Results are paged via pageSize (default 100, max 500). Use shader.info or shader.get-data for details on a specific shader.",
            Group = "shader",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List shaders matching a keyword", Input = new Request { Keyword = "Lit", PageSize = 25 } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var keyword = request.Keyword ?? request.Search;
            var guids = AssetDatabase.FindAssets("t:Shader " + (keyword ?? string.Empty));
            var pageSize = Math.Max(1, Math.Min(request.PageSize ?? 100, 500));
            var results = new List<ShaderListItem>();
            foreach (var guid in guids)
            {
                if (results.Count >= pageSize) break;
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var shader = AssetDatabase.LoadMainAssetAtPath(assetPath) as Shader;
                if (shader == null) continue;
                results.Add(new ShaderListItem
                {
                    Path = AssetToolSupport.NormalizeAssetPath(assetPath),
                    Guid = guid,
                    Name = shader.name,
                    PropertyCount = shader.GetPropertyCount(),
                    RenderQueue = shader.renderQueue
                });
            }
            return new Result
            {
                Command = "shader.list",
                Count = results.Count,
                Shaders = results
            };
        }
    }
}
#endif
