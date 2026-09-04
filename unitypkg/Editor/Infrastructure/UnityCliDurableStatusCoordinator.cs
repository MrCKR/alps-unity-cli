#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>域专用公开status Tool共用的只读Bridge协调器；公开输入不包含内部digest或scope。</summary>
        internal static class UnityCliDurableStatusCoordinator
        {
            internal const string AssetRefreshCommand = "internal.asset.refresh.public-status";
            internal const string BuildPlayerCommand = "internal.build.player.public-status";
            internal const string EditorPlayModeCommand = "internal.editor.play-mode.public-status";
            internal const string PackageInstallCommand = "internal.package.install.public-status";
            internal const string PackageRemoveCommand = "internal.package.remove.public-status";
            internal const string PackageUpdateCommand = "internal.package.update.public-status";
            internal const string TestRunCommand = "internal.test.run.public-status";

            internal static bool IsCommand(string command)
            {
                return command == AssetRefreshCommand || command == BuildPlayerCommand || command == EditorPlayModeCommand
                    || command == PackageInstallCommand || command == PackageRemoveCommand
                    || command == PackageUpdateCommand || command == TestRunCommand;
            }

            internal static void Register(BridgeCommandRegistry registry)
            {
                registry.Register<BridgeRequest>("internal.asset.refresh.public-status", "internal", false, 30000, CoordinatorOwned, UnityCliBridgeQueuePolicy.WorkClass.SafeStatus);
                registry.Register<BridgeRequest>("internal.build.player.public-status", "internal", false, 30000, CoordinatorOwned, UnityCliBridgeQueuePolicy.WorkClass.SafeStatus);
                registry.Register<BridgeRequest>("internal.editor.play-mode.public-status", "internal", false, 30000, CoordinatorOwned, UnityCliBridgeQueuePolicy.WorkClass.SafeStatus);
                registry.Register<BridgeRequest>("internal.package.install.public-status", "internal", false, 30000, CoordinatorOwned, UnityCliBridgeQueuePolicy.WorkClass.SafeStatus);
                registry.Register<BridgeRequest>("internal.package.remove.public-status", "internal", false, 30000, CoordinatorOwned, UnityCliBridgeQueuePolicy.WorkClass.SafeStatus);
                registry.Register<BridgeRequest>("internal.package.update.public-status", "internal", false, 30000, CoordinatorOwned, UnityCliBridgeQueuePolicy.WorkClass.SafeStatus);
                registry.Register<BridgeRequest>("internal.test.run.public-status", "internal", false, 30000, CoordinatorOwned, UnityCliBridgeQueuePolicy.WorkClass.SafeStatus);
            }

            private static ValueTask<object> CoordinatorOwned(BridgeRequest request, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("Durable status is coordinator-owned.");
            }

            internal static Task<string> Execute(BridgeRequest request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var args = request.DeserializeArgs<Args>();
                    if (args == null || !IsSafeRequestId(args.RequestId))
                        throw new BridgeCommandException("E_SCHEMA_INVALID", "requestId is required and must be a safe root request identity.");
                    object data;
                    switch (request.Command)
                    {
                        case AssetRefreshCommand: data = ReadScope("asset-refresh", args.RequestId); break;
                        case BuildPlayerCommand: data = ReadScope("build", args.RequestId); break;
                        case EditorPlayModeCommand: data = ReadEditorPlayModeStatus(args.RequestId); break;
                        case PackageInstallCommand: data = UnityCliPackageResolveCoordinator.ReadPublicStatus(args.RequestId, "install"); break;
                        case PackageRemoveCommand: data = UnityCliPackageResolveCoordinator.ReadPublicStatus(args.RequestId, "remove"); break;
                        case PackageUpdateCommand: data = UnityCliPackageResolveCoordinator.ReadPublicStatus(args.RequestId, "update"); break;
                        case TestRunCommand: data = ReadScope("test-run", args.RequestId); break;
                        default: throw new BridgeCommandException("E_TOOL_NOT_FOUND", "Durable status command is not registered.");
                    }
                    return Task.FromResult(UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, data)));
                }
                catch (BridgeCommandException exception)
                {
                    return Task.FromResult(SerializeBridgeExceptionEnvelope(request.RequestId, exception));
                }
            }

            /// <summary>先读有界terminal cache，再读单scope active journal；读取不获取lease且不推进operation。</summary>
            internal static object ReadScope(string scope, string requestId)
            {
                string requestDigest;
                string statusDomain;
                object terminal;
                if (UnityCliActiveOperation.TryGetTerminalByRequestId(scope, requestId, out requestDigest, out statusDomain, out terminal))
                    return Terminal(requestId, terminal);
                var root = UnityCliActiveOperation.Load(scope);
                if (root == null || !string.Equals(Text(root, "requestId"), requestId, StringComparison.Ordinal))
                    throw new BridgeCommandException("E_REQUEST_NOT_FOUND", "The requested durable operation was not found in this Editor session.");
                return Running(requestId, Text(root, "phase"));
            }

            internal static object Running(string requestId, string phase)
            {
                return new Dictionary<string, object>
                {
                    { "requestId", requestId }, { "state", "running" }, { "phase", string.IsNullOrWhiteSpace(phase) ? "accepted" : phase }
                };
            }

            internal static object Terminal(string requestId, object envelope)
            {
                return new Dictionary<string, object>
                {
                    { "requestId", requestId }, { "state", "completed" }, { "terminalEnvelope", envelope }
                };
            }

            private static bool IsSafeRequestId(string requestId)
            {
                if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 200) return false;
                foreach (var character in requestId)
                    if (!(char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.' || character == ':')) return false;
                return true;
            }

            private sealed class Args { public string RequestId { get; set; } }
        }
    }
}
#endif
