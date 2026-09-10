#if UNITY_EDITOR
using UnityEditorInternal;

namespace UnityCli.Tools.Project
{
    /// <summary>
    /// project-settings.tags 只读 Tool：读取 TagManager 中定义的标签列表。
    /// </summary>
    public class ProjectTagsTool : IUnityCliTool<ProjectTagsTool.Request, ProjectTagsTool.Result>
    {
        public sealed class Request { }

        public sealed class Result
        {
            public string[] Tags { get; set; }
            public int Count { get; set; }
        }

        // ── desc:只声明人工字段,source=built-in 表示进入只读 Tool 表 ──
        public ToolDescriptor Describe() => new()
        {
            Name = "project-settings.tags", Summary = "List all tags defined in the project's Tag Manager.",
            Description = "List all tags defined in the project's Tag Manager.",
            DestructiveHint = false,
            Group = "project-settings",
            Target = "editor",
            PlayModeSupport = "supported",
            Examples = new[] { new ToolExample { Description = "List configured project tags", Input = new Request { } } }
        };

        /// <summary>
        /// 读取 TagManager 标签；只读，不创建、删除或重命名标签。
    /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
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
