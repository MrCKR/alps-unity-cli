#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        internal sealed class BridgeCommandRegistry
        {
            private readonly Dictionary<string, BridgeCommandDefinition> commands = new Dictionary<string, BridgeCommandDefinition>(StringComparer.Ordinal);

            public void Register<TInput>(
                string command,
                string domain,
                bool destructive,
                int timeoutMs,
                Func<TInput, CancellationToken, ValueTask<object>> handler,
                UnityCliBridgeQueuePolicy.WorkClass? queueClass = null)
            {
                if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Bridge command registration requires a command name.", "command");
                if (handler == null) throw new ArgumentNullException("handler");
                if (commands.ContainsKey(command)) throw new InvalidOperationException("Duplicate bridge command registration: " + command);
                var registeredQueueClass = queueClass;
                if (!registeredQueueClass.HasValue && command.StartsWith("internal.", StringComparison.Ordinal))
                    registeredQueueClass = UnityCliBridgeQueuePolicy.IsSafeStatusCommand(command)
                        ? UnityCliBridgeQueuePolicy.WorkClass.SafeStatus
                        : UnityCliBridgeQueuePolicy.WorkClass.Mutation;
                if (registeredQueueClass == UnityCliBridgeQueuePolicy.WorkClass.SafeStatus && !UnityCliBridgeQueuePolicy.IsSafeStatusCommand(command))
                    throw new InvalidOperationException("Only exact SafeStatus commands may use SafeStatus classification: " + command);
                if (registeredQueueClass == UnityCliBridgeQueuePolicy.WorkClass.Control && !UnityCliBridgeQueuePolicy.IsDurableControlCommand(command))
                    throw new InvalidOperationException("Only exact durable control commands may use Control classification: " + command);
                if (UnityCliBridgeQueuePolicy.IsDurableControlCommand(command) && registeredQueueClass != UnityCliBridgeQueuePolicy.WorkClass.Control)
                    throw new InvalidOperationException("Durable control command must use Control classification: " + command);
                if (command.StartsWith("internal.", StringComparison.Ordinal)
                    && registeredQueueClass == UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead
                    && !UnityCliBridgeQueuePolicy.IsOrdinaryReadInternalCommand(command))
                    throw new InvalidOperationException("Only exact observation commands may use OrdinaryRead classification: " + command);
                commands.Add(command, new BridgeCommandDefinition(
                    command,
                    domain,
                    destructive,
                    timeoutMs,
                    typeof(TInput),
                    registeredQueueClass,
                    async (input, cancellationToken) => await handler((TInput)input, cancellationToken)));
            }

            public async ValueTask<object> ExecuteAsync(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
            {
                BridgeCommandDefinition definition;
                if (request == null || !commands.TryGetValue(request.Command, out definition))
                    throw new BridgeCommandException("E_BRIDGE_COMMAND_UNSUPPORTED", "Unsupported bridge command: " + (request == null ? string.Empty : request.Command), "Upgrade the UnityCLI Bridge package or use a registered CLI command.");
                cancellationToken.ThrowIfCancellationRequested();
                object input;
                if (definition.InputType == typeof(UnityCli.Protocol.BridgeRequest))
                {
                    input = request;
                }
                else
                {
                    var inputJson = UnityCliSerializer.Serialize(request.Input ?? new Dictionary<string, object>());
                    var schemaError = ValidateTypedInput(definition.InputType, inputJson);
                    if (schemaError != null) throw SchemaValidationException(schemaError);
                    input = UnityCliSerializer.Deserialize(inputJson, definition.InputType);
                }
                return await definition.Handler(input, cancellationToken);
            }

            public bool IsMutating(string command)
            {
                BridgeCommandDefinition definition;
                return command != null && commands.TryGetValue(command, out definition) && definition.Destructive;
            }

            public bool TryGetQueueClass(string command, out UnityCliBridgeQueuePolicy.WorkClass workClass)
            {
                BridgeCommandDefinition definition;
                if (command != null && commands.TryGetValue(command, out definition) && definition.QueueClass.HasValue)
                {
                    workClass = definition.QueueClass.Value;
                    return true;
                }
                workClass = default(UnityCliBridgeQueuePolicy.WorkClass);
                return false;
            }

            internal IReadOnlyDictionary<string, UnityCliBridgeQueuePolicy.WorkClass> InternalQueueClasses()
            {
                var result = new Dictionary<string, UnityCliBridgeQueuePolicy.WorkClass>(StringComparer.Ordinal);
                foreach (var pair in commands)
                    if (pair.Key.StartsWith("internal.", StringComparison.Ordinal) && pair.Value.QueueClass.HasValue)
                        result.Add(pair.Key, pair.Value.QueueClass.Value);
                return result;
            }
        }

        private sealed class BridgeCommandDefinition
        {
            public BridgeCommandDefinition(string command, string domain, bool destructive, int timeoutMs, Type inputType, UnityCliBridgeQueuePolicy.WorkClass? queueClass, Func<object, CancellationToken, ValueTask<object>> handler)
            {
                Command = command;
                Domain = domain;
                Destructive = destructive;
                TimeoutMs = timeoutMs;
                InputType = inputType;
                QueueClass = queueClass;
                Handler = handler;
            }

            public string Command { get; private set; }
            public string Domain { get; private set; }
            public bool Destructive { get; private set; }
            public int TimeoutMs { get; private set; }
            public Type InputType { get; private set; }
            public UnityCliBridgeQueuePolicy.WorkClass? QueueClass { get; private set; }
            public Func<object, CancellationToken, ValueTask<object>> Handler { get; private set; }
        }

        internal static IReadOnlyDictionary<string, UnityCliBridgeQueuePolicy.WorkClass> InternalQueueClassesForTests()
        {
            return CommandRegistry.InternalQueueClasses();
        }

        private static BridgeCommandRegistry CreateCommandRegistry()
        {
            var registry = new BridgeCommandRegistry();
            RegisterCodeExecCommand(registry);
            RegisterCodeFlowCommands(registry);
            RegisterEditorStateSnapshotCommand(registry);
            RegisterDoctorDiagnosticCommands(registry);
            RegisterInstanceCleanupCommand(registry);
            UnityCliDurableStatusCoordinator.Register(registry);
            UnityCliCompileCoordinator.Register(registry);
            UnityCliLiveBuildCoordinator.Register(registry);
            UnityCliLiveRefreshCoordinator.Register(registry);
            UnityCliAssetRefreshCoordinator.Register(registry);
            UnityCliPackageEmbedCoordinator.Register(registry);
            UnityCliPackageResolveCoordinator.Register(registry);
            UnityCliTestRunCoordinator.Register(registry);
            UnityCliNavMeshBakeCoordinator.Register(registry);
            UnityCliOcclusionBakeCoordinator.Register(registry);
            RegisterProjectToolCommands(registry);
            RegisterRuntimeToolCommands(registry);
            return registry;
        }

        private static void RegisterCommand(BridgeCommandRegistry registry, string command, string domain, bool destructive, Func<UnityCli.Protocol.BridgeRequest, object> handler)
        {
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                command,
                domain,
                destructive,
                destructive ? 60000 : 30000,
                (request, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new ValueTask<object>(NormalizeCommandResult(handler(request)));
                });
        }

        private static object NormalizeCommandResult(object result)
        {
            var serialized = result as string;
            if (serialized == null) return result;
            var envelope = UnityCliSerializer.Parse(serialized) as IDictionary<string, object>;
            object okValue;
            if (envelope == null || !envelope.TryGetValue("ok", out okValue) || !(okValue is bool)) return UnityCliSerializer.Parse(serialized);
            if ((bool)okValue)
            {
                object data;
                return envelope.TryGetValue("data", out data) ? data : null;
            }
            object errorValue;
            var error = envelope.TryGetValue("error", out errorValue) ? errorValue as IDictionary<string, object> : null;
            if (error == null) throw new BridgeCommandException("E_BRIDGE_COMMAND_FAILED", "Bridge command returned an invalid error envelope.");
            object code;
            object message;
            object details;
            object actions;
            error.TryGetValue("code", out code);
            error.TryGetValue("message", out message);
            error.TryGetValue("details", out details);
            error.TryGetValue("nextActions", out actions);
            throw new BridgeCommandException(
                Convert.ToString(code),
                Convert.ToString(message),
                details,
                StringValues(actions));
        }

        private static IEnumerable<string> StringValues(object value)
        {
            var items = value as System.Collections.IEnumerable;
            if (items == null) return Array.Empty<string>();
            var result = new List<string>();
            foreach (var item in items)
            {
                var text = Convert.ToString(item);
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
            }
            return result;
        }

    }
}
#endif
