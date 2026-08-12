#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli.Tools.Project
{
    /// <summary>
    /// project.layers 只读 Tool：读取 TagManager 中定义的 Layer 索引和名称。
    /// </summary>
    public class ProjectLayersTool : IUnityCliTool<ProjectLayersTool.Request, ProjectLayersTool.Result>
    {
        public sealed class Request { }

        public sealed class LayerEntry
        {
            public int Index { get; set; }
            public string Name { get; set; }
        }

        public sealed class Result
        {
            public LayerEntry[] Layers { get; set; }
            public int Count { get; set; }
        }

        // ── desc:只声明人工字段,source=built-in 表示进入只读 Tool 表 ──
        public ToolDescriptor Describe() => new()
        {
            Name = "project.layers", Summary = "List all layers defined in the project's Tag Manager.",
            Description = "List all layers defined in the project's Tag Manager.",
            DestructiveHint = false,
            Group = "project",
            Target = "editor",
            PlayModeSupport = "supported",
            Examples = new[] { new ToolExample { Description = "List configured project layers", Input = new Request { } } }
        };

        /// <summary>
        /// 读取非空 Layer 槽位；只读，不修改 TagManager 或项目设置。
    /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var layers = new List<LayerEntry>();
            for (var i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(name))
                {
                    layers.Add(new LayerEntry { Index = i, Name = name });
                }
            }

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Layers = layers.ToArray(),
                Count = layers.Count
            });
        }
    }
}
#endif
