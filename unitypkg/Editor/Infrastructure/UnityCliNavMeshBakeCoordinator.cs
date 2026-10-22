#if UNITY_EDITOR
#pragma warning disable 0618
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.AI;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using NavMeshBuilder = UnityEditor.AI.NavMeshBuilder;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>legacy 与 AI Navigation surface bake 共用的单 root durable coordinator。</summary>
        internal static class UnityCliNavMeshBakeCoordinator
        {
            internal const string StartCommand = "internal.navmesh.bake.start";
            internal const string StatusCommand = "internal.navmesh.bake.status";
            internal const string PlanCommand = "internal.navmesh.bake.plan";
            internal const string SnapshotCommand = "internal.navmesh.bake.snapshot";
            internal const string CancelCommand = "internal.navmesh.bake.cancel";
            private const string ActiveScope = "navmesh-bake";
            private const string StatusScope = "navmesh-bake-status";
            private const string TerminalLedgerScope = "navmesh-bake-terminal-ledger";
            private const int TerminalLedgerCapacity = 128;
            // terminal 只在原调用的重投窗口内保留；窗口关闭后不再占容量。
            private const long TerminalLedgerRetentionGraceMs = 5 * 60 * 1000;
            private const long TerminalLedgerFallbackWindowMs = 60 * 60 * 1000;
            private const string StatusSessionInitializedKey = "UnityCli.NavMeshBake.StatusSessionInitialized";
            private const string StatusWrittenThisSessionKey = "UnityCli.NavMeshBake.StatusWrittenThisSession";
            private const string TerminalLedgerWrittenThisSessionKey = "UnityCli.NavMeshBake.TerminalLedgerWrittenThisSession";
            private const string StatusProofKeySessionKey = "UnityCli.NavMeshBake.StatusProofKey";
            private const string OwnerRequestIdSessionKey = "UnityCli.NavMeshBake.OwnerRequestId";
            private const string OwnerRequestDigestSessionKey = "UnityCli.NavMeshBake.OwnerRequestDigest";
            private const string OwnerKindSessionKey = "UnityCli.NavMeshBake.OwnerKind";
            private const string PreEffectAbortRequestIdSessionKey = "UnityCli.NavMeshBake.PreEffectAbortRequestId";
            private const string PreEffectAbortRequestDigestSessionKey = "UnityCli.NavMeshBake.PreEffectAbortRequestDigest";
            private static readonly Coordinator Operation = new Coordinator();
            private enum StatusLoadOutcome { Missing, Loaded, TransientFailure, Corrupt }
            private static StatusLoadOutcome LastStatusLoadOutcome;
            private static bool StatusJournalCorrupt;

            private sealed class StartArgs
            {
                public string ExpectedEditorSessionId { get; set; }
                public string Kind { get; set; }
            }

            private sealed class CancelArgs
            {
                public string OperationId { get; set; }
            }

            private sealed class RootStatusArgs { public string RootRequestId { get; set; } public string RootRequestDigest { get; set; } }

            private sealed class Coordinator : UnityCliReloadOperation
            {
                private Dictionary<string, object> rejectedRoot;
                private SurfaceApi.SurfaceDispatchObservation surfaceObservation;
                private string surfaceObservationRequestId;
                private string surfaceObservationRequestDigest;
                private string reloadUncertainRequestId;
                private string reloadUncertainRequestDigest;
                protected override string Scope { get { return ActiveScope; } }
                protected override string StartCommand { get { return UnityCliNavMeshBakeCoordinator.StartCommand; } }
                protected override string StatusCommand { get { return UnityCliNavMeshBakeCoordinator.StatusCommand; } }
                protected override string OperationDisplayName { get { return "NavMesh bake"; } }
                protected override int CommandTimeoutMs { get { return 600000; } }
                protected override bool IncludeAcceptedInStatus { get { return true; } }
                protected override IEnumerable<(string name, bool destructive, UnityCliBridgeQueuePolicy.WorkClass queueClass)> AdditionalCommands
                {
                    get
                    {
                        yield return (PlanCommand, false, UnityCliBridgeQueuePolicy.WorkClass.Mutation);
                        yield return (SnapshotCommand, false, UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead);
                        yield return (CancelCommand, true, UnityCliBridgeQueuePolicy.WorkClass.Control);
                    }
                }

                protected override object DeserializeStartArgs(BridgeRequest request) { return request.DeserializeArgs<StartArgs>(); }
                protected override void ValidateStart(object value)
                {
                    var args = value as StartArgs;
                    ValidateStartShape(args);
                    if (!string.Equals(args.ExpectedEditorSessionId, EditorSessionId(), StringComparison.Ordinal))
                        throw new BridgeCommandException("E_EDITOR_SESSION_REPLACED", "The selected Editor session changed after NavMesh bake preflight.");
                    NavMeshToolSupport.EnsureEditorReady(args.Kind == "legacy" ? "navmesh.bake" : "navmesh.surface.bake", true);
                }

                private static void ValidateStartShape(StartArgs args)
                {
                    if (args == null || string.IsNullOrWhiteSpace(args.ExpectedEditorSessionId) || (args.Kind != "legacy" && args.Kind != "surfaces"))
                        throw new BridgeCommandException("E_SCHEMA_INVALID", "NavMesh bake requires expectedEditorSessionId and kind=legacy|surfaces.");
                }

                /// <summary>语法可读但identity、phase、status或terminal proof不一致的root必须进入保守恢复。</summary>
                protected override void ValidateLoadedRoot(Dictionary<string, object> root)
                {
                    if (!ValidateNavMeshRoot(root))
                    {
                        rejectedRoot = root;
                        throw new InvalidOperationException("NavMesh active root semantic validation failed.");
                    }
                    rejectedRoot = null;
                }

                protected override TerminalCleanupDecision EvaluateTerminalCleanupRoot(Dictionary<string, object> root, string requestId, string requestDigest)
                {
                    if (Text(root, "phase") != "queued" && NativeGateRunning(root)) return TerminalCleanupDecision.Retry;
                    var hasTerminalState = !string.IsNullOrEmpty(Text(root, "terminalState"));
                    if (hasTerminalState)
                    {
                        try { if (TryRestoreTerminalStatus(root)) return TerminalCleanupDecision.Cleanup; }
                        catch (Exception exception) { WriteLog("navmesh_terminal_restore_retry " + exception.Message); }
                    }
                    var status = LoadStatus();
                    if (status == null) return hasTerminalState || LastStatusLoadOutcome == StatusLoadOutcome.TransientFailure
                        ? TerminalCleanupDecision.Retry : TerminalCleanupDecision.Reject;
                    return TerminalStatusMatchesIdentity(status, requestId, requestDigest)
                        ? TerminalCleanupDecision.Cleanup : TerminalCleanupDecision.Reject;
                }

                protected override TerminalCleanupDecision EvaluateUnreadableTerminalCleanup(string requestId, string requestDigest)
                {
                    var status = LoadStatus();
                    if (status == null) return TerminalCleanupDecision.Retry;
                    return TerminalStatusMatchesIdentity(status, requestId, requestDigest)
                        ? TerminalCleanupDecision.Cleanup : TerminalCleanupDecision.Retry;
                }

                protected override bool CanCleanupAbandonedPreEffectRoot(Dictionary<string, object> root, string requestId, string requestDigest)
                {
                    if (root == null || !IsBoundPreEffectAbort(requestId, requestDigest)
                        || !string.Equals(Text(root, "requestId"), requestId, StringComparison.Ordinal)
                        || !string.Equals(Text(root, "requestDigest"), requestDigest, StringComparison.Ordinal)
                        || Bool(root, "dispatchConfirmed") || !string.IsNullOrEmpty(Text(root, "terminalState"))) return false;
                    var phase = Text(root, "phase");
                    var nonce = Text(root, "dispatchNonce");
                    return (phase == "queued" && string.IsNullOrEmpty(nonce))
                        || (phase == "dispatching" && !string.IsNullOrEmpty(nonce));
                }

                /// <summary>effect前root删除后只清理同identity且明确未dispatch的active status。</summary>
                protected override bool FinalizeAbandonedPreEffectState(string requestId, string requestDigest)
                {
                    var status = LoadStatus();
                    if (status == null)
                        return LastStatusLoadOutcome == StatusLoadOutcome.Missing && !StatusJournalCorrupt
                            && !SessionState.GetBool(StatusWrittenThisSessionKey, false)
                            && !UnityCliActiveOperation.Exists(StatusScope);
                    object confirmed;
                    if (!string.Equals(Text(status, "rootRequestId"), requestId, StringComparison.Ordinal)
                        || !string.Equals(Text(status, "rootRequestDigest"), requestDigest, StringComparison.Ordinal)
                        || !Bool(status, "active") || !status.TryGetValue("dispatchConfirmed", out confirmed)
                        || !(confirmed is bool) || (bool)confirmed) return false;
                    try { UnityCliActiveOperation.Clear(StatusScope); }
                    catch (Exception exception) { WriteLog("navmesh_pre_effect_status_cleanup_failed " + exception.Message); return false; }
                    if (UnityCliActiveOperation.Exists(StatusScope)) return false;
                    StatusJournalCorrupt = false;
                    LastStatusLoadOutcome = StatusLoadOutcome.Missing;
                    SessionState.SetBool(StatusWrittenThisSessionKey, false);
                    return true;
                }

                protected override bool TryResumePublishedTerminal(Dictionary<string, object> root)
                {
                    if (!string.IsNullOrEmpty(Text(root, "terminalState")))
                    {
                        if (TryRestoreTerminalStatus(root)) ResumeTerminalCleanup(root);
                        return true;
                    }
                    var terminal = RecoveredStoredTerminal(Text(root, "requestId"), Text(root, "requestDigest"));
                    if (terminal == null) return false;
                    ResumeTerminalCleanup(root);
                    return true;
                }

                protected override bool OnActiveStateCorrupt(Exception exception) { return RecoverUnavailableActiveState(exception); }
                protected override bool OnActiveStateMissing() { return RecoverUnavailableActiveState(new InvalidOperationException("NavMesh active state is missing.")); }

                protected override object Planned(object value) { return DryRun((StartArgs)value, BuildPlan((StartArgs)value)); }

                protected override Dictionary<string, object> CreateRoot(BridgeRequest request, string digest, object value)
                {
                    return CreateRoot(request, digest, (StartArgs)value, BuildPlan((StartArgs)value));
                }

                /// <summary>Start先解析durable identity，再在accept barrier后创建唯一root；历史不确定时禁止新effect。</summary>
                internal override async Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken cancellationToken)
                {
                    if (request.Command != StartCommand) return await base.Execute(request, raw, accept, cancellationToken);
                    var args = (StartArgs)DeserializeStartArgs(request);
                    ValidateStartShape(args);
                    if (request.Execution != null && request.Execution.DryRun == true)
                    {
                        ValidateStart(args);
                        return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, DryRun(args, BuildPlan(args))));
                    }
                    var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
                    var inspectedRoot = Load();
                    var recovered = RecoveredLedgerTerminal(request.RequestId, digest)
                        ?? RecoveredStoredTerminal(request.RequestId, digest) ?? RecoveredTerminal(request.RequestId, digest);
                    if (recovered != null) return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, recovered));

                    var replay = inspectedRoot != null;
                    if (inspectedRoot != null)
                    {
                        if (!string.Equals(Text(inspectedRoot, "requestId"), request.RequestId, StringComparison.Ordinal)) return Busy(request.RequestId);
                        if (!string.Equals(Text(inspectedRoot, "requestDigest"), digest, StringComparison.Ordinal))
                            throw new BridgeCommandException("E_REQUEST_ID_REUSED", "NavMesh bake requestId was reused with different input.");
                        if (!UnityCliSessionMutationLease.IsOwnedBy(request.RequestId, digest))
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh active root is not owned by the current session lease; outcome is unknown.");
                        BindSessionOwner(request.RequestId, digest, Text(inspectedRoot, "kind"));
                    }
                    else
                    {
                        if (LastLoadWasCorrupt || LastLoadRecoveredTerminal || StatusHistoryUnavailable())
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh active history is unavailable; outcome is unknown.");
                        string ownerId;
                        string ownerDigest;
                        if (!UnityCliSessionMutationLease.TryGetCurrentOwner(out ownerId, out ownerDigest))
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh lease ownership cannot be read; outcome is unknown.");
                        if (!string.IsNullOrEmpty(ownerId))
                        {
                            if (!string.Equals(ownerId, request.RequestId, StringComparison.Ordinal)) return Busy(request.RequestId);
                            if (!string.Equals(ownerDigest, digest, StringComparison.Ordinal))
                                throw new BridgeCommandException("E_REQUEST_ID_REUSED", "NavMesh bake requestId was reused with different input.");
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh lease exists without a readable root; outcome is unknown.");
                        }
                        ValidateStart(args);
                        EnsureTerminalLedgerCapacity();
                        CreateFirst(request, digest, args, BuildPlan(args), accept);
                    }
                    var acceptedRoot = Load();
                    if (acceptedRoot == null || !string.Equals(Text(acceptedRoot, "requestId"), request.RequestId, StringComparison.Ordinal)
                        || !string.Equals(Text(acceptedRoot, "requestDigest"), digest, StringComparison.Ordinal))
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh acceptance root could not be confirmed; outcome is unknown.");
                    await Task.FromResult(0);
                    return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object>
                    {
                        { "terminal", false }, { "accepted", true }, { "operationId", Text(acceptedRoot, "operationId") }, { "rootRequestDigest", digest }, { "replayed", replay }
                    }));
                }

                /// <summary>仅在确认无root、无status缺口且无owner后跨accept barrier创建首次root。</summary>
                private void CreateFirst(BridgeRequest request, string digest, StartArgs args, Dictionary<string, object> plan, Func<bool> accept)
                {
                    bool acquired;
                    if (!UnityCliSessionMutationLease.TryAcquire(request.RequestId, digest, out acquired))
                        throw new BridgeCommandException("E_UNITY_BUSY", "Another Unity mutation owns the active session.");
                    var saved = false;
                    try
                    {
                        var candidate = CreateRoot(request, digest, args, plan);
                        if (accept == null || !accept()) throw new OperationCanceledException("HTTP timeout cancelled NavMesh bake before acceptance.");
                        BindSessionOwner(request.RequestId, digest, args.Kind);
                        Save(candidate);
                        saved = true;
                        try { WriteRunningStatus(candidate); }
                        catch (Exception exception)
                        {
                            AbortBeforeNativeEffect(candidate, "initial running status could not be stored: " + exception.GetType().Name);
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh bake was aborted before the native effect because its initial status could not be stored.");
                        }
                    }
                    catch
                    {
                        if (acquired && !saved) UnityCliSessionMutationLease.Release(request.RequestId, digest);
                        throw;
                    }
                }

                /// <summary>status优先返回same-session durable terminal，不从active root重造结果。</summary>
                protected override object Status(BridgeRequest request)
                {
                    var args = request.DeserializeArgs<RootStatusArgs>();
                    if (args == null || string.IsNullOrWhiteSpace(args.RootRequestId) || !IsSha256Text(args.RootRequestDigest))
                        throw new BridgeCommandException("E_SCHEMA_INVALID", "NavMesh bake status requires rootRequestId and rootRequestDigest.");
                    var ledgerTerminal = RecoveredLedgerTerminal(args.RootRequestId, args.RootRequestDigest);
                    if (ledgerTerminal != null) return ProtocolEnvelopeFactory.Success(request.RequestId, ledgerTerminal);
                    var root = Load();
                    if (root != null && string.Equals(Text(root, "requestId"), args.RootRequestId, StringComparison.Ordinal))
                    {
                        if (!string.Equals(Text(root, "requestDigest"), args.RootRequestDigest, StringComparison.Ordinal))
                            throw new BridgeCommandException("E_REQUEST_ID_REUSED", "NavMesh bake requestId was reused with different input.");
                        var terminal = RecoveredStoredTerminal(args.RootRequestId, args.RootRequestDigest);
                        if (terminal != null) return ProtocolEnvelopeFactory.Success(request.RequestId, terminal);
                        return ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object>
                        {
                            { "terminal", false }, { "accepted", true }, { "phase", Text(root, "phase") }, { "rootRequestDigest", args.RootRequestDigest }
                        });
                    }
                    var stored = RecoveredStoredTerminal(args.RootRequestId, args.RootRequestDigest) ?? RecoveredTerminal(args.RootRequestId, args.RootRequestDigest);
                    if (stored != null) return ProtocolEnvelopeFactory.Success(request.RequestId, stored);
                    throw new BridgeCommandException("E_REQUEST_NOT_FOUND", "NavMesh bake request was not found.");
                }

                protected override object ExecuteAdditionalCommand(BridgeRequest request)
                {
                    if (request.Command == PlanCommand)
                    {
                        var args = request.DeserializeArgs<StartArgs>();
                        ValidateStart(args);
                        return ProtocolEnvelopeFactory.Success(request.RequestId, DryRun(args, BuildPlan(args)));
                    }
                    if (request.Command == SnapshotCommand) return ProtocolEnvelopeFactory.Success(request.RequestId, PublicStatus());
                    if (request.Command == CancelCommand) return ProtocolEnvelopeFactory.Success(request.RequestId, Cancel(request.DeserializeArgs<CancelArgs>()));
                    return base.ExecuteAdditionalCommand(request);
                }

                protected override void OnTick(Dictionary<string, object> root)
                {
                    if (!string.IsNullOrEmpty(Text(root, "terminalState"))) return;
                    if (IsBoundPreEffectAbort(Text(root, "requestId"), Text(root, "requestDigest")))
                    {
                        PublishPreEffectAbortTerminal(root);
                        return;
                    }
                    var phase = Text(root, "phase");
                    if (Bool(root, "reloadObserved") || IsReloadUncertain(root)) { RecoverReload(root); return; }
                    if (Bool(root, "cancelRequested") && phase == "queued") { FinishCancelled(root); return; }
                    if (phase == "queued" && (EditorApplication.isCompiling || EditorApplication.isUpdating)) { WriteRunningStatus(root); return; }
                    if (phase == "queued" && DateTimeOffset.UtcNow < Time(root, "dispatchNotBefore")) { WriteRunningStatus(root); return; }
                    var kind = Text(root, "kind");
                    if (kind == "legacy") TickLegacy(root); else if (kind == "surfaces") TickSurfaces(root);
                    else Fail(root, "E_EXECUTION_TIMEOUT", "NavMesh bake journal kind is invalid; outcome is unknown.");
                }

                protected override object BuildSuccessData(Dictionary<string, object> root)
                {
                    var finishedAt = Text(root, "finishedAt");
                    var startedAt = Text(root, "startedAt");
                    return new Dictionary<string, object>
                    {
                        { "operationId", Text(root, "operationId") },
                        { "kind", Text(root, "kind") },
                        { "status", "completed" },
                        { "startedAt", startedAt },
                        { "finishedAt", finishedAt },
                        { "durationMs", DurationMs(startedAt, finishedAt) },
                        { "topology", root.ContainsKey("topologyAfter") ? root["topologyAfter"] : NavMeshToolSupport.TopologyDictionary() },
                        { "scenes", root.ContainsKey("scenesResult") ? root["scenesResult"] : root["scenes"] },
                        { "surfaces", root.ContainsKey("surfacesResult") ? root["surfacesResult"] : new List<object>() },
                        { "plannedEffects", new List<object> { PlannedEffect(Text(root, "kind"), Long(root, "targetCount")) } }
                    };
                }

                /// <summary>先冻结root exact envelope，再首次发布带session HMAC的status；任一失败均由后续Tick原样恢复。</summary>
                protected override void OnTerminal(Dictionary<string, object> root, object envelope)
                {
                    try
                    {
                        StoreTerminalEnvelope(root, envelope);
                        if (!TerminalAlreadyPublished(root, envelope))
                        {
                            if (Text(root, "terminalState") != "completed" && Text(root, "phase") != "queued" && NativeGateRunning(root))
                                throw new InvalidOperationException("NavMesh native work must stop before terminal publication.");
                            WriteTerminalStatus(root, Text(root, "terminalState"), envelope);
                        }
                        StoreTerminalLedger(root, envelope);
                    }
                    catch (Exception exception)
                    {
                        WriteLog("navmesh_bake_status_write_failed " + exception.GetType().Name + ": " + exception.Message);
                        throw;
                    }
                }

                /// <summary>Editor重启清空机制状态；domain reload只恢复exact cleanup或保守观察，绝不重放effect。</summary>
                internal void InitializeState()
                {
                    ClearSurfaceObservation();
                    if (!SessionState.GetBool(StatusSessionInitializedKey, false))
                    {
                        UnityCliActiveOperation.Clear(StatusScope);
                        UnityCliActiveOperation.Clear(TerminalLedgerScope);
                        UnityCliActiveOperation.Clear(ActiveScope);
                        UnityCliActiveOperation.Clear(ActiveScope + "-terminal-cleanup");
                        UnityCliActiveOperation.Clear(ActiveScope + "-pre-effect-cleanup");
                        SessionState.SetBool(StatusSessionInitializedKey, true);
                        SessionState.SetBool(StatusWrittenThisSessionKey, false);
                        SessionState.SetBool(TerminalLedgerWrittenThisSessionKey, false);
                        SessionState.SetString(StatusProofKeySessionKey, NewStatusProofKey());
                        ClearSessionBindings();
                        StatusJournalCorrupt = false;
                        LastStatusLoadOutcome = StatusLoadOutcome.Missing;
                    }
                    else if (string.IsNullOrEmpty(SessionState.GetString(StatusProofKeySessionKey, string.Empty)))
                    {
                        SessionState.SetString(StatusProofKeySessionKey, NewStatusProofKey());
                        StatusJournalCorrupt = UnityCliActiveOperation.Exists(StatusScope);
                    }
                    if (ResumeBoundPreEffectAbort()) return;
                    if (ResumePreEffectCleanup()) return;
                    ClearReloadUncertain();
                    var active = Load();
                    if (active == null) return;
                    if (TryResumePublishedTerminal(active)) return;
                    TryRecoverActiveLease(active);
                    var activePhase = Text(active, "phase");
                    if (!Bool(active, "reloadObserved") && (activePhase == "dispatching" || activePhase == "running"))
                        BindReloadUncertain(active);
                    WriteRunningStatus(active);
                }

                /// <summary>domain reload 后只按 SessionState 与已验证 root 的 exact identity 恢复 lease；竞争失败保持 binding 并在后续 tick 重试。</summary>
                internal void RecoverActiveLease()
                {
                    Dictionary<string, object> active;
                    try { active = Load(); }
                    catch { return; }
                    if (active == null || !string.IsNullOrEmpty(Text(active, "terminalState"))) return;
                    TryRecoverActiveLease(active);
                }

                private static bool TryRecoverActiveLease(Dictionary<string, object> active)
                {
                    var requestId = Text(active, "requestId");
                    var digest = Text(active, "requestDigest");
                    var kind = Text(active, "kind");
                    if (!SessionOwnerIdentityMatches(requestId, digest, kind)
                        || !string.Equals(Text(active, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) return false;
                    try
                    {
                        if (UnityCliSessionMutationLease.IsOwnedBy(requestId, digest)) return true;
                        bool newlyAcquired;
                        return UnityCliSessionMutationLease.TryRecoverExact(requestId, digest, out newlyAcquired)
                            && UnityCliSessionMutationLease.IsOwnedBy(requestId, digest);
                    }
                    catch (Exception exception)
                    {
                        WriteLog("navmesh_bake_lease_recovery_failed " + exception.GetType().Name + ": " + exception.Message);
                        return false;
                    }
                }

                internal void BeforeReload()
                {
                    ClearSurfaceObservation();
                    Update(root =>
                    {
                        if (!string.IsNullOrEmpty(Text(root, "terminalState"))) return;
                        root["reloadObserved"] = true;
                        root["phaseBeforeReload"] = Text(root, "phase");
                    });
                }

                private Dictionary<string, object> CreateRoot(BridgeRequest request, string digest, StartArgs args, Dictionary<string, object> plan)
                {
                    var operationId = "navmesh-" + digest.Substring(0, 32);
                    return new Dictionary<string, object>
                    {
                        { "requestId", request.RequestId }, { "requestDigest", digest }, { "editorSessionId", EditorSessionId() },
                        { "deadline", request.Execution.AbsoluteDeadlineUtc }, { "operationId", operationId }, { "kind", args.Kind },
                        { "phase", "queued" }, { "startedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                        { "dispatchNotBefore", DateTime.UtcNow.AddMilliseconds(3000).ToString("O", CultureInfo.InvariantCulture) },
                        { "targetCount", plan["targetCount"] }, { "targets", plan["targets"] }, { "scenes", plan["scenes"] }, { "topologyBefore", plan["topology"] },
                        { "reloadObserved", false }, { "cancelRequested", false }, { "dispatchConfirmed", false },
                        { "foreignNativeOperationObserved", false }
                    };
                }

                private Dictionary<string, object> BuildPlan(StartArgs args)
                {
                    if (NavMeshBuilder.isRunning)
                        throw new BridgeCommandException("E_NAVMESH_BAKE_ACTIVE", "An unowned legacy NavMesh bake is already running.", "Wait for it to finish before starting a tracked bake.");
                    if (SurfaceApi.Available && SurfaceApi.ActiveOperationCount() > 0)
                        throw new BridgeCommandException("E_NAVMESH_BAKE_ACTIVE", "An unowned NavMeshSurface bake is already running.", "Wait for it to finish before starting a tracked bake.");
                    List<object> targets;
                    List<NavMeshSceneFileData> scenes;
                    if (args.Kind == "legacy")
                    {
                        scenes = NavMeshToolSupport.PrepareLoadedSceneFiles("navmesh.bake");
                        targets = LegacyTargets(scenes);
                    }
                    else
                    {
                        targets = SurfaceApi.PlanTargets();
                        scenes = NavMeshToolSupport.PrepareSceneFiles(targets.Cast<Dictionary<string, object>>().Select(target => Text(target, "scenePath")), "navmesh.surface.bake");
                    }
                    return new Dictionary<string, object>
                    {
                        { "kind", args.Kind }, { "targetCount", (long)targets.Count }, { "targets", targets },
                        { "scenes", SceneObjects(scenes) }, { "topology", NavMeshToolSupport.TopologyDictionary() },
                        { "plannedEffects", new List<object> { PlannedEffect(args.Kind, targets.Count) } }
                    };
                }

                private object DryRun(StartArgs args, Dictionary<string, object> plan)
                {
                    return new Dictionary<string, object>
                    {
                        { "dryRun", true }, { "kind", args.Kind }, { "targetCount", plan["targetCount"] },
                        { "targets", plan["targets"] }, { "scenes", plan["scenes"] }, { "topology", plan["topology"] }, { "plannedEffects", plan["plannedEffects"] }
                    };
                }

                private void TickLegacy(Dictionary<string, object> root)
                {
                    var phase = Text(root, "phase");
                    if (phase == "queued") { DispatchLegacy(root); return; }
                    if (phase == "dispatching") { RecoverDispatch(root); return; }
                    if (phase == "verifying")
                    {
                        if (Bool(root, "cancelRequested")) FinishCancelled(root); else CompleteLegacy(root);
                        return;
                    }
                    if (phase != "running") { Fail(root, "E_EXECUTION_TIMEOUT", "Legacy NavMesh bake phase is invalid; outcome is unknown."); return; }
                    WriteRunningStatus(root);
                    if (SurfaceApi.ActiveOperationCount() > 0)
                    {
                        if (!Bool(root, "foreignNativeOperationObserved")) MarkForeignNativeOperation(root);
                        return;
                    }
                    if (NavMeshBuilder.isRunning || DateTimeOffset.UtcNow < Time(root, "observeNotBefore")) return;
                    if (Bool(root, "foreignNativeOperationObserved"))
                    {
                        Fail(root, "E_EXECUTION_TIMEOUT", "Another native NavMesh bake overlapped the observed legacy operation; outcome is unknown.");
                        return;
                    }
                    if (Bool(root, "cancelRequested")) FinishCancelled(root); else CompleteLegacy(root);
                }

                /// <summary>nonce/status均在Unity调用前durable；调用后只确认一次，失败只能same-stack rollback或observe。</summary>
                private void DispatchLegacy(Dictionary<string, object> root)
                {
                    var preEffectRoot = root;
                    try
                    {
                        if (!TryUpdateAndReload(value => { value["phase"] = "dispatching"; value["dispatchNonce"] = Guid.NewGuid().ToString("N"); }, out root))
                        {
                            AbortBeforeNativeEffect(preEffectRoot, "legacy dispatch transition could not be confirmed");
                            return;
                        }
                    }
                    catch (Exception exception) { AbortBeforeNativeEffect(preEffectRoot, "legacy dispatch transition failed: " + exception.GetType().Name); return; }
                    var nativeDispatched = false;
                    try
                    {
                        NavMeshToolSupport.EnsureEditorReady("navmesh.bake", true);
                        if (AnyNativeOperationRunning())
                            throw new BridgeCommandException("E_NAVMESH_BAKE_ACTIVE", "An unowned native NavMesh bake started before legacy dispatch.");
                        NavMeshToolSupport.ValidateFrozenSceneFiles(SceneFiles(root), "navmesh.bake");
                        NavMeshBuilder.BuildNavMeshAsync();
                        nativeDispatched = true;
                        if (ConfirmDispatch(root)) return;
                        if (NavMeshBuilder.isRunning) { CancelOwnedLegacyBake(); if (NavMeshBuilder.isRunning) return; }
                        Fail(root, "E_EXECUTION_TIMEOUT", "Legacy NavMesh dispatch journal disappeared after the native effect; outcome is unknown.");
                    }
                    catch (Exception exception)
                    {
                        if (nativeDispatched && NavMeshBuilder.isRunning) { CancelOwnedLegacyBake(); if (NavMeshBuilder.isRunning) return; }
                        var bridge = exception as BridgeCommandException;
                        Fail(root, bridge == null ? "E_EXECUTION_TIMEOUT" : bridge.Code,
                            bridge == null ? "Legacy NavMesh dispatch could not be confirmed: " + exception.GetType().Name + "." : bridge.Message);
                    }
                }

                /// <summary>save与fresh readback跨至少一个Editor update；verifying恢复只继续proof。</summary>
                private void CompleteLegacy(Dictionary<string, object> root)
                {
                    root = Load();
                    if (root == null) return;
                    try
                    {
                        if (AnyNativeOperationRunning())
                        {
                            if (!Bool(root, "foreignNativeOperationObserved")) MarkForeignNativeOperation(root);
                            return;
                        }
                        if (Bool(root, "foreignNativeOperationObserved"))
                        {
                            Fail(root, "E_EXECUTION_TIMEOUT", "Another native NavMesh bake overlapped legacy terminal proof; outcome is unknown.");
                            return;
                        }
                        if (Text(root, "phase") != "verifying")
                        {
                            if (NavMeshBuilder.isRunning) return;
                            root["topologyAfter"] = NavMeshToolSupport.TopologyDictionary();
                            root["scenesResult"] = SceneObjects(NavMeshToolSupport.SaveAndProveSceneFiles(SceneFiles(root), false));
                            root["phase"] = "verifying";
                            root["verifyNotBefore"] = DateTime.UtcNow.AddMilliseconds(250).ToString("O", CultureInfo.InvariantCulture);
                            Save(root);
                            WriteRunningStatus(root);
                            return;
                        }
                        if (DateTimeOffset.UtcNow < Time(root, "verifyNotBefore")) return;
                        NavMeshToolSupport.ValidateFrozenSceneFiles(SceneFilesFrom(root, "scenesResult"), "navmesh.bake terminal proof");
                        if (!SameTopology(root["topologyAfter"] as IDictionary<string, object>, NavMeshToolSupport.TopologyDictionary()))
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Legacy NavMesh topology changed during terminal proof.");
                        root["finishedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                        root["terminalState"] = "completed";
                        Succeed(root);
                    }
                    catch (BridgeCommandException exception) { if (Text(root, "terminalState") != "completed") Fail(root, exception.Code, exception.Message); }
                    catch (Exception exception) { if (Text(root, "terminalState") != "completed") Fail(root, "E_EXECUTION_TIMEOUT", "Legacy NavMesh scene persistence proof failed: " + exception.GetType().Name + "."); }
                }

                private void TickSurfaces(Dictionary<string, object> root)
                {
                    var phase = Text(root, "phase");
                    if (phase == "queued") { DispatchSurfaces(root); return; }
                    if (phase == "dispatching") { RecoverDispatch(root); return; }
                    if (phase == "verifying")
                    {
                        if (Bool(root, "cancelRequested")) FinishCancelled(root); else CompleteSurfaces(root, null);
                        return;
                    }
                    if (phase != "running") { Fail(root, "E_EXECUTION_TIMEOUT", "NavMeshSurface bake phase is invalid; outcome is unknown."); return; }
                    WriteRunningStatus(root);
                    var observation = SurfaceObservationFor(root);
                    if (NavMeshBuilder.isRunning || (observation != null && SurfaceApi.HasForeignOperations(observation)))
                    {
                        if (!Bool(root, "foreignNativeOperationObserved")) MarkForeignNativeOperation(root);
                        return;
                    }
                    if (observation != null && (SurfaceApi.IsRunning(observation) || SurfaceApi.IsPresent(observation))) return;
                    if (SurfaceApi.AnyBaking(TargetIds(root)))
                    {
                        if (observation != null && !Bool(root, "foreignNativeOperationObserved")) MarkForeignNativeOperation(root);
                        return;
                    }
                    if (Bool(root, "foreignNativeOperationObserved") || (observation != null && !SurfaceApi.PublishedDataMatches(observation)))
                    {
                        Fail(root, "E_EXECUTION_TIMEOUT", "Another native NavMesh bake replaced or overlapped the observed Surface operation; outcome is unknown.");
                        return;
                    }
                    if (DateTimeOffset.UtcNow < Time(root, "observeNotBefore")) return;
                    if (Bool(root, "cancelRequested")) { FinishCancelled(root); return; }
                    List<object> result;
                    if (!SurfaceApi.TryProve(TargetIds(root), Targets(root), false, out result))
                    {
                        Fail(root, "E_EXECUTION_TIMEOUT", "NavMeshSurface terminal assets could not be proven; outcome is unknown.");
                        return;
                    }
                    CompleteSurfaces(root, result);
                }

                private void MarkForeignNativeOperation(Dictionary<string, object> root)
                {
                    Dictionary<string, object> fresh;
                    if (TryUpdateAndReload(value => value["foreignNativeOperationObserved"] = true, out fresh)) return;
                    Fail(root, "E_EXECUTION_TIMEOUT", "Foreign native NavMesh operation observation could not be stored; outcome is unknown.");
                }

                private void DispatchSurfaces(Dictionary<string, object> root)
                {
                    var preEffectRoot = root;
                    try
                    {
                        if (!TryUpdateAndReload(value => { value["phase"] = "dispatching"; value["dispatchNonce"] = Guid.NewGuid().ToString("N"); }, out root))
                        {
                            AbortBeforeNativeEffect(preEffectRoot, "surface dispatch transition could not be confirmed");
                            return;
                        }
                    }
                    catch (Exception exception) { AbortBeforeNativeEffect(preEffectRoot, "surface dispatch transition failed: " + exception.GetType().Name); return; }
                    var nativeDispatched = false;
                    try
                    {
                        NavMeshToolSupport.EnsureEditorReady("navmesh.surface.bake", true);
                        if (AnyNativeOperationRunning())
                            throw new BridgeCommandException("E_NAVMESH_BAKE_ACTIVE", "An unowned native NavMesh bake started before Surface dispatch.");
                        NavMeshToolSupport.ValidateFrozenSceneFiles(SceneFiles(root), "navmesh.surface.bake");
                        SurfaceApi.Start(TargetIds(root));
                        nativeDispatched = true;
                        var observation = SurfaceApi.CaptureObservation(TargetIds(root));
                        SetSurfaceObservation(root, observation);
                        if (ConfirmDispatch(root)) return;
                        if (SurfaceObservationRunning(root)) { CancelOwnedSurfaceBake(root); if (SurfaceObservationRunning(root)) return; }
                        Fail(root, "E_EXECUTION_TIMEOUT", "NavMeshSurface dispatch journal disappeared after the native effect; outcome is unknown.");
                    }
                    catch (Exception exception)
                    {
                        if (nativeDispatched && SurfaceObservationRunning(root)) { CancelOwnedSurfaceBake(root); if (SurfaceObservationRunning(root)) return; }
                        var bridge = exception as BridgeCommandException;
                        Fail(root, bridge == null ? "E_EXECUTION_TIMEOUT" : bridge.Code,
                            bridge == null ? "NavMeshSurface dispatch could not be confirmed: " + exception.GetType().Name + "." : bridge.Message);
                    }
                }

                private void CompleteSurfaces(Dictionary<string, object> root, List<object> result)
                {
                    root = Load();
                    if (root == null) return;
                    try
                    {
                        if (AnyNativeOperationRunning())
                        {
                            if (!Bool(root, "foreignNativeOperationObserved")) MarkForeignNativeOperation(root);
                            return;
                        }
                        if (Bool(root, "foreignNativeOperationObserved"))
                        {
                            Fail(root, "E_EXECUTION_TIMEOUT", "Another native NavMesh bake overlapped Surface terminal proof; outcome is unknown.");
                            return;
                        }
                        if (Text(root, "phase") != "verifying")
                        {
                            if (SurfaceObservationRunning(root) || SurfaceApi.AnyBaking(TargetIds(root))) return;
                            if (result == null && !SurfaceApi.TryProve(TargetIds(root), Targets(root), false, out result))
                                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMeshSurface terminal assets could not be proven; outcome is unknown.");
                            root["scenesResult"] = SceneObjects(NavMeshToolSupport.SaveAndProveSceneFiles(SceneFiles(root), false));
                            List<object> persisted;
                            if (!SurfaceApi.TryProve(TargetIds(root), Targets(root), false, out persisted))
                                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMeshSurface persisted assets could not be proven; outcome is unknown.");
                            root["surfacesResult"] = persisted;
                            root["topologyAfter"] = NavMeshToolSupport.TopologyDictionary();
                            root["phase"] = "verifying";
                            root["verifyNotBefore"] = DateTime.UtcNow.AddMilliseconds(250).ToString("O", CultureInfo.InvariantCulture);
                            Save(root);
                            WriteRunningStatus(root);
                            return;
                        }
                        if (DateTimeOffset.UtcNow < Time(root, "verifyNotBefore")) return;
                        NavMeshToolSupport.ValidateFrozenSceneFiles(SceneFilesFrom(root, "scenesResult"), "navmesh.surface.bake terminal proof");
                        List<object> observed;
                        if (!SurfaceApi.TryProve(TargetIds(root), Targets(root), false, out observed)
                            || !SameJsonList(RootList(root, "surfacesResult"), observed))
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMeshSurface asset changed during terminal proof.");
                        if (!SameTopology(root["topologyAfter"] as IDictionary<string, object>, NavMeshToolSupport.TopologyDictionary()))
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMeshSurface topology changed during terminal proof.");
                        root["finishedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                        root["terminalState"] = "completed";
                        Succeed(root);
                    }
                    catch (BridgeCommandException exception) { if (Text(root, "terminalState") != "completed") Fail(root, exception.Code, exception.Message); }
                    catch (Exception exception) { if (Text(root, "terminalState") != "completed") Fail(root, "E_EXECUTION_TIMEOUT", "NavMeshSurface scene persistence proof failed: " + exception.GetType().Name + "."); }
                }

                private object Cancel(CancelArgs args)
                {
                    if (args == null || string.IsNullOrWhiteSpace(args.OperationId))
                        throw new BridgeCommandException("E_SCHEMA_INVALID", "navmesh.bake-cancel requires the frozen operationId.");
                    var root = Load();
                    if (root == null)
                    {
                        var storedStatus = PublicStatus();
                        var exactCancelled = string.Equals(Text(storedStatus, "operationId"), args.OperationId, StringComparison.Ordinal)
                            && string.Equals(Text(storedStatus, "state"), "cancelled", StringComparison.Ordinal);
                        return new Dictionary<string, object>
                        {
                            { "cancelled", exactCancelled }, { "operationId", exactCancelled ? args.OperationId : null }, { "status", storedStatus }
                        };
                    }
                    if (!string.Equals(Text(root, "operationId"), args.OperationId, StringComparison.Ordinal))
                        throw new BridgeCommandException("E_NAVMESH_OPERATION_MISMATCH", "The active NavMesh bake does not match operationId.", "Read navmesh.bake-status and retry only for that exact operation.");
                    if (!string.IsNullOrEmpty(Text(root, "terminalState")))
                    {
                        var terminalStatus = PublicStatus();
                        return new Dictionary<string, object>
                        {
                            { "cancelled", Text(terminalStatus, "state") == "cancelled" }, { "operationId", args.OperationId }, { "status", terminalStatus }
                        };
                    }
                    if (!UnityCliSessionMutationLease.IsOwnedBy(Text(root, "requestId"), Text(root, "requestDigest")))
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh cancel cannot prove current root ownership; outcome is unknown.");
                    Dictionary<string, object> fresh;
                    if (Text(root, "phase") == "queued")
                    {
                        if (!TryUpdateAndReload(value => value["cancelRequested"] = true, out fresh))
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh queued cancel could not confirm its root; outcome is unknown.");
                        FinishCancelled(fresh);
                    }
                    else if (!TryUpdateAndReload(value => value["cancelRequested"] = true, out fresh))
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh cooperative cancel could not confirm its root; outcome is unknown.");
                    return new Dictionary<string, object> { { "cancelled", true }, { "operationId", args.OperationId }, { "status", PublicStatus() } };
                }

                private void FinishCancelled(Dictionary<string, object> root)
                {
                    root = Load();
                    if (root == null || (Text(root, "phase") != "queued" && NativeGateRunning(root))) return;
                    root["finishedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    root["terminalState"] = "cancelled";
                    root["topologyAfter"] = NavMeshToolSupport.TopologyDictionary();
                    Fail(root, "E_NAVMESH_BAKE_CANCELLED", "NavMesh bake was cancelled for operation " + Text(root, "operationId") + ".");
                }

                /// <summary>只在fresh root durable更新、exact lease与running status均确认后返回true。</summary>
                private bool TryUpdateAndReload(Action<Dictionary<string, object>> action, out Dictionary<string, object> root)
                {
                    root = null;
                    var before = Load();
                    if (before == null || !string.IsNullOrEmpty(Text(before, "terminalState"))) return false;
                    if (!Update(action)) return false;
                    Dictionary<string, object> updated;
                    try { updated = UnityCliActiveOperation.Load(ActiveScope); }
                    catch (Exception exception) { WriteLog("navmesh_updated_root_reload_failed " + exception.Message); return false; }
                    if (!UnityCliSessionMutationLease.IsOwnedBy(Text(updated, "requestId"), Text(updated, "requestDigest"))) return false;
                    WriteRunningStatus(updated);
                    root = Load();
                    return root != null;
                }

                /// <summary>effect前dispatch无法durable确认时冻结identity并发布唯一终态；写入失败由后续tick/reload原样重试。</summary>
                private void AbortBeforeNativeEffect(Dictionary<string, object> root, string reason)
                {
                    BindPreEffectAbort(root);
                    WriteLog("navmesh_pre_effect_abort requestId=" + Text(root, "requestId") + " reason=" + reason);
                    PublishPreEffectAbortTerminal(root);
                }

                private bool PublishPreEffectAbortTerminal(Dictionary<string, object> root)
                {
                    try
                    {
                        Fail(root, "E_EXECUTION_TIMEOUT", "NavMesh bake was aborted before the native effect because the dispatch transition could not be durably confirmed.");
                        return true;
                    }
                    catch (Exception exception)
                    {
                        WriteLog("navmesh_pre_effect_terminal_retry " + exception.GetType().Name + ": " + exception.Message);
                        return false;
                    }
                }

                /// <summary>Unity调用返回后只允许一次running确认；失败由调用方same-stack rollback。</summary>
                private bool ConfirmDispatch(Dictionary<string, object> root)
                {
                    var fresh = Load();
                    if (fresh == null) return false;
                    fresh["phase"] = "running";
                    fresh["dispatchConfirmed"] = true;
                    fresh["observeNotBefore"] = DateTime.UtcNow.AddMilliseconds(100).ToString("O", CultureInfo.InvariantCulture);
                    Save(fresh);
                    WriteRunningStatus(fresh);
                    return true;
                }

                /// <summary>dispatching表示Unity可能已被调用；global状态与产物变化只用于等待安全idle，不能归因成功。</summary>
                private void RecoverDispatch(Dictionary<string, object> root)
                {
                    WriteRunningStatus(root);
                    if (NativeGateRunning(root)) return;
                    Fail(root, "E_EXECUTION_TIMEOUT", "NavMesh dispatch could not be attributed after the effect boundary; outcome is unknown.");
                }

                /// <summary>reload后queued可继续、verifying可继续冻结proof；更早phase丢失native identity后只能等待idle并发布unknown。</summary>
                private void RecoverReload(Dictionary<string, object> root)
                {
                    var phase = Text(root, "phase");
                    var beforeReload = Text(root, "phaseBeforeReload");
                    Dictionary<string, object> fresh;
                    if (phase == "queued" && beforeReload == "queued")
                    {
                        try
                        {
                            if (!TryUpdateAndReload(value => { value["reloadObserved"] = false; value.Remove("phaseBeforeReload"); }, out fresh))
                                AbortBeforeNativeEffect(root, "queued reload recovery could not be confirmed");
                        }
                        catch (Exception exception) { AbortBeforeNativeEffect(root, "queued reload recovery failed: " + exception.GetType().Name); }
                        return;
                    }
                    if (phase == "verifying" && beforeReload == "verifying")
                    {
                        if (Bool(root, "cancelRequested"))
                        {
                            WriteRunningStatus(root);
                            if (NativeGateRunning(root)) return;
                            Fail(root, "E_EXECUTION_TIMEOUT", "NavMesh bake crossed a domain reload without request-specific completion proof; outcome is unknown.");
                            return;
                        }
                        if (!TryUpdateAndReload(value => { value["reloadObserved"] = false; value.Remove("phaseBeforeReload"); }, out fresh)) return;
                        if (Text(fresh, "kind") == "legacy") CompleteLegacy(fresh); else CompleteSurfaces(fresh, null);
                        return;
                    }
                    WriteRunningStatus(root);
                    if (NativeGateRunning(root)) return;
                    Fail(root, "E_EXECUTION_TIMEOUT", "NavMesh bake crossed a domain reload without request-specific completion proof; outcome is unknown.");
                }

                private void BindReloadUncertain(Dictionary<string, object> root)
                {
                    reloadUncertainRequestId = Text(root, "requestId");
                    reloadUncertainRequestDigest = Text(root, "requestDigest");
                }

                private bool IsReloadUncertain(Dictionary<string, object> root)
                {
                    return !string.IsNullOrEmpty(reloadUncertainRequestId)
                        && string.Equals(reloadUncertainRequestId, Text(root, "requestId"), StringComparison.Ordinal)
                        && string.Equals(reloadUncertainRequestDigest, Text(root, "requestDigest"), StringComparison.Ordinal);
                }

                private void ClearReloadUncertain()
                {
                    reloadUncertainRequestId = null;
                    reloadUncertainRequestDigest = null;
                }

                private void SetSurfaceObservation(Dictionary<string, object> root, SurfaceApi.SurfaceDispatchObservation observation)
                {
                    surfaceObservation = observation;
                    surfaceObservationRequestId = Text(root, "requestId");
                    surfaceObservationRequestDigest = Text(root, "requestDigest");
                }

                private SurfaceApi.SurfaceDispatchObservation SurfaceObservationFor(Dictionary<string, object> root)
                {
                    return surfaceObservation != null
                        && string.Equals(surfaceObservationRequestId, Text(root, "requestId"), StringComparison.Ordinal)
                        && string.Equals(surfaceObservationRequestDigest, Text(root, "requestDigest"), StringComparison.Ordinal)
                        ? surfaceObservation : null;
                }

                private bool SurfaceObservationRunning(Dictionary<string, object> root)
                {
                    var observation = SurfaceObservationFor(root);
                    return observation != null && SurfaceApi.IsRunning(observation);
                }

                private double? SurfaceObservationProgress(Dictionary<string, object> root)
                {
                    var observation = SurfaceObservationFor(root);
                    return observation == null ? (double?)null : SurfaceApi.Progress(observation);
                }

                private void ClearSurfaceObservation()
                {
                    surfaceObservation = null;
                    surfaceObservationRequestId = null;
                    surfaceObservationRequestDigest = null;
                }

                private static bool AnyNativeOperationRunning()
                {
                    try { return NavMeshBuilder.isRunning || SurfaceApi.ActiveOperationCount() > 0; }
                    catch { return true; }
                }

                private bool NativeGateRunning(Dictionary<string, object> root)
                {
                    return root != null && AnyNativeOperationRunning();
                }

                private static void CancelOwnedLegacyBake() { NavMeshBuilder.Cancel(); }
                private void CancelOwnedSurfaceBake(Dictionary<string, object> root)
                {
                    var observation = SurfaceObservationFor(root);
                    if (observation != null) SurfaceApi.Cancel(observation);
                }

                /// <summary>公开snapshot只直接读取并验证文件，不触发corrupt recovery或cleanup。</summary>
                internal Dictionary<string, object> LoadActiveForStatus()
                {
                    if (!UnityCliActiveOperation.Exists(ActiveScope)) return null;
                    try
                    {
                        var root = UnityCliActiveOperation.Load(ActiveScope);
                        if (ValidateNavMeshRoot(root)) return root;
                    }
                    catch (BridgeCommandException) { throw; }
                    catch { }
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh active snapshot cannot be verified; outcome is unknown.");
                }

                internal void WriteRunningStatus(Dictionary<string, object> root)
                {
                    if (!string.IsNullOrEmpty(Text(root, "terminalState")))
                        throw new InvalidOperationException("NavMesh terminal root cannot be projected as running.");
                    SaveStatus(BuildRunningStatus(root));
                }

                internal Dictionary<string, object> BuildRunningStatus(Dictionary<string, object> root)
                {
                    return new Dictionary<string, object>
                    {
                        { "editorSessionId", EditorSessionId() }, { "state", "running" }, { "active", true },
                        { "operationId", Text(root, "operationId") }, { "kind", Text(root, "kind") }, { "phase", PublicPhase(Text(root, "phase")) },
                        { "startedAt", Text(root, "startedAt") }, { "finishedAt", null },
                        { "durationMs", DurationMs(Text(root, "startedAt"), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)) },
                        { "progress", Text(root, "kind") == "legacy" ? (NavMeshBuilder.isRunning ? (object)null : 1d) : SurfaceObservationProgress(root) },
                        { "targetCount", Long(root, "targetCount") }, { "topology", NavMeshToolSupport.TopologyDictionary() },
                        { "dispatchConfirmed", Bool(root, "dispatchConfirmed") },
                        { "rootRequestId", Text(root, "requestId") }, { "rootRequestDigest", Text(root, "requestDigest") }
                    };
                }

                private void WriteTerminalStatus(Dictionary<string, object> root, string state, object envelope)
                {
                    var finishedAt = string.IsNullOrEmpty(Text(root, "finishedAt")) ? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) : Text(root, "finishedAt");
                    SaveStatus(new Dictionary<string, object>
                    {
                        { "editorSessionId", EditorSessionId() }, { "state", state }, { "active", false },
                        { "operationId", Text(root, "operationId") }, { "kind", Text(root, "kind") }, { "phase", "terminal" },
                        { "startedAt", Text(root, "startedAt") }, { "finishedAt", finishedAt },
                        { "durationMs", DurationMs(Text(root, "startedAt"), finishedAt) }, { "progress", state == "completed" ? 1d : (object)null },
                        { "targetCount", Long(root, "targetCount") },
                        { "topology", root.ContainsKey("topologyAfter") ? root["topologyAfter"] : NavMeshToolSupport.TopologyDictionary() },
                        { "dispatchConfirmed", Bool(root, "dispatchConfirmed") },
                        { "rootRequestId", Text(root, "requestId") }, { "rootRequestDigest", Text(root, "requestDigest") },
                        { "rootEnvelope", envelope }
                    });
                }

                private void StoreTerminalEnvelope(Dictionary<string, object> root, object envelope)
                {
                    var serialized = UnityCliSerializer.Serialize(envelope);
                    object existing;
                    if (root.TryGetValue("terminalEnvelope", out existing) && existing != null)
                    {
                        if (!string.Equals(UnityCliSerializer.Serialize(existing), serialized, StringComparison.Ordinal) || !RootTerminalProofMatches(root))
                            throw new InvalidOperationException("NavMesh root terminal cannot be overwritten by a different envelope.");
                        return;
                    }
                    var parsed = UnityCliSerializer.Parse(serialized) as IDictionary<string, object>;
                    object ok;
                    if (parsed == null || !parsed.TryGetValue("ok", out ok) || !(ok is bool)) throw new InvalidOperationException("NavMesh terminal envelope is invalid.");
                    var state = (bool)ok ? "completed" : Text(root, "terminalState") == "cancelled" ? "cancelled" : "failed";
                    var existingState = Text(root, "terminalState");
                    if (!string.IsNullOrEmpty(existingState) && !string.Equals(existingState, state, StringComparison.Ordinal))
                        throw new InvalidOperationException("NavMesh terminal state conflicts with the proposed envelope.");
                    root["terminalState"] = state;
                    if (string.IsNullOrEmpty(Text(root, "finishedAt"))) root["finishedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    if (!root.ContainsKey("topologyAfter")) root["topologyAfter"] = NavMeshToolSupport.TopologyDictionary();
                    root["terminalEnvelope"] = parsed;
                    root["terminalProofSha256"] = RootTerminalProofSha256(root);
                    if (!RootTerminalProofMatches(root)) throw new InvalidOperationException("NavMesh terminal proof key is unavailable.");
                    Save(root);
                }

                private static bool TerminalAlreadyPublished(Dictionary<string, object> root, object envelope)
                {
                    var status = LoadStatus();
                    if (status == null)
                    {
                        if (LastStatusLoadOutcome == StatusLoadOutcome.TransientFailure)
                            throw new InvalidOperationException("NavMesh durable terminal cannot be verified while status I/O is unavailable.");
                        return false;
                    }
                    if (!string.Equals(Text(status, "rootRequestId"), Text(root, "requestId"), StringComparison.Ordinal)
                        || !string.Equals(Text(status, "rootRequestDigest"), Text(root, "requestDigest"), StringComparison.Ordinal))
                        throw new InvalidOperationException("NavMesh status belongs to a different request identity.");
                    if (Bool(status, "active")) return false;
                    object stored;
                    if (!status.TryGetValue("rootEnvelope", out stored) || stored == null) return false;
                    if (!string.Equals(UnityCliSerializer.Serialize(stored), UnityCliSerializer.Serialize(envelope), StringComparison.Ordinal))
                        throw new InvalidOperationException("NavMesh durable terminal cannot be overwritten by a different envelope.");
                    return true;
                }

                private bool TryRestoreTerminalStatus(Dictionary<string, object> root)
                {
                    if (!ValidateStoredTerminalEnvelope(root)) return false;
                    var requestId = Text(root, "requestId");
                    var digest = Text(root, "requestDigest");
                    object envelope;
                    root.TryGetValue("terminalEnvelope", out envelope);
                    var status = LoadStatus();
                    if (status != null)
                    {
                        if (!string.Equals(Text(status, "rootRequestId"), requestId, StringComparison.Ordinal)
                            || !string.Equals(Text(status, "rootRequestDigest"), digest, StringComparison.Ordinal)) return false;
                        if (!Bool(status, "active"))
                        {
                            object stored;
                            if (!status.TryGetValue("rootEnvelope", out stored) || stored == null
                                || !string.Equals(UnityCliSerializer.Serialize(stored), UnityCliSerializer.Serialize(envelope), StringComparison.Ordinal))
                                throw new InvalidOperationException("NavMesh stored terminal conflicts with the root envelope.");
                            StoreTerminalLedger(root, envelope);
                            return true;
                        }
                    }
                    else if (LastStatusLoadOutcome == StatusLoadOutcome.TransientFailure) return false;
                    if (Text(root, "terminalState") != "completed" && Text(root, "phase") != "queued" && NativeGateRunning(root)) return false;
                    WriteTerminalStatus(root, Text(root, "terminalState"), envelope);
                    if (!TerminalStatusMatchesRoot(root, LoadStatus(), requestId, digest)) return false;
                    StoreTerminalLedger(root, envelope);
                    return true;
                }

                private bool ValidateStoredTerminalEnvelope(Dictionary<string, object> root)
                {
                    if (!RootTerminalProofMatches(root)) return false;
                    var state = Text(root, "terminalState");
                    object value;
                    var envelope = root.TryGetValue("terminalEnvelope", out value) ? value as IDictionary<string, object> : null;
                    object ok;
                    string requestId;
                    if ((state != "completed" && state != "cancelled" && state != "failed") || envelope == null
                        || !envelope.TryGetValue("ok", out ok) || !(ok is bool)
                        || !TryNonEmptyString(envelope, "requestId", out requestId)
                        || !string.Equals(requestId, Text(root, "requestId"), StringComparison.Ordinal)) return false;
                    if (state == "completed")
                    {
                        object dataValue;
                        var data = envelope.TryGetValue("data", out dataValue) ? dataValue as IDictionary<string, object> : null;
                        return (bool)ok && data != null
                            && string.Equals(UnityCliSerializer.Serialize(data), UnityCliSerializer.Serialize(BuildSuccessData(root)), StringComparison.Ordinal);
                    }
                    object errorValue;
                    var error = envelope.TryGetValue("error", out errorValue) ? errorValue as IDictionary<string, object> : null;
                    string code;
                    return !(bool)ok && error != null && TryNonEmptyString(error, "code", out code)
                        && (state != "cancelled" || code == "E_NAVMESH_BAKE_CANCELLED");
                }

                internal Dictionary<string, object> RecoveredStoredTerminal(string requestId, string requestDigest)
                {
                    var status = LoadStatus();
                    if (status == null)
                    {
                        if (StatusHistoryUnavailable()) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh terminal status is unavailable; outcome is unknown.");
                        return null;
                    }
                    if (!string.Equals(Text(status, "rootRequestId"), requestId, StringComparison.Ordinal) || !status.ContainsKey("rootEnvelope")) return null;
                    if (!string.IsNullOrEmpty(requestDigest) && !string.Equals(Text(status, "rootRequestDigest"), requestDigest, StringComparison.Ordinal))
                        throw new BridgeCommandException("E_REQUEST_ID_REUSED", "NavMesh bake requestId was reused with different input.");
                    StoreTerminalLedgerIdentity(requestId, Text(status, "rootRequestDigest"), status["rootEnvelope"]);
                    if (UnityCliActiveOperation.Exists(ActiveScope))
                    {
                        var active = UnityCliActiveOperation.Load(ActiveScope);
                        if (!TerminalStatusMatchesRoot(active, status, requestId, Text(status, "rootRequestDigest")))
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh terminal status does not match the active root.");
                    }
                    else if (IsBoundSessionOwner(requestId, Text(status, "rootRequestDigest")))
                    {
                        ResumeTerminalCleanup(new Dictionary<string, object> { { "requestId", requestId }, { "requestDigest", Text(status, "rootRequestDigest") } });
                    }
                    return new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", status["rootEnvelope"] } };
                }

                private bool RecoverUnavailableActiveState(Exception exception)
                {
                    string requestId;
                    string digest;
                    if (!UnityCliSessionMutationLease.TryGetCurrentOwner(out requestId, out digest)
                        || !IsBoundSessionOwner(requestId, digest)) return false;
                    var observed = rejectedRoot;
                    rejectedRoot = null;
                    if (observed != null && !string.IsNullOrEmpty(Text(observed, "terminalState")))
                    {
                        if (TryRestoreTerminalStatus(observed)) return true;
                        return TerminalStatusMatchesIdentity(LoadStatus(), requestId, digest);
                    }
                    var status = LoadStatus();
                    if (observed == null && status == null
                        && (LastStatusLoadOutcome == StatusLoadOutcome.TransientFailure || LastStatusLoadOutcome == StatusLoadOutcome.Corrupt)) return false;
                    if (status != null && (!string.Equals(Text(status, "rootRequestId"), requestId, StringComparison.Ordinal)
                        || !string.Equals(Text(status, "rootRequestDigest"), digest, StringComparison.Ordinal))) return false;
                    object terminalEnvelope;
                    if (status != null && !Bool(status, "active") && status.TryGetValue("rootEnvelope", out terminalEnvelope) && terminalEnvelope != null)
                    {
                        StoreTerminalLedgerIdentity(requestId, digest, terminalEnvelope);
                        return true;
                    }
                    var kind = SessionState.GetString(OwnerKindSessionKey, string.Empty);
                    if (OwnerNativeRunning(kind)) return false;
                    var finishedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    var startedAt = Text(status, "startedAt");
                    if (!ValidateTimestamp(startedAt)) startedAt = finishedAt;
                    var terminalRoot = new Dictionary<string, object>
                    {
                        { "requestId", requestId }, { "requestDigest", digest }, { "operationId", "navmesh-" + digest.Substring(0, 32) },
                        { "kind", kind }, { "startedAt", startedAt }, { "finishedAt", finishedAt }, { "targetCount", Long(status, "targetCount") },
                        { "topologyAfter", NavMeshToolSupport.TopologyDictionary() }, { "dispatchConfirmed", Bool(status, "dispatchConfirmed") }
                    };
                    var envelope = ProtocolEnvelopeFactory.Error(requestId, "E_EXECUTION_TIMEOUT", "NavMesh active state is unavailable; outcome is unknown.", null,
                        new[] { "Inspect navmesh.bake-status and the target scenes before submitting a new request." });
                    WriteTerminalStatus(terminalRoot, "failed", envelope);
                    StoreTerminalLedgerIdentity(requestId, digest, envelope);
                    WriteLog("navmesh_active_state_recovered requestId=" + requestId + " " + exception.Message);
                    return true;
                }

                private bool ValidateNavMeshRoot(Dictionary<string, object> root)
                {
                    if (root == null || string.IsNullOrWhiteSpace(Text(root, "requestId")) || !IsSha256Text(Text(root, "requestDigest"))
                        || !string.Equals(Text(root, "operationId"), "navmesh-" + Text(root, "requestDigest").Substring(0, 32), StringComparison.Ordinal)
                        || (Text(root, "kind") != "legacy" && Text(root, "kind") != "surfaces")
                        || !string.Equals(Text(root, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)
                        || !ValidateTimestamp(Text(root, "startedAt")) || !ValidateTimestamp(Text(root, "deadline"))
                        || !ValidateTimestamp(Text(root, "dispatchNotBefore")) || !TryPositiveLong(root, "targetCount")
                        || !ValidateTopology(root["topologyBefore"] as IDictionary<string, object>)
                        || !TryBoolean(root, "reloadObserved") || !TryBoolean(root, "cancelRequested") || !TryBoolean(root, "dispatchConfirmed")
                        || !TryBoolean(root, "foreignNativeOperationObserved")) return false;
                    var targets = RootList(root, "targets");
                    var scenes = RootList(root, "scenes");
                    if (!ValidateSceneList(scenes) || !ValidateTargetList(Text(root, "kind"), targets, scenes)
                        || targets.Count != Long(root, "targetCount")) return false;
                    var phase = Text(root, "phase");
                    var nonce = Text(root, "dispatchNonce");
                    var confirmed = Bool(root, "dispatchConfirmed");
                    if (phase != "queued" && phase != "dispatching" && phase != "running" && phase != "verifying") return false;
                    if (phase == "queued" && (!string.IsNullOrEmpty(nonce) || confirmed)) return false;
                    if (phase == "dispatching" && (string.IsNullOrEmpty(nonce) || confirmed)) return false;
                    if ((phase == "running" || phase == "verifying") && (string.IsNullOrEmpty(nonce) || !confirmed)) return false;
                    if ((phase == "running" || phase == "verifying") && !ValidateTimestamp(Text(root, "observeNotBefore"))) return false;
                    if (Bool(root, "reloadObserved"))
                    {
                        var beforeReload = Text(root, "phaseBeforeReload");
                        if (beforeReload != "queued" && beforeReload != "dispatching" && beforeReload != "running" && beforeReload != "verifying") return false;
                    }
                    if (phase == "verifying")
                    {
                        if (!ValidateTimestamp(Text(root, "verifyNotBefore")) || !ValidateSceneList(RootList(root, "scenesResult"))
                            || !ValidateTopology(root["topologyAfter"] as IDictionary<string, object>)) return false;
                        if (Text(root, "kind") == "surfaces" && !ValidateSurfaceResultList(RootList(root, "surfacesResult"), Long(root, "targetCount"))) return false;
                    }
                    var terminalState = Text(root, "terminalState");
                    if (!string.IsNullOrEmpty(terminalState))
                    {
                        if ((terminalState != "completed" && terminalState != "cancelled" && terminalState != "failed")
                            || !ValidateTimestamp(Text(root, "finishedAt")) || !ValidateTopology(root["topologyAfter"] as IDictionary<string, object>)
                            || !RootTerminalProofMatches(root)) return false;
                        if (terminalState == "completed" && (phase != "verifying" || !confirmed)) return false;
                        if (terminalState == "cancelled" && !Bool(root, "cancelRequested")) return false;
                    }
                    var status = LoadStatus();
                    if (status == null || !string.Equals(Text(status, "rootRequestId"), Text(root, "requestId"), StringComparison.Ordinal)
                        || !string.Equals(Text(status, "rootRequestDigest"), Text(root, "requestDigest"), StringComparison.Ordinal)
                        || !string.Equals(Text(status, "startedAt"), Text(root, "startedAt"), StringComparison.Ordinal)
                        || Long(status, "targetCount") != Long(root, "targetCount")) return false;
                    if (Bool(status, "active"))
                        return Bool(status, "dispatchConfirmed") == confirmed && Text(status, "phase") == PublicPhase(phase);
                    return TerminalStatusMatchesRoot(root, status, Text(root, "requestId"), Text(root, "requestDigest"));
                }

                private static bool TerminalStatusMatchesIdentity(IDictionary<string, object> status, string requestId, string digest)
                {
                    return status != null && !Bool(status, "active") && status.ContainsKey("rootEnvelope")
                        && string.Equals(Text(status, "rootRequestId"), requestId, StringComparison.Ordinal)
                        && string.Equals(Text(status, "rootRequestDigest"), digest, StringComparison.Ordinal);
                }

                internal static bool TerminalStatusMatchesRoot(Dictionary<string, object> root, IDictionary<string, object> status, string requestId, string digest)
                {
                    if (root == null || !TerminalStatusMatchesIdentity(status, requestId, digest)
                        || !string.Equals(Text(root, "requestId"), requestId, StringComparison.Ordinal)
                        || !string.Equals(Text(root, "requestDigest"), digest, StringComparison.Ordinal)
                        || !string.Equals(Text(root, "operationId"), Text(status, "operationId"), StringComparison.Ordinal)
                        || !string.Equals(Text(root, "kind"), Text(status, "kind"), StringComparison.Ordinal)
                        || !string.Equals(Text(root, "startedAt"), Text(status, "startedAt"), StringComparison.Ordinal)
                        || !string.Equals(Text(root, "finishedAt"), Text(status, "finishedAt"), StringComparison.Ordinal)
                        || Long(root, "targetCount") != Long(status, "targetCount")
                        || !string.Equals(Text(root, "terminalState"), Text(status, "state"), StringComparison.Ordinal)) return false;
                    object rootEnvelope;
                    object statusEnvelope;
                    return RootTerminalProofMatches(root)
                        && root.TryGetValue("terminalEnvelope", out rootEnvelope) && rootEnvelope != null
                        && status.TryGetValue("rootEnvelope", out statusEnvelope) && statusEnvelope != null
                        && string.Equals(UnityCliSerializer.Serialize(rootEnvelope), UnityCliSerializer.Serialize(statusEnvelope), StringComparison.Ordinal);
                }

                /// <summary>domain reload从exact binding恢复effect前abort，只发布同一terminal或保守恢复identity，绝不再次dispatch。</summary>
                private bool ResumeBoundPreEffectAbort()
                {
                    var requestId = SessionState.GetString(PreEffectAbortRequestIdSessionKey, string.Empty);
                    var digest = SessionState.GetString(PreEffectAbortRequestDigestSessionKey, string.Empty);
                    if (!IsBoundPreEffectAbort(requestId, digest)) return false;
                    if (!UnityCliSessionMutationLease.IsOwnedBy(requestId, digest)) { ClearPreEffectAbortBinding(); return false; }
                    if (!UnityCliActiveOperation.Exists(ActiveScope))
                    {
                        if (RecoverUnavailableActiveState(new InvalidOperationException("NavMesh pre-effect abort root is missing.")))
                            ResumeTerminalCleanup(new Dictionary<string, object> { { "requestId", requestId }, { "requestDigest", digest } });
                        return true;
                    }
                    Dictionary<string, object> root;
                    try { root = UnityCliActiveOperation.Load(ActiveScope); }
                    catch { return true; }
                    if (!string.Equals(Text(root, "requestId"), requestId, StringComparison.Ordinal)
                        || !string.Equals(Text(root, "requestDigest"), digest, StringComparison.Ordinal)) return true;
                    PublishPreEffectAbortTerminal(root);
                    return true;
                }
            }

            internal static bool IsCommand(string command) { return Operation.IsCommand(command); }
            internal static void Register(BridgeCommandRegistry registry) { Operation.Register(registry); }
            internal static Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken ct) { return Operation.Execute(request, raw, accept, ct); }
            internal static void Initialize() { Operation.InitializeState(); }
            internal static void Shutdown() { }
            internal static void BeforeReload() { Operation.BeforeReload(); }
            internal static void Tick()
            {
                Operation.RecoverActiveLease();
                Operation.Tick();
                ClearSessionBindingsIfReleased();
            }
            internal static bool HasActiveSurfaceData() { return SurfaceApi.HasActiveData(); }
            /// <summary>mutation effect前同时观察legacy全局gate与AI Navigation当前operation，禁止与未跟踪native bake并发。</summary>
            internal static bool HasActiveNativeBake()
            {
                return NavMeshBuilder.isRunning || (SurfaceApi.Available && SurfaceApi.ActiveOperationCount() > 0);
            }

            /// <summary>公开status只构造有界snapshot，不写status、journal或lease。</summary>
            internal static Dictionary<string, object> PublicStatus()
            {
                var active = Operation.LoadActiveForStatus();
                if (active != null && string.Equals(Text(active, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(Text(active, "terminalState"))) return PublicStatusFromStored(Operation.BuildRunningStatus(active));
                    var terminalStatus = LoadStatus();
                    if (!Coordinator.TerminalStatusMatchesRoot(active, terminalStatus, Text(active, "requestId"), Text(active, "requestDigest")))
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh terminal snapshot cannot be verified; outcome is unknown.");
                    return PublicStatusFromStored(terminalStatus);
                }
                var stored = LoadStatus();
                if (stored == null && StatusHistoryUnavailable())
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh status snapshot cannot be verified; outcome is unknown.");
                return PublicStatusFromStored(stored);
            }

            private static Dictionary<string, object> PublicStatusFromStored(Dictionary<string, object> stored)
            {
                if (stored == null || !string.Equals(Text(stored, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal))
                    return new Dictionary<string, object> { { "state", "idle" }, { "active", false }, { "operationId", null }, { "kind", null }, { "phase", null }, { "startedAt", null }, { "finishedAt", null }, { "durationMs", 0L }, { "progress", null }, { "targetCount", 0L }, { "topology", NavMeshToolSupport.TopologyDictionary() } };
                var result = new Dictionary<string, object>(stored, StringComparer.Ordinal);
                result.Remove("editorSessionId");
                result.Remove("dispatchConfirmed");
                result.Remove("rootRequestId");
                result.Remove("rootRequestDigest");
                result.Remove("rootEnvelope");
                result.Remove("statusProofSha256");
                return result;
            }

            private static Dictionary<string, object> LoadStatus()
            {
                try
                {
                    if (!UnityCliActiveOperation.Exists(StatusScope))
                    {
                        LastStatusLoadOutcome = StatusLoadOutcome.Missing;
                        if (StatusHistoryUnavailable()) StatusJournalCorrupt = true;
                        return null;
                    }
                    var status = UnityCliActiveOperation.Load(StatusScope);
                    if (!ValidateStatus(status))
                    {
                        LastStatusLoadOutcome = StatusLoadOutcome.Corrupt;
                        StatusJournalCorrupt = true;
                        return null;
                    }
                    LastStatusLoadOutcome = StatusLoadOutcome.Loaded;
                    return status;
                }
                catch (IOException) { LastStatusLoadOutcome = StatusLoadOutcome.TransientFailure; return null; }
                catch (UnauthorizedAccessException) { LastStatusLoadOutcome = StatusLoadOutcome.TransientFailure; return null; }
                catch { LastStatusLoadOutcome = StatusLoadOutcome.Corrupt; StatusJournalCorrupt = true; return null; }
            }

            private static void SaveStatus(Dictionary<string, object> status)
            {
                status.Remove("statusProofSha256");
                status["statusProofSha256"] = StatusProofSha256(status);
                UnityCliActiveOperation.Save(StatusScope, status);
                SessionState.SetBool(StatusWrittenThisSessionKey, true);
                StatusJournalCorrupt = false;
                LastStatusLoadOutcome = StatusLoadOutcome.Loaded;
            }

            /// <summary>terminal ledger是单文件、固定容量、session-scoped；只保留重投窗口内的identity。</summary>
            private static Dictionary<string, object> LoadTerminalLedger()
            {
                try
                {
                    if (!UnityCliActiveOperation.Exists(TerminalLedgerScope))
                    {
                        if (SessionState.GetBool(TerminalLedgerWrittenThisSessionKey, false))
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh terminal ledger is missing; outcome is unknown.");
                        return new Dictionary<string, object>
                        {
                            { "editorSessionId", EditorSessionId() }, { "entries", new List<object>() }
                        };
                    }
                    var ledger = UnityCliActiveOperation.Load(TerminalLedgerScope);
                    if (!ValidateTerminalLedger(ledger))
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh terminal ledger is corrupt; outcome is unknown.");
                    PruneTerminalLedger(ledger["entries"] as IList);
                    return ledger;
                }
                catch (BridgeCommandException) { throw; }
                catch (Exception exception)
                {
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh terminal ledger cannot be read; outcome is unknown.", exception.GetType().Name);
                }
            }

            private static void SaveTerminalLedger(Dictionary<string, object> ledger)
            {
                ledger.Remove("ledgerProofSha256");
                ledger["ledgerProofSha256"] = TerminalLedgerProofSha256(ledger);
                UnityCliActiveOperation.Save(TerminalLedgerScope, ledger);
                SessionState.SetBool(TerminalLedgerWrittenThisSessionKey, true);
            }

            private static bool ValidateTerminalLedger(Dictionary<string, object> ledger)
            {
                if (ledger == null || !string.Equals(Text(ledger, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) return false;
                object entriesValue;
                var entries = ledger.TryGetValue("entries", out entriesValue) ? entriesValue as IList : null;
                string proof;
                if (entries == null || entries.Count > TerminalLedgerCapacity || !TryNonEmptyString(ledger, "ledgerProofSha256", out proof)
                    || !IsSha256Text(proof) || !string.Equals(proof, TerminalLedgerProofSha256(ledger), StringComparison.Ordinal)) return false;
                var requestIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in entries)
                {
                    var entry = value as IDictionary<string, object>;
                    object envelopeValue;
                    var envelope = entry != null && entry.TryGetValue("rootEnvelope", out envelopeValue) ? envelopeValue as IDictionary<string, object> : null;
                    object ok;
                    string requestId;
                    if (entry == null || !TryNonEmptyString(entry, "requestId", out requestId) || !requestIds.Add(requestId)
                        || !IsSha256Text(Text(entry, "requestDigest")) || !ValidateTimestamp(Text(entry, "storedAt"))
                        || !LedgerRetentionValid(entry)
                        || envelope == null || !envelope.TryGetValue("ok", out ok) || !(ok is bool)
                        || !string.Equals(Text(envelope, "requestId"), requestId, StringComparison.Ordinal)) return false;
                }
                return true;
            }

            private static string TerminalLedgerProofSha256(IDictionary<string, object> ledger)
            {
                var key = SessionState.GetString(StatusProofKeySessionKey, string.Empty);
                if (string.IsNullOrEmpty(key) || ledger == null) return string.Empty;
                object entries;
                ledger.TryGetValue("entries", out entries);
                return HmacSha256(key, UnityCliSerializer.Serialize(new Dictionary<string, object>
                {
                    { "editorSessionId", Text(ledger, "editorSessionId") }, { "entries", entries }
                }));
            }

            private static Dictionary<string, object> RecoveredLedgerTerminal(string requestId, string requestDigest)
            {
                var ledger = LoadTerminalLedger();
                var entries = (ledger["entries"] as IList).Cast<object>();
                foreach (var value in entries)
                {
                    var entry = value as IDictionary<string, object>;
                    if (entry == null || !string.Equals(Text(entry, "requestId"), requestId, StringComparison.Ordinal)) continue;
                    if (!string.IsNullOrEmpty(requestDigest) && !string.Equals(Text(entry, "requestDigest"), requestDigest, StringComparison.Ordinal))
                        throw new BridgeCommandException("E_REQUEST_ID_REUSED", "NavMesh bake requestId was reused with different input.");
                    return new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", entry["rootEnvelope"] } };
                }
                return null;
            }

            internal static void EnsureTerminalLedgerCapacity()
            {
                var ledger = LoadTerminalLedger();
                var entries = ledger["entries"] as IList;
                if (entries == null || entries.Count < TerminalLedgerCapacity) return;
                var retryAfterMs = (int)Math.Max(0, Math.Min(int.MaxValue, EarliestLedgerRetentionMs(entries) - UtcNowMs()));
                throw new BridgeCommandException(
                    "E_UNITY_BUSY",
                    "NavMesh bake terminal ledger is full of unexpired entries; no bake was started.",
                    new Dictionary<string, object>
                    {
                        { "reason", "navmesh-terminal-ledger-capacity" },
                        { "entriesUsed", (long)entries.Count },
                        { "capacity", (long)TerminalLedgerCapacity },
                        { "ownEffectStarted", false },
                        { "retryAfterMs", (long)retryAfterMs }
                    },
                    new[]
                    {
                        "Wait for the reported retryAfterMs, then submit a new request; expired terminal entries are pruned by themselves and no Editor restart is required.",
                        "Run unitycli tools run editor.state to confirm the Editor is ready before resubmitting."
                    },
                    true,
                    retryAfterMs);
            }

            /// <summary>只保留原调用重投窗口内的identity；缺少窗口的旧条目视为已过期。</summary>
            private static void PruneTerminalLedger(IList entries)
            {
                if (entries == null) return;
                var now = UtcNowMs();
                for (var index = entries.Count - 1; index >= 0; index--)
                {
                    var entry = entries[index] as IDictionary<string, object>;
                    if (entry != null && Long(entry, "retainUntilUtcMs") > now) continue;
                    entries.RemoveAt(index);
                }
            }

            private static bool LedgerRetentionValid(IDictionary<string, object> entry)
            {
                object value;
                return !entry.TryGetValue("retainUntilUtcMs", out value) || value is long;
            }

            private static long EarliestLedgerRetentionMs(IList entries)
            {
                var earliest = long.MaxValue;
                foreach (var value in entries)
                {
                    var entry = value as IDictionary<string, object>;
                    var retainUntil = entry == null ? 0L : Long(entry, "retainUntilUtcMs");
                    if (retainUntil > 0 && retainUntil < earliest) earliest = retainUntil;
                }
                return earliest == long.MaxValue ? UtcNowMs() : earliest;
            }

            /// <summary>重投窗口取原调用的execution deadline；没有admission记录时使用有界兜底窗口。</summary>
            private static long LedgerRetentionUntilMs(string requestId)
            {
                long executionDeadlineMs;
                return UnityCliBridge.TryCaptureAdmissionDeadlineMs(requestId, out executionDeadlineMs)
                    ? executionDeadlineMs + TerminalLedgerRetentionGraceMs
                    : UtcNowMs() + TerminalLedgerFallbackWindowMs;
            }

            private static long UtcNowMs()
            {
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            private static void StoreTerminalLedger(Dictionary<string, object> root, object envelope)
            {
                if (root == null || !RootTerminalProofMatches(root))
                    throw new InvalidOperationException("NavMesh terminal ledger requires an exact root proof.");
                StoreTerminalLedgerIdentity(Text(root, "requestId"), Text(root, "requestDigest"), envelope);
            }

            internal static void StoreTerminalLedgerIdentity(string requestId, string requestDigest, object envelope, long retainUntilUtcMs = 0)
            {
                if (string.IsNullOrWhiteSpace(requestId) || !IsSha256Text(requestDigest))
                    throw new InvalidOperationException("NavMesh terminal ledger identity is invalid.");
                var parsedEnvelope = UnityCliSerializer.Parse(UnityCliSerializer.Serialize(envelope)) as IDictionary<string, object>;
                if (parsedEnvelope == null || !string.Equals(Text(parsedEnvelope, "requestId"), requestId, StringComparison.Ordinal))
                    throw new InvalidOperationException("NavMesh terminal ledger envelope identity is invalid.");
                var ledger = LoadTerminalLedger();
                var entries = (ledger["entries"] as IList).Cast<object>().ToList();
                foreach (var value in entries)
                {
                    var entry = value as IDictionary<string, object>;
                    if (entry == null || !string.Equals(Text(entry, "requestId"), requestId, StringComparison.Ordinal)) continue;
                    if (!string.Equals(Text(entry, "requestDigest"), requestDigest, StringComparison.Ordinal)
                        || !string.Equals(UnityCliSerializer.Serialize(entry["rootEnvelope"]), UnityCliSerializer.Serialize(parsedEnvelope), StringComparison.Ordinal))
                        throw new InvalidOperationException("NavMesh terminal ledger entry cannot be overwritten.");
                    return;
                }
                if (retainUntilUtcMs <= 0) retainUntilUtcMs = LedgerRetentionUntilMs(requestId);
                if (entries.Count >= TerminalLedgerCapacity)
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh bake terminal ledger is full; the effect outcome is unknown.", "Inspect the NavMesh bake state before submitting a new request; the effect already ran.");
                entries.Add(new Dictionary<string, object>
                {
                    { "requestId", requestId }, { "requestDigest", requestDigest },
                    { "storedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                    { "retainUntilUtcMs", retainUntilUtcMs }, { "rootEnvelope", parsedEnvelope }
                });
                ledger["entries"] = entries;
                SaveTerminalLedger(ledger);
            }

            private static bool ValidateStatus(Dictionary<string, object> status)
            {
                if (status == null || !string.Equals(Text(status, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) return false;
                string state; string operationId; string kind; string phase; string startedAt; string requestId; string digest; string proof;
                object active; object confirmed;
                if (!TryNonEmptyString(status, "state", out state) || !TryNonEmptyString(status, "operationId", out operationId)
                    || !TryNonEmptyString(status, "kind", out kind) || !TryNonEmptyString(status, "phase", out phase)
                    || !TryNonEmptyString(status, "startedAt", out startedAt) || !TryNonEmptyString(status, "rootRequestId", out requestId)
                    || !TryNonEmptyString(status, "rootRequestDigest", out digest) || !IsSha256Text(digest)
                    || !TryNonEmptyString(status, "statusProofSha256", out proof) || !IsSha256Text(proof)
                    || !string.Equals(proof, StatusProofSha256(status), StringComparison.Ordinal)
                    || !status.TryGetValue("active", out active) || !(active is bool)
                    || !status.TryGetValue("dispatchConfirmed", out confirmed) || !(confirmed is bool)
                    || (kind != "legacy" && kind != "surfaces") || !ValidateTimestamp(startedAt)
                    || !string.Equals(operationId, "navmesh-" + digest.Substring(0, 32), StringComparison.Ordinal)
                    || !TryNonNegativeLong(status, "durationMs") || !TryNonNegativeLong(status, "targetCount")
                    || !ValidateTopology(status["topology"] as IDictionary<string, object>)) return false;
                var isActive = (bool)active;
                if (isActive) return state == "running" && (phase == "queued" || phase == "starting" || phase == "baking")
                    && !status.ContainsKey("rootEnvelope") && string.IsNullOrEmpty(Text(status, "finishedAt"));
                if ((state != "completed" && state != "cancelled" && state != "failed") || phase != "terminal"
                    || !ValidateTimestamp(Text(status, "finishedAt"))) return false;
                object envelopeValue;
                var envelope = status.TryGetValue("rootEnvelope", out envelopeValue) ? envelopeValue as IDictionary<string, object> : null;
                object ok;
                string envelopeRequestId;
                if (envelope == null || !envelope.TryGetValue("ok", out ok) || !(ok is bool)
                    || !TryNonEmptyString(envelope, "requestId", out envelopeRequestId)
                    || !string.Equals(envelopeRequestId, requestId, StringComparison.Ordinal)) return false;
                if (state == "completed") return (bool)ok && envelope.ContainsKey("data");
                object errorValue;
                var error = envelope.TryGetValue("error", out errorValue) ? errorValue as IDictionary<string, object> : null;
                string code;
                return !(bool)ok && error != null && TryNonEmptyString(error, "code", out code)
                    && (state != "cancelled" || code == "E_NAVMESH_BAKE_CANCELLED");
            }

            private static bool StatusHistoryUnavailable()
            {
                return StatusJournalCorrupt || (SessionState.GetBool(StatusWrittenThisSessionKey, false) && !UnityCliActiveOperation.Exists(StatusScope));
            }

            private static string StatusProofSha256(IDictionary<string, object> status)
            {
                var key = SessionState.GetString(StatusProofKeySessionKey, string.Empty);
                if (string.IsNullOrEmpty(key) || status == null) return string.Empty;
                var material = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var field in new[] { "editorSessionId", "state", "active", "operationId", "kind", "phase", "startedAt", "finishedAt", "durationMs", "progress", "targetCount", "topology", "dispatchConfirmed", "rootRequestId", "rootRequestDigest", "rootEnvelope" })
                {
                    object value;
                    if (status.TryGetValue(field, out value)) material[field] = value;
                }
                return HmacSha256(key, UnityCliSerializer.Serialize(material));
            }

            private static string RootTerminalProofSha256(IDictionary<string, object> root)
            {
                var key = SessionState.GetString(StatusProofKeySessionKey, string.Empty);
                if (string.IsNullOrEmpty(key) || root == null) return string.Empty;
                var material = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var field in new[] { "editorSessionId", "requestId", "requestDigest", "operationId", "kind", "phase", "startedAt", "finishedAt", "targetCount", "topologyAfter", "dispatchConfirmed", "terminalState", "terminalEnvelope" })
                {
                    object value;
                    if (root.TryGetValue(field, out value)) material[field] = value;
                }
                return HmacSha256(key, UnityCliSerializer.Serialize(material));
            }

            private static bool RootTerminalProofMatches(IDictionary<string, object> root)
            {
                string proof;
                return TryNonEmptyString(root, "terminalProofSha256", out proof) && IsSha256Text(proof)
                    && string.Equals(proof, RootTerminalProofSha256(root), StringComparison.Ordinal);
            }

            private static string HmacSha256(string key, string text)
            {
                using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key)))
                {
                    var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(text));
                    var builder = new StringBuilder(bytes.Length * 2);
                    foreach (var value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                    return builder.ToString();
                }
            }

            private static string NewStatusProofKey() { return Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"); }

            private static void BindSessionOwner(string requestId, string digest, string kind)
            {
                if (string.IsNullOrWhiteSpace(requestId) || !IsSha256Text(digest) || (kind != "legacy" && kind != "surfaces"))
                    throw new InvalidOperationException("NavMesh session owner identity is invalid.");
                SessionState.SetString(OwnerRequestIdSessionKey, requestId);
                SessionState.SetString(OwnerRequestDigestSessionKey, digest);
                SessionState.SetString(OwnerKindSessionKey, kind);
            }

            private static bool SessionOwnerIdentityMatches(string requestId, string digest, string kind)
            {
                return !string.IsNullOrWhiteSpace(requestId) && IsSha256Text(digest)
                    && (kind == "legacy" || kind == "surfaces")
                    && string.Equals(SessionState.GetString(OwnerRequestIdSessionKey, string.Empty), requestId, StringComparison.Ordinal)
                    && string.Equals(SessionState.GetString(OwnerRequestDigestSessionKey, string.Empty), digest, StringComparison.Ordinal)
                    && string.Equals(SessionState.GetString(OwnerKindSessionKey, string.Empty), kind, StringComparison.Ordinal);
            }

            private static bool IsBoundSessionOwner(string requestId, string digest)
            {
                var kind = SessionState.GetString(OwnerKindSessionKey, string.Empty);
                return SessionOwnerIdentityMatches(requestId, digest, kind)
                    && UnityCliSessionMutationLease.IsOwnedBy(requestId, digest);
            }

            private static void BindPreEffectAbort(Dictionary<string, object> root)
            {
                SessionState.SetString(PreEffectAbortRequestIdSessionKey, Text(root, "requestId"));
                SessionState.SetString(PreEffectAbortRequestDigestSessionKey, Text(root, "requestDigest"));
            }

            private static bool IsBoundPreEffectAbort(string requestId, string digest)
            {
                return !string.IsNullOrWhiteSpace(requestId) && IsSha256Text(digest)
                    && string.Equals(SessionState.GetString(PreEffectAbortRequestIdSessionKey, string.Empty), requestId, StringComparison.Ordinal)
                    && string.Equals(SessionState.GetString(PreEffectAbortRequestDigestSessionKey, string.Empty), digest, StringComparison.Ordinal);
            }

            private static void ClearPreEffectAbortBinding()
            {
                SessionState.SetString(PreEffectAbortRequestIdSessionKey, string.Empty);
                SessionState.SetString(PreEffectAbortRequestDigestSessionKey, string.Empty);
            }

            private static void ClearSessionBindings()
            {
                SessionState.SetString(OwnerRequestIdSessionKey, string.Empty);
                SessionState.SetString(OwnerRequestDigestSessionKey, string.Empty);
                SessionState.SetString(OwnerKindSessionKey, string.Empty);
                ClearPreEffectAbortBinding();
            }

            private static void ClearSessionBindingsIfReleased()
            {
                var ownerId = SessionState.GetString(OwnerRequestIdSessionKey, string.Empty);
                var ownerDigest = SessionState.GetString(OwnerRequestDigestSessionKey, string.Empty);
                var abortId = SessionState.GetString(PreEffectAbortRequestIdSessionKey, string.Empty);
                var abortDigest = SessionState.GetString(PreEffectAbortRequestDigestSessionKey, string.Empty);
                if ((!string.IsNullOrEmpty(ownerId) || !string.IsNullOrEmpty(ownerDigest)) && UnityCliSessionMutationLease.IsOwnedBy(ownerId, ownerDigest)) return;
                if ((!string.IsNullOrEmpty(abortId) || !string.IsNullOrEmpty(abortDigest)) && UnityCliSessionMutationLease.IsOwnedBy(abortId, abortDigest)) return;
                try
                {
                    var active = UnityCliActiveOperation.Load(ActiveScope);
                    var kind = SessionState.GetString(OwnerKindSessionKey, string.Empty);
                    if (active != null && string.IsNullOrEmpty(Text(active, "terminalState"))
                        && SessionOwnerIdentityMatches(Text(active, "requestId"), Text(active, "requestDigest"), kind)) return;
                }
                catch { return; }
                ClearSessionBindings();
            }

            private static bool OwnerNativeRunning(string kind)
            {
                if (kind != "legacy" && kind != "surfaces") return false;
                try { return NavMeshBuilder.isRunning || SurfaceApi.ActiveOperationCount() > 0; }
                catch { return true; }
            }

            private static bool ValidateTimestamp(string value) { DateTimeOffset parsed; return !string.IsNullOrWhiteSpace(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed); }
            private static bool IsSha256Text(string value) { return value != null && value.Length == 64 && value.All(character => (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')); }
            private static bool TryNonEmptyString(IDictionary<string, object> value, string key, out string result) { result = Text(value, key); return !string.IsNullOrWhiteSpace(result); }
            private static bool TryNonNegativeLong(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item is long && (long)item >= 0L; }
            private static bool TryPositiveLong(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item is long && (long)item > 0L; }
            private static bool TryBoolean(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item is bool; }
            private static bool ValidateTopology(IDictionary<string, object> value) { return value != null && TryNonNegativeLong(value, "vertexCount") && TryNonNegativeLong(value, "indexCount") && IsSha256Text(Text(value, "sha256")); }
            private static bool SameTopology(IDictionary<string, object> left, IDictionary<string, object> right) { return ValidateTopology(left) && ValidateTopology(right) && Long(left, "vertexCount") == Long(right, "vertexCount") && Long(left, "indexCount") == Long(right, "indexCount") && string.Equals(Text(left, "sha256"), Text(right, "sha256"), StringComparison.Ordinal); }
            private static List<object> RootList(Dictionary<string, object> root, string key) { object value; var list = root != null && root.TryGetValue(key, out value) ? value as IList : null; return list == null ? null : list.Cast<object>().ToList(); }
            private static bool SameJsonList(List<object> left, List<object> right) { return left != null && right != null && string.Equals(UnityCliSerializer.Serialize(left), UnityCliSerializer.Serialize(right), StringComparison.Ordinal); }

            private static bool ValidateSceneList(List<object> scenes)
            {
                if (scenes == null || scenes.Count == 0) return false;
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in scenes)
                {
                    var scene = value as IDictionary<string, object>;
                    object dirty;
                    if (scene == null || !Text(scene, "path").StartsWith("Assets/", StringComparison.Ordinal)
                        || !Text(scene, "path").EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                        || Text(scene, "guid").Length != 32 || !Text(scene, "guid").All(character => (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))
                        || !IsSha256Text(Text(scene, "sha256")) || !TryPositiveLong(scene, "sizeBytes")
                        || !scene.TryGetValue("dirty", out dirty) || !(dirty is bool) || (bool)dirty
                        || !paths.Add(Text(scene, "path"))) return false;
                }
                return true;
            }

            private static bool ValidateTargetList(string kind, List<object> targets, List<object> scenes)
            {
                if (targets == null || targets.Count == 0 || scenes == null) return false;
                var sceneByPath = scenes.Cast<IDictionary<string, object>>().ToDictionary(scene => Text(scene, "path"), StringComparer.Ordinal);
                var identities = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in targets)
                {
                    var target = value as IDictionary<string, object>;
                    if (target == null || !sceneByPath.ContainsKey(Text(target, "scenePath"))) return false;
                    if (kind == "legacy")
                    {
                        if (!string.Equals(Text(target, "sceneGuid"), Text(sceneByPath[Text(target, "scenePath")], "guid"), StringComparison.Ordinal)
                            || !identities.Add(Text(target, "sceneGuid"))) return false;
                    }
                    else
                    {
                        object nameValue; object beforeIdentityValue; object beforeHashValue;
                        if (string.IsNullOrWhiteSpace(Text(target, "globalId")) || !identities.Add(Text(target, "globalId"))
                            || !target.TryGetValue("name", out nameValue) || !(nameValue is string)
                            || !target.TryGetValue("beforeDataIdentity", out beforeIdentityValue) || !(beforeIdentityValue is string)
                            || !target.TryGetValue("beforeDataHash", out beforeHashValue) || !(beforeHashValue is string)) return false;
                        var beforeIdentity = (string)beforeIdentityValue;
                        var beforeHash = (string)beforeHashValue;
                        if ((string.IsNullOrEmpty(beforeIdentity) && !string.IsNullOrEmpty(beforeHash))
                            || (!string.IsNullOrEmpty(beforeIdentity) && !IsSha256Text(beforeHash))) return false;
                    }
                }
                return kind != "legacy" || targets.Count == scenes.Count;
            }

            private static bool ValidateSurfaceResultList(List<object> values, long targetCount)
            {
                if (values == null || values.Count != targetCount) return false;
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in values)
                {
                    var item = value as IDictionary<string, object>;
                    object dirty;
                    if (item == null || string.IsNullOrWhiteSpace(Text(item, "globalId")) || !ids.Add(Text(item, "globalId"))
                        || string.IsNullOrWhiteSpace(Text(item, "scenePath")) || string.IsNullOrWhiteSpace(Text(item, "dataPath"))
                        || Text(item, "dataGuid").Length != 32 || !IsSha256Text(Text(item, "dataHash")) || !TryPositiveLong(item, "dataSizeBytes")
                        || !item.TryGetValue("sceneDirty", out dirty) || !(dirty is bool) || (bool)dirty) return false;
                }
                return true;
            }

            private static List<object> LegacyTargets(IEnumerable<NavMeshSceneFileData> scenes)
            {
                return scenes.Select(scene => (object)new Dictionary<string, object> { { "scenePath", scene.Path }, { "sceneGuid", scene.Guid } }).ToList();
            }

            private static List<object> SceneObjects(IEnumerable<NavMeshSceneFileData> scenes)
            {
                return scenes.Select(scene => (object)NavMeshToolSupport.SceneDictionary(scene)).ToList();
            }

            private static List<NavMeshSceneFileData> SceneFiles(Dictionary<string, object> root) { return SceneFilesFrom(root, "scenes"); }

            private static List<NavMeshSceneFileData> SceneFilesFrom(Dictionary<string, object> root, string key)
            {
                var result = new List<NavMeshSceneFileData>();
                object source;
                var values = root != null && root.TryGetValue(key, out source) ? source as IEnumerable : null;
                if (values != null) foreach (var value in values)
                {
                    var item = value as IDictionary<string, object>;
                    if (item == null) continue;
                    result.Add(new NavMeshSceneFileData { Path = Text(item, "path"), Guid = Text(item, "guid"), Sha256 = Text(item, "sha256"), SizeBytes = Long(item, "sizeBytes"), Dirty = Bool(item, "dirty") });
                }
                return result;
            }

            private static List<Dictionary<string, object>> Targets(Dictionary<string, object> root)
            {
                var result = new List<Dictionary<string, object>>();
                var values = root["targets"] as IEnumerable;
                if (values != null) foreach (var value in values) if (value is Dictionary<string, object>) result.Add((Dictionary<string, object>)value);
                return result;
            }
            private static List<string> TargetIds(Dictionary<string, object> root) { return Targets(root).Select(value => Text(value, "globalId")).Where(value => !string.IsNullOrEmpty(value)).ToList(); }
            private static Dictionary<string, object> PlannedEffect(string kind, long count) { return new Dictionary<string, object> { { "type", kind == "legacy" ? "navmesh.bake" : "navmesh.surface.bake" }, { "target", kind == "legacy" ? "open-scenes" : "all-active-surfaces" }, { "targetCount", count } }; }
            private static string PublicPhase(string phase) { return phase == "queued" ? "queued" : phase == "dispatching" ? "starting" : phase == "running" || phase == "verifying" ? "baking" : "unknown"; }
            private static long DurationMs(string start, string finish) { DateTimeOffset a; DateTimeOffset b; return DateTimeOffset.TryParse(start, out a) && DateTimeOffset.TryParse(finish, out b) ? (long)Math.Max(0, (b - a).TotalMilliseconds) : 0L; }
            private static string Text(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item != null ? Convert.ToString(item, CultureInfo.InvariantCulture) : string.Empty; }
            private static bool Bool(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item is bool && (bool)item; }
            private static long Long(IDictionary<string, object> value, string key) { long result; return long.TryParse(Text(value, key), out result) ? result : 0L; }

            /// <summary>AI Navigation editor API 的唯一 package-optional reflection boundary。</summary>
            private static class SurfaceApi
            {
                private static readonly Type SurfaceType = Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation", false);
                private static readonly Type ManagerType = Type.GetType("Unity.AI.Navigation.Editor.NavMeshAssetManager, Unity.AI.Navigation.Editor", false);
                internal static bool Available { get { return SurfaceType != null && ManagerType != null; } }

                internal sealed class SurfaceDispatchObservation
                {
                    internal readonly List<SurfaceDispatchEntry> Entries;
                    internal SurfaceDispatchObservation(List<SurfaceDispatchEntry> entries) { Entries = entries; }
                }

                internal sealed class SurfaceDispatchEntry
                {
                    internal string GlobalId;
                    internal Object Surface;
                    internal NavMeshData Data;
                    internal AsyncOperation Operation;
                }

                internal static List<object> PlanTargets()
                {
                    EnsureAvailable();
                    EnsureShape();
                    if (ActiveOperationCount() > 0) throw new BridgeCommandException("E_NAVMESH_BAKE_ACTIVE", "An unowned NavMeshSurface bake is already running.");
                    var result = new List<Dictionary<string, object>>();
                    foreach (var raw in Resources.FindObjectsOfTypeAll(SurfaceType))
                    {
                        var component = raw as Component;
                        var behaviour = raw as Behaviour;
                        if (component == null || behaviour == null || !behaviour.isActiveAndEnabled || EditorUtility.IsPersistent(component)) continue;
                        var scene = component.gameObject.scene;
                        if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path)) continue;
                        var globalId = GlobalObjectId.GetGlobalObjectIdSlow(component).ToString();
                        if (string.IsNullOrEmpty(globalId)) continue;
                        var beforeIdentity = DataIdentity(component);
                        var beforeHash = string.Empty;
                        if (!string.IsNullOrEmpty(beforeIdentity))
                        {
                            try { beforeHash = Text(DataProof(component), "dataHash"); }
                            catch { throw new BridgeCommandException("E_NAVMESH_SURFACE_PROOF_UNAVAILABLE", "Existing NavMeshSurface data could not be read before baking.", "Inspect or clear the surface data asset, then retry."); }
                        }
                        result.Add(new Dictionary<string, object>
                        {
                            { "globalId", globalId }, { "name", component.gameObject.name }, { "scenePath", scene.path },
                            { "beforeDataIdentity", beforeIdentity }, { "beforeDataHash", beforeHash }
                        });
                    }
                    result.Sort((left, right) => string.CompareOrdinal(Text(left, "globalId"), Text(right, "globalId")));
                    if (result.Count == 0)
                        throw new BridgeCommandException("E_NAVMESH_SURFACE_NOT_FOUND", "No active NavMeshSurface component exists in a loaded, saved scene.", "Add and enable a NavMeshSurface in a saved scene, then retry.");
                    return result.Cast<object>().ToList();
                }

                internal static void Start(List<string> ids)
                {
                    var surfaces = Resolve(ids);
                    Method("StartBakingSurfaces", BindingFlags.Instance | BindingFlags.Public, new[] { typeof(Object[]) }).Invoke(Manager(), new object[] { surfaces });
                }

                /// <summary>Start返回后的同一调用栈捕获exact AsyncOperation引用；该对象绝不写入journal。</summary>
                internal static SurfaceDispatchObservation CaptureObservation(List<string> ids)
                {
                    var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
                    if (wanted.Count != ids.Count)
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMeshSurface target identities are not unique; outcome is unknown.");
                    var byId = new Dictionary<string, SurfaceDispatchEntry>(StringComparer.Ordinal);
                    foreach (var operation in Operations())
                    {
                        var surface = Field(operation, "surface") as Object;
                        var data = Field(operation, "bakeData") as NavMeshData;
                        var async = Field(operation, "bakeOperation") as AsyncOperation;
                        var globalId = surface == null ? string.Empty : GlobalObjectId.GetGlobalObjectIdSlow(surface).ToString();
                        if (surface == null || data == null || async == null || !wanted.Contains(globalId)) continue;
                        if (byId.ContainsKey(globalId))
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMeshSurface exact AsyncOperation observation is duplicated; outcome is unknown.");
                        byId.Add(globalId, new SurfaceDispatchEntry { GlobalId = globalId, Surface = surface, Data = data, Operation = async });
                    }
                    if (byId.Count != wanted.Count || wanted.Any(globalId => !byId.ContainsKey(globalId)))
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMeshSurface exact AsyncOperation observation could not be captured; outcome is unknown.");
                    return new SurfaceDispatchObservation(ids.Select(globalId => byId[globalId]).ToList());
                }

                internal static bool IsRunning(SurfaceDispatchObservation observation)
                {
                    if (observation == null) return false;
                    var current = Operations();
                    foreach (var entry in observation.Entries)
                    {
                        if (entry.Operation == null || entry.Operation.isDone) continue;
                        var exact = false;
                        foreach (var operation in current)
                        {
                            var async = Field(operation, "bakeOperation") as AsyncOperation;
                            var data = Field(operation, "bakeData") as NavMeshData;
                            if (ReferenceEquals(async, entry.Operation) && ReferenceEquals(data, entry.Data)) { exact = true; break; }
                        }
                        if (!exact) return true;
                        return true;
                    }
                    return false;
                }

                internal static bool IsPresent(SurfaceDispatchObservation observation)
                {
                    if (observation == null) return false;
                    foreach (var operation in Operations())
                    {
                        var async = Field(operation, "bakeOperation") as AsyncOperation;
                        var data = Field(operation, "bakeData") as NavMeshData;
                        if (observation.Entries.Any(entry => ReferenceEquals(async, entry.Operation) && ReferenceEquals(data, entry.Data))) return true;
                    }
                    return false;
                }

                internal static bool HasForeignOperations(SurfaceDispatchObservation observation)
                {
                    if (observation == null) return false;
                    foreach (var operation in Operations())
                    {
                        var async = Field(operation, "bakeOperation") as AsyncOperation;
                        var data = Field(operation, "bakeData") as NavMeshData;
                        if (!observation.Entries.Any(entry => ReferenceEquals(async, entry.Operation) && ReferenceEquals(data, entry.Data))) return true;
                    }
                    return false;
                }

                internal static bool PublishedDataMatches(SurfaceDispatchObservation observation)
                {
                    if (observation == null || observation.Entries.Count == 0) return false;
                    var property = SurfaceType.GetProperty("navMeshData", BindingFlags.Instance | BindingFlags.Public);
                    if (property == null) return false;
                    foreach (var entry in observation.Entries)
                    {
                        if (entry.Surface == null || !SurfaceType.IsInstanceOfType(entry.Surface)
                            || !ReferenceEquals(property.GetValue(entry.Surface, null), entry.Data)) return false;
                    }
                    return true;
                }

                internal static bool AnyBaking(List<string> ids)
                {
                    var manager = Manager();
                    var method = Method("IsSurfaceBaking", BindingFlags.Instance | BindingFlags.Public, new[] { SurfaceType });
                    foreach (var surface in Resolve(ids)) if ((bool)method.Invoke(manager, new object[] { surface })) return true;
                    return false;
                }

                internal static double? Progress(SurfaceDispatchObservation observation)
                {
                    if (observation == null || observation.Entries.Count == 0) return null;
                    double total = 0d;
                    foreach (var entry in observation.Entries)
                        total += entry.Operation == null ? 0d : Math.Max(0d, Math.Min(1d, entry.Operation.progress));
                    return total / observation.Entries.Count;
                }

                /// <summary>仅供Start返回后的同一主线程调用栈rollback exact bakeData。</summary>
                internal static void Cancel(SurfaceDispatchObservation observation)
                {
                    if (observation == null) return;
                    foreach (var entry in observation.Entries)
                        if (entry.Data != null && entry.Operation != null && !entry.Operation.isDone)
                            UnityEngine.AI.NavMeshBuilder.Cancel(entry.Data);
                }

                internal static int ActiveOperationCount() { return Available ? Operations().Count : 0; }
                internal static bool HasActiveData()
                {
                    if (!Available) return false;
                    var property = SurfaceType.GetProperty("navMeshData", BindingFlags.Instance | BindingFlags.Public);
                    if (property == null) throw new BridgeCommandException("E_NAVMESH_SURFACE_API_UNAVAILABLE", "AI Navigation navMeshData API is unavailable.");
                    foreach (var raw in Resources.FindObjectsOfTypeAll(SurfaceType))
                    {
                        var behaviour = raw as Behaviour;
                        var component = raw as Component;
                        if (behaviour == null || component == null || !behaviour.isActiveAndEnabled || EditorUtility.IsPersistent(component)) continue;
                        if (property.GetValue(component, null) != null) return true;
                    }
                    return false;
                }

                internal static bool TryProve(List<string> ids, List<Dictionary<string, object>> plan, bool requireChangedArtifact, out List<object> result)
                {
                    result = new List<object>();
                    var planned = plan.ToDictionary(value => Text(value, "globalId"), StringComparer.Ordinal);
                    try
                    {
                        foreach (var surface in Resolve(ids))
                        {
                            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(surface).ToString();
                            Dictionary<string, object> expected;
                            if (!planned.TryGetValue(globalId, out expected)) return false;
                            var proof = DataProof((Component)surface);
                            var beforeIdentity = Text(expected, "beforeDataIdentity");
                            if (string.IsNullOrEmpty(Text(proof, "dataIdentity"))) return false;
                            if (requireChangedArtifact && !string.IsNullOrEmpty(beforeIdentity)
                                && string.Equals(Text(proof, "dataIdentity"), beforeIdentity, StringComparison.Ordinal)
                                && string.Equals(Text(proof, "dataHash"), Text(expected, "beforeDataHash"), StringComparison.Ordinal)) return false;
                            result.Add(proof);
                        }
                        return result.Count == ids.Count;
                    }
                    catch { result = new List<object>(); return false; }
                }

                private static Object[] Resolve(List<string> ids)
                {
                    EnsureAvailable();
                    var result = new List<Object>();
                    foreach (var id in ids)
                    {
                        GlobalObjectId parsed;
                        if (!GlobalObjectId.TryParse(id, out parsed)) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Stored NavMeshSurface identity is invalid; outcome is unknown.");
                        var value = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed);
                        if (value == null || !SurfaceType.IsInstanceOfType(value)) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Stored NavMeshSurface no longer resolves; outcome is unknown.");
                        result.Add(value);
                    }
                    return result.ToArray();
                }

                private static Dictionary<string, object> DataProof(Component surface)
                {
                    var property = SurfaceType.GetProperty("navMeshData", BindingFlags.Instance | BindingFlags.Public);
                    var data = property == null ? null : property.GetValue(surface, null) as Object;
                    if (data == null) throw new InvalidOperationException("NavMeshSurface data is null.");
                    long localId; var guid = string.Empty;
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(data, out guid, out localId) || string.IsNullOrEmpty(guid)) throw new InvalidOperationException("NavMeshSurface data identity is unavailable.");
                    var path = AssetDatabase.GetAssetPath(data);
                    if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("NavMeshSurface data path is invalid.");
                    var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), path));
                    if (!File.Exists(full)) throw new InvalidOperationException("NavMeshSurface data file is missing.");
                    var bytes = File.ReadAllBytes(full);
                    return new Dictionary<string, object>
                    {
                        { "globalId", GlobalObjectId.GetGlobalObjectIdSlow(surface).ToString() }, { "name", surface.gameObject.name }, { "scenePath", surface.gameObject.scene.path },
                        { "sceneDirty", surface.gameObject.scene.isDirty }, { "dataPath", path }, { "dataGuid", guid },
                        { "dataLocalId", localId.ToString(CultureInfo.InvariantCulture) }, { "dataIdentity", guid + ":" + localId.ToString(CultureInfo.InvariantCulture) },
                        { "dataHash", NavMeshToolSupport.Sha256(bytes) }, { "dataSizeBytes", bytes.LongLength }
                    };
                }

                private static string DataIdentity(Component surface)
                {
                    var property = SurfaceType.GetProperty("navMeshData", BindingFlags.Instance | BindingFlags.Public);
                    var data = property == null ? null : property.GetValue(surface, null) as Object;
                    long localId; string guid;
                    return data != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(data, out guid, out localId) && !string.IsNullOrEmpty(guid) ? guid + ":" + localId.ToString(CultureInfo.InvariantCulture) : string.Empty;
                }

                private static object Manager()
                {
                    EnsureAvailable();
                    var property = ManagerType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                    var value = property == null ? null : property.GetValue(null, null);
                    if (value == null) throw new BridgeCommandException("E_NAVMESH_SURFACE_API_UNAVAILABLE", "AI Navigation NavMeshAssetManager.instance is unavailable.");
                    return value;
                }

                private static IList Operations()
                {
                    EnsureAvailable();
                    var method = ManagerType.GetMethod("GetBakeOperations", BindingFlags.Instance | BindingFlags.NonPublic);
                    var value = method == null ? null : method.Invoke(Manager(), null) as IList;
                    if (value == null) throw new BridgeCommandException("E_NAVMESH_SURFACE_API_UNAVAILABLE", "AI Navigation bake operation API is unavailable.", "Use a supported com.unity.ai.navigation version.");
                    return value;
                }

                private static void EnsureShape()
                {
                    Method("StartBakingSurfaces", BindingFlags.Instance | BindingFlags.Public, new[] { typeof(Object[]) });
                    Method("IsSurfaceBaking", BindingFlags.Instance | BindingFlags.Public, new[] { SurfaceType });
                    Operations();
                }
                private static MethodInfo Method(string name, BindingFlags flags, Type[] parameters)
                {
                    EnsureAvailable();
                    var method = ManagerType.GetMethod(name, flags, null, parameters, null);
                    if (method == null) throw new BridgeCommandException("E_NAVMESH_SURFACE_API_UNAVAILABLE", "AI Navigation API is unavailable: " + name + ".", "Use a supported com.unity.ai.navigation version.");
                    return method;
                }
                private static object Field(object value, string name)
                {
                    var field = value == null ? null : value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field == null) throw new BridgeCommandException("E_NAVMESH_SURFACE_API_UNAVAILABLE", "AI Navigation bake operation field is unavailable: " + name + ".");
                    return field.GetValue(value);
                }
                private static void EnsureAvailable()
                {
                    if (!Available) throw new BridgeCommandException("E_PACKAGE_UNAVAILABLE", "navmesh.surface.bake requires com.unity.ai.navigation.", "Install com.unity.ai.navigation, then retry.");
                }
            }
        }
    }
}
#pragma warning restore 0618
#endif
