#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private const int MaxCodeOutputBytes = 256 * 1024;
        private const int MaxCodeTerminalBytes = 1536 * 1024;

        // 注册运行时代码执行命令。
        private static void RegisterCodeExecCommand(BridgeCommandRegistry registry)
        {
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "debug.code.execute",
                "code",
                true,
                60000,
                async (request, cancellationToken) => (object)NormalizeCommandResult(await CodeExec(request, cancellationToken)));
        }

        /// <summary>
        /// 编译并执行请求中的 C# 源码；会临时重定向进程标准输出，失败时返回结构化诊断或异常详情。
        /// 编译由共享 DynamicCodeCompiler 承担，本方法只保留 source wrapping、Run 入口、stdout 与异常投影语义。
        /// </summary>
        private static async ValueTask<string> CodeExec(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            if (request.Execution != null && request.Execution.DryRun == true)
                throw new BridgeCommandException("E_DRY_RUN_UNSUPPORTED", "Raw Bridge debug.code.execute dry-run cannot execute or compile caller source.", "Use the public tools run debug.code.execute --dry-run path, which returns the CLI-side source preview without Bridge dispatch.");
            var source = request.StringArg("sourceText");
            // raw Bridge不读取调用方路径；公开CLI已按入口deadline有界读取并内联唯一源码。
            if (string.IsNullOrWhiteSpace(source))
            {
                return SerializeErrorEnvelope("E_CODE_SOURCE_INVALID", "Code source is empty.", "Pass --file, --stdin or --code with non-empty C# source.");
            }

            var result = await ExecuteCodeSource(request, source, cancellationToken);
            WriteLog("code_exec requestId=" + request.RequestId + " ok=" + result.Ok + " exitState=" + result.ExitState);
            var serialized = SerializeCodeExecutionResult(request.RequestId, result);
            if (result.Ok) RecordDirectCompletionEvidence(request.RequestId, "debug.code.execute", ResolveRequestCompletionContract(request), result.Data);
            return serialized;
        }

        /// <summary>所有 debug.code.execute terminal 统一经过同一 Bridge envelope 与总 byte 上限。</summary>
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
        private static async ValueTask<CodeExecutionResult> ExecuteCodeSource(UnityCli.Protocol.BridgeRequest request, string source, CancellationToken cancellationToken)
        {
            var argsJson = request.StringArg("argsJson");
            Func<bool> deadlineOk = () =>
            {
                int remaining;
                return !cancellationToken.IsCancellationRequested && request.TryDeadlineRemainingMs(out remaining);
            };
            int deadlineRemainingMs;
            if (!deadlineOk())
                return CodeExecutionResult.Fail("E_EXECUTION_TIMEOUT", "Runtime Code compilation exceeded the immutable deadline.", new Dictionary<string, object>(), "Submit a new request after the Editor is responsive.");
            // 唯一编译所有权与共享预算都来自 DebugCompilerOwner；主线程路径必须非阻塞取得所有权。
            if (!DebugCompilerOwner.TryBeginOwnership(DebugCompilerLane.MainThreadTick))
                return CodeExecutionResult.Fail("E_UNITY_BUSY", "Another dynamic compilation currently owns the single compiler.", new Dictionary<string, object> { { "owner", "dynamic-compiler" } }, "Wait for the current compilation to reach its terminal result, then submit this request again.");
            Assembly compiledAssembly;
            try
            {
                var wrappedSource = WrapCodeSource(source);
                var candidateIdentity = DebugCompilerOwner.CandidateIdentity("debug.code.execute", wrappedSource, request.StringArg("defines"));
                // 编译前的粗判：数量或累计字节已经用尽时不再启动编译。
                var admission = DebugCompilerOwner.TryAdmitCandidate(candidateIdentity, 0L);
                if (!admission.Admitted) return BudgetExhausted(admission);
                var references = DebugCompilerOwner.References(request.StringArg("refs"), null, deadlineOk);
                request.TryDeadlineRemainingMs(out deadlineRemainingMs);
                var compileRequest = new DynamicCodeCompileRequest
                {
                    SourceText = wrappedSource,
                    SourceFileName = "source.cs",
                    ResponseFileName = "references.rsp",
                    AssemblyFileName = "assembly.dll",
                    PdbFileName = "assembly.pdb",
                    AssemblySimpleName = "UnityCli.RuntimeCode",
                    Defines = request.StringArg("defines")
                };
                var compilation = await DynamicCodeCompiler.CompileAsync(
                    compileRequest,
                    references,
                    deadlineOk,
                    new UnityCliWaiter(request.RequestId, "debug.code.execute"),
                    TimeSpan.FromMilliseconds(Math.Max(1, deadlineRemainingMs)),
                    cancellationToken);
                if (compilation.TimedOut)
                    return CodeExecutionResult.Fail("E_EXECUTION_TIMEOUT", "Runtime Code compilation exceeded the immutable deadline.", new Dictionary<string, object>(), "Inspect the Editor state before submitting a new request.");
                if (!compilation.Success)
                {
                    if (!string.IsNullOrEmpty(compilation.FailureCode))
                        return CodeExecutionResult.Fail(compilation.FailureCode, compilation.FailureMessage, new Dictionary<string, object>(), "Fix the request input and rerun code exec.");
                    var errors = compilation.Diagnostics;
                    var details = new Dictionary<string, object> { { "diagnostics", CompactRoslynDiagnostics(errors) } };
                    var referenceFailure = RoslynReferenceFailureFromDiagnostics(errors);
                    return !string.IsNullOrEmpty(referenceFailure)
                        ? CodeExecutionResult.Fail("E_REFERENCE_RESOLUTION_FAILED", referenceFailure, details, "Check Unity/Editor/project assembly references and --refs.")
                        : CodeExecutionResult.Fail("E_CODE_COMPILE_FAILED", RoslynCompileFailureMessage(errors), details, "Fix compiler diagnostics and rerun code exec.");
                }
                // 加载前的精确准入：用真实 bytes 判定，超限则不加载该产物，并报告当前占用。
                var loadAdmission = DebugCompilerOwner.TryAdmitCandidate(candidateIdentity, compilation.AssemblyBytes.LongLength);
                if (!loadAdmission.Admitted) return BudgetExhausted(loadAdmission);
                compiledAssembly = Assembly.Load(compilation.AssemblyBytes);
                // 加载成功即占用：Mono 不能逐个卸载，后续 ABI 校验失败也不扣减。
                DebugCompilerOwner.CommitLoadedArtifact(candidateIdentity, compilation.AssemblyBytes.LongLength, compiledAssembly.GetName().Name);
            }
            catch (OperationCanceledException)
            {
                return CodeExecutionResult.Fail("E_EXECUTION_TIMEOUT", "Runtime Code compilation exceeded the immutable deadline.", new Dictionary<string, object>(), "Submit a new request after the Editor is responsive.");
            }
            catch (Exception exception)
            {
                return ExceptionFailure("E_REFERENCE_RESOLUTION_FAILED", "Failed to compile Runtime Code: ", exception, "", "", "Check Unity/Editor/project assembly references and --refs.");
            }
            finally
            {
                DebugCompilerOwner.EndOwnership(DebugCompilerLane.MainThreadTick);
            }

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
            // 返回值被有界投影裁剪时必须把事实带回结果，调用方才能区分完整值与残片。
            var returnValueTruncated = false;
            Action invoke = () =>
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                try
                {
                    // 返回对象的ToString也属于caller effect，必须发生在fresh completion readback之前。
                    returnValue = NormalizeReturnValue(method.Invoke(null, new object[] { argsJson ?? "{}" }), out returnValueTruncated);
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
            try { return CodeExecutionResult.Success(CodeSuccessData(stdout.ToString(), stderr.ToString(), stdout.Truncated, stderr.Truncated, returnValue, returnValueTruncated, completion)); }
            catch (Exception exception) { return ExceptionFailure("E_CODE_RUNTIME_EXCEPTION", string.Empty, exception, stdout.ToString(), stderr.ToString(), "Inspect the bounded return-value conversion cause chain and affected business state."); }
        }

        /// <summary>
        /// 共享编译预算拒绝：投影当前占用（数量、字节、剩余）与显式 reload 建议。
        /// owner 不自动 reload；已加载程序集只能由 Domain Reload 回收。
        /// </summary>
        private static CodeExecutionResult BudgetExhausted(DebugCompilerAdmission admission)
        {
            var details = new Dictionary<string, object>
            {
                { "reason", admission.Reason },
                { "budget", admission.Report.ToData() }
            };
            return CodeExecutionResult.Fail(
                DebugCompilerOwner.BudgetExhaustedCode,
                "This domain cannot load another compiled artifact: " + admission.Reason + ".",
                details,
                DebugCompilerOwner.ReloadSuggestion);
        }

        /// <summary>caller effect紧前同时重验immutable deadline、cancel token与exact direct lease。</summary>
        private static void BeginCodeEffect(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            int remainingMs;
            if (cancellationToken.IsCancellationRequested || request == null || !request.TryDeadlineRemainingMs(out remainingMs))
                throw new OperationCanceledException("Runtime Code deadline elapsed before its effect barrier.", cancellationToken);
            BeginDirectEffect(request.RequestId);
        }

        // 将源码包装为运行时约定的 Script.Run 入口。pause.evaluate 与 debug.code.execute 共用同一包装形状。
        internal static string WrapCodeSource(string source)
        {
            if (Regex.IsMatch(source, "\\bpublic\\s+static\\s+object\\s+Run\\s*\\("))
            {
                return "using System; using System.IO; using System.Linq; using System.Collections.Generic; using UnityEngine; using UnityEditor; namespace UnityCli.RuntimeCode { public static class Script { " + source + " } }";
            }
            return "using System; using System.IO; using System.Linq; using System.Collections.Generic; using UnityEngine; using UnityEditor; namespace UnityCli.RuntimeCode { public static class Script { public static object Run(string argsJson) { " + source + " } } }";
        }

        // 从结构化错误诊断识别引用缺失错误（CS0006），保持为引用解析错误而非普通编译错误。
        private static string RoslynReferenceFailureFromDiagnostics(List<Dictionary<string, object>> errors)
        {
            var referenceError = errors.FirstOrDefault(item => string.Equals((string)item["id"], "CS0006", StringComparison.OrdinalIgnoreCase));
            return referenceError != null ? "Runtime Code reference was not found or readable: " + (string)referenceError["message"] : null;
        }

        // 拆分并清理逗号分隔参数。
        internal static IEnumerable<string> SplitCsv(string value)
        {
            if (string.IsNullOrEmpty(value)) yield break;
            var parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed)) yield return trimmed;
            }
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

        // 为 ProcessStartInfo.Arguments 转义单个路径参数。
        internal static string QuoteProcessArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        // 将公开文本字段裁剪到固定UTF-8字节预算，并避免截断surrogate pair。
        internal static string BoundedText(string value, int maxBytes)
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

        // 两个compiler pipe共享一个UTF-8预算；超限后继续排空但不继续保留。
        internal static async Task<string> ReadBoundedToEndAsync(TextReader reader, Utf8ByteBudget budget)
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

        internal sealed class Utf8ByteBudget
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

        /// <summary>
        /// 有界重试删除编译临时文件：子进程刚被终止时 Windows 可能仍持有句柄。
        /// 重试窗口固定且很短，不阻塞正常命令结果；调用方仍可依赖开工前的自愈清理。
        /// </summary>
        internal static void DeleteTemporaryCodeFileWithRetry(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return;
                    File.Delete(path);
                    if (!File.Exists(path)) return;
                }
                catch
                {
                    // 句柄未释放时重试更短窗口。
                }
                try { Thread.Sleep(150); } catch { return; }
            }
        }

        /// <summary>成功仅返回编译结果、运行返回值（含裁剪事实）与 request-specific fresh readback。</summary>
        private static Dictionary<string, object> CodeSuccessData(string stdout, string stderr, bool stdoutTruncated, bool stderrTruncated, object returnValue, bool returnValueTruncated, UnityCliCompletionProbeReadback completion)
        {
            return new Dictionary<string, object>
            {
                { "compile", new Dictionary<string, object> { { "diagnostics", Array.Empty<object>() } } },
                { "runtime", new Dictionary<string, object>
                    {
                        { "returnValue", returnValue },
                        // 显式值截断事实与适用的UTF-8字节预算：标量返回值为false，字符串返回值可能为true。
                        { "returnValueTruncated", returnValueTruncated },
                        { "returnValueByteBudget", (long)MaxCodeOutputBytes },
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

        // 将执行返回值规范化为序列化器支持的协议值；字符串返回按固定UTF-8字节预算裁剪并输出裁剪事实。
        internal static object NormalizeReturnValue(object value, out bool truncated)
        {
            truncated = false;
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
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            var bounded = BoundedText(text, MaxCodeOutputBytes);
            truncated = bounded.Length != (text == null ? 0 : text.Length);
            return bounded;
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
