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
        private const int MaxCodeOutputBytes = 256 * 1024;
        private const int MaxCodeTerminalBytes = 1536 * 1024;
        private const int MaxRoslynOutputBytes = 512 * 1024;
        private const int MaxRoslynDiagnostics = 100;

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
            if (request.Execution != null && request.Execution.DryRun == true)
                throw new BridgeCommandException("E_DRY_RUN_UNSUPPORTED", "Raw Bridge code.exec dry-run cannot execute or compile caller source.", "Use the public tools run code.exec --dry-run path, which returns the CLI-side source preview without Bridge dispatch.");
            var source = request.StringArg("sourceText");
            // raw Bridge不读取调用方路径；公开CLI已按入口deadline有界读取并内联唯一源码。
            if (string.IsNullOrWhiteSpace(source))
            {
                return SerializeErrorEnvelope("E_CODE_SOURCE_INVALID", "Code source is empty.", "Pass --file, --stdin or --code with non-empty C# source.");
            }

            var result = ExecuteCodeSource(request, source, cancellationToken);
            WriteLog("code_exec requestId=" + request.RequestId + " ok=" + result.Ok + " exitState=" + result.ExitState);
            var serialized = SerializeCodeExecutionResult(request.RequestId, result);
            if (result.Ok) RecordDirectCompletionEvidence(request.RequestId, "code.exec", ResolveRequestCompletionContract(request), result.Data);
            return serialized;
        }

        /// <summary>所有 code.exec terminal 统一经过同一 Bridge envelope 与总 byte 上限。</summary>
        private static string SerializeCodeExecutionResult(string requestId, CodeExecutionResult result)
        {
            object envelope = result.Ok
                ? (object)UnityCli.Protocol.ProtocolEnvelopeFactory.Success(requestId, result.Data)
                : UnityCli.Protocol.ProtocolEnvelopeFactory.Error(requestId, result.ErrorCode, BoundedText(result.ErrorMessage, 4096), result.Details, result.NextActions);
            var serialized = UnityCliSerializer.Serialize(envelope);
            return Encoding.UTF8.GetByteCount(serialized) <= MaxCodeTerminalBytes
                ? serialized
                : UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(requestId, "E_EXECUTION_TIMEOUT", "Runtime Code completed, but its bounded terminal could not be represented safely.", null, new[] { "Inspect the affected business state before submitting another mutation." }));
        }

        // 编译源码并执行约定的 Run 方法，收集编译诊断、输出和运行时异常。
        private static CodeExecutionResult ExecuteCodeSource(UnityCli.Protocol.BridgeRequest request, string source, CancellationToken cancellationToken)
        {
            var argsJson = request.StringArg("argsJson");
            List<string> references;
            try
            {
                references = DefaultCodeReferences(request.StringArg("refs"), request, cancellationToken).ToList();
            }
            catch (OperationCanceledException)
            {
                return CodeExecutionResult.Fail("E_EXECUTION_TIMEOUT", "Runtime Code reference discovery exceeded the immutable deadline.", new Dictionary<string, object>(), "Submit a new request after the Editor is responsive.");
            }
            byte[] assemblyBytes;
            try
            {
                var compilation = CompileWithUnityCompiler(source, references, request.StringArg("defines"), request, cancellationToken);
                if (compilation.TimedOut)
                    return CodeExecutionResult.Fail("E_EXECUTION_TIMEOUT", "Runtime Code compilation exceeded the immutable deadline.", new Dictionary<string, object>(), "Inspect the Editor state before submitting a new request.");
                if (!compilation.Success)
                {
                    var errors = compilation.Diagnostics;
                    var details = new Dictionary<string, object> { { "diagnostics", CompactRoslynDiagnostics(errors) } };
                    var referenceFailure = RoslynReferenceFailureFromDiagnostics(errors);
                    return !string.IsNullOrEmpty(referenceFailure)
                        ? CodeExecutionResult.Fail("E_REFERENCE_RESOLUTION_FAILED", referenceFailure, details, "Check Unity/Editor/project assembly references and --refs.")
                        : CodeExecutionResult.Fail("E_CODE_COMPILE_FAILED", RoslynCompileFailureMessage(errors), details, "Fix compiler diagnostics and rerun code exec.");
                }
                assemblyBytes = compilation.AssemblyBytes;
            }
            catch (Exception exception)
            {
                return ExceptionFailure("E_REFERENCE_RESOLUTION_FAILED", "Failed to compile Runtime Code: ", exception, "", "", "Check Unity/Editor/project assembly references and --refs.");
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
            BridgeCommandException effectBarrierException = null;
            BridgeCommandException completionProbeException = null;
            var executionContext = new UnityCliProjectToolContext
            {
                RequestId = request.RequestId,
                ProjectPath = ProjectRoot(),
                DryRun = request.Execution != null && request.Execution.DryRun == true,
                CancellationToken = cancellationToken,
                CommitEffectAction = () =>
                {
                    try { BeginCodeEffect(request, cancellationToken); }
                    catch (BridgeCommandException exception)
                    {
                        // 只标记框架barrier自身异常；caller源码构造的同类型异常仍属于runtime failure。
                        effectBarrierException = exception;
                        throw;
                    }
                }
            };
            var stdout = new BoundedStringWriter(MaxCodeOutputBytes);
            var stderr = new BoundedStringWriter(MaxCodeOutputBytes);
            var previousOut = Console.Out;
            var previousError = Console.Error;
            object returnValue = null;
            Action invoke = () =>
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                try
                {
                    // 返回对象的ToString也属于caller effect，必须发生在fresh completion readback之前。
                    returnValue = NormalizeReturnValue(method.Invoke(null, new object[] { argsJson ?? "{}" }));
                }
                catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
                finally
                {
                    Console.SetOut(previousOut);
                    Console.SetError(previousError);
                }
            };
            UnityCliCompletionProbeReadback completion = null;
            try
            {
                if (completionProbe == null)
                {
                    executionContext.CommitEffect();
                    invoke();
                }
                else
                {
                    try { completion = ExecuteCompletionProbe(executionContext, completionProbe, invoke); }
                    catch (BridgeCommandException exception)
                    {
                        // caller异常会先由probe包装；这里只记录probe边界实际发布的框架异常实例。
                        completionProbeException = exception;
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                if (ReferenceEquals(exception, effectBarrierException)
                    || ReferenceEquals(exception, completionProbeException)) throw;
                return ExceptionFailure("E_CODE_RUNTIME_EXCEPTION", string.Empty, exception, stdout.ToString(), stderr.ToString(), "Inspect the bounded runtime exception cause chain and affected business state.");
            }
            try { return CodeExecutionResult.Success(CodeSuccessData(stdout.ToString(), stderr.ToString(), stdout.Truncated, stderr.Truncated, returnValue, completion)); }
            catch (Exception exception) { return ExceptionFailure("E_CODE_RUNTIME_EXCEPTION", string.Empty, exception, stdout.ToString(), stderr.ToString(), "Inspect the bounded return-value conversion cause chain and affected business state."); }
        }

        /// <summary>caller effect紧前同时重验immutable deadline、cancel token与exact direct lease。</summary>
        private static void BeginCodeEffect(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            int remainingMs;
            if (cancellationToken.IsCancellationRequested || request == null || !request.TryDeadlineRemainingMs(out remainingMs))
                throw new OperationCanceledException("Runtime Code deadline elapsed before its effect barrier.", cancellationToken);
            BeginDirectEffect(request.RequestId);
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
        /// 汇总 Unity Editor 编译器所需引用；全包含 AppDomain 已加载程序集，
        /// 不排除 mscorlib/System*/netstandard，避免项目程序集缺少核心库引用。
        /// </summary>
        private static IEnumerable<string> DefaultCodeReferences(string refsCsv, UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            var references = new List<string>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                EnsureCodePreEffectDeadline(request, cancellationToken);
                if (assembly.IsDynamic) continue;
                var location = assembly.Location;
                if (string.IsNullOrEmpty(location)) continue;
                AddReference(references, location);
            }
            foreach (var reference in SplitCsv(refsCsv))
            {
                EnsureCodePreEffectDeadline(request, cancellationToken);
                AddReference(references, reference);
            }
            EnsureCodePreEffectDeadline(request, cancellationToken);
            return references;
        }

        // 向引用列表添加非空且不重复的路径。
        private static void AddReference(List<string> references, string reference)
        {
            if (string.IsNullOrEmpty(reference)) return;
            if (!references.Contains(reference)) references.Add(reference);
        }

        // Unity Editor 编译器结果；成功时携带程序集字节，失败时携带结构化诊断。
        private sealed class CodeCompilationResult
        {
            public bool Success;
            public byte[] AssemblyBytes;
            public List<Dictionary<string, object>> Diagnostics = new List<Dictionary<string, object>>();
            public bool TimedOut;
        }

        /// <summary>
        /// 使用当前 Unity Editor 自带的 csc.exe 编译源码；子进程受请求 deadline 约束并可终止。
        /// </summary>
        private static CodeCompilationResult CompileWithUnityCompiler(string source, IEnumerable<string> references, string definesCsv, UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            var result = new CodeCompilationResult();
            var editorDataPath = EditorDataPath();
            if (string.IsNullOrEmpty(editorDataPath))
            {
                result.Diagnostics.Add(new Dictionary<string, object> { { "severity", "error" }, { "id", "E_REFERENCE_RESOLUTION_FAILED" }, { "message", "Unity Editor Data directory was not found." }, { "file", "" }, { "line", 0L }, { "column", 0L }, { "span", null } });
                return result;
            }

            var temporaryRoot = Path.Combine(ProjectRoot(), "Library", "UnityCli", "code-exec-temp");
            Directory.CreateDirectory(temporaryRoot);
            var sourceFilePath = Path.Combine(temporaryRoot, "source.cs");
            var responseFilePath = Path.Combine(temporaryRoot, "references.rsp");
            var assemblyFilePath = Path.Combine(temporaryRoot, "assembly.dll");
            var pdbFilePath = Path.Combine(temporaryRoot, "assembly.pdb");
            try
            {
                File.WriteAllText(sourceFilePath, WrapCodeSource(source), new UTF8Encoding(true));
                WriteReferenceResponseFile(responseFilePath, references);
                var compilation = RunUnityCompiler(editorDataPath, sourceFilePath, responseFilePath, assemblyFilePath, definesCsv, request, cancellationToken);
                if (compilation.TimedOut)
                {
                    result.TimedOut = true;
                    return result;
                }
                var diagnostics = RoslynDiagnostics(compilation.Output);
                if (compilation.ExitCode != 0 || diagnostics.Any(item => string.Equals((string)item["severity"], "error", StringComparison.Ordinal)))
                {
                    result.Diagnostics = diagnostics.Where(item => string.Equals((string)item["severity"], "error", StringComparison.Ordinal)).ToList();
                    return result;
                }
                result.AssemblyBytes = File.ReadAllBytes(assemblyFilePath);
                result.Success = true;
                return result;
            }
            finally
            {
                DeleteTemporaryCodeFile(sourceFilePath);
                DeleteTemporaryCodeFile(responseFilePath);
                DeleteTemporaryCodeFile(assemblyFilePath);
                DeleteTemporaryCodeFile(pdbFilePath);
            }
        }

        // 从结构化错误诊断识别引用缺失错误（CS0006），保持为引用解析错误而非普通编译错误。
        private static string RoslynReferenceFailureFromDiagnostics(List<Dictionary<string, object>> errors)
        {
            var referenceError = errors.FirstOrDefault(item => string.Equals((string)item["id"], "CS0006", StringComparison.OrdinalIgnoreCase));
            return referenceError != null ? "Runtime Code reference was not found or readable: " + (string)referenceError["message"] : null;
        }

        // 引用枚举只做内存处理；真实文件解析由可按deadline终止的Roslyn子进程负责。
        private static void EnsureCodePreEffectDeadline(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            int remainingMs;
            if (cancellationToken.IsCancellationRequested || request == null || !request.TryDeadlineRemainingMs(out remainingMs))
                throw new OperationCanceledException("Runtime Code immutable deadline elapsed before compilation.", cancellationToken);
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
        private static RoslynCompilation RunUnityCompiler(string editorDataPath, string sourceFilePath, string responseFilePath, string assemblyFilePath, string definesCsv, UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            var monoPath = Path.Combine(editorDataPath, "MonoBleedingEdge", "bin", "mono.exe");
            var cscPath = Path.Combine(editorDataPath, "MonoBleedingEdge", "lib", "mono", "4.5", "csc.exe");
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
                var outputBudget = new Utf8ByteBudget(MaxRoslynOutputBytes);
                var stdoutTask = ReadBoundedToEndAsync(process.StandardOutput, outputBudget);
                var stderrTask = ReadBoundedToEndAsync(process.StandardError, outputBudget);
                var compileLimit = DateTimeOffset.UtcNow.AddSeconds(60);
                while (true)
                {
                    int remainingMs;
                    var compileRemainingMs = (int)Math.Ceiling((compileLimit - DateTimeOffset.UtcNow).TotalMilliseconds);
                    if (cancellationToken.IsCancellationRequested || request == null || !request.TryDeadlineRemainingMs(out remainingMs) || compileRemainingMs <= 0)
                    {
                        TerminateCompiler(process, stdoutTask, stderrTask);
                        return new RoslynCompilation { TimedOut = true, Output = string.Empty };
                    }
                    if (process.WaitForExit(Math.Max(1, Math.Min(100, Math.Min(remainingMs, compileRemainingMs))))) break;
                }
                int outputRemainingMs;
                if (cancellationToken.IsCancellationRequested || !request.TryDeadlineRemainingMs(out outputRemainingMs)
                    || !Task.WaitAll(new Task[] { stdoutTask, stderrTask }, Math.Max(1, Math.Min(5000, outputRemainingMs))))
                {
                    TerminateCompiler(process, stdoutTask, stderrTask);
                    return new RoslynCompilation { TimedOut = true, Output = string.Empty };
                }
                return new RoslynCompilation { ExitCode = process.ExitCode, Output = stdoutTask.Result + Environment.NewLine + stderrTask.Result };
            }
        }

        // deadline到达后立即终止compiler，并以短固定窗口完成进程与pipe回收。
        private static void TerminateCompiler(Process process, params Task[] outputTasks)
        {
            try { if (process != null && !process.HasExited) process.Kill(); } catch { }
            try { if (process != null) process.WaitForExit(1000); } catch { }
            try { Task.WaitAll(outputTasks, 1000); } catch { }
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
                if (diagnostics.Count >= MaxRoslynDiagnostics) continue;
                diagnostics.Add(new Dictionary<string, object>
                {
                    { "severity", match.Groups[4].Value.ToLowerInvariant() }, { "id", BoundedText(match.Groups[5].Value, 64) },
                    { "message", BoundedText(match.Groups[6].Value, 4096) }, { "file", BoundedText(match.Groups[1].Value, 2048) },
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
            return string.IsNullOrEmpty(dataPath) ? null : dataPath;
        }

        // 为 ProcessStartInfo.Arguments 转义单个路径参数。
        private static string QuoteProcessArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        // 将公开文本字段裁剪到固定UTF-8字节预算，并避免截断surrogate pair。
        private static string BoundedText(string value, int maxBytes)
        {
            if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) <= maxBytes) return value ?? string.Empty;
            var low = 0;
            var high = Math.Min(value.Length, maxBytes);
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                if (Encoding.UTF8.GetByteCount(value.Substring(0, middle)) <= maxBytes) low = middle;
                else high = middle - 1;
            }
            if (low > 0 && char.IsHighSurrogate(value[low - 1])) low--;
            return value.Substring(0, low);
        }

        // 两个Roslyn pipe共享一个UTF-8预算；超限后继续排空但不继续保留。
        private static async Task<string> ReadBoundedToEndAsync(TextReader reader, Utf8ByteBudget budget)
        {
            var result = new StringBuilder(4096);
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                var accepted = budget.Take(new string(buffer, 0, read));
                if (accepted.Length > 0) result.Append(accepted);
            }
            return result.ToString();
        }

        private sealed class Utf8ByteBudget
        {
            private int remaining;
            private readonly object gate = new object();
            internal Utf8ByteBudget(int bytes) { remaining = bytes; }
            internal string Take(string value)
            {
                lock (gate)
                {
                    if (remaining <= 0 || string.IsNullOrEmpty(value)) return string.Empty;
                    var accepted = BoundedText(value, remaining);
                    remaining -= Encoding.UTF8.GetByteCount(accepted);
                    return accepted;
                }
            }
        }

        private sealed class BoundedStringWriter : StringWriter
        {
            private readonly int maxBytes;
            private int writtenBytes;
            internal bool Truncated { get; private set; }
            internal BoundedStringWriter(int maxBytes) : base(CultureInfo.InvariantCulture) { this.maxBytes = maxBytes; }
            public override void Write(char value) { Write(value.ToString()); }
            public override void Write(string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                var accepted = BoundedText(value, maxBytes - writtenBytes);
                if (accepted.Length > 0) { base.Write(accepted); writtenBytes += Encoding.UTF8.GetByteCount(accepted); }
                if (accepted.Length != value.Length) Truncated = true;
            }
            public override void Write(char[] buffer, int index, int count) { Write(new string(buffer, index, count)); }
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
        private static Dictionary<string, object> CodeSuccessData(string stdout, string stderr, bool stdoutTruncated, bool stderrTruncated, object returnValue, UnityCliCompletionProbeReadback completion)
        {
            return new Dictionary<string, object>
            {
                { "compile", new Dictionary<string, object> { { "diagnostics", Array.Empty<object>() } } },
                { "runtime", new Dictionary<string, object>
                    {
                        { "returnValue", returnValue },
                        { "stdout", stdout }, { "stderr", stderr }, { "stdoutTruncated", stdoutTruncated }, { "stderrTruncated", stderrTruncated }
                    }
                },
                { "completion", completion }
            };
        }

        /// <summary>错误 code 保持稳定，公开 message 与兼容字段统一指向最深可操作 cause。</summary>
        private static CodeExecutionResult ExceptionFailure(string code, string prefix, Exception exception, string stdout, string stderr, string nextAction)
        {
            var projection = UnityCliExceptionProjection.Project(exception);
            return CodeExecutionResult.Fail(code, prefix + projection.PublicMessage, ExceptionDetails(projection, stdout, stderr), nextAction);
        }

        // cause chain 由 Shared helper 统一执行循环、深度和 UTF-8 byte 上限。
        private static Dictionary<string, object> ExceptionDetails(UnityCliExceptionProjection.Result projection, string stdout, string stderr)
        {
            return new Dictionary<string, object>
            {
                { "diagnostics", Array.Empty<object>() }, { "exceptionType", projection.DeepestExceptionType },
                { "message", projection.DeepestMessage }, { "stackTrace", projection.DeepestStackTrace },
                { "causes", projection.Causes }, { "causeChainTruncated", projection.Truncated },
                { "stdout", stdout }, { "stderr", stderr }
            };
        }

        // 将执行返回值规范化为序列化器支持的协议值。
        private static object NormalizeReturnValue(object value)
        {
            if (value == null || value is bool || value is int || value is long) return value;
            if (value is float)
            {
                var number = (float)value;
                if (float.IsNaN(number) || float.IsInfinity(number))
                    throw new InvalidOperationException("Runtime Code returned a non-finite floating-point value.");
                return number;
            }
            if (value is double)
            {
                var number = (double)value;
                if (double.IsNaN(number) || double.IsInfinity(number))
                    throw new InvalidOperationException("Runtime Code returned a non-finite floating-point value.");
                return number;
            }
            if (value is decimal) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return BoundedText(Convert.ToString(value, CultureInfo.InvariantCulture), MaxCodeOutputBytes);
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
