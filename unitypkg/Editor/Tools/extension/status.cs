#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnityCli
{
    /// <summary>
    /// extension.status — 返回可选 Unity 包的安装可用性(Library/PackageCache 探测)。
    /// </summary>
    public class ExtensionStatusTool : IUnityCliTool<ExtensionStatusTool.Request, ExtensionStatusTool.Result>
    {
        public sealed class Request
        {
            public string ExtensionId { get; set; }
            public string Id { get; set; }
        }

        public sealed class Result
        {
            public bool Ok { get; set; } = true;
            public List<string> Installed { get; set; }
            public List<PackageAvailability> Packages { get; set; }
        }

        public sealed class PackageAvailability
        {
            public string Name { get; set; }
            public bool Available { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "extension.status",
            Summary = "Report optional Unity package availability for CLI extension flows.",
            Description = "Return basic package availability status for CLI-side extension flows. Probes Library/PackageCache for known optional packages (probuilder, cinemachine, visualeffectgraph, URP, HDRP). Install/remove are handled by the CLI command layer.",
            Group = "extension",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Check extension package availability", Input = new Request { } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            // 已知可选包列表,用于检测扩展依赖的 Unity 包是否已安装
            var optionalPackages = new[]
            {
                "com.unity.probuilder",
                "com.unity.cinemachine",
                "com.unity.visualeffectgraph",
                "com.unity.render-pipelines.universal",
                "com.unity.render-pipelines.high-definition"
            };

            var packages = new List<PackageAvailability>();
            foreach (var pkg in optionalPackages.OrderBy(p => p, StringComparer.Ordinal))
            {
                var available = false;
                try
                {
                    var matches = Directory.GetFiles("Library/PackageCache", pkg + "@*", SearchOption.TopDirectoryOnly);
                    available = matches.Length > 0;
                }
                catch
                {
                    available = false;
                }
                packages.Add(new PackageAvailability { Name = pkg, Available = available });
            }

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Ok = true,
                Installed = new List<string>(),
                Packages = packages
            });
        }
    }
}
#endif
