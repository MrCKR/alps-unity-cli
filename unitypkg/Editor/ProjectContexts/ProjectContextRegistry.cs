#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    // ───────────────────────────────────────────────────────────────
    // Project Context Registry — discovery, registration, source
    // guard, read-only enforcement, fingerprint, and bridge command
    // merging for context.list / context.describe / context.read.
    // ───────────────────────────────────────────────────────────────

    public static partial class UnityCliBridge
    {
        // ── Constants ──

        private const string ContextFingerprintAlgorithm = "unity-cli-project-context-fingerprint-v1";
        private const string ContextFingerprintDefaultMode = "block";

        // ── Forbidden write-semantic tokens for source guard ──

        private static readonly string[] ContextSourceGuardForbiddenTokens = new[]
        {
            "ExecuteMenuItem",
            "SaveAssets",
            "AssetDatabase.Refresh",
            "isPlaying =",
            "EditorSceneManager.SaveScene",
            "MarkSceneDirty",
            "PackageManager.",
            "File.Write",
            "File.WriteAll",
            "File.Append",
            "Directory.Create",
            "Directory.Delete",
            "Process.Start"
        };

        // ── Forbidden method names for context handler ──

        private static readonly string[] ContextForbiddenMethodNames = new[]
        {
            "Execute",
            "Run"
        };

        // ── Forbidden descriptor fields (write/destructive semantics) ──

        private static readonly string[] ContextForbiddenDescriptorFields = new[]
        {
            "DestructiveHint",
            "SupportsJob",
            "ArtifactKinds"
        };

        // ── Registry storage ──

        private static readonly Dictionary<string, UnityCliContextDefinition> ProjectContexts =
            new Dictionary<string, UnityCliContextDefinition>(StringComparer.Ordinal);

        private static bool projectContextsDiscovered;

        // ── Built-in context descriptors ──
        // (Placeholder: the actual built-in context definitions will be populated
        // by the existing bridge context code. For now, we store them in a parallel
        // dictionary so context.list/describe/read can merge built-in + project.)

        private static readonly Dictionary<string, Dictionary<string, object>> BuiltInContextDescriptors =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);

        private static readonly Dictionary<string, BuiltInContextDefinition> InterfaceBuiltInContexts =
            new Dictionary<string, BuiltInContextDefinition>(StringComparer.Ordinal);

        // ── Registration ──

        /// <summary>
        /// Register a project custom context definition. Enforces:
        /// - name + description + Read delegate required
        /// - Source=project (fixed)
        /// - No destructive/write descriptor fields
        /// - Source guard on handler source
        /// - Name conflict detection against built-in contexts
        /// - Fingerprint computation
        /// </summary>
        public static void RegisterProjectContext(UnityCliContextDefinition definition)
        {
            if (definition == null || string.IsNullOrEmpty(definition.Name) || string.IsNullOrEmpty(definition.Description))
            {
                throw new ArgumentException("Custom context registration requires a non-null definition with name and description.");
            }
            if (definition.Read == null)
            {
                throw new ArgumentException("Custom context registration requires a Read delegate for context: " + definition.Name);
            }

            // ── Fixed descriptor constraints ──
            definition.Source = "project";

            // ── Reject forbidden descriptor fields (write/destructive semantics) ──
            foreach (var field in ContextForbiddenDescriptorFields)
            {
                if (definition.GetType().GetProperty(field) != null)
                {
                    var value = definition.GetType().GetProperty(field).GetValue(definition);
                    if (value != null)
                    {
                        var stringValue = value as string;
                        var boolValue = value as bool?;
                        var arrayValue = value as string[];
                        if (boolValue == true || !string.IsNullOrEmpty(stringValue) || (arrayValue != null && arrayValue.Length > 0))
                        {
                            throw new ArgumentException(
                                "Custom context " + definition.Name + " declares write/destructive semantics via " + field +
                                ". Custom contexts are strictly read-only and must not modify Unity state.",
                                field);
                        }
                    }
                }
            }

            // ── Name conflict detection ──
            if (BuiltInContextDescriptors.ContainsKey(definition.Name))
            {
                throw new ArgumentException(
                    "Custom context name conflict: project context '" + definition.Name +
                    "' conflicts with a built-in context. Use a unique name that does not overlap with built-in contexts.",
                    "Name");
            }

            // ── Defaults ──
            definition.Category = string.IsNullOrEmpty(definition.Category) ? "custom" : definition.Category;
            definition.Version = string.IsNullOrEmpty(definition.Version) ? "1.0.0" : definition.Version;
            definition.InputSchema = string.IsNullOrEmpty(definition.InputSchema) ? "{}" : definition.InputSchema;
            definition.OutputSchema = string.IsNullOrEmpty(definition.OutputSchema) ? "{}" : definition.OutputSchema;
            definition.Target = string.IsNullOrEmpty(definition.Target) ? "editor" : definition.Target;
            definition.AvailabilityScope = string.IsNullOrEmpty(definition.AvailabilityScope)
                ? (definition.Target == "runtime" ? "playModeSession" : "editorSession")
                : definition.AvailabilityScope;
            definition.PlayModeSupport = NormalizeProjectContextPlayModeSupport(definition.PlayModeSupport, definition.Target);
            definition.RefreshBehavior = string.IsNullOrEmpty(definition.RefreshBehavior) ? "onDemand" : definition.RefreshBehavior;
            definition.RequiresPackages = definition.RequiresPackages ?? new string[0];
            definition.RegisteredBy = string.IsNullOrEmpty(definition.RegisteredBy) ? "unity-editor-package" : definition.RegisteredBy;
            definition.DescriptorSource = string.IsNullOrEmpty(definition.DescriptorSource) ? "IUnityCliContext" : definition.DescriptorSource;

            // ── Source guard on handler source ──
            if (!string.IsNullOrEmpty(definition.Method))
            {
                var handlerSource = ProjectContextHandlerSource(definition);
                var guardViolation = ProjectContextSourceGuardCheck(handlerSource, definition.Name);
                if (guardViolation != null)
                {
                    throw new ArgumentException(
                        "Custom context " + definition.Name + " handler source contains forbidden write-semantic token: " +
                        guardViolation +
                        ". Custom contexts must not perform writes, modify state, or invoke destructive operations.",
                        guardViolation);
                }
            }

            // ── Fingerprint ──
            RefreshProjectContextFingerprint(definition, null);

            ProjectContexts[definition.Name] = definition;
            WriteLog("custom_context_registered name=" + definition.Name +
                " source=" + definition.Source +
                " fingerprint=" + definition.Fingerprint);
        }

        // ── Discovery ──

        private static void DiscoverProjectContexts()
        {
            if (projectContextsDiscovered) return;
            projectContextsDiscovered = true;

            // Populate built-in context descriptors from ContextCommands.BuiltInContexts
            PopulateBuiltInContextDescriptors();

            var discoveredTypes = new List<Tuple<Assembly, Type>>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException exception) { types = exception.Types; }
                catch { continue; }

                foreach (var type in types)
                {
                    if (type == null) continue;
                    discoveredTypes.Add(Tuple.Create(assembly, type));
                }
            }

            foreach (var item in discoveredTypes)
            {
                TryRegisterInterfaceContext(item.Item1, item.Item2, true);
            }
            foreach (var item in discoveredTypes)
            {
                TryRegisterInterfaceContext(item.Item1, item.Item2, false);
            }
        }

        // ── 新形态:IUnityCliContext<TReq,TRes> 泛型接口实现类发现与注册 ──
        // 设计依据:.plan/Context形态.md(实现接口即声明只读 Context;Source=built-in 进入内置表,否则进入项目自定义表)。
        private static void TryRegisterInterfaceContext(Assembly assembly, Type type, bool builtInOnly)
        {
            if (type == null || type.IsAbstract || type.IsInterface) return;

            Type contextInterface = null;
            Type reqType = null;
            Type resType = null;
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IUnityCliContext<,>))
                {
                    contextInterface = iface;
                    var args = iface.GetGenericArguments();
                    reqType = args[0];
                    resType = args[1];
                    break;
                }
            }
            if (contextInterface == null) return;

            object instance;
            try
            {
                instance = Activator.CreateInstance(type);
            }
            catch (Exception exception)
            {
                WriteLog("context_registration_skipped type=" + type.FullName + " reason=instantiation_failed err=" + exception.GetType().Name);
                return;
            }

            ContextDescriptor descriptor = null;
            try
            {
                var describeMethod = type.GetMethod("Describe", Type.EmptyTypes);
                if (describeMethod == null)
                {
                    WriteLog("context_registration_skipped type=" + type.FullName + " reason=missing_describe");
                    return;
                }
                descriptor = describeMethod.Invoke(instance, null) as ContextDescriptor;
            }
            catch (Exception exception)
            {
                WriteLog("context_registration_skipped type=" + type.FullName + " reason=describe_failed err=" + exception.GetType().Name);
                return;
            }
            if (descriptor == null || string.IsNullOrEmpty(descriptor.Name) || string.IsNullOrEmpty(descriptor.Description))
            {
                WriteLog("context_registration_skipped type=" + type.FullName + " reason=descriptor_incomplete");
                return;
            }

            var isBuiltIn = string.Equals(descriptor.Source, "built-in", StringComparison.OrdinalIgnoreCase);
            if (builtInOnly != isBuiltIn) return;

            var inputSchema = UnityCliSchemaDeriver.DeriveSchema(reqType);
            var outputSchema = UnityCliSchemaDeriver.DeriveSchema(resType);
            var readMethod = contextInterface.GetMethod("Read");

            if (isBuiltIn)
            {
                RegisterInterfaceBuiltInContext(new BuiltInContextDefinition
                {
                    Name = descriptor.Name,
                    Summary = string.IsNullOrEmpty(descriptor.Summary) ? descriptor.Description : descriptor.Summary,
                    Description = descriptor.Description,
                    Category = string.IsNullOrEmpty(descriptor.Category) ? "built-in" : descriptor.Category,
                    Source = "built-in",
                    RefreshBehavior = string.IsNullOrEmpty(descriptor.RefreshBehavior) ? "on-demand" : descriptor.RefreshBehavior,
                    Target = string.IsNullOrEmpty(descriptor.Target) ? "editor" : descriptor.Target,
                    AvailabilityScope = string.IsNullOrEmpty(descriptor.AvailabilityScope) ? "editorSession" : descriptor.AvailabilityScope,
                    Handler = "bridge",
                    InputSchema = string.IsNullOrEmpty(inputSchema) ? "{}" : inputSchema,
                    OutputSchema = string.IsNullOrEmpty(outputSchema) ? "{}" : outputSchema,
                    RequiresPackages = descriptor.RequiresPackages,
                    ReadHandler = delegate(BridgeRequest request)
                    {
                        var inputJson = request.RawJsonArg("input") ?? "{}";
                        var context = new UnityCliProjectToolContext
                        {
                            RequestId = request.RequestId,
                            ProjectPath = request.StringArg("projectPath") ?? ProjectRoot(),
                            InputJson = inputJson,
                            LogPath = LogPath()
                        };
                        // 少数内置 Context 需要复用 Bridge 控制流(例如 deferred marker);项目自定义 Context 不走此旁路。
                        var rawBridgeContext = instance as IUnityCliBuiltInRawBridgeContext;
                        if (rawBridgeContext != null)
                        {
                            return rawBridgeContext.ReadRaw(request, context);
                        }
                        var typedRequest = UnityCliSerializer.Deserialize(inputJson, reqType);
                        var result = readMethod.Invoke(instance, new object[] { typedRequest, context });
                        return SuccessJson(result == null ? "null" : UnityCliSerializer.Serialize(result));
                    }
                });
                WriteLog("built_in_context_registered name=" + descriptor.Name + " source=IUnityCliContext type=" + type.FullName);
                return;
            }

            try
            {
                RegisterProjectContext(new UnityCliContextDefinition
                {
                    Name = descriptor.Name,
                    Summary = string.IsNullOrEmpty(descriptor.Summary) ? descriptor.Description : descriptor.Summary,
                    Description = descriptor.Description,
                    Category = string.IsNullOrEmpty(descriptor.Category) ? "custom" : descriptor.Category,
                    Version = string.IsNullOrEmpty(descriptor.Version) ? "1.0.0" : descriptor.Version,
                    InputSchema = string.IsNullOrEmpty(inputSchema) ? "{}" : inputSchema,
                    OutputSchema = string.IsNullOrEmpty(outputSchema) ? "{}" : outputSchema,
                    Target = string.IsNullOrEmpty(descriptor.Target) ? "editor" : descriptor.Target,
                    AvailabilityScope = descriptor.AvailabilityScope,
                    PlayModeSupport = descriptor.PlayModeSupport,
                    RefreshBehavior = descriptor.RefreshBehavior,
                    RequiresPackages = descriptor.RequiresPackages ?? new string[0],
                    RegisteredBy = "unity-editor-package",
                    DescriptorSource = "IUnityCliContext",
                    Assembly = assembly.GetName().Name,
                    Method = type.FullName + ".Read",
                    Fingerprint = descriptor.Fingerprint,
                    PinnedFingerprint = descriptor.Fingerprint,
                    ExpectedFingerprint = descriptor.Fingerprint,
                    Read = delegate(UnityCliProjectToolContext context)
                    {
                        var typedRequest = UnityCliSerializer.Deserialize(context.InputJson, reqType);
                        var result = readMethod.Invoke(instance, new object[] { typedRequest, context });
                        return result == null ? "null" : UnityCliSerializer.Serialize(result);
                    }
                });
                WriteLog("custom_context_registered name=" + descriptor.Name + " source=IUnityCliContext type=" + type.FullName);
            }
            catch (ArgumentException exception)
            {
                WriteLog("custom_context_registration_failed name=" + descriptor.Name +
                    " reason=" + exception.Message);
            }
        }

        private static void RegisterInterfaceBuiltInContext(BuiltInContextDefinition definition)
        {
            if (definition == null || string.IsNullOrEmpty(definition.Name) || string.IsNullOrEmpty(definition.Description) || definition.ReadHandler == null)
            {
                throw new ArgumentException("Built-in context registration requires name, description, and ReadHandler.");
            }
            definition.Source = "built-in";
            definition.InputSchema = string.IsNullOrEmpty(definition.InputSchema) ? "{}" : definition.InputSchema;
            definition.OutputSchema = string.IsNullOrEmpty(definition.OutputSchema) ? "{}" : definition.OutputSchema;
            definition.Target = string.IsNullOrEmpty(definition.Target) ? "editor" : definition.Target;
            definition.AvailabilityScope = string.IsNullOrEmpty(definition.AvailabilityScope) ? "editorSession" : definition.AvailabilityScope;
            definition.Handler = string.IsNullOrEmpty(definition.Handler) ? "bridge" : definition.Handler;
            definition.RequiresPackages = definition.RequiresPackages ?? new string[0];
            InterfaceBuiltInContexts[definition.Name] = definition;
            AddBuiltInContextDescriptor(definition);
        }

        // ── Source Guard ──

        /// <summary>
        /// Checks handler source code for forbidden write-semantic tokens.
        /// Returns the first matched forbidden token, or null if the source passes.
        /// </summary>
        private static string ProjectContextSourceGuardCheck(string handlerSource, string contextName)
        {
            if (string.IsNullOrEmpty(handlerSource)) return null;
            foreach (var token in ContextSourceGuardForbiddenTokens)
            {
                if (handlerSource.IndexOf(token, StringComparison.Ordinal) >= 0)
                {
                    return token;
                }
            }
            return null;
        }

        private static string ProjectContextHandlerSource(UnityCliContextDefinition definition)
        {
            try
            {
                var typeName = definition.Method;
                if (string.IsNullOrEmpty(typeName)) return string.Empty;
                var dot = typeName.LastIndexOf('.');
                if (dot <= 0) return string.Empty;
                typeName = typeName.Substring(0, dot);
                var fileName = typeName.Substring(typeName.LastIndexOf('.') + 1) + ".cs";
                foreach (var guid in AssetDatabase.FindAssets(
                    Path.GetFileNameWithoutExtension(fileName) + " t:MonoScript"))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.Equals(Path.GetFileName(path), fileName,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    var text = File.ReadAllText(path);
                    if (text.IndexOf(definition.Method.Substring(dot + 1),
                        StringComparison.Ordinal) >= 0)
                    {
                        return NormalizeFingerprintText(text);
                    }
                }
            }
            catch (Exception exception)
            {
                WriteLog("custom_context_fingerprint_source_failed name=" + definition.Name +
                    " error=" + exception.GetType().Name + ": " + exception.Message);
            }
            return string.Empty;
        }

        // ── Fingerprint ──

        private sealed class ProjectContextFingerprintCheck
        {
            public bool Blocked;
            public bool Warn;
            public string Expected;
            public string Actual;
            public string Message;
        }

        private static ProjectContextFingerprintCheck RefreshProjectContextFingerprint(
            UnityCliContextDefinition definition, string requestedMode)
        {
            var descriptor = ProjectContextDescriptorFingerprintMaterial(definition);
            definition.DescriptorSha256 = Sha256Hex(descriptor);
            definition.HandlerSha256 = Sha256Hex(ProjectContextHandlerFingerprintMaterial(definition));
            definition.Fingerprint = Sha256Hex(
                ContextFingerprintAlgorithm +
                "\nhandler=" + definition.HandlerSha256 +
                "\ndescriptor=" + definition.DescriptorSha256);

            var isPinned = !string.IsNullOrEmpty(definition.PinnedFingerprint);
            if (isPinned)
            {
                definition.ExpectedFingerprint = definition.PinnedFingerprint;
            }
            else
            {
                definition.ExpectedFingerprint = definition.Fingerprint;
                SaveProjectContextExpectedFingerprint(definition.Name, definition.Fingerprint);
            }

            definition.FingerprintMode = NormalizeProjectContextFingerprintMode(requestedMode);
            var matches = string.Equals(definition.ExpectedFingerprint, definition.Fingerprint,
                StringComparison.OrdinalIgnoreCase);
            definition.FingerprintStatus = matches ? "match"
                : (definition.FingerprintMode == "warn" ? "warn" : "blocked");
            definition.FingerprintMessage = matches
                ? "Custom context fingerprint matches handler code + descriptor."
                : "Custom context fingerprint mismatch for " + definition.Name +
                  ": expected " + definition.ExpectedFingerprint +
                  " but computed " + definition.Fingerprint + ".";

            return new ProjectContextFingerprintCheck
            {
                Blocked = !matches && definition.FingerprintMode == "block",
                Warn = !matches && definition.FingerprintMode == "warn",
                Expected = definition.ExpectedFingerprint,
                Actual = definition.Fingerprint,
                Message = definition.FingerprintMessage
            };
        }

        private static string NormalizeProjectContextFingerprintMode(string requestedMode)
        {
            if (string.Equals(requestedMode, "warn", StringComparison.OrdinalIgnoreCase)) return "warn";
            if (string.Equals(requestedMode, "block", StringComparison.OrdinalIgnoreCase)) return "block";
            var environment = Environment.GetEnvironmentVariable("UNITY_CLI_PROJECT_CONTEXT_FINGERPRINT_MODE");
            if (string.Equals(environment, "warn", StringComparison.OrdinalIgnoreCase)) return "warn";
            if (string.Equals(environment, "block", StringComparison.OrdinalIgnoreCase)) return "block";
            return ContextFingerprintDefaultMode;
        }

        private static string ProjectContextDescriptorFingerprintMaterial(UnityCliContextDefinition definition)
        {
            return "name=" + (definition.Name ?? string.Empty)
                + "\nsummary=" + (definition.Summary ?? definition.Description ?? string.Empty)
                + "\ndescription=" + (definition.Description ?? string.Empty)
                + "\ncategory=" + (definition.Category ?? string.Empty)
                + "\nversion=" + (definition.Version ?? string.Empty)
                + "\ninputSchema=" + NormalizeFingerprintJson(definition.InputSchema)
                + "\noutputSchema=" + NormalizeFingerprintJson(definition.OutputSchema)
                + "\nsource=" + (definition.Source ?? string.Empty)
                + "\ntarget=" + (definition.Target ?? string.Empty)
                + "\navailabilityScope=" + (definition.AvailabilityScope ?? string.Empty)
                + "\nplayModeSupport=" + (definition.PlayModeSupport ?? string.Empty)
                + "\nrefreshBehavior=" + (definition.RefreshBehavior ?? string.Empty)
                + "\nrequiresPackages=" + string.Join(",", (definition.RequiresPackages ?? new string[0])
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray())
                + "\ndescriptorSource=" + (definition.DescriptorSource ?? string.Empty);
        }

        private static string NormalizeProjectContextPlayModeSupport(string value, string target)
        {
            if (string.Equals(value, "supported", StringComparison.OrdinalIgnoreCase)) return "supported";
            if (string.Equals(value, "runtime-only", StringComparison.OrdinalIgnoreCase)) return "runtime-only";
            if (string.Equals(value, "session-affecting", StringComparison.OrdinalIgnoreCase)) return "session-affecting";
            if (string.Equals(value, "editor-only", StringComparison.OrdinalIgnoreCase)) return "editor-only";
            if (string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase)) return "unknown";
            return string.Equals(target, "runtime", StringComparison.OrdinalIgnoreCase) ? "runtime-only" : "supported";
        }

        private static string ProjectContextHandlerFingerprintMaterial(UnityCliContextDefinition definition)
        {
            return "assembly=" + (definition.Assembly ?? string.Empty)
                + "\nmethod=" + (definition.Method ?? string.Empty)
                + "\nhandler=" + ProjectContextHandlerSource(definition);
        }

        private static string LoadProjectContextExpectedFingerprint(string name)
        {
            try
            {
                var path = ProjectContextExpectedFingerprintPath(name);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (Exception exception)
            {
                WriteLog("custom_context_fingerprint_baseline_read_failed name=" + name +
                    " error=" + exception.GetType().Name + ": " + exception.Message);
                return null;
            }
        }

        private static void SaveProjectContextExpectedFingerprint(string name, string fingerprint)
        {
            try
            {
                var path = ProjectContextExpectedFingerprintPath(name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, fingerprint ?? string.Empty);
            }
            catch (Exception exception)
            {
                WriteLog("custom_context_fingerprint_baseline_write_failed name=" + name +
                    " error=" + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static string ProjectContextExpectedFingerprintPath(string name)
        {
            return Path.Combine(ProjectRoot(),
                "Library", "UnityCliProjectContextFingerprints",
                SafeProjectContextFingerprintFileName(name) + ".sha256");
        }

        private static string SafeProjectContextFingerprintFileName(string name)
        {
            return Regex.Replace(name ?? "project-context", "[^A-Za-z0-9_.-]", "_");
        }

        private static string ProjectContextFingerprintJson(UnityCliContextDefinition definition)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("algorithm", ContextFingerprintAlgorithm)
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

        // ── Populate built-in context descriptors from ContextCommands ──

        private static void PopulateBuiltInContextDescriptors()
        {
            foreach (var ctx in BuiltInContexts)
            {
                AddBuiltInContextDescriptor(ctx);
            }
        }

        private static void AddBuiltInContextDescriptor(BuiltInContextDefinition ctx)
        {
            var descriptor = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "name", ctx.Name },
                { "summary", string.IsNullOrEmpty(ctx.Summary) ? ctx.Description : ctx.Summary },
                { "description", ctx.Description },
                { "source", ctx.Source ?? "built-in" },
                { "category", ctx.Category ?? "" },
                { "target", ctx.Target ?? "editor" },
                { "availabilityScope", ctx.AvailabilityScope ?? "editorSession" },
                { "playModeSupport", "supported" },
                { "supportStatus", string.Equals(ctx.Handler, "offline", StringComparison.Ordinal) ? "cli-only" : "live-bridge" },
                { "cliCommand", "context read " + ctx.Name },
                { "inputSchema", string.IsNullOrEmpty(ctx.InputSchema) ? "{}" : ctx.InputSchema },
                { "outputSchema", string.IsNullOrEmpty(ctx.OutputSchema) ? "{}" : ctx.OutputSchema },
                { "refreshBehavior", string.IsNullOrEmpty(ctx.RefreshBehavior) ? "on-demand" : ctx.RefreshBehavior },
            };
            if (ctx.RequiresPackages != null && ctx.RequiresPackages.Length > 0)
            {
                descriptor["requiresPackages"] = ctx.RequiresPackages;
            }
            BuiltInContextDescriptors[ctx.Name] = descriptor;
        }

        // ── Bridge commands: context.list, context.describe, context.read ──

        private static void RegisterContextCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "context.list", "context", false, ContextList);
            RegisterCommand(registry, "context.describe", "context", false, ContextDescribe);
            RegisterCommand(registry, "context.read", "context", false, ContextRead);
        }

        /// <summary>
        /// context.list — returns all context descriptors (built-in + project custom).
        /// Built-in contexts first, project custom contexts second.
        /// Play Mode: allowed as read-only query.
        /// </summary>
        private static string ContextList(BridgeRequest request)
        {
            DiscoverProjectContexts();
            var items = new List<string>();

            // Built-in contexts first
            foreach (var pair in BuiltInContextDescriptors.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                items.Add(BuiltInContextDescriptorJson(pair.Key, pair.Value));
            }

            // Project custom contexts second
            foreach (var pair in ProjectContexts.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                items.Add(ProjectContextDefinitionJson(pair.Value));
            }

            var data = new UnityCliJsonWriter().BeginObject()
                .PropArray("contexts", arr => { foreach (var item in items) { arr.RawValue(item); } })
                .Prop("totalCount", items.Count)
                .Prop("builtInCount", BuiltInContextDescriptors.Count)
                .Prop("projectCustomCount", ProjectContexts.Count)
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
            return SuccessJson(data);
        }

        /// <summary>
        /// context.describe — returns full descriptor/schema/fingerprint for a named context.
        /// Lookup order: built-in first, project custom second.
        /// Name conflict: project custom with same name as built-in is rejected at registration,
        /// so this lookup is always unambiguous.
        /// </summary>
        private static string ContextDescribe(BridgeRequest request)
        {
            DiscoverProjectContexts();
            var name = request.StringArg("name");
            if (string.IsNullOrEmpty(name))
            {
                return ErrorJson("E_CONTEXT_NAME_REQUIRED",
                    "context.describe requires a context name.",
                    "Pass a valid context name from context.list output.");
            }

            // Built-in first — return full descriptor from ContextCommands
            var builtInCtx = FindBuiltInContext(name);
            if (builtInCtx != null)
            {
                return SuccessJson(ContextDescriptorJson(builtInCtx));
            }

            // Project custom second
            if (ProjectContexts.ContainsKey(name))
            {
                var definition = ProjectContexts[name];
                RefreshProjectContextFingerprint(definition, request.StringArg("fingerprintMode"));
                return SuccessJson(ProjectContextDefinitionJson(definition));
            }

            return ErrorJson("E_CONTEXT_NOT_FOUND",
                "Context not found: " + name,
                "Run context.list to see available contexts.");
        }

        /// <summary>
        /// context.read — executes the read handler for a named context and returns
        /// a unified response envelope.
        /// Lookup order: built-in first, project custom second.
        /// For project custom contexts, runs fingerprint check and source-guarded handler.
        /// Play Mode: allowed as read-only query.
        /// </summary>
        private static string ContextRead(BridgeRequest request)
        {
            DiscoverProjectContexts();
            var name = request.StringArg("name");
            if (string.IsNullOrEmpty(name))
            {
                return ErrorJson("E_CONTEXT_NAME_REQUIRED",
                    "context.read requires a context name.",
                    "Pass a valid context name from context.list output.");
            }

            // Built-in first — delegate to ContextCommands.BuiltInContexts read handlers
            var builtInCtx = FindBuiltInContext(name);
            if (builtInCtx != null)
            {
                try
                {
                    return builtInCtx.ReadHandler(request);
                }
                catch (Exception exception)
                {
                    return ErrorJson("E_CONTEXT_READ_FAILED",
                        "Built-in context read failed for " + name + ": " + exception.Message,
                        "Check Unity Console and UnityCliBridge.log.");
                }
            }

            // Project custom second
            if (!ProjectContexts.ContainsKey(name))
            {
                return ErrorJson("E_CONTEXT_NOT_FOUND",
                    "Context not found: " + name,
                    "Run context.list to see available contexts.");
            }

            var definition = ProjectContexts[name];

            // ── Fingerprint check ──
            var fingerprintCheck = RefreshProjectContextFingerprint(
                definition, request.StringArg("fingerprintMode"));
            if (fingerprintCheck.Blocked)
            {
                WriteLog("custom_context_fingerprint_blocked requestId=" + request.RequestId +
                    " name=" + name +
                    " expected=" + fingerprintCheck.Expected +
                    " actual=" + fingerprintCheck.Actual);
                return ErrorJsonWithDetails("E_CONTEXT_FINGERPRINT_MISMATCH",
                    fingerprintCheck.Message,
                    ProjectContextFingerprintDetailsJson(definition),
                    "Review the registered custom context descriptor/handler code, " +
                    "then update the expected fingerprint or rerun with --fingerprint-mode warn.");
            }
            if (fingerprintCheck.Warn)
            {
                WriteLog("custom_context_fingerprint_warn requestId=" + request.RequestId +
                    " name=" + name +
                    " expected=" + fingerprintCheck.Expected +
                    " actual=" + fingerprintCheck.Actual);
            }

            // ── Input basic validation ──
            var inputJson = request.RawJsonArg("input") ?? "{}";
            if (!string.IsNullOrEmpty(inputJson))
            {
                var trimmed = inputJson.Trim();
                if (!trimmed.StartsWith("{", StringComparison.Ordinal) ||
                    !trimmed.EndsWith("}", StringComparison.Ordinal))
                {
                    return ErrorJson("E_CONTEXT_INPUT_INVALID",
                        "Context read input must be a JSON object.",
                        "Pass input as a valid JSON object, e.g. {} or {\"key\":\"value\"}.");
                }
            }

            // ── Execute handler ──
            string resultJson;
            try
            {
                WriteLog("custom_context_read requestId=" + request.RequestId + " name=" + name);
                resultJson = definition.Read(new UnityCliProjectToolContext
                {
                    RequestId = request.RequestId,
                    ProjectPath = request.StringArg("projectPath") ?? ProjectRoot(),
                    InputJson = inputJson,
                    LogPath = LogPath()
                });
            }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException ?? exception;
                return ErrorJson("E_CONTEXT_HANDLER_FAILED",
                    inner.GetType().Name + ": " + inner.Message,
                    "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }
            catch (Exception exception)
            {
                return ErrorJson("E_CONTEXT_HANDLER_FAILED",
                    exception.GetType().Name + ": " + exception.Message,
                    "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }

            // ── Unified response envelope ──
            var startedAt = DateTime.UtcNow;
            var data = new UnityCliJsonWriter().BeginObject()
                .Prop("command", "context.read")
                .PropObject("context", ctx =>
                {
                    ctx.Prop("name", definition.Name)
                        .Prop("version", definition.Version)
                        .Prop("source", definition.Source);
                })
                .PropObject("execution", execution =>
                {
                    execution.Prop("channel", "live")
                        .Prop("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture))
                        .Prop("exitState", "completed");
                })
                .PropRaw("result", resultJson)
                .PropObject("logs", logs => logs.Prop("bridge", LogPath()))
                .EndObject()
                .ToString();
            return SuccessJson(data);
        }

        // ── JSON serialization helpers ──

        private static string ProjectContextDefinitionJson(UnityCliContextDefinition definition)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("name", definition.Name)
                .Prop("summary", string.IsNullOrEmpty(definition.Summary) ? definition.Description : definition.Summary)
                .Prop("description", definition.Description)
                .Prop("category", definition.Category)
                .Prop("version", definition.Version)
                .PropRaw("inputSchema", definition.InputSchema)
                .PropRaw("outputSchema", definition.OutputSchema)
                .Prop("source", definition.Source)
                .Prop("target", definition.Target)
                .Prop("availabilityScope", definition.AvailabilityScope)
                .Prop("playModeSupport", definition.PlayModeSupport)
                .Prop("refreshBehavior", definition.RefreshBehavior)
                .PropRaw("requiresPackages", StringArrayJson(definition.RequiresPackages))
                .Prop("supportStatus", "live-bridge")
                .Prop("cliCommand", "context read " + definition.Name)
                .Prop("descriptorSource", definition.DescriptorSource)
                .Prop("registeredBy", definition.RegisteredBy)
                .Prop("assembly", definition.Assembly)
                .Prop("method", definition.Method)
                .PropRaw("fingerprint", ProjectContextFingerprintJson(definition))
                .EndObject()
                .ToString();
        }

        private static string BuiltInContextDescriptorJson(string name, Dictionary<string, object> descriptor)
        {
            var w = new UnityCliJsonWriter().BeginObject()
                .Prop("name", name)
                .Prop("source", "built-in");
            if (descriptor != null)
            {
                foreach (var pair in descriptor)
                {
                    if (pair.Value is string s)
                    {
                        if (string.Equals(pair.Key, "inputSchema", StringComparison.Ordinal) || string.Equals(pair.Key, "outputSchema", StringComparison.Ordinal)) w.PropRaw(pair.Key, string.IsNullOrEmpty(s) ? "{}" : s);
                        else w.Prop(pair.Key, s);
                    }
                    else if (pair.Value is bool b) w.Prop(pair.Key, b);
                    else if (pair.Value is int i) w.Prop(pair.Key, i);
                    else if (pair.Value is string[] array) w.PropArray(pair.Key, arr => { foreach (var item in array) arr.Value(item); });
                }
            }
            w.EndObject();
            return w.ToString();
        }

        private static string ProjectContextFingerprintDetailsJson(UnityCliContextDefinition definition)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("context", definition.Name)
                .PropRaw("fingerprint", ProjectContextFingerprintJson(definition))
                .EndObject()
                .ToString();
        }

        private static string ProjectContextFingerprintSummaryJson()
        {
            var items = new List<string>();
            var blocked = 0;
            var warn = 0;
            foreach (var pair in ProjectContexts)
            {
                var definition = pair.Value;
                RefreshProjectContextFingerprint(definition, null);
                if (definition.FingerprintStatus == "blocked") blocked++;
                if (definition.FingerprintStatus == "warn") warn++;
                items.Add(new UnityCliJsonWriter().BeginObject()
                    .Prop("name", definition.Name)
                    .PropRaw("fingerprint", ProjectContextFingerprintJson(definition))
                    .EndObject()
                    .ToString());
            }
            items.Sort(StringComparer.Ordinal);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("algorithm", ContextFingerprintAlgorithm)
                .Prop("mode", NormalizeProjectContextFingerprintMode(null))
                .Prop("contextCount", ProjectContexts.Count)
                .Prop("blockedCount", blocked)
                .Prop("warnCount", warn)
                .PropArray("contexts", arr => { foreach (var item in items) { arr.RawValue(item); } })
                .EndObject()
                .ToString();
        }
    }
}
#endif
