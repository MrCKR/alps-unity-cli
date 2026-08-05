#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor.PackageManager.Requests;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>Package 文件 mutation 与 UPM completion fence 共用一个公开 root journal。</summary>
        internal static partial class UnityCliPackageResolveCoordinator
        {
            internal const string Command = "internal.package.resolve.start";
            private const string StatusCommand = "internal.package.resolve.status";
            private const string ClaimFilesCommand = "internal.package.files.claim";
            private const string CommitFilesCommand = "internal.package.files.commit";
            private const string ProveFilesCommand = "internal.package.files.prove";
            private const string FailFilesCommand = "internal.package.files.fail";
            private static readonly Dictionary<string, ListRequest> ActiveLists = new Dictionary<string, ListRequest>(StringComparer.Ordinal);
            private static readonly Coordinator operation = new Coordinator();

            private sealed class Coordinator : UnityCliReloadOperation
            {
                protected override string Scope { get { return ActiveScope; } }
                protected override string StartCommand { get { return Command; } }
                protected override string StatusCommand { get { return UnityCliPackageResolveCoordinator.StatusCommand; } }
                protected override string OperationDisplayName { get { return "Package root"; } }
                protected override IEnumerable<(string name, bool destructive)> AdditionalCommands
                {
                    get
                    {
                        yield return (ClaimFilesCommand, true);
                        yield return (CommitFilesCommand, true);
                        yield return (ProveFilesCommand, true);
                        yield return (FailFilesCommand, true);
                    }
                }
                protected override object DeserializeStartArgs(BridgeRequest request) { return request.DeserializeArgs<StartArgs>(); }
                protected override void ValidateStart(object args) { UnityCliPackageResolveCoordinator.ValidateStart((StartArgs)args); }
                protected override object Planned(object args) { return null; }
                protected override Dictionary<string, object> CreateRoot(BridgeRequest request, string digest, object args) { return null; }
                protected override void OnTick(Dictionary<string, object> root) { }
                protected override object BuildSuccessData(Dictionary<string, object> root) { return null; }
                protected override object Status(BridgeRequest request) { return UnityCliPackageResolveCoordinator.Status(request); }
                protected override object ExecuteAdditionalCommand(BridgeRequest request)
                {
                    if (request.Command == ClaimFilesCommand) return ClaimFiles(request);
                    if (request.Command == CommitFilesCommand) return CommitFiles(request);
                    if (request.Command == ProveFilesCommand) return ProveFiles(request);
                    if (request.Command == FailFilesCommand) return FailFiles(request);
                    return base.ExecuteAdditionalCommand(request);
                }
                internal override async Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken cancellationToken)
                {
                    try
                    {
                        if (request.Command != Command) return await base.Execute(request, raw, accept, cancellationToken);
                        return await ExecuteStart(request, raw, accept, cancellationToken);
                    }
                    catch (BridgeCommandException exception) { return SerializeBridgeExceptionEnvelope(request.RequestId, exception); }
                }
                internal override void Tick() { TickPackageRoots(); }
            }

            internal static bool IsCommand(string command)
            {
                return operation.IsCommand(command);
            }

            internal static void Register(BridgeCommandRegistry registry)
            {
                operation.Register(registry);
            }

            internal static void Initialize() { ActiveLists.Clear(); }
            internal static void Shutdown() { ActiveLists.Clear(); }
            internal static void Tick() { operation.Tick(); }

            internal static void BeforeReload()
            {
                ActiveLists.Clear();
                foreach (var root in OpenRoots())
                {
                    var phase = Text(root.PackageOperation, "phase");
                    if (phase == "resolve_dispatching")
                    {
                        TerminalTimeout(root, "Domain reload interrupted PackageManager.Resolve dispatch before its return was persisted.");
                        continue;
                    }
                    AdvancePackageOperation(root.RequestId, current =>
                    {
                        current.PackageOperation["reloadObserved"] = true;
                        var currentPhase = Text(current.PackageOperation, "phase");
                        if (currentPhase == "resolve_returned" || currentPhase == "list_pending") current.PackageOperation["phase"] = "reload_observed";
                    });
                }
            }

            internal static async Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken cancellationToken)
            {
                return await operation.Execute(request, raw, accept, cancellationToken);
            }

            private static async Task<string> ExecuteStart(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken cancellationToken)
            {
                var newlyAcquiredLease = false;
                try
                {
                    var args = request.DeserializeArgs<StartArgs>();
                    ValidateStart(args);
                    if (!UnityCliSessionMutationLease.TryAcquire(request.RequestId, UnityCliRequestDigest.Compute(raw, request.InstanceId), out newlyAcquiredLease)) return BusyEnvelope(request.RequestId);
                    PackageActiveDecision decision;
                    try
                    {
                        UnityCliToolCompletionContract contract;
                        if (!UnityCliToolCompletionRegistry.TryGetPackageRoot(args.Operation, out contract)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Unsupported package root operation.");
                        decision = AcceptPackageActiveOperation(request, raw, contract,
                            () => { if (accept == null || !accept()) throw new OperationCanceledException(); },
                            () => ValidateStart(args), root => InitializeRoot(root, args));
                    }
                    catch { if (newlyAcquiredLease) UnityCliSessionMutationLease.Release(request.RequestId); throw; }
                    if (decision.TerminalEnvelope != null)
                    {
                        UnityCliSessionMutationLease.Release(request.RequestId);
                        return StartTerminal(request.RequestId, decision.TerminalEnvelope);
                    }
                    if (!decision.Execute)
                    {
                        if (newlyAcquiredLease) UnityCliSessionMutationLease.Release(request.RequestId);
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Package active operation did not authorize recovery.");
                    }
                    await Task.FromResult(0);
                    return UnityCliSerializer.Serialize(StartState(request.RequestId));
                }
                catch (BridgeCommandException exception) { return SerializeBridgeExceptionEnvelope(request.RequestId, exception); }
            }

            /// <summary>effect 前先持久化唯一 file executor；只有旧 owner 已死亡时才允许 recovery 重绑。</summary>
            private static object ClaimFiles(BridgeRequest request)
            {
                var args = request.DeserializeArgs<FileOwnerArgs>();
                if (args == null || !IsSafeId(args.RootRequestId) || args.ExecutorPid <= 0 || !IsSha256Text(args.ExecutorCapability)) throw Schema("rootRequestId, executorPid, and executorCapability are required.");
                var root = RequiredRoot(args.RootRequestId);
                var operation = Text(root.PackageOperation, "operation");
                if (operation == "resolve") throw Schema("package.resolve has no TS file executor.");
                RequireLease(root.RequestId);
                if (Text(root.PackageOperation, "phase") != "awaiting_files" || root.Phase != "accepted_before_effect")
                    throw new BridgeCommandException("E_EXECUTION_FAILED", "Package root is not awaiting a file executor claim.");
                if (FileExecutorMatches(root.PackageOperation, args.ExecutorPid, args.ExecutorCapability))
                    return ProtocolEnvelopeFactory.Success(request.RequestId, ControlState(root, args.ExecutorPid, args.ExecutorCapability));
                if (FileExecutorAlive(root.PackageOperation))
                    return ProtocolEnvelopeFactory.Success(request.RequestId, ControlState(root, args.ExecutorPid, args.ExecutorCapability));
                BindFileExecutor(root.RequestId, args.ExecutorPid, args.ExecutorCapability);
                return ProtocolEnvelopeFactory.Success(request.RequestId, ControlState(RequiredRoot(root.RequestId), args.ExecutorPid, args.ExecutorCapability));
            }

            private static object CommitFiles(BridgeRequest request)
            {
                var args = request.DeserializeArgs<CommitArgs>();
                if (args == null || !IsSafeId(args.RootRequestId) || args.FilePlan == null || args.ExecutorPid <= 0 || !IsSha256Text(args.ExecutorCapability)) throw Schema("rootRequestId, filePlan, executorPid, and executorCapability are required.");
                if (Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(args.FilePlan)) > 1024 * 1024) throw Schema("Package filePlan exceeds the private journal limit.");
                var root = RequiredRoot(args.RootRequestId);
                var operation = Text(root.PackageOperation, "operation");
                if (operation == "resolve") throw Schema("package.resolve has no TS file mutation.");
                VerifyFilePlanBinding(operation, root.PackageOperation["input"] as Dictionary<string, object>, args.FilePlan);
                RequireLease(root.RequestId);
                var phase = Text(root.PackageOperation, "phase");
                if (phase == "files_committing")
                {
                    if (!SameJson(root.PackageOperation["filePlan"], args.FilePlan)) throw new BridgeCommandException("E_EXECUTION_FAILED", "Package filePlan changed after effect barrier.");
                    if (FileExecutorMatches(root.PackageOperation, args.ExecutorPid, args.ExecutorCapability)) return ProtocolEnvelopeFactory.Success(request.RequestId, ControlState(root, args.ExecutorPid, args.ExecutorCapability));
                    if (FileExecutorAlive(root.PackageOperation)) throw new BridgeCommandException("E_UNITY_BUSY", "Another Package file executor still owns this root.");
                    BindFileExecutor(root.RequestId, args.ExecutorPid, args.ExecutorCapability);
                    return ProtocolEnvelopeFactory.Success(request.RequestId, ControlState(RequiredRoot(root.RequestId), args.ExecutorPid, args.ExecutorCapability));
                }
                if (phase != "awaiting_files" || root.Phase != "accepted_before_effect") throw new BridgeCommandException("E_EXECUTION_FAILED", "Package root is not awaiting its file effect barrier.");
                if (root.PackageOperation.ContainsKey("filePlan")) throw new BridgeCommandException("E_EXECUTION_FAILED", "Package root contains a file plan before its atomic effect barrier.");
                RequireFileExecutor(root.PackageOperation, args.ExecutorPid, args.ExecutorCapability);
                MarkPackageFileEffectStartedActive(root.RequestId, args.FilePlan, Text(root.PackageOperation, "operation"));
                return ProtocolEnvelopeFactory.Success(request.RequestId, ControlState(RequiredRoot(root.RequestId), args.ExecutorPid, args.ExecutorCapability));
            }

            private static object ProveFiles(BridgeRequest request)
            {
                var args = request.DeserializeArgs<ProveArgs>();
                ValidateProve(args);
                var root = RequiredRoot(args.RootRequestId);
                var operation = Text(root.PackageOperation, "operation");
                if (operation == "resolve") throw Schema("package.resolve has no TS file proof.");
                RequireLease(root.RequestId);
                RequireFileExecutor(root.PackageOperation, args.ExecutorPid, args.ExecutorCapability);
                var phase = Text(root.PackageOperation, "phase");
                if (phase != "files_committing")
                {
                    if (phase != "awaiting_files" && root.PackageOperation.ContainsKey("filesProof")) return ProtocolEnvelopeFactory.Success(request.RequestId, ControlState(root, args.ExecutorPid, args.ExecutorCapability));
                    throw new BridgeCommandException("E_EXECUTION_FAILED", "Package root is not awaiting file proof.");
                }
                VerifyFileProof(Text(root.PackageOperation, "operation"), args.ExpectedManifestSha256, args.FilesProof, args.PublicResultSeed);
                AdvancePackageOperation(root.RequestId, current =>
                {
                    current.PackageOperation["expectedManifestSha256"] = args.ExpectedManifestSha256;
                    current.PackageOperation["predicates"] = args.Predicates != null ? (object)args.Predicates : new List<Dictionary<string, object>>();
                    current.PackageOperation["filesProof"] = args.FilesProof;
                    current.PackageOperation["publicResultSeed"] = args.PublicResultSeed;
                    current.PackageOperation["phase"] = "files_proven";
                });
                return ProtocolEnvelopeFactory.Success(request.RequestId, ControlState(RequiredRoot(root.RequestId), args.ExecutorPid, args.ExecutorCapability));
            }

            private static object FailFiles(BridgeRequest request)
            {
                var args = request.DeserializeArgs<FailArgs>();
                if (args == null || !IsSafeId(args.RootRequestId) || args.ExecutorPid <= 0 || !IsSha256Text(args.ExecutorCapability)) throw Schema("rootRequestId, executorPid, and executorCapability are required.");
                var root = RequiredRoot(args.RootRequestId);
                var operation = Text(root.PackageOperation, "operation");
                var phase = Text(root.PackageOperation, "phase");
                if (phase != "awaiting_files" && phase != "files_committing") return ProtocolEnvelopeFactory.Success(request.RequestId, ControlState(root, args.ExecutorPid, args.ExecutorCapability));
                RequireLease(root.RequestId);
                RequireFileExecutor(root.PackageOperation, args.ExecutorPid, args.ExecutorCapability);
                if (PackageEffectStarted(root.RequestId)) TerminalTimeout(root, string.IsNullOrWhiteSpace(args.Message) ? "Package file mutation did not reach fresh proof." : args.Message);
                else TerminalError(root, string.IsNullOrWhiteSpace(args.Code) ? "E_EXECUTION_FAILED" : args.Code, string.IsNullOrWhiteSpace(args.Message) ? "Package file preparation failed." : args.Message, null, args.NextActions);
                return ProtocolEnvelopeFactory.Success(request.RequestId, ControlState(RequiredRoot(root.RequestId), args.ExecutorPid, args.ExecutorCapability));
            }

            private static object Status(BridgeRequest request)
            {
                var args = request.DeserializeArgs<StatusArgs>();
                if (args == null || !IsSafeId(args.RootRequestId) || args.ExecutorPid <= 0 || !IsSha256Text(args.ExecutorCapability)) throw Schema("rootRequestId, executorPid, and executorCapability are required.");
                return ProtocolEnvelopeFactory.Success(request.RequestId, ControlState(RequiredRoot(args.RootRequestId), args.ExecutorPid, args.ExecutorCapability));
            }

            private static Dictionary<string, object> ControlState(ActivePackageOperationRecord root, int callerPid = 0, string callerCapability = null)
            {
                var data = new Dictionary<string, object> {
                    { "accepted", true }, { "terminal", IsTerminal(root) }, { "phase", Text(root.PackageOperation, "phase") },
                    { "operation", Text(root.PackageOperation, "operation") }, { "rootInputSha256", UnityCliSerializer.Sha256Canonical(root.PackageOperation["input"]) },
                    { "fileExecutorAlive", FileExecutorAlive(root.PackageOperation) },
                    { "fileExecutorOwnedByCaller", callerPid > 0 && FileExecutorMatches(root.PackageOperation, callerPid, callerCapability) }
                };
                if (root.PackageOperation.ContainsKey("filePlan")) data["filePlan"] = root.PackageOperation["filePlan"];
                if (root.TerminalEnvelope != null) data["terminalEnvelope"] = root.TerminalEnvelope;
                return data;
            }

            private static object StartState(string requestId)
            {
                var root = RequiredRoot(requestId);
                var data = ControlState(root);
                data["requestId"] = requestId;
                return ProtocolEnvelopeFactory.Success(requestId, data);
            }

            private static string StartTerminal(string requestId, object terminalEnvelope)
            {
                return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(requestId, new Dictionary<string, object> { { "requestId", requestId }, { "accepted", true }, { "terminal", true }, { "terminalEnvelope", terminalEnvelope } }));
            }

            private sealed class StartArgs { public string ExpectedEditorSessionId { get; set; } public string Operation { get; set; } public Dictionary<string, object> Input { get; set; } }
            private sealed class StatusArgs { public string RootRequestId { get; set; } public int ExecutorPid { get; set; } public string ExecutorCapability { get; set; } }
            private sealed class FileOwnerArgs { public string RootRequestId { get; set; } public int ExecutorPid { get; set; } public string ExecutorCapability { get; set; } }
            private static bool FileExecutorAlive(Dictionary<string, object> operation)
            {
                object pidValue; object identityValue;
                if (operation == null || !operation.TryGetValue("fileExecutorPid", out pidValue) || !operation.TryGetValue("fileExecutorStartIdentity", out identityValue)) return false;
                var pid = Convert.ToInt32(pidValue);
                return string.Equals(ProcessIdentity(pid), identityValue as string, StringComparison.Ordinal);
            }
            /// <summary>PID、Windows start identity 与高熵 capability 必须同时等于 durable owner。</summary>
            private static bool FileExecutorMatches(Dictionary<string, object> operation, int pid, string capability)
            {
                object pidValue; object identityValue; object capabilityHashValue;
                return operation != null && IsSha256Text(capability)
                    && operation.TryGetValue("fileExecutorPid", out pidValue) && Convert.ToInt32(pidValue) == pid
                    && operation.TryGetValue("fileExecutorStartIdentity", out identityValue)
                    && string.Equals(ProcessIdentity(pid), identityValue as string, StringComparison.Ordinal)
                    && operation.TryGetValue("fileExecutorCapabilitySha256", out capabilityHashValue)
                    && string.Equals(Sha256Hex(Encoding.UTF8.GetBytes(capability)), capabilityHashValue as string, StringComparison.Ordinal);
            }
            /// <summary>校验 file control 的不可伪造 owner capability，拒绝其他本地进程代为证明或裁决。</summary>
            private static void RequireFileExecutor(Dictionary<string, object> operation, int pid, string capability)
            {
                if (pid <= 0 || !FileExecutorMatches(operation, pid, capability)) throw new BridgeCommandException("E_PACKAGE_FILE_EXECUTOR_MISMATCH", "Package file control is not owned by the current executor process.");
            }
            /// <summary>仅在旧 executor 已证明不存活后，把 recovery PID、identity 与 capability hash 原子写回 root。</summary>
            private static void BindFileExecutor(string requestId, int pid, string capability)
            {
                if (!IsSha256Text(capability)) throw Schema("executorCapability must be 256-bit lowercase hex.");
                var identity = RequireProcessIdentity(pid);
                var capabilityHash = Sha256Hex(Encoding.UTF8.GetBytes(capability));
                AdvancePackageOperation(requestId, current =>
                {
                    current.PackageOperation["fileExecutorPid"] = (long)pid;
                    current.PackageOperation["fileExecutorStartIdentity"] = identity;
                    current.PackageOperation["fileExecutorCapabilitySha256"] = capabilityHash;
                });
            }
            /// <summary>读取 fail-closed Windows process identity，无法证明时禁止推进 root。</summary>
            private static string RequireProcessIdentity(int pid)
            {
                var identity = ProcessIdentity(pid);
                if (identity == null) throw new BridgeCommandException("E_EXECUTION_FAILED", "Package file executor identity could not be verified before changing root state.");
                return identity;
            }
            /// <summary>file plan 必须逐字段绑定 root 的公开输入，不能由 private control 换成另一项业务 mutation。</summary>
            private static void VerifyFilePlanBinding(string operation, Dictionary<string, object> rootInput, Dictionary<string, object> filePlan)
            {
                var matches = rootInput != null && filePlan != null;
                if (matches && operation == "install")
                    matches = Same(Text(rootInput, "name"), Text(filePlan, "name")) && Same(Text(rootInput, "version"), Text(filePlan, "version"));
                else if (matches && operation == "remove")
                    matches = Same(Text(rootInput, "name"), Text(filePlan, "name")) && Bool(rootInput, "force") == Bool(filePlan, "force");
                else if (matches && operation == "deploy")
                    matches = Same(Text(rootInput, "source"), Text(filePlan, "requestedSource"))
                        && OptionalSame(Text(rootInput, "packageName"), Text(filePlan, "requestedPackageName"));
                else if (matches && operation == "restore")
                    matches = Same(Text(rootInput, "packageName"), Text(filePlan, "packageName"));
                else matches = false;
                if (!matches) throw new BridgeCommandException("E_EXECUTION_FAILED", "Package filePlan is not bound to the accepted public root input.");
            }

            /// <summary>可选字符串只有双 null 或双非空精确相等时才匹配。</summary>
            private static bool OptionalSame(string left, string right)
            {
                return left == null ? right == null : Same(left, right);
            }

            private static string ProcessIdentity(int pid)
            {
                try { using (var process = Process.GetProcessById(pid)) return "win32:" + process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture); }
                catch { return null; }
            }

            private sealed class CommitArgs { public string RootRequestId { get; set; } public Dictionary<string, object> FilePlan { get; set; } public int ExecutorPid { get; set; } public string ExecutorCapability { get; set; } }
            private sealed class ProveArgs { public string RootRequestId { get; set; } public int ExecutorPid { get; set; } public string ExecutorCapability { get; set; } public string ExpectedManifestSha256 { get; set; } public List<Dictionary<string, object>> Predicates { get; set; } public Dictionary<string, object> FilesProof { get; set; } public Dictionary<string, object> PublicResultSeed { get; set; } }
            private sealed class FailArgs { public string RootRequestId { get; set; } public int ExecutorPid { get; set; } public string ExecutorCapability { get; set; } public string Code { get; set; } public string Message { get; set; } public string[] NextActions { get; set; } }
        }
    }
}
#endif
