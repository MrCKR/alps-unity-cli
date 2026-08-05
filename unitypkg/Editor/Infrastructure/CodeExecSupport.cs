#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "code.exec",
                "code",
                true,
                60000,
                (request, cancellationToken) => new ValueTask<object>(NormalizeCommandResult(CodeExec(request, cancellationToken))));
        }

        /// <summary>
        /// 编译并执行请求中的 C# 源码；会临时重定向进程标准输出，失败时返回结构化诊断或异常详情。
        /// </summary>
        private static string CodeExec(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
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

            var result = ExecuteCodeSource(request, source, cancellationToken);
            WriteLog("code_exec requestId=" + request.RequestId + " ok=" + result.Ok + " exitState=" + result.ExitState);
            if (!result.Ok)
            {
                return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    request.RequestId, result.ErrorCode, result.ErrorMessage, result.Details, result.NextActions));
            }
            RecordDirectCompletionEvidence(request.RequestId, "code.exec", ResolveRequestCompletionContract(request), result.Data);
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Success(request.RequestId, result.Data));
        }

        // 编译源码并执行约定的 Run 方法，收集编译诊断、输出和运行时异常。
        private static CodeExecutionResult ExecuteCodeSource(UnityCli.Protocol.BridgeRequest request, string source, CancellationToken cancellationToken)
        {
            var argsJson = request.StringArg("argsJson");
            var references = DefaultCodeReferences(request.StringArg("refs")).ToList();
            var referenceError = ValidateCodeReferences(references);
            if (!string.IsNullOrEmpty(referenceError))
            {
                var details = new Dictionary<string, object> { { "referenceError", referenceError } };
                return CodeExecutionResult.Fail("E_REFERENCE_RESOLUTION_FAILED", referenceError, details, "Check Unity/Editor/project assembly references and --refs.");
            }
            var editorDataPath = EditorDataPath();
            if (string.IsNullOrEmpty(editorDataPath))
            {
                var message = "Unity Editor Data directory was not found.";
                return CodeExecutionResult.Fail("E_REFERENCE_RESOLUTION_FAILED", message, new Dictionary<string, object> { { "referenceError", message } }, "Check the Unity Editor installation path.");
            }

            var sourceFilePath = Path.Combine(Path.GetTempPath(), "unity-cli-roslyn-" + Guid.NewGuid().ToString("N") + ".cs");
            var responseFilePath = Path.ChangeExtension(sourceFilePath, ".rsp");
            var assemblyFilePath = Path.ChangeExtension(sourceFilePath, ".dll");
            var pdbFilePath = Path.ChangeExtension(sourceFilePath, ".pdb");
            byte[] assemblyBytes;
            try
            {
                File.WriteAllText(sourceFilePath, WrapCodeSource(source), new UTF8Encoding(true));
                WriteReferenceResponseFile(responseFilePath, references);
                var compilation = CompileWithRoslyn(editorDataPath, sourceFilePath, responseFilePath, assemblyFilePath, request.StringArg("defines"));
                if (compilation.TimedOut)
                    return CodeExecutionResult.Fail("E_EXECUTION_TIMEOUT", "Runtime Code compilation timed out.", new Dictionary<string, object>(), "Inspect the Editor state before submitting a new request.");
                var diagnostics = RoslynDiagnostics(compilation.Output);
                if (compilation.ExitCode != 0 || diagnostics.Any(item => string.Equals((string)item["severity"], "error", StringComparison.Ordinal)))
                {
                    var errors = diagnostics.Where(item => string.Equals((string)item["severity"], "error", StringComparison.Ordinal)).ToList();
                    var details = new Dictionary<string, object> { { "diagnostics", CompactRoslynDiagnostics(errors) } };
                    return CodeExecutionResult.Fail("E_CODE_COMPILE_FAILED", RoslynCompileFailureMessage(errors), details, "Fix compiler diagnostics and rerun code exec.");
                }
                assemblyBytes = File.ReadAllBytes(assemblyFilePath);
            }
            catch (Exception exception)
            {
                return CodeExecutionResult.Fail("E_REFERENCE_RESOLUTION_FAILED", "Failed to compile Runtime Code: " + exception.Message, ExceptionDetails(exception, "", ""), "Check Unity/Editor/project assembly references and --refs.");
            }
            finally
            {
                DeleteTemporaryCodeFile(sourceFilePath);
                DeleteTemporaryCodeFile(responseFilePath);
                DeleteTemporaryCodeFile(assemblyFilePath);
                DeleteTemporaryCodeFile(pdbFilePath);
            }

            var compiledAssembly = Assembly.Load(assemblyBytes);
            var type = compiledAssembly.GetType("UnityCli.RuntimeCode.Script", true);
            var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            if (method == null || method.GetParameters().Length != 1 || method.GetParameters()[0].ParameterType != typeof(string))
                return CodeExecutionResult.Fail("E_CODE_ENTRYPOINT_INVALID", "Runtime Code requires public static object Run(string argsJson).", new Dictionary<string, object>(), "Use the default wrapped source body or declare the exact Run signature.");

            var probeJson = request.RawJsonArg("completionProbe");
            UnityCliCompletionProbe completionProbe = null;
            if (!string.IsNullOrWhiteSpace(probeJson))
            {
                var probeFailure = ValidateTypedInput(typeof(UnityCliCompletionProbe), probeJson);
                if (probeFailure != null) throw SchemaValidationException(probeFailure);
                completionProbe = UnityCliSerializer.Deserialize<UnityCliCompletionProbe>(probeJson);
            }
            var executionContext = new UnityCliProjectToolContext
            {
                RequestId = request.RequestId,
                ProjectPath = ProjectRoot(),
                CancellationToken = cancellationToken,
                CommitEffectAction = () => BeginDirectEffect(request.RequestId)
            };
            var stdout = new StringWriter(CultureInfo.InvariantCulture);
            var stderr = new StringWriter(CultureInfo.InvariantCulture);
            var previousOut = Console.Out;
            var previousError = Console.Error;
            object returnValue = null;
            Action invoke = () =>
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                try { returnValue = method.Invoke(null, new object[] { argsJson ?? "{}" }); }
                catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
                finally
                {
                    Console.SetOut(previousOut);
                    Console.SetError(previousError);
                }
            };
            UnityCliCompletionProbeReadback completion = null;
            if (completionProbe == null)
            {
                executionContext.CommitEffect();
                invoke();
            }
            else completion = ExecuteCompletionProbe(executionContext, completionProbe, invoke);
            return CodeExecutionResult.Success(CodeSuccessData(stdout.ToString(), stderr.ToString(), returnValue, completion));
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

        /// <summary>
        /// 在编译前汇总 Roslyn 所需引用；排除 facade 与 UnityEngine 主程序集，避免类型转发和重复定义。
        /// 输入为调用方附加引用，输出只包含实际磁盘程序集；不修改 Editor 或项目状态。
        /// </summary>
        private static IEnumerable<string> DefaultCodeReferences(string refsCsv)
        {
            var references = new List<string>();
            var editorDataPath = EditorDataPath();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                var location = assembly.Location;
                if (string.IsNullOrEmpty(location) || !File.Exists(location)) continue;
                AddReference(references, location);
            }
            var scriptAssemblies = Path.Combine(ProjectRoot(), "Library", "ScriptAssemblies");
            if (Directory.Exists(scriptAssemblies))
            {
                foreach (var assemblyPath in Directory.GetFiles(scriptAssemblies, "*.dll"))
                    AddReference(references, assemblyPath);
            }
            foreach (var reference in SplitCsv(refsCsv)) AddReference(references, reference);

            var excludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "mscorlib", "System.Private.CoreLib", "System.Runtime", "System.Collections", "netstandard"
            };
            var unityEnginePath = string.IsNullOrEmpty(editorDataPath) ? null : Path.Combine(editorDataPath, "Managed", "UnityEngine.dll");
            // Mono 运行目录下的 System*.dll 实现程序集针对 mscorlib 编译，与 netstandard ref 混用时
            // lambda/LINQ 的委托签名会被强制解析到 mscorlib（CS0012）；netstandard 2.1 ref 已含全部
            // System.* API 定义，因此整体排除，第三方包内针对 netstandard 编译的 System.* 不受影响。
            var monoLibPrefix = string.IsNullOrEmpty(editorDataPath) ? null : Path.GetFullPath(Path.Combine(editorDataPath, "MonoBleedingEdge", "lib"));
            references = references.Where(reference =>
            {
                var fileName = Path.GetFileNameWithoutExtension(reference);
                if (excludedNames.Contains(fileName)) return false;
                if (unityEnginePath != null && string.Equals(Path.GetFullPath(reference), Path.GetFullPath(unityEnginePath), StringComparison.OrdinalIgnoreCase)) return false;
                if (monoLibPrefix != null && fileName.StartsWith("System", StringComparison.OrdinalIgnoreCase)
                    && Path.GetFullPath(reference).StartsWith(monoLibPrefix, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }).ToList();
            if (!string.IsNullOrEmpty(editorDataPath))
                AddReferenceIfExists(references, Path.Combine(editorDataPath, "NetStandard", "ref", "2.1.0", "netstandard.dll"));
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

        /// <summary>
        /// 使用 Unity 内置 Roslyn 编译临时源码；进程最多运行 60 秒，输出仅用于诊断，成功程序集由调用方读取到内存。
        /// </summary>
        private static RoslynCompilation CompileWithRoslyn(string editorDataPath, string sourceFilePath, string responseFilePath, string assemblyFilePath, string definesCsv)
        {
            var monoPath = Path.Combine(editorDataPath, "MonoBleedingEdge", "bin", "mono.exe");
            var cscPath = Path.Combine(editorDataPath, "MonoBleedingEdge", "lib", "mono", "4.5", "csc.exe");
            if (!File.Exists(monoPath) || !File.Exists(cscPath))
                throw new FileNotFoundException("Unity-bundled Roslyn compiler was not found.");
            var arguments = QuoteProcessArgument(cscPath) + " /nologo /noconfig /nostdlib /utf8output /target:library /langversion:latest /out:" + QuoteProcessArgument(assemblyFilePath) + " @" + QuoteProcessArgument(responseFilePath);
            foreach (var define in SplitCsv(definesCsv)) arguments += " /define:" + define;
            arguments += " " + QuoteProcessArgument(sourceFilePath);
            var startInfo = new ProcessStartInfo
            {
                FileName = monoPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            using (var process = Process.Start(startInfo))
            {
                if (process == null) throw new InvalidOperationException("Unity-bundled Roslyn compiler did not start.");
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(60000))
                {
                    try { process.Kill(); } catch { }
                    return new RoslynCompilation { TimedOut = true, Output = string.Empty };
                }
                Task.WaitAll(stdoutTask, stderrTask);
                return new RoslynCompilation { ExitCode = process.ExitCode, Output = stdoutTask.Result + Environment.NewLine + stderrTask.Result };
            }
        }

        // 写入 Roslyn 引用 response file，避免 Windows 命令行长度限制。
        private static void WriteReferenceResponseFile(string responseFilePath, IEnumerable<string> references)
        {
            using (var writer = new StreamWriter(responseFilePath, false, new UTF8Encoding(false)))
                foreach (var reference in references) writer.WriteLine("/r:\"" + reference + "\"");
        }

        // 解析 Roslyn 标准诊断行，无法解析的输出不作为协议诊断公开。
        private static List<Dictionary<string, object>> RoslynDiagnostics(string output)
        {
            var diagnostics = new List<Dictionary<string, object>>();
            var pattern = new Regex("^(.*)\\((\\d+),(\\d+)\\):\\s*(error|warning)\\s+(CS\\d+):\\s*(.*)$", RegexOptions.IgnoreCase);
            foreach (var line in (output ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = pattern.Match(line);
                if (!match.Success) continue;
                diagnostics.Add(new Dictionary<string, object>
                {
                    { "severity", match.Groups[4].Value.ToLowerInvariant() }, { "id", match.Groups[5].Value },
                    { "message", match.Groups[6].Value }, { "file", match.Groups[1].Value },
                    { "line", long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) },
                    { "column", long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) }, { "span", null }
                });
            }
            return diagnostics;
        }

        /// <summary>编译失败时保留首个根因及最多五条错误，丢弃 warning 和后续连锁错误。</summary>
        private static object[] CompactRoslynDiagnostics(List<Dictionary<string, object>> errors)
        {
            return errors.Take(5).Cast<object>().ToArray();
        }

        // 构造受限长度的首个错误消息，避免连锁错误淹没根因。
        private static string RoslynCompileFailureMessage(List<Dictionary<string, object>> errors)
        {
            if (errors.Count <= 5 || errors.Count == 0) return "Runtime Code compilation failed.";
            var message = (string)errors[0]["message"];
            if (message.Length > 240) message = message.Substring(0, 240) + "...";
            return "Runtime Code compilation failed: " + errors[0]["id"] + " " + message + " (后续还有 " + (errors.Count - 1).ToString(CultureInfo.InvariantCulture) + " 条错误，多为连锁错误)";
        }

        // 解析当前 Editor 安装目录下的 Data 目录；不存在时返回 null。
        private static string EditorDataPath()
        {
            var applicationPath = EditorApplication.applicationPath;
            var editorDirectory = string.IsNullOrEmpty(applicationPath) ? null : Path.GetDirectoryName(applicationPath);
            var dataPath = string.IsNullOrEmpty(editorDirectory) ? null : Path.Combine(editorDirectory, "Data");
            return !string.IsNullOrEmpty(dataPath) && Directory.Exists(dataPath) ? dataPath : null;
        }

        // 为 ProcessStartInfo.Arguments 转义单个路径参数。
        private static string QuoteProcessArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        // 尽力删除临时文件，不因 Windows 延迟释放句柄影响命令结果。
        private static void DeleteTemporaryCodeFile(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        private sealed class RoslynCompilation
        {
            public int ExitCode;
            public string Output;
            public bool TimedOut;
        }

        /// <summary>成功仅返回编译结果、运行返回值与 request-specific fresh readback。</summary>
        private static Dictionary<string, object> CodeSuccessData(string stdout, string stderr, object returnValue, UnityCliCompletionProbeReadback completion)
        {
            return new Dictionary<string, object>
            {
                { "compile", new Dictionary<string, object> { { "diagnostics", Array.Empty<object>() } } },
                { "runtime", new Dictionary<string, object>
                    {
                        { "returnValue", NormalizeReturnValue(returnValue) },
                        { "stdout", stdout }, { "stderr", stderr }
                    }
                },
                { "completion", completion }
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
