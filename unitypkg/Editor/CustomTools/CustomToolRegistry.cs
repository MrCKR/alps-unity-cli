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
        private static readonly Dictionary<string, UnityCliCustomToolDefinition> CustomTools = new Dictionary<string, UnityCliCustomToolDefinition>(StringComparer.Ordinal);

        public static void RegisterCustomTool(UnityCliCustomToolDefinition definition)
        {
            if (definition == null || string.IsNullOrEmpty(definition.Name) || definition.Execute == null)
            {
                throw new ArgumentException("Custom tool registration requires name and Execute delegate.");
            }
            if (definition.ArtifactKinds == null)
            {
                definition.ArtifactKinds = new[] { "json", "log", "file", "image" };
            }
            if (definition.RequiredInput == null)
            {
                definition.RequiredInput = new string[0];
            }
            definition.DisplayName = string.IsNullOrEmpty(definition.DisplayName) ? definition.Name : definition.DisplayName;
            definition.Title = string.IsNullOrEmpty(definition.Title) ? definition.DisplayName : definition.Title;
            definition.Group = string.IsNullOrEmpty(definition.Group) ? CustomToolGroupFromName(definition.Name) : definition.Group;
            definition.Version = string.IsNullOrEmpty(definition.Version) ? "1.0.0" : definition.Version;
            definition.InputSchema = string.IsNullOrEmpty(definition.InputSchema) ? "{}" : definition.InputSchema;
            definition.OutputSchema = string.IsNullOrEmpty(definition.OutputSchema) ? "{}" : definition.OutputSchema;
            definition.CapabilityType = string.IsNullOrEmpty(definition.CapabilityType) ? "project-write" : definition.CapabilityType;
            definition.CoverageScope = definition.CoverageScope ?? new string[0];
            definition.DefaultTimeoutMs = definition.DefaultTimeoutMs <= 0 ? 30000 : definition.DefaultTimeoutMs;
            definition.MaxTimeoutMs = definition.MaxTimeoutMs <= 0 ? 600000 : definition.MaxTimeoutMs;
            definition.RegisteredBy = string.IsNullOrEmpty(definition.RegisteredBy) ? "unity-editor-package" : definition.RegisteredBy;
            definition.DescriptorSource = string.IsNullOrEmpty(definition.DescriptorSource) ? "runtime-registration" : definition.DescriptorSource;
            CustomTools[definition.Name] = definition;
        }

        private static void DiscoverCustomTools()
        {
            if (customToolsDiscovered)
            {
                return;
            }
            customToolsDiscovered = true;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null) continue;
                    var classAttribute = Attribute.GetCustomAttribute(type, typeof(UnityCliToolAttribute)) as UnityCliToolAttribute;
                    if (classAttribute != null)
                    {
                        TryRegisterAttributedCustomTool(assembly, type, null, classAttribute);
                    }

                    foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        var toolAttribute = Attribute.GetCustomAttribute(method, typeof(UnityCliToolAttribute)) as UnityCliToolAttribute;
                        if (toolAttribute != null)
                        {
                            TryRegisterAttributedCustomTool(assembly, type, method, toolAttribute);
                            continue;
                        }

                        var legacyAttribute = Attribute.GetCustomAttribute(method, typeof(UnityCliCustomToolAttribute)) as UnityCliCustomToolAttribute;
                        if (legacyAttribute == null) continue;
                        TryRegisterAttributedCustomTool(assembly, type, method, legacyAttribute);
                    }
                }
            }
        }

        private static void TryRegisterAttributedCustomTool(Assembly assembly, Type type, MethodInfo method, UnityCliToolAttribute attribute)
        {
            var executeMethod = method ?? ResolveClassAttributedToolMethod(type, attribute.Name);
            if (executeMethod == null)
            {
                WriteLog("custom_tool_registration_skipped name=" + attribute.Name + " reason=missing_execute_method");
                return;
            }

            var parameters = executeMethod.GetParameters();
            if (executeMethod.ReturnType != typeof(string) || parameters.Length > 1 || (parameters.Length == 1 && parameters[0].ParameterType != typeof(UnityCliCustomToolContext)))
            {
                WriteLog("custom_tool_registration_skipped name=" + attribute.Name + " reason=signature");
                return;
            }

            RegisterCustomTool(new UnityCliCustomToolDefinition
            {
                Name = attribute.Name,
                DisplayName = string.IsNullOrEmpty(attribute.DisplayName) ? attribute.Title : attribute.DisplayName,
                Title = string.IsNullOrEmpty(attribute.Title) ? attribute.DisplayName : attribute.Title,
                Description = attribute.Description,
                Group = attribute.Group,
                Version = attribute.Version,
                InputSchema = attribute.InputSchema,
                OutputSchema = attribute.OutputSchema,
                CapabilityType = attribute.CapabilityType,
                ReadOnlyHint = attribute.ReadOnlyHint,
                DestructiveHint = attribute.DestructiveHint,
                ArchitectureSensitive = attribute.ArchitectureSensitive,
                FrozenDecision = attribute.FrozenDecision,
                CoverageScope = SplitCsv(attribute.CoverageScope).ToArray(),
                DefaultTimeoutMs = attribute.DefaultTimeoutMs,
                MaxTimeoutMs = attribute.MaxTimeoutMs,
                SupportsJob = attribute.SupportsJob,
                ArtifactKinds = SplitCsv(attribute.ArtifactKinds).ToArray(),
                RequiredInput = SplitCsv(attribute.RequiredInput).ToArray(),
                RegisteredBy = "unity-editor-package",
                DescriptorSource = "UnityCliToolAttribute",
                Assembly = assembly.GetName().Name,
                Method = type.FullName + "." + executeMethod.Name,
                Execute = delegate(UnityCliCustomToolContext context)
                {
                    var result = parameters.Length == 0 ? executeMethod.Invoke(null, null) : executeMethod.Invoke(null, new object[] { context });
                    return result == null ? "null" : (string)result;
                }
            });
        }

        private static void TryRegisterAttributedCustomTool(Assembly assembly, Type type, MethodInfo method, UnityCliCustomToolAttribute attribute)
        {
            var parameters = method.GetParameters();
            if (method.ReturnType != typeof(string) || parameters.Length > 1 || (parameters.Length == 1 && parameters[0].ParameterType != typeof(UnityCliCustomToolContext)))
            {
                WriteLog("custom_tool_registration_skipped name=" + attribute.Name + " reason=signature");
                return;
            }

            RegisterCustomTool(new UnityCliCustomToolDefinition
            {
                Name = attribute.Name,
                DisplayName = attribute.DisplayName,
                Title = attribute.DisplayName,
                Description = attribute.Description,
                Group = CustomToolGroupFromName(attribute.Name),
                Version = attribute.Version,
                InputSchema = attribute.InputSchema,
                OutputSchema = attribute.OutputSchema,
                CapabilityType = attribute.CapabilityType,
                DefaultTimeoutMs = attribute.DefaultTimeoutMs,
                MaxTimeoutMs = attribute.MaxTimeoutMs,
                SupportsJob = attribute.SupportsJob,
                ArtifactKinds = SplitCsv(attribute.ArtifactKinds).ToArray(),
                RequiredInput = SplitCsv(attribute.RequiredInput).ToArray(),
                RegisteredBy = "unity-editor-package",
                DescriptorSource = "UnityCliCustomToolAttribute",
                Assembly = assembly.GetName().Name,
                Method = type.FullName + "." + method.Name,
                Execute = delegate(UnityCliCustomToolContext context)
                {
                    var result = parameters.Length == 0 ? method.Invoke(null, null) : method.Invoke(null, new object[] { context });
                    return result == null ? "null" : (string)result;
                }
            });
        }

        private static MethodInfo ResolveClassAttributedToolMethod(Type type, string toolName)
        {
            // Class-level [UnityCliTool] is intentionally conservative: the class must
            // expose exactly one static Execute/Run candidate. Put [UnityCliTool] on a
            // specific method when multiple candidates exist.
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var methods = type.GetMethods(flags).Where(m => m.Name == "Execute" || m.Name == "Run").ToArray();
            if (methods.Length == 1)
            {
                return methods[0];
            }
            WriteLog("custom_tool_registration_skipped name=" + toolName + " reason=ambiguous_class_level_execute_method type=" + type.FullName + " candidateCount=" + methods.Length.ToString(CultureInfo.InvariantCulture));
            return null;
        }

        private static string CustomToolGroupFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "custom";
            var dot = name.IndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : "custom";
        }

        private static string CustomToolList(BridgeRequest request)
        {
            DiscoverCustomTools();
            var tools = new List<string>();
            foreach (var pair in CustomTools)
            {
                tools.Add(CustomToolDefinitionJson(pair.Value));
            }
            tools.Sort(StringComparer.Ordinal);
            return SuccessJson("{\"tools\":[" + string.Join(",", tools.ToArray()) + "],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string CustomToolRun(BridgeRequest request)
        {
            DiscoverCustomTools();
            var name = request.StringArg("name");
            if (string.IsNullOrEmpty(name) || !CustomTools.ContainsKey(name))
            {
                return ErrorJson("E_TOOL_NOT_FOUND", "Tool not found: " + name, "Run unity_cli custom-tool list to inspect registered tools.");
            }

            var definition = CustomTools[name];
            var timeoutMs = request.IntArg("timeoutMs", definition.DefaultTimeoutMs);
            if (timeoutMs <= 0 || timeoutMs > definition.MaxTimeoutMs)
            {
                return ErrorJson("E_SCHEMA_INVALID", "timeoutMs exceeds the registered tool limit.", "Use a timeout between 1 and " + definition.MaxTimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms.");
            }

            if (request.BoolArgAtPath(new[] { "args", "execution", "preferJob" }, false))
            {
                return ErrorJson("E_JOB_UNSUPPORTED", "--job / Local Agent custom-tool execution is not implemented by the bundled Live Bridge yet.", "Run without --job for synchronous Live Bridge execution, or wait for Local Agent job support before relying on asynchronous custom-tool semantics.");
            }

            var inputJson = request.RawJsonArg("input") ?? "{}";
            var schemaError = ValidateCustomToolInput(definition, inputJson);
            if (!string.IsNullOrEmpty(schemaError))
            {
                return ErrorJson("E_TOOL_SCHEMA_INVALID", schemaError, "Run unity_cli custom-tool list to inspect the tool inputSchema.");
            }

            var startedAt = DateTime.UtcNow;
            var artifactDir = ResolveArtifactDir(request.StringArg("artifactDir"), request.RequestId);
            try
            {
                Directory.CreateDirectory(artifactDir);
            }
            catch (Exception exception)
            {
                return ErrorJson("E_ARTIFACT_WRITE_FAILED", "Failed to create artifact directory: " + exception.Message, "Pass --artifact-dir to a writable directory.");
            }

            string resultJson;
            try
            {
                WriteLog("custom_tool_run requestId=" + request.RequestId + " name=" + name + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
                resultJson = definition.Execute(new UnityCliCustomToolContext
                {
                    RequestId = request.RequestId,
                    ProjectPath = request.StringArg("projectPath") ?? ProjectRoot(),
                    InputJson = inputJson,
                    ArtifactDir = artifactDir,
                    LogPath = LogPath()
                });
            }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException ?? exception;
                return ErrorJson("E_TOOL_EXECUTION_FAILED", inner.GetType().Name + ": " + inner.Message, "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }
            catch (Exception exception)
            {
                return ErrorJson("E_TOOL_EXECUTION_FAILED", exception.GetType().Name + ": " + exception.Message, "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }

            var endedAt = DateTime.UtcNow;
            var durationMs = (long)(endedAt - startedAt).TotalMilliseconds;
            var manifest = CustomToolManifestJson(request.RequestId, definition, artifactDir, startedAt, endedAt);
            try
            {
                File.WriteAllText(Path.Combine(artifactDir, "manifest.json"), manifest);
            }
            catch (Exception exception)
            {
                return ErrorJson("E_ARTIFACT_WRITE_FAILED", "Failed to write artifact manifest: " + exception.Message, "Pass --artifact-dir to a writable directory.");
            }
            if (durationMs > timeoutMs)
            {
                WriteLog("custom_tool_timeout requestId=" + request.RequestId + " name=" + name + " durationMs=" + durationMs.ToString(CultureInfo.InvariantCulture) + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
                return ErrorJson("E_EXECUTION_TIMEOUT", "Tool exceeded timeoutMs after completion: " + durationMs.ToString(CultureInfo.InvariantCulture) + "ms > " + timeoutMs.ToString(CultureInfo.InvariantCulture) + "ms.", "Inspect bridge logs and artifact manifest with the returned requestId before retrying.");
            }

            return SuccessJson("{"
                + "\"command\":\"custom-tool.run\","
                + "\"tool\":{\"name\":" + Quote(definition.Name) + ",\"version\":" + Quote(definition.Version) + "},"
                + "\"execution\":{\"channel\":\"live\",\"startedAt\":" + Quote(startedAt.ToString("O", CultureInfo.InvariantCulture)) + ",\"endedAt\":" + Quote(endedAt.ToString("O", CultureInfo.InvariantCulture)) + ",\"durationMs\":" + durationMs.ToString(CultureInfo.InvariantCulture) + ",\"exitState\":\"completed\"},"
                + "\"result\":" + JsonLiteralOrString(resultJson) + ","
                + "\"artifacts\":[{\"id\":\"manifest\",\"kind\":\"json\",\"path\":" + Quote(Path.Combine(artifactDir, "manifest.json")) + ",\"description\":\"custom-tool artifact manifest\"}],"
                + "\"audit\":" + CustomToolAuditJson(request, definition, artifactDir, inputJson, timeoutMs, startedAt, endedAt, "completed") + ","
                + "\"logs\":{\"bridge\":" + Quote(LogPath()) + "}"
                + "}");
        }

        private static string CustomToolDefinitionJson(UnityCliCustomToolDefinition definition)
        {
            return "{"
                + "\"name\":" + Quote(definition.Name) + ","
                + "\"displayName\":" + Quote(definition.DisplayName) + ","
                + "\"title\":" + Quote(definition.Title) + ","
                + "\"description\":" + Quote(definition.Description) + ","
                + "\"group\":" + Quote(definition.Group) + ","
                + "\"version\":" + Quote(definition.Version) + ","
                + "\"inputSchema\":" + JsonLiteralOrString(definition.InputSchema) + ","
                + "\"outputSchema\":" + JsonLiteralOrString(definition.OutputSchema) + ","
                + "\"capabilityType\":" + Quote(definition.CapabilityType) + ","
                + "\"readOnlyHint\":" + Bool(definition.ReadOnlyHint) + ","
                + "\"destructiveHint\":" + Bool(definition.DestructiveHint) + ","
                + "\"defaultTimeoutMs\":" + definition.DefaultTimeoutMs.ToString(CultureInfo.InvariantCulture) + ","
                + "\"maxTimeoutMs\":" + definition.MaxTimeoutMs.ToString(CultureInfo.InvariantCulture) + ","
                + "\"supportsJob\":" + Bool(definition.SupportsJob) + ","
                + "\"artifactKinds\":" + StringArrayJson(definition.ArtifactKinds) + ","
                + "\"architectureSensitive\":" + Bool(definition.ArchitectureSensitive) + ","
                + "\"frozenDecision\":" + Quote(definition.FrozenDecision) + ","
                + "\"coverageScope\":" + StringArrayJson(definition.CoverageScope) + ","
                + "\"handler\":\"bridge\","
                + "\"supportStatus\":\"live-bridge\","
                + "\"cliCommand\":" + Quote("custom-tool run " + definition.Name) + ","
                + "\"docsTopic\":\"custom-tool\","
                + "\"descriptorSource\":" + Quote(definition.DescriptorSource) + ","
                + "\"registeredBy\":" + Quote(definition.RegisteredBy) + ","
                + "\"assembly\":" + Quote(definition.Assembly) + ","
                + "\"method\":" + Quote(definition.Method)
                + "}";
        }

        private static string CustomToolManifestJson(string requestId, UnityCliCustomToolDefinition definition, string artifactDir, DateTime startedAt, DateTime endedAt)
        {
            return "{"
                + "\"requestId\":" + Quote(requestId) + ","
                + "\"command\":\"custom-tool.run\","
                + "\"tool\":{\"name\":" + Quote(definition.Name) + ",\"version\":" + Quote(definition.Version) + "},"
                + "\"artifact\":[{\"id\":\"manifest\",\"kind\":\"json\",\"relativePath\":\"manifest.json\",\"producer\":\"unity-editor-package\"}],"
                + "\"createdAt\":" + Quote(endedAt.ToString("O", CultureInfo.InvariantCulture)) + ","
                + "\"startedAt\":" + Quote(startedAt.ToString("O", CultureInfo.InvariantCulture)) + ","
                + "\"root\":" + Quote(artifactDir)
                + "}";
        }

        private static string CustomToolAuditJson(BridgeRequest request, UnityCliCustomToolDefinition definition, string artifactDir, string inputJson, int timeoutMs, DateTime startedAt, DateTime endedAt, string exitState)
        {
            return "{"
                + "\"requestId\":" + Quote(request.RequestId) + ","
                + "\"command\":\"custom-tool.run\","
                + "\"projectPath\":" + Quote(request.StringArg("projectPath") ?? ProjectRoot()) + ","
                + "\"instanceId\":" + Quote(request.StringArg("instanceId")) + ","
                + "\"unityVersion\":" + Quote(Application.unityVersion) + ","
                + "\"bridgeVersion\":\"com.unity_cli\","
                + "\"startedAt\":" + Quote(startedAt.ToString("O", CultureInfo.InvariantCulture)) + ","
                + "\"endedAt\":" + Quote(endedAt.ToString("O", CultureInfo.InvariantCulture)) + ","
                + "\"durationMs\":" + ((long)(endedAt - startedAt).TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + ","
                + "\"initiator\":{\"cliUser\":\"local\",\"cwd\":" + Quote(Directory.GetCurrentDirectory()) + "},"
                + "\"input\":{\"sourceKind\":\"json\",\"sizeBytes\":" + Encoding.UTF8.GetByteCount(inputJson ?? string.Empty).ToString(CultureInfo.InvariantCulture) + "},"
                + "\"execution\":{\"channel\":\"live\",\"timeoutMs\":" + timeoutMs.ToString(CultureInfo.InvariantCulture) + ",\"exitState\":" + Quote(exitState) + "},"
                + "\"effects\":{\"fileWrites\":[],\"assetRefresh\":false,\"domainReload\":false,\"playModeChanged\":false,\"packagesChanged\":false},"
                + "\"artifacts\":[{\"kind\":\"json\",\"path\":" + Quote(Path.Combine(artifactDir, "manifest.json")) + "}],"
                + "\"tool\":{\"name\":" + Quote(definition.Name) + ",\"capabilityType\":" + Quote(definition.CapabilityType) + "}"
                + "}";
        }
    }
}
#endif
