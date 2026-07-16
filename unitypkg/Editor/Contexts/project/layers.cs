#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli.Contexts.Project
{
    /// <summary>
    /// project.layers 内置 Context：读取 TagManager 中定义的 Layer 索引和名称。
    /// </summary>
    public class ProjectLayersContext : IUnityCliContext<ProjectLayersContext.Request, ProjectLayersContext.Result>
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

        // ── desc:只声明人工字段,source=built-in 表示进入内置 Context 表 ──
        public ContextDescriptor Describe() => new ContextDescriptor
        {
            Name = "project.layers",
            Description = "List all layers defined in the project's Tag Manager.",
            Category = "project",
            Version = "1.0.0",
            Source = "built-in",
            Target = "editor",
            AvailabilityScope = "editorSession",
            PlayModeSupport = "supported",
            RefreshBehavior = "on-demand",
            Examples = new[] { new ContextExample { Description = "List configured project layers", Input = new Request { } } }
        };

        /// <summary>
        /// 读取非空 Layer 槽位；只读，不修改 TagManager 或项目设置。
    /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
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
