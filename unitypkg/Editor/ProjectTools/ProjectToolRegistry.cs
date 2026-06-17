#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
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
        private const string ProjectToolFingerprintDefaultMode = "block";
        private static readonly Dictionary<string, UnityCliProjectToolDefinition> ProjectTools = new Dictionary<string, UnityCliProjectToolDefinition>(StringComparer.Ordinal);

        public static void RegisterProjectTool(UnityCliProjectToolDefinition definition)
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
            definition.Group = string.IsNullOrEmpty(definition.Group) ? ProjectToolGroupFromName(definition.Name) : definition.Group;
            definition.Version = string.IsNullOrEmpty(definition.Version) ? "1.0.0" : definition.Version;
            definition.InputSchema = string.IsNullOrEmpty(definition.InputSchema) ? "{}" : definition.InputSchema;
            definition.OutputSchema = string.IsNullOrEmpty(definition.OutputSchema) ? "{}" : definition.OutputSchema;
            definition.CapabilityType = string.IsNullOrEmpty(definition.CapabilityType) ? "project-write" : definition.CapabilityType;
            definition.CoverageScope = definition.CoverageScope ?? new string[0];
            definition.DefaultTimeoutMs = definition.DefaultTimeoutMs <= 0 ? 30000 : definition.DefaultTimeoutMs;
            definition.MaxTimeoutMs = definition.MaxTimeoutMs <= 0 ? 600000 : definition.MaxTimeoutMs;
            definition.RegisteredBy = string.IsNullOrEmpty(definition.RegisteredBy) ? "unity-editor-package" : definition.RegisteredBy;
            definition.DescriptorSource = string.IsNullOrEmpty(definition.DescriptorSource) ? "runtime-registration" : definition.DescriptorSource;
            RefreshProjectToolFingerprint(definition, null);
            ProjectTools[definition.Name] = definition;
        }

        private static void DiscoverProjectTools()
        {
            if (projectToolsDiscovered)
            {
                return;
            }
            projectToolsDiscovered = true;

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
                        TryRegisterAttributedProjectTool(assembly, type, null, classAttribute);
                    }

                    foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        var toolAttribute = Attribute.GetCustomAttribute(method, typeof(UnityCliToolAttribute)) as UnityCliToolAttribute;
                        if (toolAttribute != null)
                        {
                            TryRegisterAttributedProjectTool(assembly, type, method, toolAttribute);
                            continue;
                        }

                    }
                }
            }
        }

        private static void TryRegisterAttributedProjectTool(Assembly assembly, Type type, MethodInfo method, UnityCliToolAttribute attribute)
        {
            var executeMethod = method ?? ResolveClassAttributedToolMethod(type, attribute.Name);
            if (executeMethod == null)
            {
                WriteLog("custom_tool_registration_skipped name=" + attribute.Name + " reason=missing_execute_method");
                return;
            }

            var parameters = executeMethod.GetParameters();
            if (executeMethod.ReturnType != typeof(string) || parameters.Length > 1 || (parameters.Length == 1 && parameters[0].ParameterType != typeof(UnityCliProjectToolContext)))
            {
                WriteLog("custom_tool_registration_skipped name=" + attribute.Name + " reason=signature");
                return;
            }

            RegisterProjectTool(new UnityCliProjectToolDefinition
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
                Fingerprint = attribute.Fingerprint,
                ExpectedFingerprint = attribute.Fingerprint,
                Execute = delegate(UnityCliProjectToolContext context)
                {
                    var result = parameters.Length == 0 ? executeMethod.Invoke(null, null) : executeMethod.Invoke(null, new object[] { context });
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

        private static string ProjectToolGroupFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "custom";
            var dot = name.IndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : "custom";
        }

        private static string ProjectToolList(BridgeRequest request)
        {
            DiscoverProjectTools();
            var tools = new List<string>();
            foreach (var pair in ProjectTools)
            {
                tools.Add(ProjectToolDefinitionJson(pair.Value));
            }
            tools.Sort(StringComparer.Ordinal);
            var data = new UnityCliJsonWriter().BeginObject()
                .PropRaw("tools", "[" + string.Join(",", tools.ToArray()) + "]")
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
            return SuccessJson(data);
        }

        private static string ProjectToolRun(BridgeRequest request)
        {
            DiscoverProjectTools();
            var name = request.StringArg("name");
            if (string.IsNullOrEmpty(name) || !ProjectTools.ContainsKey(name))
            {
                return ErrorJson("E_TOOL_NOT_FOUND", "Tool not found: " + name, "Run unity_cli tools list --include-custom to inspect registered tools.");
            }

            var definition = ProjectTools[name];
            var fingerprintCheck = RefreshProjectToolFingerprint(definition, request.StringArg("fingerprintMode"));
            if (fingerprintCheck.Blocked)
            {
                WriteLog("custom_tool_fingerprint_blocked requestId=" + request.RequestId + " name=" + name + " expected=" + fingerprintCheck.Expected + " actual=" + fingerprintCheck.Actual);
                return ErrorJsonWithDetails("E_TOOL_FINGERPRINT_MISMATCH", fingerprintCheck.Message, ProjectToolFingerprintDetailsJson(definition), "Review the registered custom tool descriptor/handler code, then update the expected fingerprint or rerun with --fingerprint-mode warn if policy allows.");
            }
            if (fingerprintCheck.Warn)
            {
                WriteLog("custom_tool_fingerprint_warn requestId=" + request.RequestId + " name=" + name + " expected=" + fingerprintCheck.Expected + " actual=" + fingerprintCheck.Actual);
            }

            var timeoutMs = request.IntArg("timeoutMs", definition.DefaultTimeoutMs);
            if (timeoutMs <= 0 || timeoutMs > definition.MaxTimeoutMs)
            {
                return ErrorJson("E_SCHEMA_INVALID", "timeoutMs exceeds the registered tool limit.", "Use a timeout between 1 and " + definition.MaxTimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms.");
            }

            if (request.BoolArgAtPath(new[] { "args", "execution", "preferJob" }, false))
            {
                return ErrorJson("E_JOB_UNSUPPORTED", "--job / Local Agent project-tool execution is not implemented by the bundled Live Bridge yet.", "Run without --job for synchronous Live Bridge execution, or wait for Local Agent job support before relying on asynchronous project-tool semantics.");
            }

            var inputJson = request.RawJsonArg("input") ?? "{}";
            var schemaError = ValidateProjectToolInput(definition, inputJson);
            if (!string.IsNullOrEmpty(schemaError))
            {
                return ErrorJson("E_TOOL_SCHEMA_INVALID", schemaError, "Run unity_cli tools list --include-custom to inspect the tool inputSchema.");
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
                resultJson = definition.Execute(new UnityCliProjectToolContext
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
            var manifest = ProjectToolManifestJson(request.RequestId, definition, artifactDir, startedAt, endedAt);
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

            var data = new UnityCliJsonWriter().BeginObject()
                .Prop("command", "project-tool.run")
                .PropObject("tool", tool =>
                {
                    tool.Prop("name", definition.Name)
                        .Prop("version", definition.Version);
                })
                .PropObject("execution", execution =>
                {
                    execution.Prop("channel", "live")
                        .Prop("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture))
                        .Prop("endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture))
                        .Prop("durationMs", durationMs)
                        .Prop("exitState", "completed");
                })
                .PropRaw("result", resultJson)
                .PropArray("artifacts", artifacts =>
                {
                    artifacts.BeginObject()
                        .Prop("id", "manifest")
                        .Prop("kind", "json")
                        .Prop("path", Path.Combine(artifactDir, "manifest.json"))
                        .Prop("description", "project-tool artifact manifest")
                        .EndObject();
                })
                .PropRaw("audit", ProjectToolAuditJson(request, definition, artifactDir, inputJson, timeoutMs, startedAt, endedAt, "completed"))
                .PropObject("logs", logs => logs.Prop("bridge", LogPath()))
                .EndObject()
                .ToString();
            return SuccessJson(data);
        }

        private static string ProjectToolDefinitionJson(UnityCliProjectToolDefinition definition)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("name", definition.Name)
                .Prop("displayName", definition.DisplayName)
                .Prop("title", definition.Title)
                .Prop("description", definition.Description)
                .Prop("group", definition.Group)
                .Prop("version", definition.Version)
                .PropRaw("inputSchema", definition.InputSchema)
                .PropRaw("outputSchema", definition.OutputSchema)
                .Prop("capabilityType", definition.CapabilityType)
                .Prop("readOnlyHint", definition.ReadOnlyHint)
                .Prop("destructiveHint", definition.DestructiveHint)
                .Prop("defaultTimeoutMs", definition.DefaultTimeoutMs)
                .Prop("maxTimeoutMs", definition.MaxTimeoutMs)
                .Prop("supportsJob", definition.SupportsJob)
                .PropRaw("artifactKinds", StringArrayJson(definition.ArtifactKinds))
                .Prop("architectureSensitive", definition.ArchitectureSensitive)
                .Prop("frozenDecision", definition.FrozenDecision)
                .PropRaw("coverageScope", StringArrayJson(definition.CoverageScope))
                .Prop("handler", "bridge")
                .Prop("supportStatus", "live-bridge")
                .Prop("cliCommand", "tools run " + definition.Name)
                .Prop("docsTopic", "project-tool")
                .Prop("descriptorSource", definition.DescriptorSource)
                .Prop("registeredBy", definition.RegisteredBy)
                .Prop("assembly", definition.Assembly)
                .Prop("method", definition.Method)
                .PropRaw("fingerprint", ProjectToolFingerprintJson(definition))
                .EndObject()
                .ToString();
        }

        private static string ProjectToolManifestJson(string requestId, UnityCliProjectToolDefinition definition, string artifactDir, DateTime startedAt, DateTime endedAt)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("requestId", requestId)
                .Prop("command", "project-tool.run")
                .PropObject("tool", tool =>
                {
                    tool.Prop("name", definition.Name)
                        .Prop("version", definition.Version);
                })
                .PropArray("artifact", artifact =>
                {
                    artifact.BeginObject()
                        .Prop("id", "manifest")
                        .Prop("kind", "json")
                        .Prop("relativePath", "manifest.json")
                        .Prop("producer", "unity-editor-package")
                        .EndObject();
                })
                .Prop("createdAt", endedAt.ToString("O", CultureInfo.InvariantCulture))
                .Prop("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture))
                .Prop("root", artifactDir)
                .EndObject()
                .ToString();
        }

        private static string ProjectToolAuditJson(BridgeRequest request, UnityCliProjectToolDefinition definition, string artifactDir, string inputJson, int timeoutMs, DateTime startedAt, DateTime endedAt, string exitState)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("requestId", request.RequestId)
                .Prop("command", "project-tool.run")
                .Prop("projectPath", request.StringArg("projectPath") ?? ProjectRoot())
                .Prop("instanceId", request.StringArg("instanceId"))
                .Prop("unityVersion", Application.unityVersion)
                .Prop("bridgeVersion", "com.unity_cli")
                .Prop("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture))
                .Prop("endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture))
                .Prop("durationMs", (long)(endedAt - startedAt).TotalMilliseconds)
                .PropObject("initiator", initiator =>
                {
                    initiator.Prop("cliUser", "local")
                        .Prop("cwd", Directory.GetCurrentDirectory());
                })
                .PropObject("input", input =>
                {
                    input.Prop("sourceKind", "json")
                        .Prop("sizeBytes", Encoding.UTF8.GetByteCount(inputJson ?? string.Empty));
                })
                .PropObject("execution", execution =>
                {
                    execution.Prop("channel", "live")
                        .Prop("timeoutMs", timeoutMs)
                        .Prop("exitState", exitState);
                })
                .PropObject("effects", effects =>
                {
                    effects.PropRaw("fileWrites", "[]")
                        .Prop("assetRefresh", false)
                        .Prop("domainReload", false)
                        .Prop("playModeChanged", false)
                        .Prop("packagesChanged", false);
                })
                .PropArray("artifacts", artifacts =>
                {
                    artifacts.BeginObject()
                        .Prop("kind", "json")
                        .Prop("path", Path.Combine(artifactDir, "manifest.json"))
                        .EndObject();
                })
                .PropObject("tool", tool =>
                {
                    tool.Prop("name", definition.Name)
                        .Prop("capabilityType", definition.CapabilityType)
                        .PropRaw("fingerprint", ProjectToolFingerprintJson(definition));
                })
                .EndObject()
                .ToString();
        }

        private sealed class ProjectToolFingerprintCheck
        {
            public bool Blocked;
            public bool Warn;
            public string Expected;
            public string Actual;
            public string Message;
        }

        private static ProjectToolFingerprintCheck RefreshProjectToolFingerprint(UnityCliProjectToolDefinition definition, string requestedMode)
        {
            var descriptor = ProjectToolDescriptorFingerprintMaterial(definition);
            definition.DescriptorSha256 = Sha256Hex(descriptor);
            definition.HandlerSha256 = Sha256Hex(ProjectToolHandlerFingerprintMaterial(definition));
            definition.Fingerprint = Sha256Hex("unity-cli-project-tool-fingerprint-v1\nhandler=" + definition.HandlerSha256 + "\ndescriptor=" + definition.DescriptorSha256);
            if (string.IsNullOrEmpty(definition.ExpectedFingerprint))
            {
                var persisted = LoadProjectToolExpectedFingerprint(definition.Name);
                definition.ExpectedFingerprint = string.IsNullOrEmpty(persisted) ? definition.Fingerprint : persisted;
                if (string.IsNullOrEmpty(persisted))
                {
                    SaveProjectToolExpectedFingerprint(definition.Name, definition.Fingerprint);
                }
            }
            definition.FingerprintMode = NormalizeProjectToolFingerprintMode(requestedMode);
            var matches = string.Equals(definition.ExpectedFingerprint, definition.Fingerprint, StringComparison.OrdinalIgnoreCase);
            definition.FingerprintStatus = matches ? "match" : (definition.FingerprintMode == "warn" ? "warn" : "blocked");
            definition.FingerprintMessage = matches
                ? "Custom tool fingerprint matches handler code + descriptor."
                : "Custom tool fingerprint mismatch for " + definition.Name + ": expected " + definition.ExpectedFingerprint + " but computed " + definition.Fingerprint + ".";
            return new ProjectToolFingerprintCheck
            {
                Blocked = !matches && definition.FingerprintMode == "block",
                Warn = !matches && definition.FingerprintMode == "warn",
                Expected = definition.ExpectedFingerprint,
                Actual = definition.Fingerprint,
                Message = definition.FingerprintMessage
            };
        }


        private static string LoadProjectToolExpectedFingerprint(string name)
        {
            try
            {
                var path = ProjectToolExpectedFingerprintPath(name);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (Exception exception)
            {
                WriteLog("custom_tool_fingerprint_baseline_read_failed name=" + name + " error=" + exception.GetType().Name + ": " + exception.Message);
                return null;
            }
        }

        private static void SaveProjectToolExpectedFingerprint(string name, string fingerprint)
        {
            try
            {
                var path = ProjectToolExpectedFingerprintPath(name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, fingerprint ?? string.Empty);
            }
            catch (Exception exception)
            {
                WriteLog("custom_tool_fingerprint_baseline_write_failed name=" + name + " error=" + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static string ProjectToolExpectedFingerprintPath(string name)
        {
            return Path.Combine(ProjectRoot(), "Library", "UnityCliProjectToolFingerprints", SafeProjectToolFingerprintFileName(name) + ".sha256");
        }

        private static string SafeProjectToolFingerprintFileName(string name)
        {
            return Regex.Replace(name ?? "project-tool", "[^A-Za-z0-9_.-]", "_");
        }

        private static string NormalizeProjectToolFingerprintMode(string requestedMode)
        {
            if (string.Equals(requestedMode, "warn", StringComparison.OrdinalIgnoreCase)) return "warn";
            if (string.Equals(requestedMode, "block", StringComparison.OrdinalIgnoreCase)) return "block";
            var environment = Environment.GetEnvironmentVariable("UNITY_CLI_PROJECT_TOOL_FINGERPRINT_MODE");
            if (string.Equals(environment, "warn", StringComparison.OrdinalIgnoreCase)) return "warn";
            if (string.Equals(environment, "block", StringComparison.OrdinalIgnoreCase)) return "block";
            return ProjectToolFingerprintDefaultMode;
        }

        private static string ProjectToolDescriptorFingerprintMaterial(UnityCliProjectToolDefinition definition)
        {
            return "name=" + (definition.Name ?? string.Empty)
                + "\ntitle=" + (definition.Title ?? string.Empty)
                + "\ndescription=" + (definition.Description ?? string.Empty)
                + "\ngroup=" + (definition.Group ?? string.Empty)
                + "\nversion=" + (definition.Version ?? string.Empty)
                + "\ninputSchema=" + NormalizeFingerprintJson(definition.InputSchema)
                + "\noutputSchema=" + NormalizeFingerprintJson(definition.OutputSchema)
                + "\ncapabilityType=" + (definition.CapabilityType ?? string.Empty)
                + "\nreadOnlyHint=" + Bool(definition.ReadOnlyHint)
                + "\ndestructiveHint=" + Bool(definition.DestructiveHint)
                + "\narchitectureSensitive=" + Bool(definition.ArchitectureSensitive)
                + "\nfrozenDecision=" + (definition.FrozenDecision ?? string.Empty)
                + "\ncoverageScope=" + string.Join(",", (definition.CoverageScope ?? new string[0]).OrderBy(value => value, StringComparer.Ordinal).ToArray())
                + "\ndefaultTimeoutMs=" + definition.DefaultTimeoutMs.ToString(CultureInfo.InvariantCulture)
                + "\nmaxTimeoutMs=" + definition.MaxTimeoutMs.ToString(CultureInfo.InvariantCulture)
                + "\nsupportsJob=" + Bool(definition.SupportsJob)
                + "\nartifactKinds=" + string.Join(",", (definition.ArtifactKinds ?? new string[0]).OrderBy(value => value, StringComparer.Ordinal).ToArray())
                + "\nrequiredInput=" + string.Join(",", (definition.RequiredInput ?? new string[0]).OrderBy(value => value, StringComparer.Ordinal).ToArray())
                + "\ndescriptorSource=" + (definition.DescriptorSource ?? string.Empty);
        }

        private static string ProjectToolHandlerFingerprintMaterial(UnityCliProjectToolDefinition definition)
        {
            return "assembly=" + (definition.Assembly ?? string.Empty)
                + "\nmethod=" + (definition.Method ?? string.Empty)
                + "\nhandler=" + ProjectToolHandlerSource(definition);
        }

        private static string ProjectToolHandlerSource(UnityCliProjectToolDefinition definition)
        {
            try
            {
                var typeName = definition.Method;
                if (string.IsNullOrEmpty(typeName)) return string.Empty;
                var dot = typeName.LastIndexOf('.');
                if (dot <= 0) return string.Empty;
                typeName = typeName.Substring(0, dot);
                var fileName = typeName.Substring(typeName.LastIndexOf('.') + 1) + ".cs";
                foreach (var guid in AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(fileName) + " t:MonoScript"))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                    var text = File.ReadAllText(path);
                    if (text.IndexOf(definition.Method.Substring(dot + 1), StringComparison.Ordinal) >= 0)
                    {
                        return NormalizeFingerprintText(text);
                    }
                }
            }
            catch (Exception exception)
            {
                WriteLog("custom_tool_fingerprint_source_failed name=" + definition.Name + " error=" + exception.GetType().Name + ": " + exception.Message);
            }
            return string.Empty;
        }

        private static string NormalizeFingerprintText(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static string NormalizeFingerprintJson(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim(), "\\s+", " ");
        }

        private static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
        }

        private static string ProjectToolFingerprintJson(UnityCliProjectToolDefinition definition)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("algorithm", "sha256-handler-code-plus-descriptor-v1")
                .Prop("value", definition.Fingerprint)
                .Prop("expected", definition.ExpectedFingerprint)
                .Prop("status", definition.FingerprintStatus)
                .Prop("mode", definition.FingerprintMode)
                .Prop("message", definition.FingerprintMessage)
                .Prop("descriptorSha256", definition.DescriptorSha256)
                .Prop("handlerSha256", definition.HandlerSha256)
                .EndObject()
                .ToString();
        }


        private static string ProjectToolFingerprintSummaryJson()
        {
            var items = new List<string>();
            var blocked = 0;
            var warn = 0;
            foreach (var pair in ProjectTools)
            {
                var definition = pair.Value;
                RefreshProjectToolFingerprint(definition, null);
                if (definition.FingerprintStatus == "blocked") blocked++;
                if (definition.FingerprintStatus == "warn") warn++;
                items.Add(new UnityCliJsonWriter().BeginObject()
                    .Prop("name", definition.Name)
                    .PropRaw("fingerprint", ProjectToolFingerprintJson(definition))
                    .Prop("architectureSensitive", true)
                    .EndObject()
                    .ToString());
            }
            items.Sort(StringComparer.Ordinal);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("algorithm", "sha256-handler-code-plus-descriptor-v1")
                .Prop("mode", NormalizeProjectToolFingerprintMode(null))
                .Prop("toolCount", ProjectTools.Count)
                .Prop("blockedCount", blocked)
                .Prop("warnCount", warn)
                .PropRaw("tools", "[" + string.Join(",", items.ToArray()) + "]")
                .EndObject()
                .ToString();
        }

        private static string ProjectToolFingerprintDetailsJson(UnityCliProjectToolDefinition definition)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("tool", definition.Name)
                .PropRaw("fingerprint", ProjectToolFingerprintJson(definition))
                .Prop("architectureSensitive", true)
                .EndObject()
                .ToString();
        }
    }
}
#endif
