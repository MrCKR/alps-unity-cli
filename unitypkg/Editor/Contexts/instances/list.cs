#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityCli.Contexts.Instances
{
    /// <summary>
    /// instances.list 内置 Context：读取当前 Unity Bridge 进程自身实例摘要。
    /// </summary>
    public class InstancesListContext : IUnityCliContext<InstancesListContext.Request, InstancesListContext.Result>
    {
        public sealed class Request { }

        public sealed class InstanceInfo
        {
            public string InstanceId { get; set; }
            public string ProjectPath { get; set; }
            public string UnityVersion { get; set; }
            public string Status { get; set; }
        }

        public sealed class Result
        {
            public List<InstanceInfo> Instances { get; set; }
            public long Count { get; set; }
        }

        public ContextDescriptor Describe() => new ContextDescriptor
        {
            Name = "instances.list",
            Summary = "Read the current logical Unity project instance summary from the live Editor process.",
            Description = "Read the CanonicalPath v1 project identity, canonical project path, Unity version, and ready status visible to the current Editor process.",
            Category = "instances",
            Source = "built-in",
            Target = "editor",
            AvailabilityScope = "always",
            PlayModeSupport = "supported",
            Examples = new[] { new ContextExample { Description = "Read the current Bridge instance", Input = new Request { } } }
        };

        /// <summary>读取当前 Editor 的逻辑项目 identity 和业务 readiness 摘要。</summary>
        public System.Threading.Tasks.ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var identity = UnityCliCanonicalProjectPath.Resolve(ProjectRoot(ctx));
            var instances = new List<InstanceInfo>
            {
                new InstanceInfo
                {
                    InstanceId = identity.InstanceId,
                    ProjectPath = identity.CanonicalProjectPath,
                    UnityVersion = Application.unityVersion,
                    Status = "ready"
                }
            };

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Instances = instances,
                Count = instances.Count
            });
        }

        // 优先使用 registry 注入的项目路径；缺失时回退到当前 Unity project root。
        private static string ProjectRoot(UnityCliProjectToolContext ctx)
        {
            if (ctx != null && !string.IsNullOrEmpty(ctx.ProjectPath)) return ctx.ProjectPath;
            return Path.GetDirectoryName(Application.dataPath);
        }
    }
}
#endif
