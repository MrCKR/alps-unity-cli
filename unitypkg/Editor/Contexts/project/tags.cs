#if UNITY_EDITOR
using UnityEditorInternal;

namespace UnityCli.Contexts.Project
{
    /// <summary>
    /// project.tags 内置 Context：读取 TagManager 中定义的标签列表。
    /// </summary>
    public class ProjectTagsContext : IUnityCliContext<ProjectTagsContext.Request, ProjectTagsContext.Result>
    {
        public sealed class Request { }

        public sealed class Result
        {
            public string[] Tags { get; set; }
            public int Count { get; set; }
        }

        // ── desc:只声明人工字段,source=built-in 表示进入内置 Context 表 ──
        public ContextDescriptor Describe() => new ContextDescriptor
        {
            Name = "project.tags",
            Description = "List all tags defined in the project's Tag Manager.",
            Category = "project",
            Version = "1.0.0",
            Source = "built-in",
            Target = "editor",
            AvailabilityScope = "editorSession",
            PlayModeSupport = "supported",
            RefreshBehavior = "on-demand",
            Examples = new[] { new ContextExample { Description = "List configured project tags", Input = new Request { } } }
        };

        /// <summary>
        /// 读取 TagManager 标签；只读，不创建、删除或重命名标签。
    /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var tags = InternalEditorUtility.tags;
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Tags = tags,
                Count = tags.Length
            });
        }
    }
}
#endif
