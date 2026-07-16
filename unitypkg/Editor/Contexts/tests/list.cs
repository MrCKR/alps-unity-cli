#if UNITY_EDITOR
using System;

namespace UnityCli.Contexts.Tests
{
    /// <summary>
    /// tests.list 内置 Context：读取 Unity Test Runner 可用性和测试发现诊断。
    /// </summary>
    public class TestsListContext : IUnityCliContext<TestsListContext.Request, TestsListContext.Result>
    {
        public sealed class Request
        {
            public string Mode { get; set; }
            public int PageSize { get; set; }
            public string Cursor { get; set; }
        }

        public sealed class TestInfo { }

        public sealed class Result
        {
            public bool Available { get; set; }
            public TestInfo[] Tests { get; set; }
            public int Count { get; set; }
            public string Diagnostics { get; set; }
            public string[] NextActions { get; set; }
        }

        // ── desc:只声明人工字段,source=built-in 表示进入内置 Context 表 ──
        public ContextDescriptor Describe() => new ContextDescriptor
        {
            Name = "tests.list",
            Summary = "Report Unity Test Runner discovery availability and diagnostics.",
            Description = "List all test fixtures and test methods in the project.",
            Category = "tests",
            Version = "1.0.0",
            Source = "built-in",
            Target = "editor",
            AvailabilityScope = "editorSession",
            PlayModeSupport = "supported",
            RefreshBehavior = "on-demand",
            Examples = new[] { new ContextExample { Description = "List available EditMode tests", Input = new Request { Mode = "EditMode", PageSize = 50 } }, new ContextExample { Description = "List available PlayMode tests", Input = new Request { Mode = "PlayMode", PageSize = 50 } } }
        };

        /// <summary>
        /// 检查 Unity Test Runner 相关类型是否可用；当前不做深度枚举，返回稳定诊断字段。
    /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var testRunnerType = FindType("UnityEditor.TestTools.TestRunner.TestRunnerWindow")
                ?? FindType("UnityEditor.TestRunner.TestRunnerWindow");
            var editorTestType = FindType("NUnit.Framework.TestAttribute");

            if (testRunnerType == null && editorTestType == null)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Available = false,
                    Tests = new TestInfo[0],
                    Count = 0,
                    Diagnostics = "Unity Test Framework is not installed or not available. Test enumeration requires the Test Runner package.",
                    NextActions = new[]
                    {
                        "Install the Unity Test Framework package via Package Manager.",
                        "Enable the Test Runner window: Window > General > Test Runner."
                    }
                });
            }

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Available = true,
                Tests = new TestInfo[0],
                Count = 0,
                Diagnostics = "Test enumeration via reflection is not reliably available. Use the Unity Test Runner window for full test discovery, or run tests via the CLI test runner tool.",
                NextActions = null
            });
        }

        // 在当前 AppDomain 中查找 Unity/NUnit 类型；找不到时返回 null 并输出可恢复诊断。
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
    }
}
#endif
