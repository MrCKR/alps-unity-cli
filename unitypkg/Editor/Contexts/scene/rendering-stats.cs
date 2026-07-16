#if UNITY_EDITOR
using System;
using System.Reflection;

namespace UnityCli.Contexts.Scene
{
    /// <summary>
    /// scene.rendering-stats 内置 Context：读取 UnityStats 内部渲染统计可用性和摘要。
    /// </summary>
    public class SceneRenderingStatsContext : IUnityCliContext<SceneRenderingStatsContext.Request, SceneRenderingStatsContext.Result>
    {
        public sealed class Request { }

        public sealed class RenderingStats
        {
            public string Batches { get; set; }
            public string Triangles { get; set; }
            public string Vertices { get; set; }
            public string ScreenResolution { get; set; }
        }

        public sealed class Result
        {
            public bool Available { get; set; }
            public RenderingStats Stats { get; set; }
            public string Diagnostics { get; set; }
            public string[] NextActions { get; set; }
        }

        public ContextDescriptor Describe() => new ContextDescriptor
        {
            Name = "scene.rendering-stats",
            Summary = "Report UnityStats rendering counters when the internal Editor API is available.",
            Description = "Read current rendering statistics from UnityStats when available. If Unity's internal stats API is unavailable, returns available=false with diagnostics and nextActions.",
            Category = "scene",
            Version = "1.0.0",
            Source = "built-in",
            Target = "editor",
            AvailabilityScope = "editorSession",
            PlayModeSupport = "supported",
            RefreshBehavior = "live",
            Examples = new[] { new ContextExample { Description = "Read rendering statistics", Input = new Request { } } }
        };

        /// <summary>
        /// 通过反射读取 UnityStats；Unity 内部 API 不存在时返回可恢复诊断。
    /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var statsType = FindType("UnityEditor.UnityStats");
            if (statsType == null)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Available = false,
                    Stats = null,
                    Diagnostics = "UnityStats internal API not available in this Unity version. Rendering stats cannot be read.",
                    NextActions = new[] { "Rendering stats may be available through the Stats overlay window in the Game view." }
                });
            }

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Available = true,
                Stats = new RenderingStats
                {
                    Batches = StringMemberValue(statsType, null, "batches"),
                    Triangles = StringMemberValue(statsType, null, "triangles"),
                    Vertices = StringMemberValue(statsType, null, "vertices"),
                    ScreenResolution = StringMemberValue(statsType, null, "screenRes")
                },
                Diagnostics = null,
                NextActions = null
            });
        }

        // 查找 UnityStats 内部类型；不同 Unity 版本可能不存在该类型。
        private static Type FindType(string fullName)
        {
            var type = Type.GetType(fullName);
            if (type != null) return type;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(fullName);
                    if (type != null) return type;
                }
                catch
                {
                    // 忽略无法反射的编辑器程序集，继续查找其它程序集。
                }
            }
            return null;
        }

        // 读取 UnityStats 静态字段/属性，失败时返回 null 以保持 Context 只读且可恢复。
        private static string StringMemberValue(Type type, object instance, string name)
        {
            try
            {
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null) return Convert.ToString(property.GetValue(instance, null));
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return Convert.ToString(field.GetValue(instance));
            }
            catch
            {
                // 单个统计字段读取失败不影响整体 envelope。
            }
            return null;
        }
    }
}
#endif
