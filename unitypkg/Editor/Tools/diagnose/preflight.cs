#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// diagnose.preflight — 聚合 Unity Editor 健康诊断。
        ///
        /// 作为 UnityCliBridge 的嵌套类实现，聚合 Editor 状态、公开 capability 计数、项目 Tool/Context
        /// fingerprint 与编译诊断。所有动态字段直接构造为 Dictionary/List 值树。
        /// </summary>
        public sealed class DiagnosePreflightTool : IUnityCliTool<DiagnosePreflightTool.Request, DiagnosePreflightTool.Result>
        {
            public sealed class Request
            {
                public string Command { get; set; }
            }

            public sealed class Result
            {
                public string TargetCommand { get; set; }
                public ProjectInfo Project { get; set; }
                [UnityCliAnyJson] public object Editor { get; set; }
                public BridgeInfo Bridge { get; set; }
                public CustomFingerprintInfo CustomTools { get; set; }
                public CustomFingerprintInfo CustomContexts { get; set; }
                [UnityCliAnyJson] public List<object> Checks { get; set; }
            }

            public sealed class ProjectInfo
            {
                public string Path { get; set; }
                public string UnityVersion { get; set; }
                public string ActiveScene { get; set; }
            }

            public sealed class BridgeInfo
            {
                public bool Ready { get; set; }
                public long ToolCount { get; set; }
                public long ContextCount { get; set; }
            }

            public sealed class CustomFingerprintInfo
            {
                [UnityCliAnyJson] public object Fingerprint { get; set; }
            }

            public sealed class CompileStatusCheck
            {
                public string Name { get; set; }
                public bool Ok { get; set; }
                public bool Compiling { get; set; }
                public bool HasCompileErrors { get; set; }
                public List<string> CompileErrors { get; set; }
                public List<string> NextActions { get; set; }
            }

            public ToolDescriptor Describe() => new()
            {
                Name = "diagnose.preflight",
                Summary = "Run a live Unity Editor health check for project state, capability counts, custom fingerprints, and compile diagnostics.",
                Description = "Reads current project and Editor busy state, public capability counts, custom Tool/Context fingerprint health, and recent compile diagnostics without exposing Bridge routes, ports, or logs.",
                Group = "diagnose",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
                DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
                Examples = new[]
                {
                    new ToolExample { Description = "Run live preflight", Input = new Request { } },
                    new ToolExample { Description = "Preflight before component work", Input = new Request { Command = "component.add" } }
                }
            };

            public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
            {
                // preflight 只读取已经发布的 registry 快照，不能为了诊断触发 discovery 或写入缓存。
                var scene = SceneManager.GetActiveScene();
                var checks = new List<object>
                {
                    MakeCheck("unityNotBusy", !(EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)),
                    MakeCheck("sceneLoaded", scene.isLoaded),
                    MakeCheck("projectToolFingerprint", !ProjectTools.Values.Any(definition => definition.FingerprintStatus == "blocked")),
                    MakeCheck("projectContextFingerprint", !ProjectContexts.Values.Any(definition => definition.FingerprintStatus == "blocked")),
                    BuildCompileStatus()
                };

                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    TargetCommand = request.Command,
                    Project = new ProjectInfo
                    {
                        Path = ProjectRoot(),
                        UnityVersion = Application.unityVersion,
                        ActiveScene = scene.path
                    },
                    Editor = new Dictionary<string, object>
                    {
                        { "busy", EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer },
                        { "compiling", EditorApplication.isCompiling },
                        { "updating", EditorApplication.isUpdating },
                        { "building", BuildPipeline.isBuildingPlayer }
                    },
                    Bridge = new BridgeInfo
                    {
                        Ready = true,
                        ToolCount = ProjectTools.Count,
                        ContextCount = ProjectContexts.Count
                    },
                    CustomTools = new CustomFingerprintInfo { Fingerprint = ProjectToolFingerprintSummary() },
                    CustomContexts = new CustomFingerprintInfo { Fingerprint = ProjectContextFingerprintSummary() },
                    Checks = checks
                });
            }

            // 构造 {name, ok} 检查项(运行时 Dictionary,UnityCliSerializer 序列化为对象)
            private static Dictionary<string, object> MakeCheck(string name, bool ok)
            {
                return new Dictionary<string, object> { { "name", name }, { "ok", ok } };
            }

            // 编译状态检查:isCompiling + 近期编译错误(通过反射 LogEntries)。
            // 扩展编译失败会让 Unity 持续无法完成编译、Bridge 报 E_UNITY_COMPILING,这里把状态和错误明示出来,
            // 让 doctor 能定位是"扩展编译失败"而非"bridge 不可达"。
            private static CompileStatusCheck BuildCompileStatus()
            {
                var compiling = EditorApplication.isCompiling;
                var hasCompileErrors = false;
                var errors = new List<string>();
                try
                {
                    var logEntriesType = FindType("UnityEditor.LogEntries") ?? FindType("UnityEditorInternal.LogEntries");
                    if (logEntriesType != null)
                    {
                        var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                        var start = logEntriesType.GetMethod("StartGettingEntries", flags);
                        var end = logEntriesType.GetMethod("EndGettingEntries", flags);
                        var count = logEntriesType.GetMethod("GetCount", flags);
                        var getEntry = logEntriesType.GetMethod("GetEntryInternal", flags);
                        var getLines = logEntriesType.GetMethod("GetLinesAndModeFromEntryInternal", flags)
                            ?? logEntriesType.GetMethod("GetLinesAndModeInternal", flags);
                        var total = start != null
                            ? Convert.ToInt32(start.Invoke(null, Type.EmptyTypes), CultureInfo.InvariantCulture)
                            : count == null ? 0 : Convert.ToInt32(count.Invoke(null, Type.EmptyTypes), CultureInfo.InvariantCulture);
                        try
                        {
                            var inspected = 0;
                            for (var i = total - 1; i >= 0 && inspected < 60; i--, inspected++)
                            {
                                string line = null;
                                var mode = 0;
                                try
                                {
                                    if (getLines != null && getLines.GetParameters().Length == 4)
                                    {
                                        var args = new object[] { i, 1, 0, null };
                                        getLines.Invoke(null, args);
                                        mode = Convert.ToInt32(args[2], CultureInfo.InvariantCulture);
                                        line = args[3] as string;
                                    }
                                    else if (getEntry != null && getEntry.GetParameters().Length == 4)
                                    {
                                        var args = new object[4] { i, null, null, null };
                                        getEntry.Invoke(null, args);
                                        line = args[1] as string;
                                        mode = args[2] == null ? 0 : Convert.ToInt32(args[2], CultureInfo.InvariantCulture);
                                    }
                                    else if (getLines != null && getLines.GetParameters().Length == 2)
                                    {
                                        var args = new object[] { i, null };
                                        getLines.Invoke(null, args);
                                        line = args[1] as string;
                                    }
                                }
                                catch { }
                                if (string.IsNullOrEmpty(line)) continue;
                                // 仅关注编译错误(CS####)。mode 位掩码中 bit1 为 error。
                                var isCompileError = line.IndexOf("CS", StringComparison.OrdinalIgnoreCase) >= 0 && line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;
                                if (isCompileError || ((mode & 1) != 0 && line.IndexOf("Compile", StringComparison.OrdinalIgnoreCase) >= 0))
                                {
                                    hasCompileErrors = true;
                                    errors.Add(line);
                                    if (errors.Count >= 20) break;
                                }
                            }
                        }
                        finally
                        {
                            if (start != null && end != null) end.Invoke(null, Type.EmptyTypes);
                        }
                    }
                }
                catch (Exception exception)
                {
                    errors.Insert(0, "Failed to read compile errors: " + exception.GetType().Name);
                }
                var nextActions = new List<string>();
                if (hasCompileErrors) nextActions.Add("Review the listed compile errors (often from a broken extension under Packages/com.unity_cli/Extensions). Fix or remove the offending extension, then let Unity recompile.");
                if (compiling) nextActions.Add("Wait for Unity to finish compiling, then re-run doctor.");
                if (hasCompileErrors && File.Exists(Path.Combine(ProjectRoot(), "Packages", "com.unity_cli", "Extensions", "extension.json")))
                {
                    nextActions.Add("If an extension no longer compiles, remove it via unity_cli extensions remove <id> --project <path>.");
                }
                return new CompileStatusCheck
                {
                    Name = "compileStatus",
                    Ok = !compiling && !hasCompileErrors,
                    Compiling = compiling,
                    HasCompileErrors = hasCompileErrors,
                    CompileErrors = errors,
                    NextActions = nextActions
                };
            }

        }
    }
}
#endif
