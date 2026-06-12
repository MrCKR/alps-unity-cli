#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CSharp;
using UnityEditor;
using UnityEngine;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace AlpsUnityCli
{
    public static class Batch
    {
        public static void ExecuteCode()
        {
            var requestId = ArgValue("-alpsRequestId") ?? Guid.NewGuid().ToString("N");
            var sourcePath = ArgValue("-alpsCodeSource");
            var resultPath = ArgValue("-alpsCodeResult");
            var artifactDir = ArgValue("-alpsArtifactDir");
            var argsJson = ArgValue("-alpsCodeArgs") ?? "{}";
            var startedAt = DateTime.UtcNow;
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    WriteCodeResult(resultPath, ErrorEnvelope(requestId, "E_CODE_SOURCE_INVALID", "Code source file was not found: " + sourcePath, "{}"));
                    EditorApplication.Exit(1);
                    return;
                }
                Directory.CreateDirectory(string.IsNullOrEmpty(artifactDir) ? Path.GetDirectoryName(resultPath) : artifactDir);
                var source = File.ReadAllText(sourcePath, Encoding.UTF8);
                var result = ExecuteCodeSource(source, argsJson, ArgValue("-alpsCodeRefs"), ArgValue("-alpsCodeDefines"), startedAt);
                var json = result.Ok ? SuccessEnvelope(requestId, result.DataJson) : ErrorEnvelope(requestId, result.ErrorCode, result.ErrorMessage, result.DetailsJson);
                WriteCodeResult(resultPath, json);
                if (!result.Ok)
                {
                    Console.Error.WriteLine(result.ErrorCode + ": " + result.ErrorMessage);
                }
                EditorApplication.Exit(result.Ok ? 0 : 1);
            }
            catch (Exception exception)
            {
                WriteCodeResult(resultPath, ErrorEnvelope(requestId, "E_CODE_RUNTIME_EXCEPTION", exception.Message, "{\"exceptionType\":" + Quote(exception.GetType().FullName) + ",\"stackTrace\":" + Quote(exception.StackTrace) + "}"));
                Console.Error.WriteLine("E_CODE_RUNTIME_EXCEPTION: " + exception.Message);
                EditorApplication.Exit(1);
            }
        }

        public static void ResolvePackages()
        {
            Client.Resolve();
            var request = Client.List(false, true);
            WaitForRequest(request);

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

        private static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var index = 0; index < args.Length - 1; index += 1)
            {
                if (args[index] == name)
                {
                    return args[index + 1];
                }
            }
            return null;
        }

        private static CodeExecutionResult ExecuteCodeSource(string source, string argsJson, string refsCsv, string definesCsv, DateTime startedAt)
        {
            var provider = new CSharpCodeProvider();
            var parameters = new CompilerParameters { GenerateExecutable = false, GenerateInMemory = true, TreatWarningsAsErrors = false };
            foreach (var reference in DefaultReferences(refsCsv)) parameters.ReferencedAssemblies.Add(reference);
            foreach (var define in SplitCsv(definesCsv)) parameters.CompilerOptions += " /define:" + define;
            var compiled = provider.CompileAssemblyFromSource(parameters, WrapCodeSource(source));
            if (compiled.Errors.HasErrors)
            {
                return CodeExecutionResult.Fail("E_CODE_COMPILE_FAILED", "Runtime Code compilation failed.", "{\"diagnostics\":" + DiagnosticsJson(compiled.Errors) + "}");
            }
            var stdout = new StringWriter(CultureInfo.InvariantCulture);
            var stderr = new StringWriter(CultureInfo.InvariantCulture);
            var previousOut = Console.Out;
            var previousError = Console.Error;
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var type = compiled.CompiledAssembly.GetType("AlpsUnityCli.RuntimeCode.Script", true);
                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                var returnValue = method.Invoke(null, new object[] { argsJson ?? "{}" });
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                var endedAt = DateTime.UtcNow;
                return CodeExecutionResult.Success("{\"command\":\"code.exec\",\"mode\":\"batchmode\",\"execution\":{\"channel\":\"batchmode\",\"startedAt\":" + Quote(startedAt.ToString("O", CultureInfo.InvariantCulture)) + ",\"endedAt\":" + Quote(endedAt.ToString("O", CultureInfo.InvariantCulture)) + ",\"durationMs\":" + ((long)(endedAt - startedAt).TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + ",\"exitState\":\"completed\"},\"compile\":{\"ok\":true,\"diagnostics\":[]},\"runtime\":{\"ok\":true,\"returnValue\":" + ReturnValueJson(returnValue) + ",\"stdout\":" + Quote(stdout.ToString()) + ",\"stderr\":" + Quote(stderr.ToString()) + "},\"artifacts\":[],\"audit\":{\"requestId\":" + Quote(ArgValue("-alpsRequestId")) + ",\"command\":\"code.exec\",\"execution\":{\"channel\":\"batchmode\",\"exitState\":\"completed\"},\"effects\":{\"fileWrites\":[],\"assetRefresh\":false,\"domainReload\":false,\"playModeChanged\":false,\"packagesChanged\":false}}}");
            }
            catch (TargetInvocationException exception)
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                var inner = exception.InnerException ?? exception;
                return CodeExecutionResult.Fail("E_CODE_RUNTIME_EXCEPTION", inner.GetType().FullName + ": " + inner.Message, DetailsJson(inner, stdout.ToString(), stderr.ToString()));
            }
            catch (Exception exception)
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                return CodeExecutionResult.Fail("E_CODE_RUNTIME_EXCEPTION", exception.GetType().FullName + ": " + exception.Message, DetailsJson(exception, stdout.ToString(), stderr.ToString()));
            }
        }

        private static string WrapCodeSource(string source)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(source, "\\bpublic\\s+static\\s+object\\s+Run\\s*\\("))
            {
                return "using System; using System.IO; using System.Linq; using System.Collections.Generic; using UnityEngine; using UnityEditor; namespace AlpsUnityCli.RuntimeCode { public static class Script { " + source + " } }";
            }
            return "using System; using System.IO; using System.Linq; using System.Collections.Generic; using UnityEngine; using UnityEditor; namespace AlpsUnityCli.RuntimeCode { public static class Script { public static object Run(string argsJson) { " + source + " } } }";
        }

        private static IEnumerable<string> DefaultReferences(string refsCsv)
        {
            var references = new List<string>();
            AddReference(references, typeof(object).Assembly.Location);
            AddReference(references, typeof(Enumerable).Assembly.Location);
            AddReference(references, typeof(UnityEngine.Object).Assembly.Location);
            AddReference(references, typeof(EditorApplication).Assembly.Location);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;
                var name = assembly.GetName().Name;
                if (name.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase)) AddReference(references, assembly.Location);
            }
            foreach (var reference in SplitCsv(refsCsv)) AddReference(references, reference);
            return references;
        }

        private static void AddReference(List<string> references, string reference)
        {
            if (!string.IsNullOrEmpty(reference) && !references.Contains(reference)) references.Add(reference);
        }

        private static IEnumerable<string> SplitCsv(string value)
        {
            if (string.IsNullOrEmpty(value)) yield break;
            foreach (var part in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed)) yield return trimmed;
            }
        }

        private static string DiagnosticsJson(CompilerErrorCollection errors)
        {
            var items = new List<string>();
            foreach (CompilerError error in errors)
            {
                items.Add("{\"severity\":" + Quote(error.IsWarning ? "warning" : "error") + ",\"id\":" + Quote(error.ErrorNumber) + ",\"message\":" + Quote(error.ErrorText) + ",\"file\":" + Quote(error.FileName) + ",\"line\":" + error.Line.ToString(CultureInfo.InvariantCulture) + ",\"column\":" + error.Column.ToString(CultureInfo.InvariantCulture) + ",\"span\":null}");
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static string DetailsJson(Exception exception, string stdout, string stderr)
        {
            return "{\"exceptionType\":" + Quote(exception.GetType().FullName) + ",\"message\":" + Quote(exception.Message) + ",\"stackTrace\":" + Quote(exception.StackTrace) + ",\"stdout\":" + Quote(stdout) + ",\"stderr\":" + Quote(stderr) + "}";
        }

        private static string SuccessEnvelope(string requestId, string dataJson)
        {
            return "{\"ok\":true,\"requestId\":" + Quote(requestId) + ",\"data\":" + dataJson + "}";
        }

        private static string ErrorEnvelope(string requestId, string code, string message, string detailsJson)
        {
            return "{\"ok\":false,\"requestId\":" + Quote(requestId) + ",\"error\":{\"code\":" + Quote(code) + ",\"message\":" + Quote(message) + ",\"details\":" + (string.IsNullOrEmpty(detailsJson) ? "{}" : detailsJson) + "}}";
        }

        private static void WriteCodeResult(string resultPath, string json)
        {
            if (string.IsNullOrEmpty(resultPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
            File.WriteAllText(resultPath, json + Environment.NewLine, Encoding.UTF8);
        }

        private static string ReturnValueJson(object value)
        {
            if (value == null) return "null";
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is int || value is long || value is float || value is double || value is decimal) return Convert.ToString(value, CultureInfo.InvariantCulture);
            return Quote(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static string Quote(string value)
        {
            if (value == null) return "null";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        private sealed class CodeExecutionResult
        {
            public bool Ok;
            public string DataJson;
            public string ErrorCode;
            public string ErrorMessage;
            public string DetailsJson;
            public static CodeExecutionResult Success(string dataJson) { return new CodeExecutionResult { Ok = true, DataJson = dataJson }; }
            public static CodeExecutionResult Fail(string code, string message, string detailsJson) { return new CodeExecutionResult { Ok = false, ErrorCode = code, ErrorMessage = message, DetailsJson = detailsJson }; }
        }

        private static void WaitForRequest(Request request)
        {
            while (!request.IsCompleted)
            {
                System.Threading.Thread.Sleep(100);
            }
        }
    }
}
#endif
