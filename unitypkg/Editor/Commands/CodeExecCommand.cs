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
        private static void RegisterCodeExecCommand(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "code.exec", "code", true, CodeExec);
        }

        private static string CodeExec(BridgeRequest request)
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
                    return ErrorJson("E_CODE_SOURCE_INVALID", "Code source is not readable: " + exception.Message, "Check that --file points to a UTF-8 C# source file.");
                }
            }
            if (string.IsNullOrWhiteSpace(source))
            {
                return ErrorJson("E_CODE_SOURCE_INVALID", "Code source is empty.", "Pass --file, --stdin or --code with non-empty C# source.");
            }

            var result = ExecuteCodeSource(request, source, startedAt);
            WriteLog("code_exec requestId=" + request.RequestId + " ok=" + result.Ok + " exitState=" + result.ExitState);
            if (!result.Ok)
            {
                return ErrorJsonWithDetails(result.ErrorCode, result.ErrorMessage, result.DetailsJson, result.NextAction);
            }
            return SuccessJson(result.DataJson);
        }

        private static CodeExecutionResult ExecuteCodeSource(BridgeRequest request, string source, DateTime startedAt)
        {
            var requestId = request.RequestId;
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
                var details = new UnityCliJsonWriter().BeginObject()
                    .Prop("referenceError", referenceError)
                    .EndObject()
                    .ToString();
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
                    return CodeExecutionResult.Fail("E_REFERENCE_RESOLUTION_FAILED", "Failed to compile Runtime Code: " + exception.Message, CodeDetailsJson("[]", Quote(exception.GetType().Name), Quote(exception.Message), Quote(exception.StackTrace), "", ""), "Check Unity/Editor/project assembly references and --refs.");
                }
            }
            finally
            {
                try { if (File.Exists(responseFilePath)) File.Delete(responseFilePath); } catch { }
            }

            if (compiled.Errors.HasErrors)
            {
                var diagnostics = CompilerDiagnosticsJson(compiled.Errors);
                var details = new UnityCliJsonWriter().BeginObject()
                    .PropRaw("diagnostics", diagnostics)
                    .EndObject()
                    .ToString();
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
                var dataJson = CodeSuccessDataJson(request, startedAt, endedAt, stdout.ToString(), stderr.ToString(), returnValue);
                return CodeExecutionResult.Success(dataJson);
            }
            catch (TargetInvocationException exception)
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                var inner = exception.InnerException ?? exception;
                return CodeExecutionResult.Fail("E_CODE_RUNTIME_EXCEPTION", inner.GetType().FullName + ": " + inner.Message, CodeDetailsJson("[]", Quote(inner.GetType().FullName), Quote(inner.Message), Quote(inner.StackTrace), stdout.ToString(), stderr.ToString()), "Inspect runtime stdout/stderr and exception stackTrace.");
            }
            catch (Exception exception)
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                return CodeExecutionResult.Fail("E_CODE_RUNTIME_EXCEPTION", exception.GetType().FullName + ": " + exception.Message, CodeDetailsJson("[]", Quote(exception.GetType().FullName), Quote(exception.Message), Quote(exception.StackTrace), stdout.ToString(), stderr.ToString()), "Inspect runtime stdout/stderr and exception stackTrace.");
            }
        }

        private static string WrapCodeSource(string source)
        {
            if (Regex.IsMatch(source, "\\bpublic\\s+static\\s+object\\s+Run\\s*\\("))
            {
                return "using System; using System.IO; using System.Linq; using System.Collections.Generic; using UnityEngine; using UnityEditor; namespace UnityCli.RuntimeCode { public static class Script { " + source + " } }";
            }
            return "using System; using System.IO; using System.Linq; using System.Collections.Generic; using UnityEngine; using UnityEditor; namespace UnityCli.RuntimeCode { public static class Script { public static object Run(string argsJson) { " + source + " } } }";
        }

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

        private static void AddReference(List<string> references, string reference)
        {
            if (string.IsNullOrEmpty(reference)) return;
            if (!references.Contains(reference)) references.Add(reference);
        }

        private static void AddReferenceIfExists(List<string> references, string reference)
        {
            if (!string.IsNullOrEmpty(reference) && File.Exists(reference)) AddReference(references, reference);
        }

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

        private static string CompilerDiagnosticsJson(CompilerErrorCollection errors)
        {
            var w = new UnityCliJsonWriter().BeginArray();
            foreach (CompilerError error in errors)
            {
                w.BeginObject()
                    .Prop("severity", error.IsWarning ? "warning" : "error")
                    .Prop("id", error.ErrorNumber)
                    .Prop("message", error.ErrorText)
                    .Prop("file", error.FileName)
                    .Prop("line", (long)error.Line)
                    .Prop("column", (long)error.Column)
                    .PropNull("span")
                    .EndObject();
            }
            w.EndArray();
            return w.ToString();
        }

        private static string CodeSuccessDataJson(BridgeRequest request, DateTime startedAt, DateTime endedAt, string stdout, string stderr, object returnValue)
        {
            var timeoutMs = request.OptionalIntArg("timeoutMs");
            var argsJson = request.StringArg("argsJson");
            var durationMs = (long)(endedAt - startedAt).TotalMilliseconds;
            return new UnityCliJsonWriter().BeginObject()
                .Prop("command", "code.exec")
                .Prop("mode", "live")
                .PropObject("execution", execution =>
                {
                    execution.Prop("channel", "live")
                        .Prop("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture))
                        .Prop("endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture))
                        .Prop("durationMs", durationMs);
                    if (timeoutMs.HasValue) execution.Prop("timeoutMs", timeoutMs.Value); else execution.PropNull("timeoutMs");
                    execution.Prop("exitState", "completed");
                })
                .PropObject("compile", compile =>
                {
                    compile.Prop("ok", true)
                        .PropRaw("diagnostics", "[]");
                })
                .PropObject("runtime", runtime =>
                {
                    runtime.Prop("ok", true)
                        .PropRaw("returnValue", ReturnValueJson(returnValue))
                        .Prop("stdout", stdout)
                        .Prop("stderr", stderr);
                })
                .PropRaw("artifacts", "[]")
                .PropObject("audit", audit =>
                {
                    audit.Prop("requestId", request.RequestId)
                        .Prop("projectPath", ProjectRoot())
                        .Prop("instanceId", InstanceId())
                        .Prop("unityVersion", Application.unityVersion)
                        .Prop("bridgeVersion", "com.unity_cli")
                        .Prop("initiator", "cli.live")
                        .PropObject("input", input =>
                        {
                            input.Prop("sourceKind", request.StringArg("sourceKind"))
                                .Prop("sourcePath", request.StringArg("sourcePath"))
                                .Prop("sourceSha256", request.StringArg("sourceSha256"))
                                .PropRaw("args", string.IsNullOrEmpty(argsJson) ? "{}" : argsJson);
                        })
                        .PropObject("execution", execution =>
                        {
                            if (timeoutMs.HasValue) execution.Prop("timeoutMs", timeoutMs.Value); else execution.PropNull("timeoutMs");
                            execution.Prop("exitState", "completed");
                        })
                        .PropRaw("artifacts", "[]")
                        .Prop("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture))
                        .Prop("endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture))
                        .Prop("durationMs", durationMs)
                        .PropObject("effects", effects =>
                        {
                            effects.PropRaw("fileWrites", "[]")
                                .Prop("assetRefresh", false)
                                .Prop("domainReload", false)
                                .Prop("playModeChanged", false)
                                .Prop("packagesChanged", false);
                        });
                })
                .PropObject("logs", logs => logs.Prop("bridge", LogPath()))
                .PropRaw("nextActions", "[]")
                .EndObject()
                .ToString();
        }

        private static string CodeDetailsJson(string diagnosticsJson, string exceptionTypeJson, string messageJson, string stackTraceJson, string stdout, string stderr)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("diagnostics", string.IsNullOrEmpty(diagnosticsJson) ? "[]" : diagnosticsJson)
                .PropRaw("exceptionType", exceptionTypeJson)
                .PropRaw("message", messageJson)
                .PropRaw("stackTrace", stackTraceJson)
                .Prop("stdout", stdout)
                .Prop("stderr", stderr)
                .EndObject()
                .ToString();
        }

        private static string ReturnValueJson(object value)
        {
            if (value == null) return "null";
            if (value is bool) return Bool((bool)value);
            if (value is float) return UnityCliJsonWriter.Number((float)value);
            if (value is double) return UnityCliJsonWriter.Number((double)value);
            if (value is int || value is long || value is decimal) return Convert.ToString(value, CultureInfo.InvariantCulture);
            return Quote(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private sealed class CodeExecutionResult
        {
            public bool Ok;
            public string DataJson;
            public string ErrorCode;
            public string ErrorMessage;
            public string DetailsJson;
            public string NextAction;
            public string ExitState;

            public static CodeExecutionResult Success(string dataJson)
            {
                return new CodeExecutionResult { Ok = true, DataJson = dataJson, ExitState = "completed" };
            }

            public static CodeExecutionResult Fail(string code, string message, string detailsJson, string nextAction)
            {
                return new CodeExecutionResult { Ok = false, ErrorCode = code, ErrorMessage = message, DetailsJson = detailsJson, NextAction = nextAction, ExitState = "failed" };
            }
        }
    }
}
#endif
