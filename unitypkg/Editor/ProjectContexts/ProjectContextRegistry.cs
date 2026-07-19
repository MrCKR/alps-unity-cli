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
using System.Threading;
using System.Threading.Tasks;
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

        private static readonly Dictionary<string, RegisteredContext> Contexts =
            new Dictionary<string, RegisteredContext>(StringComparer.Ordinal);
        private static Dictionary<string, RegisteredContext> discoveryContexts;
        private static Dictionary<string, RegisteredContext> ProjectContexts { get { return Contexts; } }
        private static bool projectContextsDiscovered;

        private static void RegisterContext(RegisteredContext definition)
        {
            if (definition == null || string.IsNullOrEmpty(definition.Name) || string.IsNullOrEmpty(definition.Description))
                throw new ArgumentException("Context registration requires name and description.");
            if (definition.ReadAsync == null || definition.RequestType == null || definition.ResultType == null)
                throw new ArgumentException("Context registration requires request/result types and a typed async invoker: " + definition.Name);
            var target = discoveryContexts ?? Contexts;
            if (target.ContainsKey(definition.Name)) throw new InvalidOperationException("Duplicate context registration: " + definition.Name);

            definition.Category = string.IsNullOrEmpty(definition.Category) ? "custom" : definition.Category;
            definition.Version = string.IsNullOrEmpty(definition.Version) ? "1.0.0" : definition.Version;
            if (string.IsNullOrWhiteSpace(definition.InputSchema) || string.IsNullOrWhiteSpace(definition.OutputSchema))
                throw new ArgumentException("Context registration requires schemas derived from request/result types: " + definition.Name);
            definition.Target = string.IsNullOrEmpty(definition.Target) ? "editor" : definition.Target;
            definition.AvailabilityScope = string.IsNullOrEmpty(definition.AvailabilityScope) ? (definition.Target == "runtime" ? "playModeSession" : "editorSession") : definition.AvailabilityScope;
            definition.PlayModeSupport = NormalizeProjectContextPlayModeSupport(definition.PlayModeSupport, definition.Target);
            definition.RefreshBehavior = string.IsNullOrEmpty(definition.RefreshBehavior) ? "onDemand" : definition.RefreshBehavior;
            definition.RequiresPackages = definition.RequiresPackages ?? Array.Empty<string>();
            definition.RegisteredBy = string.IsNullOrEmpty(definition.RegisteredBy) ? "unity-editor-package" : definition.RegisteredBy;
            definition.DescriptorSource = string.IsNullOrEmpty(definition.DescriptorSource) ? "IUnityCliContext" : definition.DescriptorSource;
            ComputeProjectContextFingerprint(definition);

            if (definition.Source == "project" && !string.IsNullOrEmpty(definition.Method))
            {
                var violation = ProjectContextSourceGuardCheck(ProjectContextHandlerSource(definition), definition.Name);
                if (violation != null) throw new ArgumentException("Custom context " + definition.Name + " contains forbidden write-semantic token: " + violation, violation);
                RefreshProjectContextFingerprint(definition, null);
            }
            target.Add(definition.Name, definition);
            WriteLog("context_registered name=" + definition.Name + " source=" + definition.Source);
        }

        private static void DiscoverProjectContexts() { DiscoverContexts(); }

        private static void DiscoverContexts()
        {
            if (projectContextsDiscovered) return;
            var pending = new Dictionary<string, RegisteredContext>(StringComparer.Ordinal);
            try
            {
                discoveryContexts = pending;
                if (registryOwnership == null) registryOwnership = RegistryOwnershipResolver.Load();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException exception) { types = exception.Types; }
                    catch (Exception exception) { throw new InvalidOperationException("Unable to inspect assembly " + assembly.FullName + ".", exception); }
                    foreach (var type in types) if (type != null) TryRegisterInterfaceContext(assembly, type);
                }
                Contexts.Clear();
                foreach (var item in pending) Contexts.Add(item.Key, item.Value);
                projectContextsDiscovered = true;
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_REGISTRY_UNHEALTHY", "Context registry discovery failed: " + exception.Message,
                    new Dictionary<string, object> { { "registry", "contexts" } },
                    new[] { "Fix the extension manifest or context implementation, then retry discovery.", "Run unity_cli extensions doctor --project <path> to inspect extension installation state." }, true, 1000);
            }
            finally
            {
                discoveryContexts = null;
            }
        }

        private static void TryRegisterInterfaceContext(Assembly assembly, Type type)
        {
            if (type == null || type.IsAbstract || type.IsInterface) return;
            var contextInterface = type.GetInterfaces().FirstOrDefault(iface => iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IUnityCliContext<,>));
            if (contextInterface == null) return;
            var args = contextInterface.GetGenericArguments();
            var instance = Activator.CreateInstance(type);
            var descriptor = type.GetMethod("Describe", Type.EmptyTypes).Invoke(instance, null) as ContextDescriptor;
            if (descriptor == null || string.IsNullOrEmpty(descriptor.Name) || string.IsNullOrEmpty(descriptor.Description))
                throw new InvalidOperationException("Context descriptor is incomplete: " + type.FullName);
            var factory = typeof(UnityCliBridge).GetMethod("RegisterTypedContext", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(args[0], args[1]);
            factory.Invoke(null, new[] { instance, descriptor, assembly, type });
        }

        private static void RegisterTypedContext<TReq, TRes>(object instance, ContextDescriptor descriptor, Assembly assembly, Type implementationType)
        {
            var context = (IUnityCliContext<TReq, TRes>)instance;
            var source = registryOwnership.Resolve(implementationType);
            RegisterContext(new RegisteredContext
            {
                Name = descriptor.Name,
                Summary = string.IsNullOrEmpty(descriptor.Summary) ? descriptor.Description : descriptor.Summary,
                Description = descriptor.Description,
                Category = string.IsNullOrEmpty(descriptor.Category) ? (source == "built-in" ? "built-in" : "custom") : descriptor.Category,
                Version = string.IsNullOrEmpty(descriptor.Version) ? "1.0.0" : descriptor.Version,
                InputSchema = UnityCliSchemaDeriver.DeriveSchema(typeof(TReq)),
                OutputSchema = UnityCliSchemaDeriver.DeriveSchema(typeof(TRes)),
                Source = source,
                Target = string.IsNullOrEmpty(descriptor.Target) ? "editor" : descriptor.Target,
                AvailabilityScope = descriptor.AvailabilityScope,
                PlayModeSupport = descriptor.PlayModeSupport,
                RefreshBehavior = descriptor.RefreshBehavior,
                Examples = descriptor.Examples ?? Array.Empty<ContextExample>(),
                RequiresPackages = descriptor.RequiresPackages ?? Array.Empty<string>(),
                RegisteredBy = source == "extension" ? "extension-manifest" : "unity-editor-package",
                DescriptorSource = "IUnityCliContext",
                Assembly = assembly.GetName().Name,
                Method = implementationType.FullName + ".ReadAsync",
                Fingerprint = descriptor.Fingerprint,
                PinnedFingerprint = descriptor.Fingerprint,
                ExpectedFingerprint = descriptor.Fingerprint,
                Descriptor = descriptor,
                RequestType = typeof(TReq),
                ResultType = typeof(TRes),
                ReadAsync = async (request, executionContext, cancellationToken) => (object)await context.ReadAsync((TReq)request, executionContext, cancellationToken)
            });
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

        private static string ProjectContextHandlerSource(RegisteredContext definition)
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

        /// <summary>计算当前 handler+descriptor fingerprint，不读写 project pin state。</summary>
        private static void ComputeProjectContextFingerprint(RegisteredContext definition)
        {
            var descriptor = ProjectContextDescriptorFingerprintMaterial(definition);
            definition.DescriptorSha256 = Sha256Hex(descriptor);
            definition.HandlerSha256 = Sha256Hex(ProjectContextHandlerFingerprintMaterial(definition));
            definition.Fingerprint = Sha256Hex(
                ContextFingerprintAlgorithm +
                "\nhandler=" + definition.HandlerSha256 +
                "\ndescriptor=" + definition.DescriptorSha256);
        }

        private static ProjectContextFingerprintCheck RefreshProjectContextFingerprint(
            RegisteredContext definition, string requestedMode)
        {
            ComputeProjectContextFingerprint(definition);
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

        private static string ProjectContextDescriptorFingerprintMaterial(RegisteredContext definition)
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

        private static string ProjectContextHandlerFingerprintMaterial(RegisteredContext definition)
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

        private static Dictionary<string, object> ProjectContextFingerprintData(RegisteredContext definition)
        {
            return new Dictionary<string, object> { { "algorithm", ContextFingerprintAlgorithm }, { "value", definition.Fingerprint }, { "expected", definition.ExpectedFingerprint }, { "status", definition.FingerprintStatus }, { "mode", definition.FingerprintMode }, { "message", definition.FingerprintMessage }, { "descriptorSha256", definition.DescriptorSha256 }, { "handlerSha256", definition.HandlerSha256 } };
        }

        // ── Bridge commands: context.list, context.describe, context.read ──

        private static void RegisterContextCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "context.list", "context", false, ContextList);
            RegisterCommand(registry, "context.describe", "context", false, ContextDescribe);
            registry.Register<UnityCli.Protocol.BridgeRequest>("context.read", "context", false, 30000, ContextRead);
        }

        /// <summary>
        /// context.list — returns all context descriptors (built-in + project custom).
        /// Built-in contexts first, project custom contexts second.
        /// Play Mode: allowed as read-only query.
        /// </summary>
        private static object ContextList(UnityCli.Protocol.BridgeRequest request)
        {
            DiscoverContexts();
            var items = Contexts.Values
                .OrderBy(context => context.Source, StringComparer.Ordinal)
                .ThenBy(context => context.Name, StringComparer.Ordinal)
                .Select(ContextDescriptorData)
                .ToList();
            return new Dictionary<string, object>
            {
                { "contexts", items },
                { "totalCount", items.Count },
                { "builtInCount", Contexts.Values.Count(context => context.Source == "built-in") },
                { "extensionCount", Contexts.Values.Count(context => context.Source == "extension") },
                { "projectCustomCount", Contexts.Values.Count(context => context.Source == "project") },
                { "logPath", LogPath() }
            };
        }

        private sealed class ContextRouteArgs
        {
            public string Name { get; set; }
            [UnityCliAnyJson] public object Input { get; set; }
            public string FingerprintMode { get; set; }
            public string ExpectedDescriptorFingerprint { get; set; }
            public string ProjectPath { get; set; }
        }

        /// <summary>
        /// context.describe — returns full descriptor/schema/fingerprint for a named context.
        /// Lookup order: built-in first, project custom second.
        /// Name conflict: project custom with same name as built-in is rejected at registration,
        /// so this lookup is always unambiguous.
        /// </summary>
        private static object ContextDescribe(UnityCli.Protocol.BridgeRequest request)
        {
            DiscoverContexts();
            var args = request.DeserializeArgs<ContextRouteArgs>();
            if (string.IsNullOrEmpty(args.Name))
                throw new BridgeCommandException("E_CONTEXT_NAME_REQUIRED", "context.describe requires a context name.", "Pass a valid context name from context.list output.");
            RegisteredContext definition;
            if (!Contexts.TryGetValue(args.Name, out definition))
                throw new BridgeCommandException("E_CONTEXT_NOT_FOUND", "Context not found: " + args.Name, "Run context.list to see available contexts.");
            if (definition.Source == "project") RefreshProjectContextFingerprint(definition, args.FingerprintMode);
            return ContextDescriptorData(definition);
        }

        /// <summary>
        /// context.read — executes the read handler for a named context and returns
        /// a unified response envelope.
        /// Lookup order: built-in first, project custom second.
        /// For project custom contexts, runs fingerprint check and source-guarded handler.
        /// Play Mode: allowed as read-only query.
        /// </summary>
        private static async ValueTask<object> ContextRead(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            DiscoverContexts();
            var args = request.DeserializeArgs<ContextRouteArgs>();
            if (string.IsNullOrEmpty(args.Name))
                throw new BridgeCommandException("E_CONTEXT_NAME_REQUIRED", "context.read requires a context name.", "Pass a valid context name from context.list output.");
            RegisteredContext definition;
            if (!Contexts.TryGetValue(args.Name, out definition))
                throw new BridgeCommandException("E_CONTEXT_NOT_FOUND", "Context not found: " + args.Name, "Run context.list to see available contexts.");

            if (definition.Source == "project")
            {
                var fingerprint = RefreshProjectContextFingerprint(definition, args.FingerprintMode);
                if (fingerprint.Blocked)
                    throw new BridgeCommandException("E_CONTEXT_FINGERPRINT_MISMATCH", fingerprint.Message, ContextFingerprintDetails(definition), new[] { "Review the registered context code, then update its fingerprint or use warn mode when policy allows." });
            }
            if (!string.IsNullOrEmpty(args.ExpectedDescriptorFingerprint)
                && !string.Equals(args.ExpectedDescriptorFingerprint, definition.Fingerprint, StringComparison.Ordinal))
            {
                throw new BridgeCommandException(
                    "E_CONTEXT_DESCRIPTOR_CHANGED",
                    "The Context descriptor changed after discovery: " + args.Name,
                    new Dictionary<string, object> { { "contextName", args.Name }, { "expectedFingerprint", args.ExpectedDescriptorFingerprint }, { "actualFingerprint", definition.Fingerprint } },
                    new[] { "Run context describe again against the selected Unity generation before constructing a new read." });
            }

            var inputJson = request.RawJsonArg("input") ?? "{}";
            var schemaError = ValidateTypedInput(definition.RequestType, inputJson);
            if (schemaError != null) throw SchemaValidationException(schemaError);
            object result;
            var startedAt = DateTime.UtcNow;
            try
            {
                var typedInput = UnityCliSerializer.Deserialize(inputJson, definition.RequestType);
                result = await definition.ReadAsync(typedInput, new UnityCliProjectToolContext
                {
                    RequestId = request.RequestId,
                    ProjectPath = args.ProjectPath ?? ProjectRoot(),
                    LogPath = LogPath(),
                    Wait = CreateWaiter(request.RequestId, args.Name),
                    CancellationToken = cancellationToken
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (BridgeCommandException) { throw; }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException ?? exception;
                var bridgeError = inner as BridgeCommandException;
                if (bridgeError != null) throw bridgeError;
                throw new BridgeCommandException("E_CONTEXT_HANDLER_FAILED", inner.GetType().Name + ": " + inner.Message, "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_CONTEXT_HANDLER_FAILED", exception.GetType().Name + ": " + exception.Message, "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }

            return new Dictionary<string, object>
            {
                { "command", "context.read" },
                { "context", new Dictionary<string, object> { { "name", args.Name }, { "version", definition.Version }, { "source", definition.Source } } },
                { "execution", new Dictionary<string, object> { { "channel", "live" }, { "startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture) }, { "exitState", "completed" } } },
                { "result", result },
                { "logs", new Dictionary<string, object> { { "bridge", LogPath() } } }
            };
        }

        // ── JSON serialization helpers ──

        private static Dictionary<string, object> ContextDescriptorData(RegisteredContext definition)
        {
            var data = new Dictionary<string, object>
            {
                { "name", definition.Name },
                { "summary", string.IsNullOrEmpty(definition.Summary) ? definition.Description : definition.Summary },
                { "description", definition.Description },
                { "category", definition.Category },
                { "version", definition.Version },
                { "inputSchema", UnityCliSerializer.Parse(definition.InputSchema) },
                { "outputSchema", UnityCliSerializer.Parse(definition.OutputSchema) },
                { "source", definition.Source },
                { "target", definition.Target },
                { "availabilityScope", definition.AvailabilityScope },
                { "playModeSupport", definition.PlayModeSupport },
                { "refreshBehavior", definition.RefreshBehavior },
                { "examples", definition.Examples },
                { "requiresPackages", definition.RequiresPackages },
                { "supportStatus", "live-bridge" },
                { "cliCommand", "context read " + definition.Name },
                { "descriptorFingerprint", definition.Fingerprint }
            };
            if (definition.Source == "project") data["fingerprint"] = ProjectContextFingerprintData(definition);
            return data;
        }

        private static Dictionary<string, object> ContextFingerprintDetails(RegisteredContext definition)
        {
            return new Dictionary<string, object>
            {
                { "context", definition.Name },
                { "fingerprint", ProjectContextFingerprintData(definition) }
            };
        }

        private static int CountTopLevelJsonArrayItems(string json)
        {
            var parsed = UnityCliSerializer.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) as System.Collections.ICollection;
            return parsed == null ? 0 : parsed.Count;
        }

        private static Dictionary<string, object> ProjectContextFingerprintSummary()
        {
            var items = new List<object>();
            var blocked = 0;
            var warn = 0;
            foreach (var pair in Contexts.Where(pair => pair.Value.Source == "project"))
            {
                var definition = pair.Value;
                RefreshProjectContextFingerprint(definition, null);
                if (definition.FingerprintStatus == "blocked") blocked++;
                if (definition.FingerprintStatus == "warn") warn++;
                items.Add(new Dictionary<string, object>
                {
                    { "name", definition.Name },
                    { "fingerprint", new Dictionary<string, object>
                        {
                            { "algorithm", ContextFingerprintAlgorithm },
                            { "value", definition.Fingerprint },
                            { "expected", definition.ExpectedFingerprint },
                            { "status", definition.FingerprintStatus },
                            { "mode", definition.FingerprintMode },
                            { "message", definition.FingerprintMessage },
                            { "descriptorSha256", definition.DescriptorSha256 },
                            { "handlerSha256", definition.HandlerSha256 }
                        }
                    }
                });
            }
            items.Sort((left, right) => StringComparer.Ordinal.Compare(
                ((Dictionary<string, object>)left)["name"] as string,
                ((Dictionary<string, object>)right)["name"] as string));
            return new Dictionary<string, object>
            {
                { "algorithm", ContextFingerprintAlgorithm },
                { "mode", NormalizeProjectContextFingerprintMode(null) },
                { "contextCount", (long)Contexts.Values.Count(context => context.Source == "project") },
                { "blockedCount", (long)blocked },
                { "warnCount", (long)warn },
                { "contexts", items }
            };
        }
    }
}
#endif
