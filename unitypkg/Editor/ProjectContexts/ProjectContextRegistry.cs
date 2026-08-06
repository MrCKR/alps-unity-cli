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

        private const string ContextFingerprintAlgorithm = "unity-cli-project-context-fingerprint-v2";

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
            "DestructiveHint"
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
            if (string.IsNullOrWhiteSpace(definition.InputSchema) || string.IsNullOrWhiteSpace(definition.OutputSchema))
                throw new ArgumentException("Context registration requires schemas derived from request/result types: " + definition.Name);
            definition.Target = string.IsNullOrEmpty(definition.Target) ? "editor" : definition.Target;
            definition.AvailabilityScope = string.IsNullOrEmpty(definition.AvailabilityScope) ? (definition.Target == "runtime" ? "playModeSession" : "editorSession") : definition.AvailabilityScope;
            definition.PlayModeSupport = NormalizeProjectContextPlayModeSupport(definition.PlayModeSupport, definition.Target);
            definition.RequiresPackages = definition.RequiresPackages ?? Array.Empty<string>();
            definition.RegisteredBy = string.IsNullOrEmpty(definition.RegisteredBy) ? "unity-editor-package" : definition.RegisteredBy;
            definition.DescriptorSource = string.IsNullOrEmpty(definition.DescriptorSource) ? "IUnityCliContext" : definition.DescriptorSource;
            ComputeProjectContextFingerprint(definition);

            if (definition.Ownership == "project" && !string.IsNullOrEmpty(definition.Method))
            {
                var violation = ProjectContextSourceGuardCheck(ProjectContextHandlerSource(definition), definition.Name);
                if (violation != null) throw new ArgumentException("Custom context " + definition.Name + " contains forbidden write-semantic token: " + violation, violation);
                RefreshProjectContextFingerprint(definition);
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
            var proofObserver = instance as IUnityCliProofObserver<TReq>;
            var ownership = registryOwnership.Resolve(implementationType);
            if (proofObserver != null && !string.Equals(ownership, "built-in", StringComparison.Ordinal))
                throw new InvalidOperationException("Only built-in Contexts may register completion proof observers.");
            RegisterContext(new RegisteredContext
            {
                Name = descriptor.Name,
                Summary = string.IsNullOrEmpty(descriptor.Summary) ? descriptor.Description : descriptor.Summary,
                Description = descriptor.Description,
                Category = string.IsNullOrEmpty(descriptor.Category) ? (ownership == "built-in" ? "built-in" : "custom") : descriptor.Category,
                InputSchema = UnityCliSchemaDeriver.DeriveSchema(typeof(TReq)),
                OutputSchema = UnityCliSchemaDeriver.DeriveSchema(typeof(TRes)),
                Source = ownership,
                Ownership = ownership,
                Target = string.IsNullOrEmpty(descriptor.Target) ? "editor" : descriptor.Target,
                AvailabilityScope = descriptor.AvailabilityScope,
                PlayModeSupport = descriptor.PlayModeSupport,
                Examples = descriptor.Examples ?? Array.Empty<ContextExample>(),
                RequiresPackages = descriptor.RequiresPackages ?? Array.Empty<string>(),
                RegisteredBy = ownership == "extension" ? "extension-manifest" : "unity-editor-package",
                DescriptorSource = "IUnityCliContext",
                Assembly = assembly.GetName().Name,
                Method = implementationType.FullName + ".ReadAsync",
                Fingerprint = descriptor.Fingerprint,
                PinnedFingerprint = descriptor.Fingerprint,
                ExpectedFingerprint = descriptor.Fingerprint,
                Descriptor = descriptor,
                RequestType = typeof(TReq),
                ResultType = typeof(TRes),
                ReadAsync = async (request, executionContext, cancellationToken) => (object)await context.ReadAsync((TReq)request, executionContext, cancellationToken),
                ResolveProofResource = proofObserver == null ? null : (Func<object, UnityCliProofResourceClaim>)(request => proofObserver.ResolveProofResource((TReq)request))
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
            public string Expected;
            public string Actual;
            public string Message;
        }

        private sealed class ProjectContextPinnedFingerprint
        {
            public string Owner;
            public string Value;
            public string Algorithm;
            public string Format;
            public string Sha256;
            public bool HardMismatch;
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
            RegisteredContext definition)
        {
            ComputeProjectContextFingerprint(definition);
            ProjectContextPinnedFingerprint pin;
            if (string.Equals(definition.Ownership, "project", StringComparison.Ordinal) && !string.IsNullOrEmpty(definition.PinnedFingerprint))
            {
                pin = ParseProjectContextPinnedFingerprint("descriptor", definition.PinnedFingerprint);
            }
            else if (string.Equals(definition.Ownership, "project", StringComparison.Ordinal))
            {
                var baseline = LoadProjectContextExpectedFingerprint(definition.Name);
                if (string.IsNullOrEmpty(baseline))
                {
                    // 首次 project baseline 只能写入携带算法的 v2 pin，无算法 pin 绝不生成。
                    baseline = FormatProjectContextFingerprint(definition.Fingerprint);
                    SaveProjectContextExpectedFingerprint(definition.Name, baseline);
                }
                pin = ParseProjectContextPinnedFingerprint("project-baseline", baseline);
            }
            else
            {
                // Built-in 与 Extension 的 expected 由当前安装包生成，不写项目 baseline。
                pin = ParseProjectContextPinnedFingerprint("installed-package", FormatProjectContextFingerprint(definition.Fingerprint));
            }

            definition.PinnedFingerprintOwner = pin.Owner;
            definition.PinnedFingerprintAlgorithm = pin.Algorithm;
            definition.PinnedFingerprintFormat = pin.Format;
            definition.ExpectedFingerprint = pin.Value;
            var matches = !pin.HardMismatch && string.Equals(pin.Sha256, definition.Fingerprint, StringComparison.OrdinalIgnoreCase);
            // 任一 pin 格式、算法或内容不匹配都使用同一阻断路径。
            definition.FingerprintStatus = matches ? "match" : "blocked";
            definition.FingerprintMessage = matches
                ? "Custom context fingerprint matches handler code + descriptor."
                : pin.HardMismatch
                    ? "Custom context fingerprint pin uses an unsupported algorithm or format and must be re-pinned."
                    : "Custom context fingerprint content differs from its v2 pin.";

            return new ProjectContextFingerprintCheck
            {
                Blocked = !matches,
                Expected = definition.ExpectedFingerprint,
                Actual = definition.Fingerprint,
                Message = definition.FingerprintMessage
            };
        }

        /// <summary>解析 project Context pin；裸 SHA、未知算法和畸形值都不能被当作 v2 expected。</summary>
        private static ProjectContextPinnedFingerprint ParseProjectContextPinnedFingerprint(string owner, string value)
        {
            var pin = new ProjectContextPinnedFingerprint { Owner = owner, Value = value ?? string.Empty, Format = "malformed" };
            var match = Regex.Match(pin.Value, "^([^:]+):([0-9a-fA-F]{64})$");
            if (!match.Success)
            {
                pin.Format = Regex.IsMatch(pin.Value, "^[0-9a-fA-F]{64}$") ? "bare-sha256" : "malformed";
                pin.Algorithm = pin.Format == "bare-sha256" ? "none" : "invalid";
                pin.HardMismatch = true;
                return pin;
            }
            pin.Algorithm = match.Groups[1].Value;
            pin.Sha256 = match.Groups[2].Value.ToLowerInvariant();
            pin.Format = string.Equals(pin.Algorithm, ContextFingerprintAlgorithm, StringComparison.Ordinal) ? "valid-v2" : "algorithm-mismatch";
            pin.HardMismatch = !string.Equals(pin.Algorithm, ContextFingerprintAlgorithm, StringComparison.Ordinal);
            return pin;
        }

        private static string FormatProjectContextFingerprint(string sha256)
        {
            return ContextFingerprintAlgorithm + ":" + (sha256 ?? string.Empty).ToLowerInvariant();
        }

        private static string ProjectContextDescriptorFingerprintMaterial(RegisteredContext definition)
        {
            return "name=" + (definition.Name ?? string.Empty)
                + "\nsummary=" + (definition.Summary ?? definition.Description ?? string.Empty)
                + "\ndescription=" + (definition.Description ?? string.Empty)
                + "\ncategory=" + (definition.Category ?? string.Empty)
                + "\ninputSchema=" + NormalizeFingerprintJson(definition.InputSchema)
                + "\noutputSchema=" + NormalizeFingerprintJson(definition.OutputSchema)
                + "\nsource=" + (definition.Source ?? string.Empty)
                + "\ntarget=" + (definition.Target ?? string.Empty)
                + "\navailabilityScope=" + (definition.AvailabilityScope ?? string.Empty)
                + "\nplayModeSupport=" + (definition.PlayModeSupport ?? string.Empty)
                + "\nrequiresPackages=" + string.Join(",", (definition.RequiresPackages ?? new string[0])
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray())
                + "\nexamples=" + NormalizeFingerprintJson(UnityCliSerializer.Serialize(definition.Examples ?? Array.Empty<ContextExample>()))
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
            return Path.Combine(UnityCliTempRoot(),
                "fingerprints", "contexts",
                SafeProjectContextFingerprintFileName(name) + ".sha256");
        }

        private static string SafeProjectContextFingerprintFileName(string name)
        {
            return Regex.Replace(name ?? "project-context", "[^A-Za-z0-9_.-]", "_");
        }

        private static Dictionary<string, object> ProjectContextFingerprintData(RegisteredContext definition)
        {
            return new Dictionary<string, object>
            {
                { "currentAlgorithm", ContextFingerprintAlgorithm }, { "actualSha256", definition.Fingerprint },
                { "pinnedOwner", definition.PinnedFingerprintOwner }, { "pinnedAlgorithm", definition.PinnedFingerprintAlgorithm },
                { "pinnedFormat", definition.PinnedFingerprintFormat }, { "pinnedValue", definition.ExpectedFingerprint },
                { "status", definition.FingerprintStatus }, { "message", definition.FingerprintMessage },
                { "rePinValue", FormatProjectContextFingerprint(definition.Fingerprint) },
                { "rePinSteps", ProjectContextFingerprintRePinSteps(definition) },
                { "descriptorSha256", definition.DescriptorSha256 }, { "handlerSha256", definition.HandlerSha256 }
            };
        }

        private static string[] ProjectContextFingerprintRePinSteps(RegisteredContext definition)
        {
            var value = FormatProjectContextFingerprint(definition.Fingerprint);
            return definition.PinnedFingerprintOwner == "descriptor"
                ? new[] { "Review the Context change.", "Set ContextDescriptor.Fingerprint to " + value + ".", "Run the Context again." }
                : new[] { "Review the Context change.", "Replace the persisted project Context fingerprint with " + value + ".", "Run the Context again." };
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
                .OrderBy(context => context.Name, StringComparer.Ordinal)
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
            if (definition.Ownership == "project") RefreshProjectContextFingerprint(definition);
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

            if (definition.Ownership == "project")
            {
                var fingerprint = RefreshProjectContextFingerprint(definition);
                if (fingerprint.Blocked)
                    throw new BridgeCommandException("E_CONTEXT_FINGERPRINT_MISMATCH", fingerprint.Message, ContextFingerprintDetails(definition), ProjectContextFingerprintRePinSteps(definition));
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

            var outputError = ValidateTypedInput(definition.ResultType, UnityCliSerializer.Serialize(result));
            if (outputError != null)
                throw new BridgeCommandException(
                    "E_CONTEXT_OUTPUT_SCHEMA_INVALID",
                    "Context result failed its derived output schema at " + outputError.Path + ".",
                    new Dictionary<string, object> { { "contextName", definition.Name }, { "path", outputError.Path }, { "expected", outputError.Expected }, { "actual", outputError.Actual } },
                    new[] { "Fix the Context Result DTO before submitting another read." });
            WriteLog("context_read_completed requestId=" + request.RequestId + " name=" + args.Name);
            return new Dictionary<string, object> { { "result", result } };
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
                { "inputSchema", UnityCliSerializer.Parse(definition.InputSchema) },
                { "outputSchema", UnityCliSerializer.Parse(definition.OutputSchema) },
                { "source", definition.Source },
                { "target", definition.Target },
                { "availabilityScope", definition.AvailabilityScope },
                { "playModeSupport", definition.PlayModeSupport },
                { "examples", definition.Examples },
                { "internalDescriptorFingerprint", definition.Fingerprint },
                { "available", ContextAvailableNow(definition) }
            };
            if (!(bool)data["available"])
                data["unavailableReason"] = ContextUnavailableReason(definition);
            if (definition.RequiresPackages != null && definition.RequiresPackages.Length > 0) data["requiresPackages"] = definition.RequiresPackages;
            return data;
        }

        /// <summary>目录始终显示 Context；available 只描述当前 Edit/Play 状态，不参与过滤。</summary>
        private static bool ContextAvailableNow(RegisteredContext definition)
        {
            if (definition.Target == "runtime" || definition.PlayModeSupport == "runtime-only")
                return EditorApplication.isPlaying && !IsPlayModeChanging();
            if (definition.PlayModeSupport == "editor-only")
                return !EditorApplication.isPlayingOrWillChangePlaymode && !IsPlayModeChanging();
            return !IsPlayModeChanging();
        }

        /// <summary>按读取门禁优先级返回与 available=false 对应的稳定原因。</summary>
        private static string ContextUnavailableReason(RegisteredContext definition)
        {
            if (definition.Target == "runtime" || definition.PlayModeSupport == "runtime-only")
                return "E_RUNTIME_NOT_READY: Enter Play Mode before reading this Context.";
            if (definition.PlayModeSupport == "editor-only" && EditorApplication.isPlayingOrWillChangePlaymode)
                return "E_CONTEXT_UNAVAILABLE: Exit Play Mode before reading this editor-only Context.";
            return "E_UNITY_TRANSITIONING: Wait until the Editor mode transition finishes.";
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
            foreach (var pair in Contexts.Where(pair => pair.Value.Ownership == "project"))
            {
                var definition = pair.Value;
                RefreshProjectContextFingerprint(definition);
                if (definition.FingerprintStatus == "blocked") blocked++;
                items.Add(new Dictionary<string, object>
                {
                    { "name", definition.Name },
                    { "fingerprint", ProjectContextFingerprintData(definition) }
                });
            }
            items.Sort((left, right) => StringComparer.Ordinal.Compare(
                ((Dictionary<string, object>)left)["name"] as string,
                ((Dictionary<string, object>)right)["name"] as string));
            return new Dictionary<string, object>
            {
                { "algorithm", ContextFingerprintAlgorithm },
                { "contextCount", (long)Contexts.Values.Count(context => context.Ownership == "project") },
                { "blockedCount", (long)blocked },
                { "contexts", items }
            };
        }
    }
}
#endif
