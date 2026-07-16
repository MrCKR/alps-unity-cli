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
        /// 作为 UnityCliBridge 的嵌套类实现，聚合 Editor 状态、capability 列表、项目工具/上下文
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
                public string Command { get; set; }
                public string TargetCommand { get; set; }
                public ProjectInfo Project { get; set; }
                [UnityCliAnyJson] public object Editor { get; set; }
                public BridgeInfo Bridge { get; set; }
                public CustomFingerprintInfo CustomTools { get; set; }
                public CustomFingerprintInfo CustomContexts { get; set; }
                [UnityCliAnyJson] public List<object> Checks { get; set; }
                public string LogPath { get; set; }
            }

            public sealed class ProjectInfo
            {
                public string Path { get; set; }
                public string UnityVersion { get; set; }
                public string ActiveScene { get; set; }
            }

            public sealed class BridgeInfo
            {
                public long Port { get; set; }
                public string LogPath { get; set; }
                [UnityCliAnyJson] public object Capabilities { get; set; }
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
                Summary = "Run a live Unity Editor health check for project path, editor busy state, bridge capabilities, custom tool/context fingerprints, and recoverable checks.",
                Description = "Run a live Unity Editor health check for project path, editor busy state, bridge capabilities, custom tool/context fingerprints, and recoverable checks. Use this before live bridge work when Unity may be compiling, importing, or using stale custom tool/context metadata.",
                Group = "diagnose", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
                DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
                Examples = new[]
                {
                    new ToolExample { Description = "Run live preflight", Input = new Request { } },
                    new ToolExample { Description = "Preflight before component work", Input = new Request { Command = "component.add" } }
                }
            };

            public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
                // 触发项目自定义工具/上下文发现,确保 fingerprint 摘要反映最新注册状态
                DiscoverProjectTools();
                DiscoverProjectContexts();

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
                    Command = "diagnose.preflight",
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
                        Port = (long)port,
                        LogPath = LogPath(),
                        Capabilities = BuildCapabilities()
                    },
                    CustomTools = new CustomFingerprintInfo { Fingerprint = ProjectToolFingerprintSummary() },
                    CustomContexts = new CustomFingerprintInfo { Fingerprint = ProjectContextFingerprintSummary() },
                    Checks = checks,
                    LogPath = LogPath()
                });
            }

            // 构造 {name, ok} 检查项(运行时 Dictionary,UnityCliSerializer 序列化为对象)
            private static Dictionary<string, object> MakeCheck(string name, bool ok)
            {
                return new Dictionary<string, object> { { "name", name }, { "ok", ok } };
            }

            // 构造 bridge capability 列表，保持与 HTTP capability discovery 相同的域和命令集合。
            private static List<object> BuildCapabilities()
            {
                var capabilities = new List<object>();
                AddCapability(capabilities, "camera", CameraCommands);
                AddCapability(capabilities, "scene", SceneCommands);
                AddCapability(capabilities, "go", GameObjectCommands);
                AddCapability(capabilities, "asset", AssetCommands);
                AddCapability(capabilities, "diagnose", DiagnoseCommands);
                AddCapability(capabilities, "screenshot", ScreenshotCommands);
                AddCapability(capabilities, "prefab", PrefabCommands);
                AddCapability(capabilities, "ui", UiCommands);
                AddCapability(capabilities, "material", MaterialCommands);
                AddCapability(capabilities, "graphics", GraphicsCommands);
                AddCapability(capabilities, "physics", PhysicsCommands);
                AddCapability(capabilities, "scriptable-object", ScriptableObjectCommands);
                AddCapability(capabilities, "profiler", ProfilerCommands);
                AddCapability(capabilities, "animation", AnimationCommands);
                AddCapability(capabilities, "shader", ShaderCommands);
                AddCapability(capabilities, "texture", TextureCommands);
                AddCapability(capabilities, "editor", EditorCommands);
                AddCapability(capabilities, "code", CodeCommands);
                AddCapability(capabilities, "project-tool", ProjectToolCommands);
                AddCapability(capabilities, "runtime-tool", RuntimeToolCommands);
                AddCapability(capabilities, "context", ContextCommands);
                AddCapability(capabilities, "extension", ExtensionCommands);
                return capabilities;
            }

            private static void AddCapability(List<object> capabilities, string domain, string[] commands)
            {
                capabilities.Add(new Dictionary<string, object>
                {
                    { "domain", domain },
                    { "route", "POST /bridge/execute" },
                    { "commands", commands }
                });
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
