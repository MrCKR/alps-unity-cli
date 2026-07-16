#if UNITY_EDITOR
using System.IO;
using UnityEngine;

namespace UnityCli.Contexts.Project
{
    /// <summary>
    /// project.info 内置 Context：读取 Unity 项目基础元数据。
    /// </summary>
    public class ProjectInfoContext : IUnityCliContext<ProjectInfoContext.Request, ProjectInfoContext.Result>
    {
        public sealed class Request { }

        public sealed class Result
        {
            public string ProjectRoot { get; set; }
            public string ProjectName { get; set; }
            public string UnityVersion { get; set; }
            public string Platform { get; set; }
            public string AssetsPath { get; set; }
        }

        // ── desc:只声明人工字段,source=built-in 表示进入内置 Context 表 ──
        public ContextDescriptor Describe() => new ContextDescriptor
        {
            Name = "project.info",
            Description = "Project metadata: name, path, Unity version, and overall project structure.",
            Category = "project",
            Version = "1.0.0",
            Source = "built-in",
            Target = "editor",
            AvailabilityScope = "editorSession",
            PlayModeSupport = "supported",
            RefreshBehavior = "static",
            Examples = new[] { new ContextExample { Description = "Read project metadata", Input = new Request { } } }
        };

        /// <summary>
        /// 读取项目路径、项目名、Unity 版本、运行平台和 Assets 路径；只读，不触发 import/refresh。
    /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                ProjectRoot = projectRoot,
                ProjectName = Path.GetFileName(projectRoot),
                UnityVersion = Application.unityVersion,
                Platform = Application.platform.ToString(),
                AssetsPath = Application.dataPath
            });
        }
    }
}
#endif
