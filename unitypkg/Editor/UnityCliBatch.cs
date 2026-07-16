#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEngine;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.Build.Reporting;

namespace UnityCli
{
    public static class UnityCliBatch
    {
        /// <summary>
        /// 启动 batchmode Bridge 请求；异步续体由中央 scheduler 驱动，完成后退出 Unity。
        /// </summary>
        public static void RunBridgeCommand()
        {
            UnityCliBridge.EnsureScheduler();
            _ = RunBridgeCommandAsync(ArgValue("-unityCliBridgeRequest") ?? string.Empty);
        }

        // 执行 batch Bridge 请求并按结果退出 Unity。
        private static async Task RunBridgeCommandAsync(string requestJson)
        {
            var exitCode = await UnityCliBridge.ExecuteBatchCommandAsync(requestJson);
            EditorApplication.Exit(exitCode);
        }

        /// <summary>
        /// 在 batchmode 中编译并执行 C# 文件；会写入结果 artifact、重定向标准输出并以结构化错误退出。
        /// </summary>
        public static void ExecuteCode()
        {
            var requestId = ArgValue("-unityCliRequestId") ?? Guid.NewGuid().ToString("N");
            var sourcePath = ArgValue("-unityCliCodeSource");
            var resultPath = ArgValue("-unityCliCodeResult");
            var artifactDir = ArgValue("-unityCliArtifactDir");
            var argsJson = ArgValue("-unityCliCodeArgs") ?? "{}";
            var startedAt = DateTime.UtcNow;
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    WriteCodeResult(resultPath, ErrorEnvelope(requestId, "E_CODE_SOURCE_INVALID", "Code source file was not found: " + sourcePath, new Dictionary<string, object>(), new[] { "Pass --file, --stdin or --code with readable C# source." }));
                    EditorApplication.Exit(1);
                    return;
                }
                Directory.CreateDirectory(string.IsNullOrEmpty(artifactDir) ? Path.GetDirectoryName(resultPath) : artifactDir);
                var source = File.ReadAllText(sourcePath, Encoding.UTF8);
                var result = ExecuteCodeSource(requestId, source, argsJson, ArgValue("-unityCliCodeRefs"), ArgValue("-unityCliCodeDefines"), startedAt);
                var json = result.Ok ? SuccessEnvelope(requestId, result.Data) : ErrorEnvelope(requestId, result.ErrorCode, result.ErrorMessage, result.Details, result.NextActions);
                WriteCodeResult(resultPath, json);
                if (!result.Ok)
                {
                    Console.Error.WriteLine(result.ErrorCode + ": " + result.ErrorMessage);
                }
                EditorApplication.Exit(result.Ok ? 0 : 1);
            }
            catch (Exception exception)
            {
                var details = new Dictionary<string, object>
                {
                    { "exceptionType", exception.GetType().FullName }, { "stackTrace", exception.StackTrace }
                };
                WriteCodeResult(resultPath, ErrorEnvelope(requestId, "E_CODE_RUNTIME_EXCEPTION", exception.Message, details, new[] { "Inspect batchmode log and result artifact for exception details." }));
                Console.Error.WriteLine("E_CODE_RUNTIME_EXCEPTION: " + exception.Message);
                EditorApplication.Exit(1);
            }
        }

        // 刷新 Unity 资产数据库并退出 batchmode。
        public static void Refresh()
        {
            AssetDatabase.Refresh();
            Console.WriteLine("AssetDatabase refresh completed.");
            EditorApplication.Exit(0);
        }

        // 刷新资产数据库以触发脚本导入和编译。
        public static void RefreshAndCompile()
        {
            AssetDatabase.Refresh();
            Console.WriteLine("AssetDatabase refresh and compile requested.");
            EditorApplication.Exit(0);
        }

        // 请求 Package Manager 解析依赖并异步等待结果。
        public static void ResolvePackages()
        {
            Client.Resolve();
            var request = Client.List(false, true);
            if (request.IsCompleted)
            {
                OnPackagesResolved(request);
                return;
            }

            UnityCliBridge.EnsureScheduler();
            var waiter = new UnityCliWaiter("batch-package-resolve", "batch.package-resolve");
            _ = ResolvePackagesAsync(request, waiter);
        }

        // 等待包解析完成或超时，并据此退出 Unity。
        private static async Task ResolvePackagesAsync(Request request, IUnityCliWaiter waiter)
        {
            var ready = await waiter.Until(() => request.IsCompleted, TimeSpan.FromSeconds(30));
            if (!ready)
            {
                Console.Error.WriteLine("E_PACKAGE_RESOLVE_TIMEOUT: Unity PackageManager resolve did not complete within 30 seconds.");
                EditorApplication.Exit(1);
                return;
            }

            OnPackagesResolved(request);
        }

        // 处理 Package Manager 完成状态并刷新资产。
        private static void OnPackagesResolved(Request request)
        {
            if (request.Status == StatusCode.Failure)
            {
                var error = request.Error != null ? request.Error.message : "Unity PackageManager resolve failed.";
                Console.Error.WriteLine("E_PACKAGE_RESOLVE_FAILED: " + error);
                EditorApplication.Exit(1);
                return;
            }

            AssetDatabase.Refresh();
            Console.WriteLine("Package resolve completed.");
            EditorApplication.Exit(0);
        }

        // 按命令行目标和输出路径构建已启用场景。
        public static void BuildProject()
        {
            var outputPath = ArgValue("-unityCliBuildOutput");
            if (string.IsNullOrEmpty(outputPath))
            {
                Console.Error.WriteLine("E_BUILD_OUTPUT_REQUIRED: Missing -unityCliBuildOutput.");
                EditorApplication.Exit(1);
                return;
            }

            var target = EditorUserBuildSettings.activeBuildTarget;
            var targetArg = ArgValue("-buildTarget");
            if (!string.IsNullOrEmpty(targetArg) && !Enum.TryParse(targetArg, true, out target))
            {
                Console.Error.WriteLine("E_BUILD_TARGET_INVALID: Unknown build target: " + targetArg);
                EditorApplication.Exit(1);
                return;
            }

            var scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            if (scenes.Length == 0)
            {
                Console.Error.WriteLine("E_BUILD_SCENES_EMPTY: No enabled scenes in Build Settings.");
                EditorApplication.Exit(1);
                return;
            }

            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);

            var report = BuildPipeline.BuildPlayer(scenes, outputPath, target, BuildOptions.None);
            var summary = report.summary;
            Console.WriteLine("Build " + summary.result + ": " + outputPath + " (" + summary.totalErrors.ToString(CultureInfo.InvariantCulture) + " errors, " + summary.totalWarnings.ToString(CultureInfo.InvariantCulture) + " warnings)");
            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        // 读取指定命令行参数的值。
        private static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var index = 0; index < args.Length - 1; index += 1)
            {
                if (args[index] == name)
                {
                    var value = args[index + 1];
                    if (value == "__UNITY_CLI_EMPTY__" || value.StartsWith("-", StringComparison.Ordinal)) return string.Empty;
                    return value;
                }
            }
            return null;
        }

        // 编译源码并执行约定的 Run 方法，收集诊断、标准输出和运行时异常。
        private static CodeExecutionResult ExecuteCodeSource(string requestId, string source, string argsJson, string refsCsv, string definesCsv, DateTime startedAt)
        {
            CompilerResults compiled;
            try
            {
                var provider = new CSharpCodeProvider();
                var parameters = new CompilerParameters { GenerateExecutable = false, GenerateInMemory = true, TreatWarningsAsErrors = false };
                var references = DefaultReferences(refsCsv).ToList();
                var referenceError = ValidateReferences(references);
                if (!string.IsNullOrEmpty(referenceError))
                {
                    var details = new Dictionary<string, object> { { "referenceError", referenceError } };
                    return CodeExecutionResult.Failed("E_REFERENCE_RESOLUTION_FAILED", referenceError, details, "Check Unity/Editor/project assembly references and --refs.");
                }
                foreach (var reference in references) parameters.ReferencedAssemblies.Add(reference);
                foreach (var define in SplitCsv(definesCsv)) parameters.CompilerOptions += " /define:" + define;
                compiled = provider.CompileAssemblyFromSource(parameters, WrapCodeSource(source));
            }
            catch (Exception exception)
            {
                return CodeExecutionResult.Failed("E_REFERENCE_RESOLUTION_FAILED", "Failed to compile Runtime Code: " + exception.Message, ExceptionDetails(exception, "", ""), "Check Unity/Editor/project assembly references and --refs.");
            }
            if (compiled.Errors.HasErrors)
            {
                var details = new Dictionary<string, object> { { "diagnostics", Diagnostics(compiled.Errors) } };
                return CodeExecutionResult.Failed("E_CODE_COMPILE_FAILED", "Runtime Code compilation failed.", details, "Fix compiler diagnostics and rerun code exec.");
            }
            var stdout = new StringWriter(CultureInfo.InvariantCulture);
            var stderr = new StringWriter(CultureInfo.InvariantCulture);
            var previousOut = Console.Out;
            var previousError = Console.Error;
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var type = compiled.CompiledAssembly.GetType("UnityCli.RuntimeCode.Script", true);
                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                var returnValue = method.Invoke(null, new object[] { argsJson ?? "{}" });
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                var endedAt = DateTime.UtcNow;
                return CodeExecutionResult.Completed(CodeSuccessData(requestId, startedAt, endedAt, stdout.ToString(), stderr.ToString(), returnValue, argsJson));
            }
            catch (TargetInvocationException exception)
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                var inner = exception.InnerException ?? exception;
                return CodeExecutionResult.Failed("E_CODE_RUNTIME_EXCEPTION", inner.GetType().FullName + ": " + inner.Message, ExceptionDetails(inner, stdout.ToString(), stderr.ToString()), "Inspect runtime stdout/stderr and exception stackTrace.");
            }
            catch (Exception exception)
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                return CodeExecutionResult.Failed("E_CODE_RUNTIME_EXCEPTION", exception.GetType().FullName + ": " + exception.Message, ExceptionDetails(exception, stdout.ToString(), stderr.ToString()), "Inspect runtime stdout/stderr and exception stackTrace.");
            }
        }

        // 将源码包装为运行时约定的 Script.Run 入口。
        private static string WrapCodeSource(string source)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(source, "\\bpublic\\s+static\\s+object\\s+Run\\s*\\("))
            {
                return "using System; using System.IO; using System.Linq; using System.Collections.Generic; using UnityEngine; using UnityEditor; namespace UnityCli.RuntimeCode { public static class Script { " + source + " } }";
            }
            return "using System; using System.IO; using System.Linq; using System.Collections.Generic; using UnityEngine; using UnityEditor; namespace UnityCli.RuntimeCode { public static class Script { public static object Run(string argsJson) { " + source + " } } }";
        }

        // 汇总 batchmode 编译所需的 Unity、项目和调用方引用。
        private static IEnumerable<string> DefaultReferences(string refsCsv)
        {
            var references = new List<string>();
            AddReference(references, typeof(object).Assembly.Location);
            AddReference(references, typeof(Enumerable).Assembly.Location);
            AddReference(references, typeof(UnityEngine.Object).Assembly.Location);
            AddReference(references, typeof(EditorApplication).Assembly.Location);
            // CodeDom 使用 classic Mono profile；显式加入 netstandard facade 会与 mscorlib 重复定义系统类型。
            AddReferenceIfExists(references, Path.Combine(EditorApplication.applicationContentsPath, "Managed", "UnityEngine", "UnityEngine.dll"));
            AddReferenceIfExists(references, Path.Combine(EditorApplication.applicationContentsPath, "Managed", "UnityEngine", "UnityEngine.CoreModule.dll"));
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;
                var name = assembly.GetName().Name;
                if (name.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase)) AddReference(references, assembly.Location);
            }
            var scriptAssemblies = Path.Combine(ProjectPath(), "Library", "ScriptAssemblies");
            if (Directory.Exists(scriptAssemblies))
            {
                foreach (var assemblyPath in Directory.GetFiles(scriptAssemblies, "*.dll")) AddReference(references, assemblyPath);
            }
            foreach (var reference in SplitCsv(refsCsv)) AddReference(references, reference);
            return references;
        }

        // 向引用列表添加非空且不重复的路径。
        private static void AddReference(List<string> references, string reference)
        {
            if (!string.IsNullOrEmpty(reference) && !references.Contains(reference)) references.Add(reference);
        }

        // 仅在文件存在时添加编译引用。
        private static void AddReferenceIfExists(List<string> references, string reference)
        {
            if (!string.IsNullOrEmpty(reference) && File.Exists(reference)) AddReference(references, reference);
        }

        // 验证所有 batchmode 编译引用均存在且可读。
        private static string ValidateReferences(IEnumerable<string> references)
        {
            foreach (var reference in references)
            {
                try
                {
                    if (!File.Exists(reference)) return "Runtime Code reference was not found: " + reference;
                    using (File.Open(reference, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
                }
                catch (Exception exception)
                {
                    return "Runtime Code reference is not readable: " + reference + " (" + exception.Message + ")";
                }
            }
            return null;
        }

        // 拆分并清理逗号分隔参数。
        private static IEnumerable<string> SplitCsv(string value)
        {
            if (string.IsNullOrEmpty(value)) yield break;
            foreach (var part in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed)) yield return trimmed;
            }
        }

        // 将编译器诊断转换为协议可序列化的对象数组。
        private static object[] Diagnostics(CompilerErrorCollection errors)
        {
            return errors.Cast<CompilerError>().Select(error => (object)new Dictionary<string, object>
            {
                { "severity", error.IsWarning ? "warning" : "error" }, { "id", error.ErrorNumber },
                { "message", error.ErrorText }, { "file", error.FileName }, { "line", (long)error.Line },
                { "column", (long)error.Column }, { "span", null }
            }).ToArray();
        }

        // 构造包含诊断、输出和堆栈的异常详情。
        private static Dictionary<string, object> ExceptionDetails(Exception exception, string stdout, string stderr)
        {
            return new Dictionary<string, object>
            {
                { "diagnostics", Array.Empty<object>() }, { "exceptionType", exception.GetType().FullName },
                { "message", exception.Message }, { "stackTrace", exception.StackTrace },
                { "stdout", stdout }, { "stderr", stderr }
            };
        }

        // 序列化成功协议 envelope。
        private static string SuccessEnvelope(string requestId, object data)
        {
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Success(requestId, data));
        }

        // 序列化带结构化详情和后续动作的错误协议 envelope。
        private static string ErrorEnvelope(string requestId, string code, string message, object details, string[] nextActions)
        {
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                requestId, code, message, details, nextActions ?? Array.Empty<string>()));
        }

        // 将 code execution envelope 写入结果 artifact。
        private static void WriteCodeResult(string resultPath, string json)
        {
            if (string.IsNullOrEmpty(resultPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
            File.WriteAllText(resultPath, json + Environment.NewLine, Encoding.UTF8);
        }

        // 构造 batchmode 执行成功数据，保留运行输出、审计信息和副作用声明。
        private static Dictionary<string, object> CodeSuccessData(string requestId, DateTime startedAt, DateTime endedAt, string stdout, string stderr, object returnValue, string argsJson)
        {
            var durationMs = (long)(endedAt - startedAt).TotalMilliseconds;
            var timeoutMs = TimeoutMs();
            return new Dictionary<string, object>
            {
                { "command", "code.exec" }, { "mode", "batchmode" },
                { "execution", new Dictionary<string, object>
                    {
                        { "channel", "batchmode" }, { "startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture) },
                        { "endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture) }, { "durationMs", durationMs },
                        { "timeoutMs", timeoutMs }, { "exitState", "completed" }
                    }
                },
                { "compile", new Dictionary<string, object> { { "ok", true }, { "diagnostics", Array.Empty<object>() } } },
                { "runtime", new Dictionary<string, object>
                    {
                        { "ok", true }, { "returnValue", NormalizeReturnValue(returnValue) },
                        { "stdout", stdout }, { "stderr", stderr }
                    }
                },
                { "artifacts", Array.Empty<object>() },
                { "audit", new Dictionary<string, object>
                    {
                        { "requestId", requestId }, { "projectPath", ProjectPath() }, { "instanceId", InstanceId() },
                        { "unityVersion", Application.unityVersion }, { "bridgeVersion", "com.unity_cli" }, { "initiator", "cli.batchmode" },
                        { "input", new Dictionary<string, object>
                            {
                                { "sourcePath", ArgValue("-unityCliCodeSource") },
                                { "args", UnityCliSerializer.Parse(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson) }
                            }
                        },
                        { "execution", new Dictionary<string, object> { { "timeoutMs", timeoutMs }, { "exitState", "completed" } } },
                        { "artifacts", Array.Empty<object>() },
                        { "effects", new Dictionary<string, object>
                            {
                                { "fileWrites", Array.Empty<string>() }, { "assetRefresh", false }, { "domainReload", false },
                                { "playModeChanged", false }, { "packagesChanged", false }
                            }
                        }
                    }
                }
            };
        }

        // 读取可选的 batchmode 超时毫秒数。
        private static object TimeoutMs()
        {
            var value = ArgValue("-unityCliTimeoutMs");
            long parsed;
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? (object)parsed : null;
        }

        // 获取命令行项目路径或当前 Unity 项目根目录。
        private static string ProjectPath()
        {
            var projectPath = ArgValue("-unityCliProjectPath");
            if (!string.IsNullOrEmpty(projectPath)) return projectPath;
            return Directory.GetParent(Application.dataPath).FullName;
        }

        // 返回当前 Unity 进程标识。
        private static string InstanceId()
        {
            return System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
        }

        // 将执行返回值规范化为序列化器支持的协议值。
        private static object NormalizeReturnValue(object value)
        {
            if (value == null || value is bool || value is float || value is double || value is int || value is long) return value;
            if (value is decimal) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private sealed class CodeExecutionResult
        {
            public bool Ok;
            public object Data;
            public string ErrorCode;
            public string ErrorMessage;
            public object Details;
            public string[] NextActions;

            // 创建成功执行结果。
            public static CodeExecutionResult Completed(object data)
            {
                return new CodeExecutionResult { Ok = true, Data = data, NextActions = Array.Empty<string>() };
            }

            // 创建包含结构化详情和后续动作的失败执行结果。
            public static CodeExecutionResult Failed(string code, string message, object details, string nextAction)
            {
                return new CodeExecutionResult
                {
                    Ok = false, ErrorCode = code, ErrorMessage = message, Details = details,
                    NextActions = string.IsNullOrWhiteSpace(nextAction) ? Array.Empty<string>() : new[] { nextAction }
                };
            }
        }
    }
}
#endif
