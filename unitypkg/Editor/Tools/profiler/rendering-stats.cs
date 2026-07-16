#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// profiler.rendering-stats — 读取 UnityEditor.UnityStats 渲染采样统计。
    /// UnityStats 字段只在 Game View 实际渲染帧被填充;非渲染帧返回 null,available=false 并给出 diagnostics。
    /// </summary>
    public class ProfilerRenderingStatsTool : IUnityCliTool<ProfilerRenderingStatsTool.Request, ProfilerRenderingStatsTool.Result>
    {
        public sealed class Request
        {
            // profiler.rendering-stats 无入参
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool Available { get; set; }
            public RenderingStats Stats { get; set; }
            public string Diagnostics { get; set; }
            public List<string> NextActions { get; set; }
        }

        // UnityStats 反射读取的渲染计数器(字段只在渲染帧被填充,非渲染帧为 null)
        public sealed class RenderingStats
        {
            public string Batches { get; set; }
            public string Triangles { get; set; }
            public string Vertices { get; set; }
            public string SetPassCalls { get; set; }
            public string ScreenRes { get; set; }
            public string UsedTextureMemory { get; set; }
            public string RenderTextureCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.rendering-stats",
            Summary = "Read UnityStats rendering counters (batches, triangles, vertices, setPassCalls) for the current frame, with an available flag when stats are null (non-rendering frame).",
            Description = "Read rendering sample statistics from UnityEditor.UnityStats (batches, triangles, vertices, setPassCalls, screenRes, usedTextureMemory, renderTextureCount). These fields are only populated while the Game View is actively rendering; the response includes an `available` flag and diagnostics explaining when values are null, so callers do not mistake empty samples for data loss.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read rendering stats", Input = new Request { } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            // UnityStats 是 internal Editor 类型,不同 Unity 版本/反射策略下可能取不到
            var statsType = ProfilerToolSupport.FindType("UnityEditor.UnityStats");
            if (statsType == null)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Command = "profiler.rendering-stats",
                    Available = false,
                    Diagnostics = "UnityStats type is not available in this Unity version.",
                    NextActions = new List<string>
                    {
                        "Use a Unity 2022.x Editor where UnityEditor.UnityStats is reachable via reflection.",
                        "For per-frame GPU/render counts prefer profiler.frame or profiler.hierarchy while the Profiler is recording."
                    }
                });
            }

            var stats = new RenderingStats();
            var names = new[] { "batches", "triangles", "vertices", "setPassCalls", "screenRes", "usedTextureMemory", "renderTextureCount" };
            var anyValue = false;
            // 用反射名匹配属性(大小写敏感),按 UnityStats 实际成员名读取
            foreach (var name in names)
            {
                var value = ProfilerToolSupport.StringMemberValue(statsType, null, name);
                if (!string.IsNullOrEmpty(value)) anyValue = true;
                AssignStat(stats, name, value);
            }

            var result = new Result
            {
                Command = "profiler.rendering-stats",
                Available = anyValue,
                Stats = stats
            };
            if (!anyValue)
            {
                result.Diagnostics = "UnityStats fields are null for the sampled frame. Rendering statistics are only populated while the Game View is actively rendering (enter Play Mode or focus the Game View and retry).";
                result.NextActions = new List<string>
                {
                    "Enter Play Mode or make the Game View render at least one frame, then retry.",
                    "For stable per-frame timings use profiler.frame (FrameTimingManager) or profiler.hierarchy."
                };
            }
            return new System.Threading.Tasks.ValueTask<Result>(result);
        }

        // 按 UnityStats 成员名把反射读到的 string 值赋给 DTO 对应字段
        private static void AssignStat(RenderingStats stats, string name, string value)
        {
            switch (name)
            {
                case "batches": stats.Batches = value; break;
                case "triangles": stats.Triangles = value; break;
                case "vertices": stats.Vertices = value; break;
                case "setPassCalls": stats.SetPassCalls = value; break;
                case "screenRes": stats.ScreenRes = value; break;
                case "usedTextureMemory": stats.UsedTextureMemory = value; break;
                case "renderTextureCount": stats.RenderTextureCount = value; break;
            }
        }
    }
}
#endif
