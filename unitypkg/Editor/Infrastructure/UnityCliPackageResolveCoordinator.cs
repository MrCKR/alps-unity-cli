#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
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
            private const string ReloadEpochScope = "package-reload-epoch";
            private const string ReloadEpochSessionKey = "UnityCli.Package.ReloadEpoch";
            private static readonly Dictionary<string, ListRequest> ActiveLists = new Dictionary<string, ListRequest>(StringComparer.Ordinal);
            private static readonly Coordinator operation = new Coordinator();
            internal static Action<string> PackageStartTestBoundary;
            internal static Action<string> PackageResolveEffectTestBoundary;

            private static void HitPackageStartTestBoundary(string name)
            {
                var boundary = PackageStartTestBoundary;
                if (boundary != null) boundary(name);
            }

            private static void HitPackageResolveEffectTestBoundary(string name)
            {
                var boundary = PackageResolveEffectTestBoundary;
                if (boundary != null) boundary(name);
            }

            private sealed class Coordinator : UnityCliReloadOperation
            {
                protected override string Scope { get { return ActiveScope; } }
                protected override string StartCommand { get { return Command; } }
                protected override string StatusCommand { get { return UnityCliPackageResolveCoordinator.StatusCommand; } }
                protected override string OperationDisplayName { get { return "Package root"; } }
                protected override IEnumerable<(string name, bool destructive, UnityCliBridgeQueuePolicy.WorkClass queueClass)> AdditionalCommands
                {
                    get
                    {
                        yield return (ClaimFilesCommand, true, UnityCliBridgeQueuePolicy.WorkClass.Mutation);
                        yield return (CommitFilesCommand, true, UnityCliBridgeQueuePolicy.WorkClass.Mutation);
                        yield return (ProveFilesCommand, true, UnityCliBridgeQueuePolicy.WorkClass.Mutation);
                        yield return (FailFilesCommand, true, UnityCliBridgeQueuePolicy.WorkClass.Mutation);
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

            internal static void Initialize() { ActiveLists.Clear(); EnsureReloadEpoch(); RecoverTerminalCleanup(); }
            internal static void Shutdown() { ActiveLists.Clear(); }
            internal static void Tick() { RecoverTerminalCleanup(); operation.Tick(); }

            internal static void BeforeReload()
            {
                RotateReloadEpoch();
                ActiveLists.Clear();
                foreach (var root in OpenRoots())
                {
                    var phase = Text(root.PackageOperation, "phase");
                    if (phase == "awaiting_files" || phase == "files_committing")
                    {
                        try
                        {
                            UnityCliActiveOperation.Save("package-file-revoke", new Dictionary<string, object>
                            {
                                { "requestId", root.RequestId }, { "requestDigest", root.RequestDigest },
                                { "editorSessionId", root.EditorSessionId }, { "revokedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) }
                            });
                        }
                        // marker写失败仍必须先冻结terminal，reload后不得接受Worker proof。
                        catch { }
                        TerminalTimeout(root, "Domain reload revoked the Package file executor; file effect outcome is unknown.");
                        continue;
                    }
                    if (phase == "files_proven")
                    {
                        TerminalTimeout(root, "Domain reload occurred after Package file proof but before Resolve dispatch; continuation is forbidden.");
                        continue;
                    }
                    if (phase == "resolve_dispatching")
                    {
                        TerminalTimeout(root, "Domain reload interrupted PackageManager.Resolve dispatch before its return was persisted.");
                        continue;
                    }
                    AdvancePackageOperation(root.RequestId, current =>
                    {
                        current.PackageOperation["reloadObserved"] = true;
                        var currentPhase = Text(current.PackageOperation, "phase");
                        if (currentPhase == "resolve_returned" || currentPhase == "list_pending" || currentPhase == "list_succeeded") current.PackageOperation["phase"] = "reload_observed";
                        current.PackageOperation["reloadEpoch"] = CurrentReloadEpoch();
                    });
                }
            }

            internal static async Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken cancellationToken)
            {
                return await operation.Execute(request, raw, accept, cancellationToken);
            }

            private static async Task<string> ExecuteStart(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken cancellationToken)
            {
                var leaseHeld = false;
                var newlyAcquiredLease = false;
                var requestDigest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
                try
                {
                    var args = request.DeserializeArgs<StartArgs>();
                    ValidateStart(args);
                    object cachedTerminal;
                    if (UnityCliActiveOperation.TryGetTerminal(ActiveScope, request.RequestId, requestDigest, out cachedTerminal))
                    {
                        try { UnityCliActiveOperation.BeginExactTerminalCleanup(ActiveScope, request.RequestId, requestDigest); } catch { }
                        return StartTerminal(request.RequestId, cachedTerminal);
                    }
                    if (!UnityCliSessionMutationLease.TryAcquire(request.RequestId, requestDigest, out newlyAcquiredLease)) return BusyEnvelope(request.RequestId);
                    leaseHeld = true;
                    PackageActiveDecision decision;
                    UnityCliToolCompletionContract contract;
                    if (!UnityCliToolCompletionRegistry.TryGetPackageRoot(args.Operation, out contract)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Unsupported package root operation.");
                    decision = AcceptPackageActiveOperation(request, raw, contract, newlyAcquiredLease,
                        () => { if (accept == null || !accept()) throw new OperationCanceledException(); },
                        () => ValidateStart(args), root => InitializeRoot(root, args));
                    HitPackageStartTestBoundary("after-package-root-created");
                    if (decision.TerminalEnvelope != null)
                    {
                        UnityCliSessionMutationLease.Release(request.RequestId, requestDigest);
                        return StartTerminal(request.RequestId, decision.TerminalEnvelope);
                    }
                    if (!decision.Execute)
                    {
                        if (newlyAcquiredLease) UnityCliSessionMutationLease.Release(request.RequestId, requestDigest);
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Package active operation did not authorize recovery.");
                    }
                    await Task.FromResult(0);
                    return UnityCliSerializer.Serialize(StartState(request.RequestId));
                }
                catch (BridgeCommandException exception) { return FinishStartFault(request, requestDigest, leaseHeld, newlyAcquiredLease, exception); }
                catch (Exception exception) { return FinishStartFault(request, requestDigest, leaseHeld, newlyAcquiredLease, exception); }
            }

            private static string FinishStartFault(BridgeRequest request, string requestDigest, bool leaseHeld, bool newlyAcquiredLease, Exception exception)
            {
                var normalized = NormalizePackageStartFault(exception);
                if (!leaseHeld) return SerializeBridgeExceptionEnvelope(request.RequestId, normalized);
                ActivePackageOperationRecord root;
                try { root = LoadPackageActiveOperation(); }
                catch
                {
                    RecoverCorruptPackageRoot();
                    return SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Package root fault cleanup remains pending because the exact active state is unreadable."));
                }
                if (root != null)
                {
                    if (!string.Equals(root.RequestId, request.RequestId, StringComparison.Ordinal)
                        || !string.Equals(root.RequestDigest, requestDigest, StringComparison.Ordinal))
                        return SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Package root fault conflicts with another exact active identity; cleanup is blocked."));
                    try
                    {
                        object first;
                        if (IsTerminal(root)) first = root.TerminalEnvelope;
                        else if (root.StartedAt.HasValue || root.Phase == "effect_started" || root.Phase == "effect_proven")
                        {
                            TerminalTimeout(root, "Package operation failed after its effect barrier: " + exception.GetType().Name + ".");
                            first = RequiredRoot(request.RequestId).TerminalEnvelope;
                        }
                        else
                        {
                            var terminal = PackageStartFaultTerminal(request.RequestId, exception);
                            first = PersistPackageTerminal(request.RequestId, terminal, "failed");
                        }
                        return StartTerminal(request.RequestId, first);
                    }
                    catch
                    {
                        RecoverCorruptPackageRoot();
                        return SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Package first-terminal cleanup remains pending after a root-created fault."));
                    }
                }
                if (!newlyAcquiredLease)
                {
                    RetainPackageAdmissionRecovery(request.RequestId, requestDigest);
                    return SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Package has an exact pre-existing lease without a readable root; cleanup is blocked."));
                }
                try { UnityCliSessionMutationLease.Release(request.RequestId, requestDigest); } catch { }
                bool isOwned;
                if (UnityCliSessionMutationLease.TryIsOwnedBy(request.RequestId, requestDigest, out isOwned) && !isOwned)
                    return SerializeBridgeExceptionEnvelope(request.RequestId, normalized);
                try { UnityCliActiveOperation.BeginExactTerminalCleanup(ActiveScope, request.RequestId, requestDigest); } catch { }
                return SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Package was rejected before its durable root, but exact lease cleanup remains pending."));
            }

            private static void RetainPackageAdmissionRecovery(string requestId, string requestDigest)
            {
                try
                {
                    var owner = CaptureAdmissionOwner(requestId, requestDigest);
                    if (owner.OwnerState != UnityCliAdmissionPool.AdmissionOwnerState.WorkItem) return;
                    PersistAdmissionOwnerTransfer(ActiveScope + "-admission-owner", new Dictionary<string, object>
                    {
                        { "requestId", requestId }, { "requestDigest", requestDigest }, { "editorSessionId", EditorSessionId() }
                    }, owner, UnityCliAdmissionPool.AdmissionOwnerState.Recovery);
                }
                catch { }
            }

            private static object PackageStartFaultTerminal(string requestId, Exception exception)
            {
                var bridge = exception as BridgeCommandException;
                if (bridge != null)
                    return ProtocolEnvelopeFactory.Error(requestId, bridge.Code, bridge.Message, bridge.Details, bridge.NextActions, bridge.Transient, bridge.RetryAfterMs);
                if (exception is OperationCanceledException)
                    return ProtocolEnvelopeFactory.Error(requestId, "E_EXECUTION_TIMEOUT", "Package operation did not begin before the request deadline.");
                return ProtocolEnvelopeFactory.Error(requestId, "E_BRIDGE_COMMAND_FAILED", exception.Message, null, new[] { "Check Unity Console and UnityCliBridge.log." });
            }

            private static BridgeCommandException NormalizePackageStartFault(Exception exception)
            {
                return exception is BridgeCommandException
                    ? (BridgeCommandException)exception
                    : exception is OperationCanceledException
                        ? new BridgeCommandException("E_EXECUTION_TIMEOUT", "Package operation did not begin before the request deadline.")
                        : new BridgeCommandException("E_BRIDGE_COMMAND_FAILED", exception.Message, "Check Unity Console and UnityCliBridge.log.");
            }

            private static string CurrentReloadEpoch()
            {
                var epoch = SessionState.GetString(ReloadEpochSessionKey, string.Empty);
                if (string.IsNullOrEmpty(epoch)) { epoch = Guid.NewGuid().ToString("N"); SessionState.SetString(ReloadEpochSessionKey, epoch); }
                return epoch;
            }

            private static void EnsureReloadEpoch()
            {
                var epoch = CurrentReloadEpoch();
                try
                {
                    UnityCliActiveOperation.Save(ReloadEpochScope, new Dictionary<string, object>
                    {
                        { "editorSessionId", EditorSessionId() }, { "epoch", epoch }
                    });
                }
                catch { }
            }

            private static void RotateReloadEpoch()
            {
                var epoch = Guid.NewGuid().ToString("N");
                SessionState.SetString(ReloadEpochSessionKey, epoch);
                try { UnityCliActiveOperation.Clear(ReloadEpochScope); } catch { }
                EnsureReloadEpoch();
            }

            private static void RecoverTerminalCleanup()
            {
                try
                {
                    if (UnityCliActiveOperation.RetryExactTerminalCleanup(ActiveScope)) return;
                    var root = LoadPackageActiveOperation();
                    if (root == null) return;
                    object terminal;
                    if (!UnityCliActiveOperation.TryGetTerminal(ActiveScope, root.RequestId, root.RequestDigest, out terminal)
                        && root.TerminalEnvelope != null)
                    {
                        var selector = UnityCliSerializer.Sha256Canonical(root.PackageOperation["input"]);
                        terminal = UnityCliActiveOperation.StoreTerminalFirst(ActiveScope, root.RequestId, root.RequestDigest, selector, root.TerminalEnvelope);
                    }
                    if (terminal != null) UnityCliActiveOperation.BeginExactTerminalCleanup(ActiveScope, root.RequestId, root.RequestDigest);
                }
                catch { RecoverCorruptPackageRoot(); }
            }

            private static void RecoverCorruptPackageRoot()
            {
                try
                {
                    string requestId;
                    string requestDigest;
                    if (!UnityCliSessionMutationLease.TryGetOnlyActiveScopeOwner(ActiveScope, out requestId, out requestDigest)) return;
                    var envelope = ProtocolEnvelopeFactory.Error(requestId, "E_EXECUTION_TIMEOUT", "Package active state is corrupt or unreadable; file and Resolve outcome is unknown.", null, new[] { "Inspect Package Manager and package files before submitting another mutation." });
                    UnityCliActiveOperation.StoreTerminalFirst(ActiveScope, requestId, requestDigest, envelope);
                    UnityCliActiveOperation.BeginExactTerminalCleanup(ActiveScope, requestId, requestDigest);
                }
                catch { }
            }

            /// <summary>effect 前先持久化唯一 file executor；只有旧 owner 已死亡时才允许 recovery 重绑。</summary>
            private static object ClaimFiles(BridgeRequest request)
            {
                var args = request.DeserializeArgs<FileOwnerArgs>();
                if (args == null || !IsSafeId(args.RootRequestId) || args.ExecutorPid <= 0 || !IsSha256Text(args.ExecutorCapability)) throw Schema("rootRequestId, executorPid, and executorCapability are required.");
                var root = RequiredRoot(args.RootRequestId);
                var operation = Text(root.PackageOperation, "operation");
                if (operation == "resolve") throw Schema("package.resolve has no TS file executor.");
                RequireLease(root);
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
                RequireLease(root);
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
                RequireLease(root);
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
                RequireLease(root);
                RequireFileExecutor(root.PackageOperation, args.ExecutorPid, args.ExecutorCapability);
                if (PackageEffectStarted(root.RequestId)) TerminalTimeout(root, string.IsNullOrWhiteSpace(args.Message) ? "Package file mutation did not reach fresh proof." : args.Message);
                else TerminalError(root, string.IsNullOrWhiteSpace(args.Code) ? "E_EXECUTION_FAILED" : args.Code, string.IsNullOrWhiteSpace(args.Message) ? "Package file preparation failed." : args.Message, null, args.NextActions);
                return ProtocolEnvelopeFactory.Success(request.RequestId, ControlState(RequiredRoot(root.RequestId), args.ExecutorPid, args.ExecutorCapability));
            }

            private static object Status(BridgeRequest request)
            {
                var args = request.DeserializeArgs<StatusArgs>();
                if (args == null || !IsSafeId(args.RootRequestId) || !IsSha256Text(args.RootInputSha256)
                    || (!string.IsNullOrEmpty(args.RootRequestDigest) && !IsSha256Text(args.RootRequestDigest))
                    || args.ExecutorPid <= 0 || !IsSha256Text(args.ExecutorCapability)) throw Schema("rootRequestId, rootInputSha256, executorPid, and executorCapability are required.");
                object cached;
                var found = !string.IsNullOrEmpty(args.RootRequestDigest)
                    ? UnityCliActiveOperation.TryGetTerminal(ActiveScope, args.RootRequestId, args.RootRequestDigest, out cached)
                    : UnityCliActiveOperation.TryGetTerminalBySelector(ActiveScope, args.RootRequestId, args.RootInputSha256, out cached);
                if (found) return ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object>
                {
                    { "accepted", true }, { "terminal", true }, { "rootInputSha256", args.RootInputSha256 }, { "terminalEnvelope", cached }
                });
                var root = RequiredRoot(args.RootRequestId);
                if (!string.Equals(UnityCliSerializer.Sha256Canonical(root.PackageOperation["input"]), args.RootInputSha256, StringComparison.Ordinal)
                    || (!string.IsNullOrEmpty(args.RootRequestDigest) && !string.Equals(root.RequestDigest, args.RootRequestDigest, StringComparison.Ordinal)))
                    throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Package requestId was reused with different input.");
                return ProtocolEnvelopeFactory.Success(request.RequestId, ControlState(root, args.ExecutorPid, args.ExecutorCapability));
            }

            private static Dictionary<string, object> ControlState(ActivePackageOperationRecord root, int callerPid = 0, string callerCapability = null)
            {
                var data = new Dictionary<string, object> {
                    { "accepted", true }, { "terminal", IsTerminal(root) }, { "phase", Text(root.PackageOperation, "phase") }, { "rootRequestDigest", root.RequestDigest },
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
            private sealed class StatusArgs { public string RootRequestId { get; set; } public string RootRequestDigest { get; set; } public string RootInputSha256 { get; set; } public int ExecutorPid { get; set; } public string ExecutorCapability { get; set; } }
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
