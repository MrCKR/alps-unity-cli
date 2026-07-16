#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
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
            public string BridgeEndpoint { get; set; }
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
            Summary = "Read the current Unity Bridge instance summary from the live Editor process.",
            Description = "Read the live Unity Bridge process instance summary currently serving this request. This observes the active Bridge listener only; it does not read or prune the CLI instance registry on disk and does not modify targets.",
            Category = "instances",
            Version = "1.0.0",
            Source = "built-in",
            Target = "editor",
            AvailabilityScope = "always",
            PlayModeSupport = "supported",
            RefreshBehavior = "live",
            Examples = new[] { new ContextExample { Description = "Read the current Bridge instance", Input = new Request { } } }
        };

        /// <summary>
        /// 读取当前 Bridge listener 的只读摘要；通过窄反射读取 Bridge 自身的实例字段。
    /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var listenerId = ReadBridgeStringField("ListenerId") ?? "unknown";
            var bridgePort = ReadBridgeIntField("port");
            var endpoint = bridgePort > 0
                ? "http://127.0.0.1:" + bridgePort.ToString(CultureInfo.InvariantCulture)
                : null;

            var instances = new List<InstanceInfo>
            {
                new InstanceInfo
                {
                    InstanceId = listenerId,
                    ProjectPath = ProjectRoot(ctx),
                    UnityVersion = Application.unityVersion,
                    BridgeEndpoint = endpoint,
                    Status = "ready"
                }
            };

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Instances = instances,
                Count = instances.Count
            });
        }

        // 读取 UnityCliBridge 私有静态字符串字段，供实例摘要使用。
        private static string ReadBridgeStringField(string name)
        {
            var field = typeof(UnityCliBridge).GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return field == null ? null : field.GetValue(null) as string;
        }

        // 读取 UnityCliBridge 私有静态 int 字段；找不到时返回 0，让调用方省略 endpoint。
        private static int ReadBridgeIntField(string name)
        {
            var field = typeof(UnityCliBridge).GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null) return 0;
            var value = field.GetValue(null);
            return value is int intValue ? intValue : 0;
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
