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
using UnityEditor.Build.Reporting;

namespace AlpsUnityCli
{
    public static class Batch
    {
        public static void RunBridgeCommand()
        {
            var command = ArgValue("-alpsBridgeCommand");
            var requestId = ArgValue("-alpsBridgeRequestId") ?? Guid.NewGuid().ToString("N");
            var args = ArgValue("-alpsBridgeArgs") ?? "{}";
            var exitCode = AlpsUnityBridge.ExecuteBatchCommand(requestId, command, args);
            EditorApplication.Exit(exitCode);
        }

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
                    WriteCodeResult(resultPath, ErrorEnvelope(requestId, "E_CODE_SOURCE_INVALID", "Code source file was not found: " + sourcePath, "{}", NextActionsJson("Pass --file, --stdin or --code with readable C# source.")));
                    EditorApplication.Exit(1);
                    return;
                }
                Directory.CreateDirectory(string.IsNullOrEmpty(artifactDir) ? Path.GetDirectoryName(resultPath) : artifactDir);
                var source = File.ReadAllText(sourcePath, Encoding.UTF8);
                var result = ExecuteCodeSource(requestId, source, argsJson, ArgValue("-alpsCodeRefs"), ArgValue("-alpsCodeDefines"), startedAt);
                var json = result.Ok ? SuccessEnvelope(requestId, result.DataJson) : ErrorEnvelope(requestId, result.ErrorCode, result.ErrorMessage, result.DetailsJson, result.NextActionsJson);
                WriteCodeResult(resultPath, json);
                if (!result.Ok)
                {
                    Console.Error.WriteLine(result.ErrorCode + ": " + result.ErrorMessage);
                }
                EditorApplication.Exit(result.Ok ? 0 : 1);
            }
            catch (Exception exception)
            {
                WriteCodeResult(resultPath, ErrorEnvelope(requestId, "E_CODE_RUNTIME_EXCEPTION", exception.Message, "{\"exceptionType\":" + Quote(exception.GetType().FullName) + ",\"stackTrace\":" + Quote(exception.StackTrace) + "}", NextActionsJson("Inspect batchmode log and result artifact for exception details.")));
                Console.Error.WriteLine("E_CODE_RUNTIME_EXCEPTION: " + exception.Message);
                EditorApplication.Exit(1);
            }
        }

        public static void Refresh()
        {
            AssetDatabase.Refresh();
            Console.WriteLine("AssetDatabase refresh completed.");
            EditorApplication.Exit(0);
        }

        public static void RefreshAndCompile()
        {
            AssetDatabase.Refresh();
            Console.WriteLine("AssetDatabase refresh and compile requested.");
            EditorApplication.Exit(0);
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

        public static void BuildProject()
        {
            var outputPath = ArgValue("-alpsBuildOutput");
            if (string.IsNullOrEmpty(outputPath))
            {
                Console.Error.WriteLine("E_BUILD_OUTPUT_REQUIRED: Missing -alpsBuildOutput.");
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

        private static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var index = 0; index < args.Length - 1; index += 1)
            {
                if (args[index] == name)
                {
                    var value = args[index + 1];
                    if (value == "__ALPS_EMPTY__" || value.StartsWith("-", StringComparison.Ordinal)) return string.Empty;
                    return value;
                }
            }
            return null;
        }

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
                    return CodeExecutionResult.Fail("E_REFERENCE_RESOLUTION_FAILED", referenceError, "{\"referenceError\":" + Quote(referenceError) + "}", NextActionsJson("Check Unity/Editor/project assembly references and --refs."));
                }
                foreach (var reference in references) parameters.ReferencedAssemblies.Add(reference);
                foreach (var define in SplitCsv(definesCsv)) parameters.CompilerOptions += " /define:" + define;
                compiled = provider.CompileAssemblyFromSource(parameters, WrapCodeSource(source));
            }
            catch (Exception exception)
            {
                return CodeExecutionResult.Fail("E_REFERENCE_RESOLUTION_FAILED", "Failed to compile Runtime Code: " + exception.Message, "{\"diagnostics\":[],\"exceptionType\":" + Quote(exception.GetType().FullName) + ",\"message\":" + Quote(exception.Message) + ",\"stackTrace\":" + Quote(exception.StackTrace) + "}", NextActionsJson("Check Unity/Editor/project assembly references and --refs."));
            }
            if (compiled.Errors.HasErrors)
            {
                return CodeExecutionResult.Fail("E_CODE_COMPILE_FAILED", "Runtime Code compilation failed.", "{\"diagnostics\":" + DiagnosticsJson(compiled.Errors) + "}", NextActionsJson("Fix compiler diagnostics and rerun code exec."));
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
                return CodeExecutionResult.Success(CodeSuccessDataJson(requestId, startedAt, endedAt, stdout.ToString(), stderr.ToString(), returnValue, argsJson));
            }
            catch (TargetInvocationException exception)
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                var inner = exception.InnerException ?? exception;
                return CodeExecutionResult.Fail("E_CODE_RUNTIME_EXCEPTION", inner.GetType().FullName + ": " + inner.Message, DetailsJson(inner, stdout.ToString(), stderr.ToString()), NextActionsJson("Inspect runtime stdout/stderr and exception stackTrace."));
            }
            catch (Exception exception)
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                return CodeExecutionResult.Fail("E_CODE_RUNTIME_EXCEPTION", exception.GetType().FullName + ": " + exception.Message, DetailsJson(exception, stdout.ToString(), stderr.ToString()), NextActionsJson("Inspect runtime stdout/stderr and exception stackTrace."));
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
            AddReferenceIfExists(references, Path.Combine(EditorApplication.applicationContentsPath, "Managed", "netstandard.dll"));
            AddReferenceIfExists(references, Path.Combine(EditorApplication.applicationContentsPath, "Managed", "UnityEngine", "UnityEngine.dll"));
            AddReferenceIfExists(references, Path.Combine(EditorApplication.applicationContentsPath, "Managed", "UnityEngine", "UnityEngine.CoreModule.dll"));
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;
                var name = assembly.GetName().Name;
                if (name.Equals("netstandard", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase)) AddReference(references, assembly.Location);
            }
            var scriptAssemblies = Path.Combine(ProjectPath(), "Library", "ScriptAssemblies");
            if (Directory.Exists(scriptAssemblies))
            {
                foreach (var assemblyPath in Directory.GetFiles(scriptAssemblies, "*.dll")) AddReference(references, assemblyPath);
            }
            foreach (var reference in SplitCsv(refsCsv)) AddReference(references, reference);
            return references;
        }

        private static void AddReference(List<string> references, string reference)
        {
            if (!string.IsNullOrEmpty(reference) && !references.Contains(reference)) references.Add(reference);
        }

        private static void AddReferenceIfExists(List<string> references, string reference)
        {
            if (!string.IsNullOrEmpty(reference) && File.Exists(reference)) AddReference(references, reference);
        }

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

        private static string ErrorEnvelope(string requestId, string code, string message, string detailsJson, string nextActionsJson)
        {
            return "{\"ok\":false,\"requestId\":" + Quote(requestId) + ",\"error\":{\"code\":" + Quote(code) + ",\"message\":" + Quote(message) + ",\"details\":" + (string.IsNullOrEmpty(detailsJson) ? "{}" : detailsJson) + "},\"nextActions\":" + (string.IsNullOrEmpty(nextActionsJson) ? "[]" : nextActionsJson) + "}";
        }

        private static void WriteCodeResult(string resultPath, string json)
        {
            if (string.IsNullOrEmpty(resultPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
            File.WriteAllText(resultPath, json + Environment.NewLine, Encoding.UTF8);
        }

        private static string CodeSuccessDataJson(string requestId, DateTime startedAt, DateTime endedAt, string stdout, string stderr, object returnValue, string argsJson)
        {
            return "{\"command\":\"code.exec\",\"mode\":\"batchmode\",\"execution\":{\"channel\":\"batchmode\",\"startedAt\":" + Quote(startedAt.ToString("O", CultureInfo.InvariantCulture)) + ",\"endedAt\":" + Quote(endedAt.ToString("O", CultureInfo.InvariantCulture)) + ",\"durationMs\":" + ((long)(endedAt - startedAt).TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + ",\"timeoutMs\":" + TimeoutMsJson() + ",\"exitState\":\"completed\"},\"compile\":{\"ok\":true,\"diagnostics\":[]},\"runtime\":{\"ok\":true,\"returnValue\":" + ReturnValueJson(returnValue) + ",\"stdout\":" + Quote(stdout) + ",\"stderr\":" + Quote(stderr) + "},\"artifacts\":[],\"audit\":{\"requestId\":" + Quote(requestId) + ",\"projectPath\":" + Quote(ProjectPath()) + ",\"instanceId\":" + Quote(InstanceId()) + ",\"unityVersion\":" + Quote(Application.unityVersion) + ",\"bridgeVersion\":\"com.unity_cli\",\"initiator\":\"cli.batchmode\",\"input\":{\"sourcePath\":" + Quote(ArgValue("-alpsCodeSource")) + ",\"args\":" + (string.IsNullOrEmpty(argsJson) ? "{}" : argsJson) + "},\"execution\":{\"timeoutMs\":" + TimeoutMsJson() + ",\"exitState\":\"completed\"},\"artifacts\":[],\"effects\":{\"fileWrites\":[],\"assetRefresh\":false,\"domainReload\":false,\"playModeChanged\":false,\"packagesChanged\":false}}}";
        }

        private static string TimeoutMsJson()
        {
            var value = ArgValue("-alpsTimeoutMs");
            long parsed;
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed.ToString(CultureInfo.InvariantCulture) : "null";
        }

        private static string ProjectPath()
        {
            var projectPath = ArgValue("-alpsProjectPath");
            if (!string.IsNullOrEmpty(projectPath)) return projectPath;
            return Directory.GetParent(Application.dataPath).FullName;
        }

        private static string InstanceId()
        {
            return System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
        }

        private static string NextActionsJson(string action)
        {
            return "[" + Quote(action) + "]";
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
            public string NextActionsJson;
            public static CodeExecutionResult Success(string dataJson) { return new CodeExecutionResult { Ok = true, DataJson = dataJson }; }
            public static CodeExecutionResult Fail(string code, string message, string detailsJson, string nextActionsJson) { return new CodeExecutionResult { Ok = false, ErrorCode = code, ErrorMessage = message, DetailsJson = detailsJson, NextActionsJson = nextActionsJson }; }
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
