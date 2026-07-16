#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        // 注册运行时代码执行命令。
        private static void RegisterCodeExecCommand(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "code.exec", "code", true, CodeExec);
        }

        /// <summary>
        /// 编译并执行请求中的 C# 源码；会临时重定向进程标准输出，失败时返回结构化诊断或异常详情。
        /// </summary>
        private static string CodeExec(UnityCli.Protocol.BridgeRequest request)
        {
            var startedAt = DateTime.UtcNow;
            var source = request.StringArg("sourceText");
            var sourcePath = request.StringArg("sourcePath");
            if (string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(sourcePath))
            {
                try
                {
                    source = File.ReadAllText(sourcePath, Encoding.UTF8);
                }
                catch (Exception exception)
                {
                    return SerializeErrorEnvelope("E_CODE_SOURCE_INVALID", "Code source is not readable: " + exception.Message, "Check that --file points to a UTF-8 C# source file.");
                }
            }
            if (string.IsNullOrWhiteSpace(source))
            {
                return SerializeErrorEnvelope("E_CODE_SOURCE_INVALID", "Code source is empty.", "Pass --file, --stdin or --code with non-empty C# source.");
            }

            var result = ExecuteCodeSource(request, source, startedAt);
            WriteLog("code_exec requestId=" + request.RequestId + " ok=" + result.Ok + " exitState=" + result.ExitState);
            if (!result.Ok)
            {
                return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    request.RequestId, result.ErrorCode, result.ErrorMessage, result.Details, result.NextActions));
            }
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Success(request.RequestId, result.Data));
        }

        // 编译源码并执行约定的 Run 方法，收集编译诊断、输出和运行时异常。
        private static CodeExecutionResult ExecuteCodeSource(UnityCli.Protocol.BridgeRequest request, string source, DateTime startedAt)
        {
            var argsJson = request.StringArg("argsJson");
            var provider = new CSharpCodeProvider();
            var parameters = new CompilerParameters
            {
                GenerateExecutable = false,
                GenerateInMemory = true,
                TreatWarningsAsErrors = false
            };
            var references = DefaultCodeReferences(request.StringArg("refs")).ToList();
            var referenceError = ValidateCodeReferences(references);
            if (!string.IsNullOrEmpty(referenceError))
            {
                var details = new Dictionary<string, object> { { "referenceError", referenceError } };
                return CodeExecutionResult.Fail("E_REFERENCE_RESOLUTION_FAILED", referenceError, details, "Check Unity/Editor/project assembly references and --refs.");
            }
            // 使用 response file 传引用，避免 Windows 32KB 命令行长度限制
            // 参考 CoplayDev ExecuteCode.CodeDomCompile
            var responseFilePath = Path.Combine(Path.GetTempPath(), "unity-cli-codedom-" + Guid.NewGuid().ToString("N") + ".rsp");
            CompilerResults compiled = null;
            try
            {
                using (var writer = new StreamWriter(responseFilePath, false, Encoding.UTF8))
                {
                    foreach (var reference in references)
                    {
                        writer.Write("/r:\"");
                        writer.Write(reference);
                        writer.WriteLine("\"");
                    }
                }
                parameters.CompilerOptions = "@\"" + responseFilePath + "\"";
                foreach (var define in SplitCsv(request.StringArg("defines")))
                {
                    parameters.CompilerOptions += " /define:" + define;
                }

                try
                {
                    compiled = provider.CompileAssemblyFromSource(parameters, WrapCodeSource(source));
                }
                catch (Exception exception)
                {
                    return CodeExecutionResult.Fail("E_REFERENCE_RESOLUTION_FAILED", "Failed to compile Runtime Code: " + exception.Message, ExceptionDetails(exception, "", ""), "Check Unity/Editor/project assembly references and --refs.");
                }
            }
            finally
            {
                try { if (File.Exists(responseFilePath)) File.Delete(responseFilePath); } catch { }
            }

            if (compiled.Errors.HasErrors)
            {
                var details = new Dictionary<string, object> { { "diagnostics", CompilerDiagnostics(compiled.Errors) } };
                return CodeExecutionResult.Fail("E_CODE_COMPILE_FAILED", "Runtime Code compilation failed.", details, "Fix compiler diagnostics and rerun code exec.");
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
                return CodeExecutionResult.Success(CodeSuccessData(request, startedAt, endedAt, stdout.ToString(), stderr.ToString(), returnValue));
            }
            catch (TargetInvocationException exception)
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                var inner = exception.InnerException ?? exception;
                return CodeExecutionResult.Fail("E_CODE_RUNTIME_EXCEPTION", inner.GetType().FullName + ": " + inner.Message, ExceptionDetails(inner, stdout.ToString(), stderr.ToString()), "Inspect runtime stdout/stderr and exception stackTrace.");
            }
            catch (Exception exception)
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                return CodeExecutionResult.Fail("E_CODE_RUNTIME_EXCEPTION", exception.GetType().FullName + ": " + exception.Message, ExceptionDetails(exception, stdout.ToString(), stderr.ToString()), "Inspect runtime stdout/stderr and exception stackTrace.");
            }
        }

        // 将源码包装为运行时约定的 Script.Run 入口。
        private static string WrapCodeSource(string source)
        {
            if (Regex.IsMatch(source, "\\bpublic\\s+static\\s+object\\s+Run\\s*\\("))
            {
                return "using System; using System.IO; using System.Linq; using System.Collections.Generic; using UnityEngine; using UnityEditor; namespace UnityCli.RuntimeCode { public static class Script { " + source + " } }";
            }
            return "using System; using System.IO; using System.Linq; using System.Collections.Generic; using UnityEngine; using UnityEditor; namespace UnityCli.RuntimeCode { public static class Script { public static object Run(string argsJson) { " + source + " } } }";
        }

        // 汇总当前 Unity 域、项目程序集和调用方指定的编译引用。
        private static IEnumerable<string> DefaultCodeReferences(string refsCsv)
        {
            var references = new List<string>();
            // CoplayDev 做法：遍历所有 AppDomain 程序集，只加有磁盘文件的
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                var location = assembly.Location;
                if (string.IsNullOrEmpty(location) || !File.Exists(location)) continue;
                AddReference(references, location);
            }
            // CodeDom (CSharpCodeProvider) 无法处理 type-forwarding，当 netstandard.dll 与 mscorlib 同存在时
            // 系统类型会重复定义导致 CS0433。参考 CoplayDev FilterAssemblyPathsForCodeDom：
            // 如果引用列表中有 netstandard.dll，则过滤掉 mscorlib 等重复程序集
            var hasNetstandard = references.Any(p =>
                string.Equals(Path.GetFileNameWithoutExtension(p), "netstandard", StringComparison.OrdinalIgnoreCase));
            if (hasNetstandard)
            {
                var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "mscorlib",
                    "System.Runtime",
                    "System.Private.CoreLib",
                    "System.Collections"
                };
                references = references.Where(p =>
                    !duplicates.Contains(Path.GetFileNameWithoutExtension(p))).ToList();
            }
            var scriptAssemblies = Path.Combine(ProjectRoot(), "Library", "ScriptAssemblies");
            if (Directory.Exists(scriptAssemblies))
            {
                foreach (var assemblyPath in Directory.GetFiles(scriptAssemblies, "*.dll"))
                {
                    AddReference(references, assemblyPath);
                }
            }
            foreach (var reference in SplitCsv(refsCsv))
            {
                AddReference(references, reference);
            }
            return references;
        }

        // 向引用列表添加非空且不重复的路径。
        private static void AddReference(List<string> references, string reference)
        {
            if (string.IsNullOrEmpty(reference)) return;
            if (!references.Contains(reference)) references.Add(reference);
        }

        // 仅在文件存在时添加编译引用。
        private static void AddReferenceIfExists(List<string> references, string reference)
        {
            if (!string.IsNullOrEmpty(reference) && File.Exists(reference)) AddReference(references, reference);
        }

        // 验证所有编译引用均存在且可读。
        private static string ValidateCodeReferences(IEnumerable<string> references)
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
            var parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed)) yield return trimmed;
            }
        }

        // 将编译器诊断转换为协议可序列化的对象数组。
        private static object[] CompilerDiagnostics(CompilerErrorCollection errors)
        {
            return errors.Cast<CompilerError>().Select(error => (object)new Dictionary<string, object>
            {
                { "severity", error.IsWarning ? "warning" : "error" },
                { "id", error.ErrorNumber },
                { "message", error.ErrorText },
                { "file", error.FileName },
                { "line", (long)error.Line },
                { "column", (long)error.Column },
                { "span", null }
            }).ToArray();
        }

        // 构造 live 执行成功数据，保留运行输出、审计信息和副作用声明。
        private static Dictionary<string, object> CodeSuccessData(UnityCli.Protocol.BridgeRequest request, DateTime startedAt, DateTime endedAt, string stdout, string stderr, object returnValue)
        {
            var timeoutMs = request.Execution == null ? null : request.Execution.TimeoutMs;
            var argsJson = request.StringArg("argsJson");
            var durationMs = (long)(endedAt - startedAt).TotalMilliseconds;
            return new Dictionary<string, object>
            {
                { "command", "code.exec" },
                { "mode", "live" },
                { "execution", new Dictionary<string, object>
                    {
                        { "channel", "live" }, { "startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture) },
                        { "endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture) }, { "durationMs", durationMs },
                        { "timeoutMs", timeoutMs.HasValue ? (object)timeoutMs.Value : null }, { "exitState", "completed" }
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
                        { "requestId", request.RequestId }, { "projectPath", ProjectRoot() }, { "instanceId", InstanceId() },
                        { "unityVersion", Application.unityVersion }, { "bridgeVersion", "com.unity_cli" }, { "initiator", "cli.live" },
                        { "input", new Dictionary<string, object>
                            {
                                { "sourceKind", request.StringArg("sourceKind") }, { "sourcePath", request.StringArg("sourcePath") },
                                { "sourceSha256", request.StringArg("sourceSha256") },
                                { "args", UnityCliSerializer.Parse(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson) }
                            }
                        },
                        { "execution", new Dictionary<string, object>
                            {
                                { "timeoutMs", timeoutMs.HasValue ? (object)timeoutMs.Value : null }, { "exitState", "completed" }
                            }
                        },
                        { "artifacts", Array.Empty<object>() }, { "startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture) },
                        { "endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture) }, { "durationMs", durationMs },
                        { "effects", new Dictionary<string, object>
                            {
                                { "fileWrites", Array.Empty<string>() }, { "assetRefresh", false }, { "domainReload", false },
                                { "playModeChanged", false }, { "packagesChanged", false }
                            }
                        }
                    }
                },
                { "logs", new Dictionary<string, object> { { "bridge", LogPath() } } },
                { "nextActions", Array.Empty<string>() }
            };
        }

        // 构造包含诊断、输出和堆栈的运行时异常详情。
        private static Dictionary<string, object> ExceptionDetails(Exception exception, string stdout, string stderr)
        {
            return new Dictionary<string, object>
            {
                { "diagnostics", Array.Empty<object>() }, { "exceptionType", exception.GetType().FullName },
                { "message", exception.Message }, { "stackTrace", exception.StackTrace },
                { "stdout", stdout }, { "stderr", stderr }
            };
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
            public string ExitState;

            // 创建成功执行结果。
            public static CodeExecutionResult Success(object data)
            {
                return new CodeExecutionResult { Ok = true, Data = data, NextActions = Array.Empty<string>(), ExitState = "completed" };
            }

            // 创建包含结构化详情和后续动作的失败执行结果。
            public static CodeExecutionResult Fail(string code, string message, object details, string nextAction)
            {
                return new CodeExecutionResult
                {
                    Ok = false, ErrorCode = code, ErrorMessage = message, Details = details,
                    NextActions = string.IsNullOrWhiteSpace(nextAction) ? Array.Empty<string>() : new[] { nextAction }, ExitState = "failed"
                };
            }
        }
    }
}
#endif
